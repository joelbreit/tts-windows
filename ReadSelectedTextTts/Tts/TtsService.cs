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
/// </summary>
public sealed class TtsService : IDisposable
{
    private readonly MediaPlayer _mediaPlayer;
    private readonly TtsProviderRegistry _registry;
    private readonly TelemetryService _telemetry;
    private IRandomAccessStream? _activeStream;

    public TtsService(TtsProviderRegistry registry, TelemetryService telemetry)
    {
        _registry = registry;
        _telemetry = telemetry;
        Log.Inf("Initializing TTS playback engine.");
        _mediaPlayer = new MediaPlayer();

        _mediaPlayer.MediaEnded += (_, _) =>
        {
            Log.Dbg("Media playback ended.");
            UpdatePlaybackState(false, false);
        };
        _mediaPlayer.MediaFailed += (_, args) =>
        {
            Log.Err($"Media playback failed: {args.Error} ({args.ErrorMessage})");
            UpdatePlaybackState(false, false);
        };
    }

    public bool IsPlaying { get; private set; }

    public bool IsPaused { get; private set; }

    public event EventHandler? PlaybackStateChanged;

    public async Task SpeakAsync(
        string text,
        VoiceOption voice,
        IProviderConfig config,
        double rate,
        string source)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            Log.Wrn("SpeakAsync called with empty text.");
            return;
        }

        var provider = _registry.GetOrDefault(voice.ProviderId);
        Log.Dbg($"Speak request: provider='{voice.ProviderId}', voice='{voice.DisplayName}', textLength={text.Length}, rate={rate:F1}x");
        Stop();

        var usageEvent = new TtsUsageEvent
        {
            Timestamp = DateTimeOffset.Now,
            ProviderId = voice.ProviderId,
            VoiceId = voice.Id,
            CharacterCount = text.Length,
            Source = source,
        };
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var result = await provider.SynthesizeAsync(text, voice, config);
            stopwatch.Stop();
            usageEvent.SynthesisMs = stopwatch.ElapsedMilliseconds;

            _activeStream = result.Stream;
            _mediaPlayer.Source = MediaSource.CreateFromStream(result.Stream, result.ContentType);
            Log.Trc($"Synthesized stream. Size={result.Stream.Size}, ContentType={result.ContentType}, synthMs={usageEvent.SynthesisMs}");

            ApplyPlaybackRate(rate);
            _mediaPlayer.Play();
            UpdatePlaybackState(true, false);
            usageEvent.Success = true;
            Log.Dbg("Playback started.");
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            usageEvent.SynthesisMs = stopwatch.ElapsedMilliseconds;
            usageEvent.Success = false;
            usageEvent.Error = ex.Message;
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

        _mediaPlayer.Play();
        UpdatePlaybackState(true, false);
        Log.Dbg("Playback resumed.");
    }

    public void Stop()
    {
        Log.Trc($"Stopping playback. IsPlaying={IsPlaying}, IsPaused={IsPaused}");
        _mediaPlayer.Pause();
        _mediaPlayer.Source = null;

        _activeStream?.Dispose();
        _activeStream = null;

        UpdatePlaybackState(false, false);
    }

    public void Dispose()
    {
        Log.Inf("Disposing TTS playback engine.");
        Stop();
        _mediaPlayer.Dispose();
        GC.SuppressFinalize(this);
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
                Log.Dbg($"Playback rate applied: {appliedRate:F2}x.");
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
        IsPlaying = isPlaying;
        IsPaused = isPaused;
        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
        Log.Trc($"Playback state updated: IsPlaying={isPlaying}, IsPaused={isPaused}");
    }
}
