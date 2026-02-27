using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Log = Logger.Logger;

namespace ReadSelectedTextTts.Hotkeys;

[Flags]
public enum HotkeyModifiers : uint
{
    None = 0x0000,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Win = 0x0008
}

public sealed class GlobalHotkey : IDisposable
{
    private const int WmHotkey = 0x0312;
    private static int _nextId = 0x2000;

    private readonly int _id;
    private readonly uint _modifiers;
    private readonly uint _key;
    private HwndSource? _source;
    private IntPtr _windowHandle;
    private bool _disposed;

    public GlobalHotkey(HotkeyModifiers modifiers, uint key)
    {
        _id = Interlocked.Increment(ref _nextId);
        _modifiers = (uint)modifiers;
        _key = key;
    }

    public event EventHandler? Pressed;

    public bool Register(Window window)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var helper = new WindowInteropHelper(window);
        _windowHandle = helper.EnsureHandle();
        _source = HwndSource.FromHwnd(_windowHandle);
        _source?.AddHook(WndProc);

        var registered = RegisterHotKey(_windowHandle, _id, _modifiers, _key);
        if (!registered)
        {
            var error = Marshal.GetLastWin32Error();
            Log.Wrn(
                $"RegisterHotKey failed. Id={_id}, Modifiers=0x{_modifiers:X}, Key=0x{_key:X}, LastError={error}");
        }
        else
        {
            Log.Dbg($"RegisterHotKey succeeded. Id={_id}, Modifiers=0x{_modifiers:X}, Key=0x{_key:X}");
        }

        return registered;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Unregister();
        GC.SuppressFinalize(this);
    }

    private void Unregister()
    {
        if (_windowHandle != IntPtr.Zero)
        {
            _ = UnregisterHotKey(_windowHandle, _id);
        }

        _source?.RemoveHook(WndProc);
        _source = null;
        _windowHandle = IntPtr.Zero;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == _id)
        {
            Log.Trc($"Global hotkey message received. Id={_id}");
            Pressed?.Invoke(this, EventArgs.Empty);
            handled = true;
        }

        return IntPtr.Zero;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
