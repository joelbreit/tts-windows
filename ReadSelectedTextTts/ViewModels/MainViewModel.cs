using System.Collections.ObjectModel;
using System.Windows.Input;
using ReadSelectedTextTts.Models;
using ReadSelectedTextTts.Selection;
using ReadSelectedTextTts.Settings;
using ReadSelectedTextTts.Telemetry;
using ReadSelectedTextTts.Tts;
using ReadSelectedTextTts.Tts.Providers;
using Windows.System;
using Log = Logger.Logger;

namespace ReadSelectedTextTts.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly SelectionReader _selectionReader;
    private readonly TtsService _ttsService;
    private readonly SettingsService _settingsService;
    private readonly TtsProviderRegistry _registry;
    private readonly TelemetryService _telemetry;
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private AppSettings _settings = new();
    private TtsProviderDescriptor? _selectedProvider;
    private VoiceOption? _selectedVoice;
    private bool _suppressProviderReload;
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
        SettingsService settingsService,
        TtsProviderRegistry registry,
        TelemetryService telemetry)
    {
        _selectionReader = selectionReader;
        _ttsService = ttsService;
        _settingsService = settingsService;
        _registry = registry;
        _telemetry = telemetry;
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
        OpenSettingsCommand = new RelayCommand(() => SettingsRequested?.Invoke(this, EventArgs.Empty));
    }

    public event EventHandler<string>? NotificationRequested;

    public event EventHandler? SettingsRequested;

    public ObservableCollection<TtsProviderDescriptor> Providers { get; } = [];

    public ObservableCollection<VoiceOption> Voices { get; } = [];

    public ICommand ReadSelectionCommand { get; }

    public ICommand ReadClipboardCommand { get; }

    public ICommand PauseCommand { get; }

    public ICommand ResumeCommand { get; }

    public ICommand StopCommand { get; }

    public ICommand ReadTestTextCommand { get; }

    public ICommand IncreaseSpeedCommand { get; }

    public ICommand DecreaseSpeedCommand { get; }

    public ICommand OpenSettingsCommand { get; }

    public TtsProviderDescriptor? SelectedProvider
    {
        get => _selectedProvider;
        set
        {
            if (!SetProperty(ref _selectedProvider, value))
            {
                return;
            }

            OnPropertyChanged(nameof(ProviderSummary));

            if (value is not null && !_suppressProviderReload)
            {
                _settings.SelectedProviderId = value.Id;
                SaveSettingsFireAndForget();
                _ = ReloadVoicesAsync();
            }
        }
    }

    /// <summary>One-line cost/quality summary for the active provider, shown under the dropdown.</summary>
    public string ProviderSummary => _selectedProvider is null
        ? string.Empty
        : $"Quality {_selectedProvider.Quality}  ·  {_selectedProvider.Latency}  ·  {_selectedProvider.Cost}";

    public VoiceOption? SelectedVoice
    {
        get => _selectedVoice;
        set
        {
            if (!SetProperty(ref _selectedVoice, value))
            {
                return;
            }

            if (value is not null)
            {
                _settings.GetProvider(value.ProviderId).SelectedVoiceId = value.Id;
                SaveSettingsFireAndForget();
            }

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

    public string SelectionHotkeyDisplay => FormatHotkey(_activeHotkeyModifiers, _activeHotkeyKey);

    public string ClipboardHotkeyDisplay => FormatHotkey(_activeClipboardHotkeyModifiers, _activeClipboardHotkeyKey);

    public async Task InitializeAsync()
    {
        Log.Inf("Initializing MainViewModel.");
        _settings = await _settingsService.LoadAsync();
        NormalizeSettings();
        _activeHotkeyModifiers = _settings.HotkeyModifiers;
        _activeHotkeyKey = _settings.HotkeyKey;
        Log.Dbg(
            $"Loaded settings. Provider='{_settings.SelectedProviderId}', Speed={_settings.Speed:F1}, Hotkey={FormatHotkey(_activeHotkeyModifiers, _activeHotkeyKey)}");

        Speed = _settings.Speed;

        Providers.Clear();
        foreach (var provider in _registry.Providers)
        {
            Providers.Add(provider.Descriptor);
        }

        _suppressProviderReload = true;
        SelectedProvider = Providers.FirstOrDefault(p => p.Id == _settings.SelectedProviderId)
                           ?? Providers.FirstOrDefault();
        _suppressProviderReload = false;

        await ReloadVoicesAsync();

        OnPropertyChanged(nameof(HotkeyDisplay));
        OnPropertyChanged(nameof(SelectionHotkeyDisplay));
        OnPropertyChanged(nameof(ClipboardHotkeyDisplay));
        await SaveSettingsAsync();
    }

    /// <summary>Reloads the voice list from the active provider and restores its saved voice.</summary>
    public async Task ReloadVoicesAsync()
    {
        Voices.Clear();
        SelectedVoice = null;

        var descriptor = _selectedProvider;
        if (descriptor is null)
        {
            return;
        }

        var provider = _registry.GetOrDefault(descriptor.Id);
        var config = _registry.ConfigFor(descriptor.Id, _settings);

        if (descriptor.RequiresApiKey && !provider.IsConfigured(config))
        {
            Log.Wrn($"Provider '{descriptor.Id}' is not configured.");
            NotificationRequested?.Invoke(this, $"{descriptor.DisplayName} needs setup. Open Settings to add an API key.");
            RaiseCommandCanExecuteChanged();
            return;
        }

        IReadOnlyList<VoiceOption> voices;
        try
        {
            voices = await provider.GetVoicesAsync(config);
        }
        catch (Exception ex)
        {
            Log.Err($"Failed to load voices for '{descriptor.Id}': {ex}");
            NotificationRequested?.Invoke(this, $"Failed to load {descriptor.DisplayName} voices: {ex.Message}");
            RaiseCommandCanExecuteChanged();
            return;
        }

        foreach (var voice in voices)
        {
            Voices.Add(voice);
        }

        if (Voices.Count == 0)
        {
            Log.Wrn($"Provider '{descriptor.Id}' returned no voices.");
            NotificationRequested?.Invoke(this, $"{descriptor.DisplayName} has no available voices.");
            return;
        }

        SelectedVoice = ResolveSelectedVoice(descriptor.Id);
        Log.Inf($"Active provider '{descriptor.Id}', voice '{SelectedVoice?.DisplayName ?? "<none>"}'.");
    }

    /// <summary>Creates a settings view model sharing this VM's live settings and save path.</summary>
    public SettingsViewModel CreateSettingsViewModel() =>
        new(_settings, _registry, _telemetry, SaveSettingsAsync);

    /// <summary>Re-syncs provider/voice state after the Settings window closes.</summary>
    public async Task RefreshAfterSettingsAsync()
    {
        _suppressProviderReload = true;
        SelectedProvider = Providers.FirstOrDefault(p => p.Id == _settings.SelectedProviderId)
                           ?? Providers.FirstOrDefault();
        _suppressProviderReload = false;

        await ReloadVoicesAsync();
    }

    public async Task ReadSelectionAsync()
    {
        if (!EnsureVoice())
        {
            return;
        }

        try
        {
            Log.Dbg($"ReadSelection requested. Active voice='{SelectedVoice!.DisplayName}', speed={Speed:F1}x");
            var selectedText = await _selectionReader.ReadSelectionAsync();
            if (string.IsNullOrWhiteSpace(selectedText))
            {
                Log.Wrn("ReadSelection found no text.");
                NotificationRequested?.Invoke(this, "No selected text found. Select text in another app or use Read Test Text.");
                return;
            }

            Log.Inf($"ReadSelection speaking text. Length={selectedText.Length}");
            await SpeakAsync(selectedText, "selection");
        }
        catch (Exception ex)
        {
            Log.Err($"ReadSelection failed: {ex}");
            NotificationRequested?.Invoke(this, $"Read failed: {ex.Message}");
        }
    }

    public async Task ReadClipboardAsync()
    {
        if (!EnsureVoice())
        {
            return;
        }

        try
        {
            Log.Dbg($"ReadClipboard requested. Active voice='{SelectedVoice!.DisplayName}', speed={Speed:F1}x");
            var clipboardText = _selectionReader.ReadClipboardText();
            if (string.IsNullOrWhiteSpace(clipboardText))
            {
                Log.Wrn("ReadClipboard found no text.");
                NotificationRequested?.Invoke(this, "Clipboard does not contain text.");
                return;
            }

            Log.Inf($"ReadClipboard speaking text. Length={clipboardText.Length}");
            await SpeakAsync(clipboardText, "clipboard");
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
        OnPropertyChanged(nameof(SelectionHotkeyDisplay));

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
        OnPropertyChanged(nameof(ClipboardHotkeyDisplay));
    }

    private async Task SpeakAsync(string text, string source)
    {
        var voice = SelectedVoice!;
        var config = _registry.ConfigFor(voice.ProviderId, _settings);
        await _ttsService.SpeakAsync(text, voice, config, Speed, source);
        IsPlaying = _ttsService.IsPlaying;
        IsPaused = _ttsService.IsPaused;
    }

    private bool EnsureVoice()
    {
        if (SelectedVoice is not null)
        {
            return true;
        }

        NotificationRequested?.Invoke(this, "No voice available. Open Settings to configure a provider.");
        return false;
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
        if (!EnsureVoice())
        {
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
            await SpeakAsync(ManualText, "test");
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

        if (string.IsNullOrWhiteSpace(_settings.SelectedProviderId))
        {
            _settings.SelectedProviderId = WindowsTtsProvider.ProviderId;
        }

        // Migrate the legacy top-level VoiceId into the Windows provider bucket.
        if (!string.IsNullOrWhiteSpace(_settings.VoiceId))
        {
            var windows = _settings.GetProvider(WindowsTtsProvider.ProviderId);
            windows.SelectedVoiceId ??= _settings.VoiceId;
            _settings.VoiceId = null;
            Log.Inf("Migrated legacy VoiceId into the Windows provider settings.");
        }
    }

    private VoiceOption? ResolveSelectedVoice(string providerId)
    {
        var savedVoiceId = _settings.GetProvider(providerId).SelectedVoiceId;
        if (!string.IsNullOrWhiteSpace(savedVoiceId))
        {
            var match = Voices.FirstOrDefault(voice => voice.Id == savedVoiceId);
            if (match is not null)
            {
                return match;
            }
        }

        return Voices.FirstOrDefault();
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
