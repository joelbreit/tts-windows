using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Windows.System;
using ReadSelectedTextTts.Hotkeys;
using ReadSelectedTextTts.Selection;
using ReadSelectedTextTts.Settings;
using ReadSelectedTextTts.Tray;
using ReadSelectedTextTts.Tts;
using ReadSelectedTextTts.ViewModels;
using Log = Logger.Logger;

namespace ReadSelectedTextTts;

public partial class MainWindow : Window
{
    private static readonly (HotkeyModifiers Modifiers, uint Key)[] SelectionHotkeyFallbacks =
    [
        (HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x52),
        (HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x52),
        (HotkeyModifiers.Alt, 0x52)
    ];
    private static readonly (HotkeyModifiers Modifiers, uint Key)[] ClipboardHotkeyFallbacks =
    [
        (HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x43),
        (HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x43),
        (HotkeyModifiers.Alt, 0x43)
    ];
    private static readonly (HotkeyModifiers Modifiers, uint Key) ClipboardDefaultHotkey =
        (HotkeyModifiers.Win | HotkeyModifiers.Alt, 0x43);

    private const int DwmwaUseImmersiveDarkMode = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private readonly MainViewModel _viewModel;
    private readonly TrayIconManager _trayIconManager;
    private GlobalHotkey? _selectionHotkey;
    private GlobalHotkey? _clipboardHotkey;
    private (HotkeyModifiers Modifiers, uint Key)? _activeSelectionHotkey;
    private bool _isExiting;
    private bool _initialized;

    public MainWindow()
    {
        InitializeComponent();
        Log.Inf("MainWindow constructor starting.");

        var settingsService = new SettingsService();
        var selectionReader = new SelectionReader();
        var ttsService = new TtsService(settingsService.AppDirectoryPath);

        _viewModel = new MainViewModel(selectionReader, ttsService, settingsService);
        DataContext = _viewModel;

        _trayIconManager = new TrayIconManager();
        _trayIconManager.ReadSelectionRequested += OnReadSelectionRequested;
        _trayIconManager.ReadClipboardRequested += OnReadClipboardRequested;
        _trayIconManager.ToggleWindowRequested += OnToggleWindowRequested;
        _trayIconManager.ExitRequested += OnExitRequested;

        _viewModel.NotificationRequested += OnNotificationRequested;

        Loaded += OnLoaded;
        Closing += OnClosing;
        StateChanged += OnStateChanged;
        Log.Inf("MainWindow initialized.");
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        ApplyDarkTitleBar();
        Log.Inf("MainWindow loaded. Initializing view model and hotkeys.");

        await _viewModel.InitializeAsync();
        RegisterHotkeys();
        _trayIconManager.SetWindowVisible(true);
    }

    private void RegisterHotkeys()
    {
        Log.Dbg("Registering global hotkeys.");
        RegisterSelectionHotkey();
        RegisterClipboardHotkey();
    }

    private void RegisterSelectionHotkey()
    {
        _selectionHotkey?.Dispose();
        _selectionHotkey = null;
        _activeSelectionHotkey = null;

        var configured = ((HotkeyModifiers)_viewModel.HotkeyModifiers, _viewModel.HotkeyKey);
        if (TryRegisterHotkeyCandidate(configured.Item1, configured.Item2, OnSelectionHotkeyPressed, out var configuredHotkey))
        {
            Log.Inf($"Registered configured hotkey: {FormatHotkey((uint)configured.Item1, configured.Item2)}");
            _viewModel.SetActiveHotkey((uint)configured.Item1, configured.Item2, persist: false);
            _selectionHotkey = configuredHotkey;
            _activeSelectionHotkey = configured;
            return;
        }

        foreach (var fallback in SelectionHotkeyFallbacks)
        {
            if (fallback.Modifiers == configured.Item1 && fallback.Key == configured.Item2)
            {
                continue;
            }

            if (!TryRegisterHotkeyCandidate(fallback.Modifiers, fallback.Key, OnSelectionHotkeyPressed, out var fallbackHotkey))
            {
                Log.Dbg($"Hotkey candidate failed: {FormatHotkey((uint)fallback.Modifiers, fallback.Key)}");
                continue;
            }

            Log.Wrn($"Configured hotkey unavailable. Falling back to {FormatHotkey((uint)fallback.Modifiers, fallback.Key)}");
            _viewModel.SetActiveHotkey((uint)fallback.Modifiers, fallback.Key, persist: true);
            _selectionHotkey = fallbackHotkey;
            _activeSelectionHotkey = fallback;
            _trayIconManager.ShowNotification(
                "Read Selected Text TTS",
                $"Configured hotkey unavailable. Using {FormatHotkey((uint)fallback.Modifiers, fallback.Key)}.");
            return;
        }

        Log.Err("Unable to register any global hotkey.");
        _trayIconManager.ShowNotification("Read Selected Text TTS", "Unable to register any global hotkey.");
    }

