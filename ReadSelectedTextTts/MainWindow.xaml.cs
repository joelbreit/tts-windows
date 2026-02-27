using System.ComponentModel;
using System.Windows;
using ReadSelectedTextTts.Hotkeys;
using ReadSelectedTextTts.Selection;
using ReadSelectedTextTts.Settings;
using ReadSelectedTextTts.Tray;
using ReadSelectedTextTts.Tts;
using ReadSelectedTextTts.ViewModels;

namespace ReadSelectedTextTts;

public partial class MainWindow : Window
{
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
        HideWindowToTray();
    }

    private void RegisterHotkey()
    {
        _globalHotkey?.Dispose();

        _globalHotkey = new GlobalHotkey((HotkeyModifiers)_viewModel.HotkeyModifiers, _viewModel.HotkeyKey);
        if (!_globalHotkey.Register(this))
        {
            _trayIconManager.ShowNotification("Read Selected Text TTS", "Unable to register global hotkey.");
            return;
        }

        _globalHotkey.Pressed += OnGlobalHotkeyPressed;
    }

    private async void OnGlobalHotkeyPressed(object? sender, EventArgs e)
    {
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
}
