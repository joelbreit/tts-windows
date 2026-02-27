using ReadSelectedTextTts.Models;
using System.IO;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.SpeechSynthesis;

namespace ReadSelectedTextTts.Tts;

public sealed class TtsService : IDisposable
{
    private readonly MediaPlayer _mediaPlayer;
    private readonly string _debugLogPath;
    private SpeechSynthesisStream? _activeStream;

    public TtsService(string appDirectoryPath)
    {
        Directory.CreateDirectory(appDirectoryPath);
        _debugLogPath = Path.Combine(appDirectoryPath, "debug.log");
        _mediaPlayer = new MediaPlayer();

        _mediaPlayer.MediaEnded += (_, _) => UpdatePlaybackState(false, false);
        _mediaPlayer.MediaFailed += (_, args) =>
        {
            LogDebug($"Media failed: {args.Error} ({args.ErrorMessage})");
            UpdatePlaybackState(false, false);
        };
    }

    public bool IsPlaying { get; private set; }

    public bool IsPaused { get; private set; }

    public event EventHandler? PlaybackStateChanged;

    public IReadOnlyList<VoiceOption> GetInstalledVoices()
    {
        return SpeechSynthesizer.AllVoices
            .Select(voice => new VoiceOption(voice.DisplayName, voice.Id, voice))
            .OrderBy(voice => voice.DisplayName.Contains("(Natural)", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(voice => voice.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task SpeakAsync(string text, VoiceInformation voice, double rate)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        Stop();

        using var synth = new SpeechSynthesizer();
        synth.Voice = voice;

        _activeStream = await synth.SynthesizeTextToStreamAsync(text);
        _mediaPlayer.Source = MediaSource.CreateFromStream(_activeStream, _activeStream.ContentType);

        ApplyPlaybackRate(rate);
        _mediaPlayer.Play();
        UpdatePlaybackState(true, false);
    }

    public void Pause()
    {
        if (!IsPlaying || IsPaused)
        {
            return;
        }

        _mediaPlayer.Pause();
        UpdatePlaybackState(true, true);
    }

    public void Resume()
    {
        if (!IsPlaying || !IsPaused)
        {
            return;
        }

        _mediaPlayer.Play();
        UpdatePlaybackState(true, false);
    }

    public void Stop()
    {
        _mediaPlayer.Pause();
        _mediaPlayer.Source = null;

        _activeStream?.Dispose();
        _activeStream = null;

        UpdatePlaybackState(false, false);
    }

    public void Dispose()
    {
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
                LogDebug($"Requested rate {requestedClamped:F1}x, applied {appliedRate:F2}x.");
            }
            else
            {
                LogDebug($"Applied rate {appliedRate:F2}x.");
            }
        }
        catch (Exception ex)
        {
            LogDebug($"Rate set failed for {requestedClamped:F1}x: {ex.Message}");
            try
            {
                _mediaPlayer.PlaybackSession.PlaybackRate = 1.0;
                LogDebug("Fell back to 1.0x playback rate.");
            }
            catch
            {
                LogDebug("Failed to apply fallback playback rate.");
            }
        }
    }

    private void UpdatePlaybackState(bool isPlaying, bool isPaused)
    {
        IsPlaying = isPlaying;
        IsPaused = isPaused;
        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void LogDebug(string message)
    {
        try
        {
            File.AppendAllText(_debugLogPath, $"{DateTime.Now:O} {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }
}
