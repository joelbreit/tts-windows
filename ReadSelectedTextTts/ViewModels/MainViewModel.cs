using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using ReadSelectedTextTts.Models;
using ReadSelectedTextTts.Selection;
using ReadSelectedTextTts.Settings;
using ReadSelectedTextTts.Tts;
using Windows.System;
using Log = Logger.Logger;

namespace ReadSelectedTextTts.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly SelectionReader _selectionReader;
    private readonly TtsService _ttsService;
    private readonly SettingsService _settingsService;
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private AppSettings _settings = new();
    private VoiceOption? _selectedVoice;
    private double _speed = 1.0;
    private string _manualText = "This is a test sentence from Read Selected Text TTS.";
    private uint _activeHotkeyModifiers;
    private uint _activeHotkeyKey;
    private uint _activeClipboardHotkeyModifiers = 0x0009;
    private uint _activeClipboardHotkeyKey = 0x43;
    private bool _isPlaying;
    private bool _isPaused;
    private bool _disposed;

    public MainViewModel(
        SelectionReader selectionReader,
        TtsService ttsService,
        SettingsService settingsService)
    {
        _selectionReader = selectionReader;
        _ttsService = ttsService;
        _settingsService = settingsService;
        _activeHotkeyModifiers = _settings.HotkeyModifiers;
        _activeHotkeyKey = _settings.HotkeyKey;

        _ttsService.PlaybackStateChanged += OnPlaybackStateChanged;

        ReadSelectionCommand = new AsyncRelayCommand(ReadSelectionAsync, () => SelectedVoice is not null);
        ReadClipboardCommand = new AsyncRelayCommand(ReadClipboardAsync, () => SelectedVoice is not null);
        PauseCommand = new RelayCommand(Pause, () => IsPlaying && !IsPaused);
        ResumeCommand = new RelayCommand(Resume, () => IsPlaying && IsPaused);
        StopCommand = new RelayCommand(Stop, () => IsPlaying);
        ReadTestTextCommand = new AsyncRelayCommand(ReadManualTextAsync, () => SelectedVoice is not null && !string.IsNullOrWhiteSpace(ManualText));
        IncreaseSpeedCommand = new RelayCommand(IncreaseSpeed, () => Speed < 4.0);
        DecreaseSpeedCommand = new RelayCommand(DecreaseSpeed, () => Speed > 0.1);
    }

    public event EventHandler<string>? NotificationRequested;

    public ObservableCollection<VoiceOption> Voices { get; } = [];

    public ICommand ReadSelectionCommand { get; }

    public ICommand ReadClipboardCommand { get; }

    public ICommand PauseCommand { get; }

    public ICommand ResumeCommand { get; }

    public ICommand StopCommand { get; }

    public ICommand ReadTestTextCommand { get; }

    public ICommand IncreaseSpeedCommand { get; }

    public ICommand DecreaseSpeedCommand { get; }

    public VoiceOption? SelectedVoice
    {
        get => _selectedVoice;
        set
        {
            if (!SetProperty(ref _selectedVoice, value))
            {
                return;
            }

            _settings.VoiceId = value?.Id;
            SaveSettingsFireAndForget();
            RaiseCommandCanExecuteChanged();
        }
    }

    public double Speed
    {
        get => _speed;
        set
        {
            var clamped = ClampSpeed(value);
            if (!SetProperty(ref _speed, clamped))
            {
                return;
            }

            OnPropertyChanged(nameof(SpeedDisplay));
            _settings.Speed = clamped;
            SaveSettingsFireAndForget();
            RaiseCommandCanExecuteChanged();
        }
    }

    public string SpeedDisplay => $"{Speed:F1}x";

    public string ManualText
    {
        get => _manualText;
        set
        {
            if (!SetProperty(ref _manualText, value))
            {
                return;
            }

            RaiseCommandCanExecuteChanged();
        }
    }

    public bool IsPlaying
    {
        get => _isPlaying;
        private set
        {
            if (SetProperty(ref _isPlaying, value))
            {
                RaiseCommandCanExecuteChanged();
            }
        }
    }

    public bool IsPaused
    {
        get => _isPaused;
        private set
        {
            if (SetProperty(ref _isPaused, value))
            {
                RaiseCommandCanExecuteChanged();
            }
        }
    }

    public uint HotkeyModifiers => _settings.HotkeyModifiers;

    public uint HotkeyKey => _settings.HotkeyKey;

    public string HotkeyDisplay =>
        $"Selection Hotkey: {FormatHotkey(_activeHotkeyModifiers, _activeHotkeyKey)} | Clipboard Hotkey: {FormatHotkey(_activeClipboardHotkeyModifiers, _activeClipboardHotkeyKey)}";

    public async Task InitializeAsync()
    {
        Log.Inf("Initializing MainViewModel.");
        _settings = await _settingsService.LoadAsync();
        NormalizeSettings();
        _activeHotkeyModifiers = _settings.HotkeyModifiers;
        _activeHotkeyKey = _settings.HotkeyKey;
        Log.Dbg(
            $"Loaded settings. Speed={_settings.Speed:F1}, VoiceId='{_settings.VoiceId ?? "<null>"}', Hotkey={FormatHotkey(_activeHotkeyModifiers, _activeHotkeyKey)}");

        Speed = _settings.Speed;

        Voices.Clear();
        foreach (var voice in _ttsService.GetInstalledVoices())
        {
            Voices.Add(voice);
        }

        if (Voices.Count == 0)
        {
            Log.Wrn("No Windows voices were found.");
            NotificationRequested?.Invoke(this, "No Windows voices installed.");
            await SaveSettingsAsync();
            return;
        }

        SelectedVoice = ResolveSelectedVoice();
        Log.Inf($"Selected default voice: {SelectedVoice?.DisplayName ?? "<none>"}");
        await SaveSettingsAsync();
        OnPropertyChanged(nameof(HotkeyDisplay));
    }

    public async Task ReadSelectionAsync()
    {
        if (SelectedVoice is null)
        {
            NotificationRequested?.Invoke(this, "No Windows voices installed.");
            return;
        }

        try
        {
            Log.Dbg($"ReadSelection requested. Active voice='{SelectedVoice.DisplayName}', speed={Speed:F1}x");
            var selectedText = await _selectionReader.ReadSelectionAsync();
            if (string.IsNullOrWhiteSpace(selectedText))
            {
                Log.Wrn("ReadSelection found no text.");
                NotificationRequested?.Invoke(this, "No selected text found. Select text in another app or use Read Test Text.");
                return;
            }

            Log.Inf($"ReadSelection speaking text. Length={selectedText.Length}");
            await _ttsService.SpeakAsync(selectedText, SelectedVoice.Voice, Speed);
            IsPlaying = _ttsService.IsPlaying;
            IsPaused = _ttsService.IsPaused;
        }
        catch (Exception ex)
        {
            Log.Err($"ReadSelection failed: {ex}");
            NotificationRequested?.Invoke(this, $"Read failed: {ex.Message}");
        }
    }

    public async Task ReadClipboardAsync()
    {
        if (SelectedVoice is null)
        {
            NotificationRequested?.Invoke(this, "No Windows voices installed.");
            return;
        }

        try
        {
            Log.Dbg($"ReadClipboard requested. Active voice='{SelectedVoice.DisplayName}', speed={Speed:F1}x");
            var clipboardText = _selectionReader.ReadClipboardText();
            if (string.IsNullOrWhiteSpace(clipboardText))
            {
                Log.Wrn("ReadClipboard found no text.");
                NotificationRequested?.Invoke(this, "Clipboard does not contain text.");
                return;
            }

            Log.Inf($"ReadClipboard speaking text. Length={clipboardText.Length}");
            await _ttsService.SpeakAsync(clipboardText, SelectedVoice.Voice, Speed);
            IsPlaying = _ttsService.IsPlaying;
            IsPaused = _ttsService.IsPaused;
        }
        catch (Exception ex)
        {
            Log.Err($"ReadClipboard failed: {ex}");
            NotificationRequested?.Invoke(this, $"Read failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Log.Inf("Disposing MainViewModel.");
        _ttsService.PlaybackStateChanged -= OnPlaybackStateChanged;
        _ttsService.Dispose();
        _saveLock.Dispose();
        GC.SuppressFinalize(this);
    }

    public void SetActiveHotkey(uint modifiers, uint key, bool persist)
    {
        _activeHotkeyModifiers = modifiers;
        _activeHotkeyKey = key;
        Log.Inf($"Active hotkey set to {FormatHotkey(modifiers, key)}. Persist={persist}");
        OnPropertyChanged(nameof(HotkeyDisplay));

        if (!persist)
        {
            return;
        }

        _settings.HotkeyModifiers = modifiers;
        _settings.HotkeyKey = key;
        SaveSettingsFireAndForget();
    }

    public void SetActiveClipboardHotkey(uint modifiers, uint key)
    {
        _activeClipboardHotkeyModifiers = modifiers;
        _activeClipboardHotkeyKey = key;
        Log.Inf($"Active clipboard hotkey set to {FormatHotkey(modifiers, key)}");
        OnPropertyChanged(nameof(HotkeyDisplay));
    }

    private void Pause()
    {
        _ttsService.Pause();
        IsPlaying = _ttsService.IsPlaying;
        IsPaused = _ttsService.IsPaused;
    }

    private void Resume()
    {
        _ttsService.Resume();
        IsPlaying = _ttsService.IsPlaying;
        IsPaused = _ttsService.IsPaused;
    }

    private void Stop()
    {
        _ttsService.Stop();
        IsPlaying = _ttsService.IsPlaying;
        IsPaused = _ttsService.IsPaused;
    }

    private async Task ReadManualTextAsync()
    {
        if (SelectedVoice is null)
        {
            NotificationRequested?.Invoke(this, "No Windows voices installed.");
            return;
        }

        if (string.IsNullOrWhiteSpace(ManualText))
        {
            NotificationRequested?.Invoke(this, "Enter test text first.");
            return;
        }

        try
        {
            Log.Dbg($"ReadTestText requested. Length={ManualText.Length}, speed={Speed:F1}x");
            await _ttsService.SpeakAsync(ManualText, SelectedVoice.Voice, Speed);
            IsPlaying = _ttsService.IsPlaying;
            IsPaused = _ttsService.IsPaused;
        }
        catch (Exception ex)
        {
            Log.Err($"ReadTestText failed: {ex}");
            NotificationRequested?.Invoke(this, $"Read failed: {ex.Message}");
        }
    }

    private void IncreaseSpeed()
    {
        Speed = Math.Round(Speed + 0.1, 1, MidpointRounding.AwayFromZero);
    }

    private void DecreaseSpeed()
    {
        Speed = Math.Round(Speed - 0.1, 1, MidpointRounding.AwayFromZero);
    }

    private static double ClampSpeed(double value)
    {
        var clamped = Math.Clamp(value, 0.1, 4.0);
        return Math.Round(clamped, 1, MidpointRounding.AwayFromZero);
    }

    private void NormalizeSettings()
    {
        _settings.Speed = ClampSpeed(_settings.Speed <= 0 ? 1.0 : _settings.Speed);

        if (_settings.HotkeyModifiers == 0)
        {
            _settings.HotkeyModifiers = 0x0009;
        }

        if (_settings.HotkeyKey == 0)
        {
            _settings.HotkeyKey = 0x52;
        }
    }

    private VoiceOption? ResolveSelectedVoice()
    {
        if (!string.IsNullOrWhiteSpace(_settings.VoiceId))
        {
            var matchingVoice = Voices.FirstOrDefault(voice => voice.Id == _settings.VoiceId);
            if (matchingVoice is not null)
            {
                return matchingVoice;
            }
        }

        var naturalVoice = Voices.FirstOrDefault(
            voice => voice.DisplayName.Contains("(Natural)", StringComparison.OrdinalIgnoreCase));

        return naturalVoice ?? Voices.FirstOrDefault();
    }

    private void OnPlaybackStateChanged(object? sender, EventArgs e)
    {
        if (System.Windows.Application.Current.Dispatcher.CheckAccess())
        {
            IsPlaying = _ttsService.IsPlaying;
            IsPaused = _ttsService.IsPaused;
            return;
        }

        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            IsPlaying = _ttsService.IsPlaying;
            IsPaused = _ttsService.IsPaused;
        });
    }

    private static string FormatHotkey(uint modifiers, uint virtualKey)
    {
        var parts = new List<string>();

        if ((modifiers & 0x0008) != 0)
        {
            parts.Add("Win");
        }

        if ((modifiers & 0x0001) != 0)
        {
            parts.Add("Alt");
        }

        if ((modifiers & 0x0002) != 0)
        {
            parts.Add("Ctrl");
        }

        if ((modifiers & 0x0004) != 0)
        {
            parts.Add("Shift");
        }

        var key = ((VirtualKey)virtualKey).ToString();
        parts.Add(key.Length == 1 ? key.ToUpperInvariant() : key);

        return string.Join("+", parts);
    }

    private void RaiseCommandCanExecuteChanged()
    {
        (ReadSelectionCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ReadClipboardCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (PauseCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ResumeCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (StopCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ReadTestTextCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (IncreaseSpeedCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (DecreaseSpeedCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private async Task SaveSettingsAsync()
    {
        await _saveLock.WaitAsync();
        try
        {
            await _settingsService.SaveAsync(_settings);
        }
        catch (Exception ex)
        {
            Log.Wrn($"Failed to save settings: {ex.Message}");
        }
        finally
        {
            _saveLock.Release();
        }
    }

    private void SaveSettingsFireAndForget()
    {
        _ = SaveSettingsAsync();
    }
}
