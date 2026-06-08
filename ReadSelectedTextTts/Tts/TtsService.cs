using ReadSelectedTextTts.Models;
using Log = Logger.Logger;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.SpeechSynthesis;

namespace ReadSelectedTextTts.Tts;

public sealed class TtsService : IDisposable
{
    private readonly MediaPlayer _mediaPlayer;
    private SpeechSynthesisStream? _activeStream;

    public TtsService(string appDirectoryPath)
    {
        Log.Inf($"Initializing TTS service. App directory: {appDirectoryPath}");
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

    public IReadOnlyList<VoiceOption> GetInstalledVoices()
    {
        // NOTE: AllVoices only ever returns the legacy SAPI voices (David, Mark, Zira).
        // Windows 11 "Natural"/"Natural HD" voices are NOT reachable through this (or any
        // public) API — see docs/windows-natural-voices-unavailable.md. Do not add a
        // "(Natural)" preference here; it can never match.
        var voices = SpeechSynthesizer.AllVoices
            .Select(voice => new VoiceOption(voice.DisplayName, voice.Id, voice))
            .OrderBy(voice => voice.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Log.Inf($"Loaded {voices.Count} voice(s).");
        return voices;
    }

    public async Task SpeakAsync(string text, VoiceInformation voice, double rate)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            Log.Wrn("SpeakAsync called with empty text.");
            return;
        }

        Log.Dbg($"Speak request: voice='{voice.DisplayName}', textLength={text.Length}, rate={rate:F1}x");
        Stop();

        using var synth = new SpeechSynthesizer();
        synth.Voice = voice;

        _activeStream = await synth.SynthesizeTextToStreamAsync(text);
        _mediaPlayer.Source = MediaSource.CreateFromStream(_activeStream, _activeStream.ContentType);
        Log.Trc($"Synthesized stream. Size={_activeStream.Size}, ContentType={_activeStream.ContentType}");

        ApplyPlaybackRate(rate);
        _mediaPlayer.Play();
        UpdatePlaybackState(true, false);
        Log.Dbg("Playback started.");
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
        Log.Inf("Disposing TTS service.");
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
