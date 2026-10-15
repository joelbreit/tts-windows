using System.Diagnostics;
using ReadSelectedTextTts.Models;
using ReadSelectedTextTts.Telemetry;
using Log = Logger.Logger;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Streams;

namespace ReadSelectedTextTts.Tts;

/// <summary>
/// Playback engine. Owns the shared <see cref="MediaPlayer"/> and rate control;
/// delegates synthesis to the active <see cref="ITtsProvider"/> (resolved per call
/// from the voice's provider id) and records usage telemetry.
///
/// Long reads are split by <see cref="TextChunker"/> and played as a pipeline: the
/// first (short) chunk starts as soon as it arrives while the rest are synthesized in
/// the background, so time-to-first-audio no longer scales with the length of the
/// selection. <see cref="PrefetchDepth"/> requests are kept in flight at a time.
/// </summary>
public sealed class TtsService : IDisposable
{
    /// <summary>How many chunks are synthesized ahead of the one currently playing.</summary>
    private const int PrefetchDepth = 3;

    private readonly MediaPlayer _mediaPlayer;
    private readonly TtsProviderRegistry _registry;
    private readonly TelemetryService _telemetry;
    private readonly object _gate = new();
    private PlaybackSession? _session;
    private IRandomAccessStream? _activeStream;
    private MediaSource? _activeSource;
    private double _rate = 1.0;

    /// <summary>
    /// True between a chunk ending and the next one loading. The finished chunk is still
    /// the player's source at that point, so Resume must not call Play — it would replay
    /// the chunk that just finished.
    /// </summary>
    private bool _awaitingChunk;

    public TtsService(TtsProviderRegistry registry, TelemetryService telemetry)
    {
        _registry = registry;
        _telemetry = telemetry;
        Log.Inf("Initializing TTS playback engine.");
        _mediaPlayer = new MediaPlayer();

        _mediaPlayer.MediaEnded += (_, _) =>
        {
            Log.Trc("Media item ended.");
            OnMediaEnded();
        };
        _mediaPlayer.MediaFailed += (_, args) =>
        {
            Log.Err($"Media playback failed: {args.Error} ({args.ErrorMessage})");
            Stop();
            PlaybackError?.Invoke(this, $"Audio playback failed: {args.ErrorMessage}");
        };
    }

    public bool IsPlaying { get; private set; }

    public bool IsPaused { get; private set; }

    public bool IsLoading { get; private set; }

    public event EventHandler? PlaybackStateChanged;

    /// <summary>Raised when synthesis or playback fails, so the UI can surface the error.</summary>
    public event EventHandler<string>? PlaybackError;

    public async Task SpeakAsync(
        string text,
        VoiceOption voice,
        IProviderConfig config,
        double rate,
        string source)
    {
        var chunks = TextChunker.Split(text);
        if (chunks.Count == 0)
        {
            Log.Wrn("SpeakAsync called with empty text.");
            return;
        }

        var provider = _registry.GetOrDefault(voice.ProviderId);
        Log.Dbg($"Speak request: provider='{voice.ProviderId}', voice='{voice.DisplayName}', textLength={text.Length}, chunks={chunks.Count}, rate={rate:F1}x");
        Stop();

        var usageEvent = new TtsUsageEvent
        {
            Timestamp = DateTimeOffset.Now,
            ProviderId = voice.ProviderId,
            VoiceId = voice.Id,
            CharacterCount = text.Length,
            ChunkCount = chunks.Count,
            Source = source,
        };
        var stopwatch = Stopwatch.StartNew();

        var session = new PlaybackSession(provider, voice, config, chunks);
        lock (_gate)
        {
            _session = session;
            _rate = rate;
        }
        UpdateLoadingState(true);

        try
        {
            // Fire the first few requests together so chunk 2 is usually ready before
            // chunk 1 finishes speaking.
            for (var i = 0; i < PrefetchDepth && i < chunks.Count; i++)
            {
                session.Prefetch(i);
            }

            var first = await session.GetAsync(0);
            stopwatch.Stop();
            usageEvent.SynthesisMs = stopwatch.ElapsedMilliseconds;

            if (!TryPlay(session, 0, first))
            {
                Log.Dbg("First chunk arrived after the session was superseded; discarding.");
                return;
            }

            usageEvent.Success = true;
            Log.Dbg($"Playback started. Chunk 1/{chunks.Count}, timeToAudioMs={usageEvent.SynthesisMs}");
        }
        catch (OperationCanceledException) when (session.IsCancellationRequested)
        {
            stopwatch.Stop();
            usageEvent.SynthesisMs = stopwatch.ElapsedMilliseconds;
            Log.Trc("First audio chunk cancelled because playback was stopped or replaced.");
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            usageEvent.SynthesisMs = stopwatch.ElapsedMilliseconds;
            usageEvent.Success = false;
            usageEvent.Error = ex.Message;
            if (EndSession(session))
            {
                PlaybackError?.Invoke(this, $"Audio could not load: {ex.Message}");
            }
            throw;
        }
        finally
        {
            _ = _telemetry.RecordAsync(usageEvent);
        }
    }

