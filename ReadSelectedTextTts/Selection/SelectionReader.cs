using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;

namespace ReadSelectedTextTts.Selection;

public sealed class SelectionReader
{
    private const uint InputKeyboard = 1;
    private const uint KeyEventfKeyUp = 0x0002;
    private const ushort VkControl = 0x11;
    private const ushort VkC = 0x43;

    public async Task<string> ReadSelectionAsync(CancellationToken cancellationToken = default)
    {
        var fromUiAutomation = TryReadUsingUiAutomation();
        if (!string.IsNullOrWhiteSpace(fromUiAutomation))
        {
            return fromUiAutomation;
        }

        return await TryReadUsingClipboardFallbackAsync(cancellationToken);
    }

    private static string TryReadUsingUiAutomation()
    {
        try
        {
            var foregroundWindow = GetForegroundWindow();
            if (foregroundWindow == IntPtr.Zero)
            {
                return string.Empty;
            }

            var focusedElement = AutomationElement.FocusedElement;
            if (focusedElement is null)
            {
                var root = AutomationElement.FromHandle(foregroundWindow);
                focusedElement = root?.FindFirst(
                    TreeScope.Subtree,
                    new PropertyCondition(AutomationElement.HasKeyboardFocusProperty, true));
            }

            if (focusedElement is null)
            {
                return string.Empty;
            }

            if (focusedElement.TryGetCurrentPattern(TextPattern.Pattern, out var textPatternObject) &&
                textPatternObject is TextPattern textPattern)
            {
                return ReadTextFromPattern(textPattern);
            }

            var textPattern2 = TryReadUsingTextPattern2(focusedElement);
            if (!string.IsNullOrWhiteSpace(textPattern2))
            {
                return textPattern2;
            }
        }
        catch (ElementNotAvailableException)
        {
            return string.Empty;
        }
        catch (InvalidOperationException)
        {
            return string.Empty;
        }

        return string.Empty;
    }

    private static string ReadTextFromPattern(TextPattern textPattern)
    {
        var ranges = textPattern.GetSelection();
        if (ranges is null || ranges.Length == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var range in ranges)
        {
            var text = range.GetText(-1);
            if (!string.IsNullOrWhiteSpace(text))
            {
                builder.Append(text);
            }
        }

        return builder.ToString().Trim();
    }

    private static string TryReadUsingTextPattern2(AutomationElement element)
    {
        var textPattern2Type = Type.GetType("System.Windows.Automation.TextPattern2, UIAutomationClient");
        if (textPattern2Type is null)
        {
            return string.Empty;
        }

        var patternField = textPattern2Type.GetField("Pattern");
        if (patternField?.GetValue(null) is not AutomationPattern pattern)
        {
            return string.Empty;
        }

        if (!element.TryGetCurrentPattern(pattern, out var patternObject) ||
            patternObject is not TextPattern textPattern)
        {
            return string.Empty;
        }

        return ReadTextFromPattern(textPattern);
    }

    private static async Task<string> TryReadUsingClipboardFallbackAsync(CancellationToken cancellationToken)
    {
        if (!TryGetClipboardDataObject(out var clipboardData))
        {
            return string.Empty;
        }

        var hadClipboardData = clipboardData is not null;
        var originalSequence = GetClipboardSequenceNumber();

        try
        {
            SendCopyShortcut();

            var stopAt = DateTime.UtcNow.AddMilliseconds(500);
            while (DateTime.UtcNow < stopAt)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (GetClipboardSequenceNumber() != originalSequence &&
                    TryGetClipboardText(out var capturedText) &&
                    !string.IsNullOrWhiteSpace(capturedText))
                {
                    return capturedText.Trim();
                }

                await Task.Delay(50, cancellationToken);
            }
        }
        finally
        {
            if (hadClipboardData && clipboardData is not null)
            {
                TrySetClipboardDataObject(clipboardData);
            }
            else
            {
                TryClearClipboard();
            }
        }

        return string.Empty;
    }

    private static bool TryGetClipboardDataObject(out System.Windows.IDataObject? dataObject)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                dataObject = System.Windows.Clipboard.GetDataObject();
                return true;
            }
            catch (COMException)
            {
                Thread.Sleep(20);
            }
        }

        dataObject = null;
        return false;
    }

    private static bool TryGetClipboardText(out string text)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (System.Windows.Clipboard.ContainsText())
                {
                    text = System.Windows.Clipboard.GetText();
                    return true;
                }

                break;
            }
            catch (COMException)
            {
                Thread.Sleep(20);
            }
        }

        text = string.Empty;
        return false;
    }

    private static void TrySetClipboardDataObject(System.Windows.IDataObject dataObject)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                System.Windows.Clipboard.SetDataObject(dataObject, true);
                return;
            }
            catch (COMException)
            {
                Thread.Sleep(20);
            }
        }
    }

    private static void TryClearClipboard()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                System.Windows.Clipboard.Clear();
                return;
            }
            catch (COMException)
            {
                Thread.Sleep(20);
            }
        }
    }

    private static void SendCopyShortcut()
    {
        var inputs = new[]
        {
            CreateKeyboardInput(VkControl, keyUp: false),
            CreateKeyboardInput(VkC, keyUp: false),
            CreateKeyboardInput(VkC, keyUp: true),
            CreateKeyboardInput(VkControl, keyUp: true)
        };

        _ = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static INPUT CreateKeyboardInput(ushort virtualKey, bool keyUp)
    {
        return new INPUT
        {
            type = InputKeyboard,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = virtualKey,
                    dwFlags = keyUp ? KeyEventfKeyUp : 0
                }
            }
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public nuint dwExtraInfo;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();
}
