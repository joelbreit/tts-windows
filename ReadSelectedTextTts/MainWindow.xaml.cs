using System.ComponentModel;
using System.Windows;
using Windows.System;
using ReadSelectedTextTts.Hotkeys;
using ReadSelectedTextTts.Selection;
using ReadSelectedTextTts.Settings;
using ReadSelectedTextTts.Tray;
using ReadSelectedTextTts.Tts;
using ReadSelectedTextTts.ViewModels;

namespace ReadSelectedTextTts;

public partial class MainWindow : Window
{
    private static readonly (HotkeyModifiers Modifiers, uint Key)[] HotkeyFallbacks =
    [
        (HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x52),
        (HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x52),
        (HotkeyModifiers.Alt, 0x52)
    ];

    private readonly MainViewModel _viewModel;
    private readonly TrayIconManager _trayIconManager;
    private GlobalHotkey? _globalHotkey;
    private bool _isExiting;
    private bool _initialized;

    public MainWindow()
    {
        InitializeComponent();

        var settingsService = new SettingsService();
        var selectionReader = new SelectionReader();
        var ttsService = new TtsService(settingsService.AppDirectoryPath);

        _viewModel = new MainViewModel(selectionReader, ttsService, settingsService);
        DataContext = _viewModel;

        _trayIconManager = new TrayIconManager();
        _trayIconManager.ReadSelectionRequested += OnReadSelectionRequested;
        _trayIconManager.ToggleWindowRequested += OnToggleWindowRequested;
        _trayIconManager.ExitRequested += OnExitRequested;

        _viewModel.NotificationRequested += OnNotificationRequested;

        Loaded += OnLoaded;
        Closing += OnClosing;
        StateChanged += OnStateChanged;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;

        await _viewModel.InitializeAsync();
        RegisterHotkey();
        _trayIconManager.SetWindowVisible(true);
    }

    private void RegisterHotkey()
    {
        _globalHotkey?.Dispose();
        _globalHotkey = null;

        var configured = ((HotkeyModifiers)_viewModel.HotkeyModifiers, _viewModel.HotkeyKey);
        if (TryRegisterHotkeyCandidate(configured.Item1, configured.Item2))
        {
            _viewModel.SetActiveHotkey((uint)configured.Item1, configured.Item2, persist: false);
            return;
        }

        foreach (var fallback in HotkeyFallbacks)
        {
            if (fallback.Modifiers == configured.Item1 && fallback.Key == configured.Item2)
            {
                continue;
            }

            if (!TryRegisterHotkeyCandidate(fallback.Modifiers, fallback.Key))
            {
                continue;
            }

            _viewModel.SetActiveHotkey((uint)fallback.Modifiers, fallback.Key, persist: true);
            _trayIconManager.ShowNotification(
                "Read Selected Text TTS",
                $"Configured hotkey unavailable. Using {FormatHotkey((uint)fallback.Modifiers, fallback.Key)}.");
            return;
        }

        _trayIconManager.ShowNotification("Read Selected Text TTS", "Unable to register any global hotkey.");
    }

    private bool TryRegisterHotkeyCandidate(HotkeyModifiers modifiers, uint key)
    {
        var candidate = new GlobalHotkey(modifiers, key);
        if (!candidate.Register(this))
        {
            candidate.Dispose();
            return false;
        }

        candidate.Pressed += OnGlobalHotkeyPressed;
        _globalHotkey = candidate;
        return true;
    }

    private async void OnGlobalHotkeyPressed(object? sender, EventArgs e)
    {
        await Task.Delay(120);
        await _viewModel.ReadSelectionAsync();
    }

    private async void OnReadSelectionRequested(object? sender, EventArgs e)
    {
        await _viewModel.ReadSelectionAsync();
    }

    private void OnToggleWindowRequested(object? sender, EventArgs e)
    {
        ToggleWindowVisibility();
    }

    private void OnExitRequested(object? sender, EventArgs e)
    {
        ExitApplication();
    }

    private void ExitButton_OnClick(object sender, RoutedEventArgs e)
    {
        ExitApplication();
    }

    private void OnNotificationRequested(object? sender, string message)
    {
        _trayIconManager.ShowNotification("Read Selected Text TTS", message);
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            HideWindowToTray();
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_isExiting)
        {
            return;
        }

        e.Cancel = true;
        HideWindowToTray();
    }

    private void ToggleWindowVisibility()
    {
        if (IsVisible)
        {
            HideWindowToTray();
            return;
        }

        ShowWindow();
    }

    private void ShowWindow()
    {
        Show();
        ShowInTaskbar = true;
        WindowState = WindowState.Normal;
        Activate();
        _trayIconManager.SetWindowVisible(true);
    }

    private void HideWindowToTray()
    {
        Hide();
        ShowInTaskbar = false;
        _trayIconManager.SetWindowVisible(false);
    }

    private void ExitApplication()
    {
        _isExiting = true;

        _globalHotkey?.Dispose();
        _viewModel.Dispose();
        _trayIconManager.Dispose();

        System.Windows.Application.Current.Shutdown();
    }

    private static string FormatHotkey(uint modifiers, uint key)
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

        var keyName = ((VirtualKey)key).ToString();
        parts.Add(keyName.Length == 1 ? keyName.ToUpperInvariant() : keyName);

        return string.Join("+", parts);
    }
}