    public void Pause()
    {
        if (!IsPlaying || IsPaused)
        {
            Log.Trc($"Pause ignored. IsPlaying={IsPlaying}, IsPaused={IsPaused}");
            return;
        }

        _mediaPlayer.Pause();
        UpdatePlaybackState(true, true);
        Log.Dbg("Playback paused.");
    }

    public void Resume()
    {
        if (!IsPlaying || !IsPaused)
        {
            Log.Trc($"Resume ignored. IsPlaying={IsPlaying}, IsPaused={IsPaused}");
            return;
        }

        UpdatePlaybackState(true, false);

        lock (_gate)
        {
            // If the pause landed in the gap between chunks, the pending chunk starts
            // itself once it arrives; playing now would repeat the previous chunk.
            if (!_awaitingChunk && _mediaPlayer.Source is not null)
            {
                _mediaPlayer.Play();
            }
        }

        Log.Dbg("Playback resumed.");
    }

    public void Stop()
    {
        PlaybackSession? session;
        lock (_gate)
        {
            session = _session;
            _session = null;
        }

        Log.Trc($"Stopping playback. IsPlaying={IsPlaying}, IsPaused={IsPaused}");
        session?.Dispose();

        lock (_gate)
        {
            _mediaPlayer.Pause();
            _mediaPlayer.Source = null;
            ReleaseCurrentItem();
        }

        UpdatePlaybackState(false, false);
        UpdateLoadingState(false);
    }