    private void RegisterClipboardHotkey()
    {
        _clipboardHotkey?.Dispose();
        _clipboardHotkey = null;

        if (!IsSelectionHotkey(ClipboardDefaultHotkey.Modifiers, ClipboardDefaultHotkey.Key) &&
            TryRegisterHotkeyCandidate(
                ClipboardDefaultHotkey.Modifiers,
                ClipboardDefaultHotkey.Key,
                OnClipboardHotkeyPressed,
                out var configuredHotkey))
        {
            Log.Inf(
                $"Registered clipboard hotkey: {FormatHotkey((uint)ClipboardDefaultHotkey.Modifiers, ClipboardDefaultHotkey.Key)}");
            _clipboardHotkey = configuredHotkey;
            _viewModel.SetActiveClipboardHotkey((uint)ClipboardDefaultHotkey.Modifiers, ClipboardDefaultHotkey.Key);
            return;
        }

        foreach (var fallback in ClipboardHotkeyFallbacks)
        {
            if (fallback.Modifiers == ClipboardDefaultHotkey.Modifiers && fallback.Key == ClipboardDefaultHotkey.Key)
            {
                continue;
            }

            if (IsSelectionHotkey(fallback.Modifiers, fallback.Key))
            {
                Log.Dbg($"Skipping clipboard hotkey candidate due to selection conflict: {FormatHotkey((uint)fallback.Modifiers, fallback.Key)}");
                continue;
            }

            if (!TryRegisterHotkeyCandidate(fallback.Modifiers, fallback.Key, OnClipboardHotkeyPressed, out var fallbackHotkey))
            {
                Log.Dbg($"Clipboard hotkey candidate failed: {FormatHotkey((uint)fallback.Modifiers, fallback.Key)}");
                continue;
            }

            Log.Wrn(
                $"Configured clipboard hotkey unavailable. Falling back to {FormatHotkey((uint)fallback.Modifiers, fallback.Key)}");
            _clipboardHotkey = fallbackHotkey;
            _viewModel.SetActiveClipboardHotkey((uint)fallback.Modifiers, fallback.Key);
            _trayIconManager.ShowNotification(
                "Read Selected Text TTS",
                $"Clipboard hotkey unavailable. Using {FormatHotkey((uint)fallback.Modifiers, fallback.Key)}.");
            return;
        }

        Log.Wrn("Unable to register clipboard hotkey.");
        _trayIconManager.ShowNotification("Read Selected Text TTS", "Unable to register clipboard hotkey.");
    }

    private bool TryRegisterHotkeyCandidate(
        HotkeyModifiers modifiers,
        uint key,
        EventHandler handler,
        out GlobalHotkey? registeredHotkey)
    {
        var candidate = new GlobalHotkey(modifiers, key);
        if (!candidate.Register(this))
        {
            candidate.Dispose();
            registeredHotkey = null;
            return false;
        }

        candidate.Pressed += handler;
        registeredHotkey = candidate;
        return true;
    }

    private bool IsSelectionHotkey(HotkeyModifiers modifiers, uint key)
    {
        return _activeSelectionHotkey is { } active && active.Modifiers == modifiers && active.Key == key;
    }

    private async void OnSelectionHotkeyPressed(object? sender, EventArgs e)
    {
        Log.Dbg("Selection hotkey pressed.");
        await Task.Delay(120);
        await _viewModel.ReadSelectionAsync();
    }

    private async void OnClipboardHotkeyPressed(object? sender, EventArgs e)
    {
        Log.Dbg("Clipboard hotkey pressed.");
        await _viewModel.ReadClipboardAsync();
    }

    private async void OnReadSelectionRequested(object? sender, EventArgs e)
    {
        Log.Dbg("Tray menu requested Read Selection.");
        await _viewModel.ReadSelectionAsync();
    }

    private async void OnReadClipboardRequested(object? sender, EventArgs e)
    {
        Log.Dbg("Tray menu requested Read Clipboard.");
        await _viewModel.ReadClipboardAsync();
    }

    private void OnToggleWindowRequested(object? sender, EventArgs e)
    {
        ToggleWindowVisibility();
    }

    private void OnExitRequested(object? sender, EventArgs e)
    {
        Log.Inf("Tray menu requested Exit.");
        ExitApplication();
    }

    private void ExitButton_OnClick(object sender, RoutedEventArgs e)
    {
        Log.Inf("Exit button clicked.");
        ExitApplication();
    }

    private void OnNotificationRequested(object? sender, string message)
    {
        Log.Inf($"User notification: {message}");
        _trayIconManager.ShowNotification("Read Selected Text TTS", message);
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            Log.Dbg("MainWindow minimized; hiding to tray.");
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
        Log.Dbg("MainWindow close intercepted; hiding to tray.");
        HideWindowToTray();
    }

    private void ToggleWindowVisibility()
    {
        if (IsVisible)
        {
            Log.Dbg("Toggle window requested: currently visible, hiding.");
            HideWindowToTray();
            return;
        }

        Log.Dbg("Toggle window requested: currently hidden, showing.");
        ShowWindow();
    }

    private void ShowWindow()
    {
        Log.Dbg("Showing main window.");
        Show();
        ShowInTaskbar = true;
        WindowState = WindowState.Normal;
        Activate();
        _trayIconManager.SetWindowVisible(true);
    }

    private void HideWindowToTray()
    {
        Log.Dbg("Hiding main window to tray.");
        Hide();
        ShowInTaskbar = false;
        _trayIconManager.SetWindowVisible(false);
    }

    private void ExitApplication()
    {
        Log.Inf("ExitApplication invoked.");
        _isExiting = true;

        _selectionHotkey?.Dispose();
        _clipboardHotkey?.Dispose();
        _viewModel.Dispose();
        _trayIconManager.Dispose();

        System.Windows.Application.Current.Shutdown();
    }

    private void ApplyDarkTitleBar()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var darkMode = 1;
        DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref darkMode, sizeof(int));
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