    public void Dispose()
    {
        Log.Inf("Disposing TTS playback engine.");
        Stop();
        _mediaPlayer.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Loads a synthesized chunk into the player. Returns false if the session was
    /// superseded or stopped while the chunk was in flight, in which case the audio is
    /// left for the session's drain to release rather than interrupting what is playing now.
    /// </summary>
    private bool TryPlay(PlaybackSession session, int index, TtsSynthesisResult result)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_session, session))
            {
                return false;
            }

            // From here the player owns this stream, so the session must not free it too.
            session.HandOff(index);
            session.CurrentIndex = index;
            _awaitingChunk = false;
            var source = MediaSource.CreateFromStream(result.Stream, result.ContentType);
            _mediaPlayer.Source = source;

            ReleaseCurrentItem();
            _activeStream = result.Stream;
            _activeSource = source;

            ApplyPlaybackRate(_rate);
            Log.Trc($"Chunk {index + 1}/{session.ChunkCount} loaded. Size={result.Stream.Size}, ContentType={result.ContentType}");

            if (!IsPaused)
            {
                _mediaPlayer.Play();
            }
        }

        UpdateLoadingState(false);
        UpdatePlaybackState(true, IsPaused);
        return true;
    }

    private void OnMediaEnded()
    {
        PlaybackSession? session;
        lock (_gate)
        {
            session = _session;
            _awaitingChunk = session is not null;
        }

        if (session is null)
        {
            UpdatePlaybackState(false, false);
            return;
        }

        if (session.CurrentIndex + 1 >= session.ChunkCount)
        {
            Log.Dbg("All chunks played.");
            EndSession(session);
            return;
        }

        UpdateLoadingState(true);
        _ = AdvanceAsync(session);
    }

    /// <summary>Waits for the next chunk (usually already synthesized) and plays it.</summary>
    private async Task AdvanceAsync(PlaybackSession session)
    {
        var next = session.CurrentIndex + 1;

        // Keep the pipeline topped up as we consume it.
        session.Prefetch(next + PrefetchDepth - 1);

        try
        {
            var result = await session.GetAsync(next);
            TryPlay(session, next, result);
        }
        catch (OperationCanceledException) when (session.IsCancellationRequested)
        {
            Log.Trc($"Chunk {next + 1} cancelled.");
        }
        catch (Exception ex)
        {
            Log.Err($"Chunk {next + 1}/{session.ChunkCount} failed: {ex}");
            if (EndSession(session))
            {
                PlaybackError?.Invoke(this, $"Read stopped after part {next} of {session.ChunkCount}: {ex.Message}");
            }
        }
    }

    /// <summary>Tears down a session, but only if it is still the active one.</summary>
    private bool EndSession(PlaybackSession session)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_session, session))
            {
                return false;
            }

            _session = null;
            _mediaPlayer.Source = null;
            ReleaseCurrentItem();
        }

        session.Dispose();
        UpdateLoadingState(false);
        UpdatePlaybackState(false, false);
        return true;
    }

    /// <summary>Releases the stream and media source backing the chunk that just finished.</summary>
    private void ReleaseCurrentItem()
    {
        _awaitingChunk = false;
        _activeSource?.Dispose();
        _activeSource = null;
        _activeStream?.Dispose();
        _activeStream = null;
    }

    private void ApplyPlaybackRate(double requestedRate)
    {
        var requestedClamped = Math.Clamp(requestedRate, 0.1, 4.0);

        try
        {
            _mediaPlayer.PlaybackSession.PlaybackRate = requestedClamped;
            var appliedRate = _mediaPlayer.PlaybackSession.PlaybackRate;

            if (Math.Abs(appliedRate - requestedClamped) > 0.001)
            {
                Log.Wrn($"Requested rate {requestedClamped:F1}x, applied {appliedRate:F2}x.");
            }
            else
            {
                Log.Trc($"Playback rate applied: {appliedRate:F2}x.");
            }
        }
        catch (Exception ex)
        {
            Log.Wrn($"Playback rate set failed for {requestedClamped:F1}x: {ex.Message}");
            try
            {
                _mediaPlayer.PlaybackSession.PlaybackRate = 1.0;
                Log.Wrn("Fell back to 1.0x playback rate.");
            }
            catch
            {
                Log.Err("Failed to apply fallback playback rate.");
            }
        }
    }

    private void UpdatePlaybackState(bool isPlaying, bool isPaused)
    {
        if (IsPlaying == isPlaying && IsPaused == isPaused)
        {
            return;
        }

        IsPlaying = isPlaying;
        IsPaused = isPaused;
        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
        Log.Trc($"Playback state updated: IsPlaying={isPlaying}, IsPaused={isPaused}");
    }

    private void UpdateLoadingState(bool isLoading)
    {
        if (IsLoading == isLoading)
        {
            return;
        }

        IsLoading = isLoading;
        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// One read request: its chunks, the synthesis tasks in flight for them, and the
    /// cancellation that unwinds the lot when the user stops or starts another read.
    /// </summary>
    private sealed class PlaybackSession : IDisposable
    {
        private readonly ITtsProvider _provider;
        private readonly VoiceOption _voice;
        private readonly IProviderConfig _config;
        private readonly IReadOnlyList<string> _chunks;
        private readonly Task<TtsSynthesisResult>?[] _pending;
        private readonly bool[] _handedOff;
        private readonly CancellationTokenSource _cts = new();
        private readonly object _lock = new();
        private bool _disposed;

        public PlaybackSession(
            ITtsProvider provider,
            VoiceOption voice,
            IProviderConfig config,
            IReadOnlyList<string> chunks)
        {
            _provider = provider;
            _voice = voice;
            _config = config;
            _chunks = chunks;
            _pending = new Task<TtsSynthesisResult>?[chunks.Count];
            _handedOff = new bool[chunks.Count];
        }

        /// <summary>Index of the chunk currently loaded in the player.</summary>
        public int CurrentIndex { get; set; }

        public int ChunkCount => _chunks.Count;

        public bool IsCancellationRequested => _cts.IsCancellationRequested;

        /// <summary>Starts synthesis for a chunk if it has not been requested yet. No-op out of range.</summary>
        public void Prefetch(int index)
        {
            if (index < 0 || index >= _chunks.Count)
            {
                return;
            }

            lock (_lock)
            {
                if (_disposed || _pending[index] is not null)
                {
                    return;
                }

                Log.Trc($"Prefetching chunk {index + 1}/{_chunks.Count} ({_chunks[index].Length} chars).");
                _pending[index] = SynthesizeWithTimeoutRetryAsync(index);
            }
        }

        private async Task<TtsSynthesisResult> SynthesizeWithTimeoutRetryAsync(int index)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    return await _provider.SynthesizeAsync(_chunks[index], _voice, _config, _cts.Token);
                }
                catch (OperationCanceledException ex) when (!_cts.IsCancellationRequested)
                {
                    if (attempt >= 2)
                    {
                        throw new TimeoutException($"Speech synthesis for part {index + 1} timed out twice.", ex);
                    }

                    Log.Wrn($"Speech synthesis for part {index + 1}/{_chunks.Count} timed out; retrying once.");
                    await Task.Delay(500, _cts.Token);
                }
            }
        }

        public Task<TtsSynthesisResult> GetAsync(int index)
        {
            Prefetch(index);

            lock (_lock)
            {
                return _pending[index]
                       ?? Task.FromCanceled<TtsSynthesisResult>(new CancellationToken(true));
            }
        }

        /// <summary>
        /// Transfers ownership of a chunk's audio to the player. Handed-off streams are
        /// skipped by the drain, because the player disposes them when it moves on.
        /// </summary>
        public void HandOff(int index)
        {
            lock (_lock)
            {
                _handedOff[index] = true;
            }
        }

        public void Dispose()
        {
            Task<TtsSynthesisResult>[] pending;
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                pending = _pending
                    .Where((task, index) => task is not null && !_handedOff[index])
                    .Select(task => task!)
                    .ToArray();
            }

            _cts.Cancel();
            _ = DrainAsync(pending);
        }

        /// <summary>
        /// Lets the cancelled requests unwind off the caller's thread: observes their faults
        /// so they never surface as unobserved exceptions, frees audio that arrived too late,
        /// and only then disposes the token source the in-flight requests are still holding.
        /// </summary>
        private async Task DrainAsync(Task<TtsSynthesisResult>[] pending)
        {
            foreach (var task in pending)
            {
                try
                {
                    var result = await task.ConfigureAwait(false);
                    result.Stream.Dispose();
                }
                catch
                {
                    // Cancelled or failed; there is no audio to release.
                }
            }

            _cts.Dispose();
        }
    }
}
