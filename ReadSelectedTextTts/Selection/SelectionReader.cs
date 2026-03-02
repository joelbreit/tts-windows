using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using Log = Logger.Logger;

namespace ReadSelectedTextTts.Selection;

public sealed class SelectionReader
{
    private const uint InputKeyboard = 1;
    private const uint KeyEventfKeyUp = 0x0002;
    private const ushort VkControl = 0x11;
    private const ushort VkC = 0x43;
    private static int _attemptCounter;

    public async Task<string> ReadSelectionAsync(CancellationToken cancellationToken = default)
    {
        var attemptId = Interlocked.Increment(ref _attemptCounter);
        Log.Inf($"Selection attempt #{attemptId} started.");

        var fromUiAutomation = TryReadUsingUiAutomation(attemptId);
        if (!string.IsNullOrWhiteSpace(fromUiAutomation))
        {
            Log.Inf(
                $"Selection attempt #{attemptId}: UI Automation success. Length={fromUiAutomation.Length}, Preview='{Preview(fromUiAutomation)}'");
            return fromUiAutomation;
        }

        Log.Dbg($"Selection attempt #{attemptId}: UI Automation returned no text. Trying clipboard fallback.");
        var fromClipboard = await TryReadUsingClipboardFallbackAsync(attemptId, cancellationToken);
        if (!string.IsNullOrWhiteSpace(fromClipboard))
        {
            Log.Inf(
                $"Selection attempt #{attemptId}: Clipboard fallback success. Length={fromClipboard.Length}, Preview='{Preview(fromClipboard)}'");
            return fromClipboard;
        }

        Log.Wrn($"Selection attempt #{attemptId}: No selected text found from UIA or clipboard fallback.");
        return string.Empty;
    }

    private static string TryReadUsingUiAutomation(int attemptId)
    {
        try
        {
            var foregroundWindow = GetForegroundWindow();
            if (foregroundWindow == IntPtr.Zero)
            {
                Log.Wrn($"Selection attempt #{attemptId}: GetForegroundWindow returned zero.");
                return string.Empty;
            }

            Log.Dbg($"Selection attempt #{attemptId}: Foreground window -> {DescribeForegroundWindow(foregroundWindow)}");

            var focusedElement = AutomationElement.FocusedElement;
            if (focusedElement is null)
            {
                Log.Wrn($"Selection attempt #{attemptId}: AutomationElement.FocusedElement is null. Searching subtree.");
                var root = AutomationElement.FromHandle(foregroundWindow);
                focusedElement = root?.FindFirst(
                    TreeScope.Subtree,
                    new PropertyCondition(AutomationElement.HasKeyboardFocusProperty, true));
            }

            if (focusedElement is null)
            {
                Log.Wrn($"Selection attempt #{attemptId}: No focused automation element found.");
                return string.Empty;
            }

            Log.Dbg($"Selection attempt #{attemptId}: Focused element -> {DescribeAutomationElement(focusedElement)}");

            if (focusedElement.TryGetCurrentPattern(TextPattern.Pattern, out var textPatternObject))
            {
                Log.Trc(
                    $"Selection attempt #{attemptId}: TextPattern supported. Pattern type={textPatternObject?.GetType().FullName ?? "null"}");
                if (textPatternObject is TextPattern textPattern)
                {
                    var text = ReadTextFromPattern(textPattern, attemptId, "TextPattern");
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        return text;
                    }
                }
            }
            else
            {
                Log.Trc($"Selection attempt #{attemptId}: TextPattern not supported.");
            }

            var textPattern2 = TryReadUsingTextPattern2(focusedElement, attemptId);
            if (!string.IsNullOrWhiteSpace(textPattern2))
            {
                return textPattern2;
            }
        }
        catch (ElementNotAvailableException ex)
        {
            Log.Wrn($"Selection attempt #{attemptId}: Focused element became unavailable: {ex.Message}");
            return string.Empty;
        }
        catch (InvalidOperationException ex)
        {
            Log.Wrn($"Selection attempt #{attemptId}: UI Automation invalid operation: {ex.Message}");
            return string.Empty;
        }
        catch (Exception ex)
        {
            Log.Err($"Selection attempt #{attemptId}: Unexpected UI Automation exception: {ex}");
            return string.Empty;
        }

        Log.Dbg($"Selection attempt #{attemptId}: UI Automation had no selected text.");
        return string.Empty;
    }

    private static string ReadTextFromPattern(TextPattern textPattern, int attemptId, string source)
    {
        Array? ranges;
        try
        {
            ranges = textPattern.GetSelection();
        }
        catch (Exception ex)
        {
            Log.Wrn($"Selection attempt #{attemptId}: {source}.GetSelection failed: {ex.Message}");
            return string.Empty;
        }

        if (ranges is null || ranges.Length == 0)
        {
            Log.Trc($"Selection attempt #{attemptId}: {source} returned 0 selected ranges.");
            return string.Empty;
        }

        Log.Trc($"Selection attempt #{attemptId}: {source} returned {ranges.Length} selected range(s).");

        var builder = new StringBuilder();
        for (var i = 0; i < ranges.Length; i++)
        {
            string? text;
            try
            {
                var range = ranges.GetValue(i);
                if (range is null)
                {
                    Log.Wrn($"Selection attempt #{attemptId}: {source} range {i} was null.");
                    continue;
                }

                var getTextMethod = range.GetType().GetMethod("GetText", [typeof(int)]);
                if (getTextMethod is null)
                {
                    Log.Wrn(
                        $"Selection attempt #{attemptId}: {source} range {i} type '{range.GetType().FullName}' has no GetText(int).");
                    continue;
                }

                text = getTextMethod.Invoke(range, [-1]) as string;
            }
            catch (Exception ex)
            {
                Log.Wrn($"Selection attempt #{attemptId}: {source} range {i} GetText failed: {ex.Message}");
                continue;
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                Log.Trc($"Selection attempt #{attemptId}: {source} range {i} is empty/whitespace.");
                continue;
            }

            Log.Trc(
                $"Selection attempt #{attemptId}: {source} range {i} length={text.Length}, preview='{Preview(text)}'");
            builder.Append(text);
        }

        var combined = builder.ToString().Trim();
        Log.Dbg(
            $"Selection attempt #{attemptId}: {source} combined length={combined.Length}, preview='{Preview(combined)}'");
        return combined;
    }

    private static string TryReadUsingTextPattern2(AutomationElement element, int attemptId)
    {
        var textPattern2Type = Type.GetType("System.Windows.Automation.TextPattern2, UIAutomationClient");
        if (textPattern2Type is null)
        {
            Log.Trc($"Selection attempt #{attemptId}: TextPattern2 type not available.");
            return string.Empty;
        }

        var patternField = textPattern2Type.GetField("Pattern");
        if (patternField?.GetValue(null) is not AutomationPattern pattern)
        {
            Log.Trc($"Selection attempt #{attemptId}: TextPattern2 pattern field missing.");
            return string.Empty;
        }

        if (!element.TryGetCurrentPattern(pattern, out var patternObject))
        {
            Log.Trc($"Selection attempt #{attemptId}: TextPattern2 not supported by focused element.");
            return string.Empty;
        }

        Log.Trc(
            $"Selection attempt #{attemptId}: TextPattern2 supported. Pattern type={patternObject?.GetType().FullName ?? "null"}");

        if (patternObject is not TextPattern textPattern)
        {
            Log.Wrn(
                $"Selection attempt #{attemptId}: TextPattern2 object is not TextPattern. Type={patternObject?.GetType().FullName ?? "null"}");
            return string.Empty;
        }

        return ReadTextFromPattern(textPattern, attemptId, "TextPattern2");
    }

    private static async Task<string> TryReadUsingClipboardFallbackAsync(int attemptId, CancellationToken cancellationToken)
    {
        if (!TryGetClipboardDataObject(out var clipboardData, attemptId))
        {
            Log.Wrn($"Selection attempt #{attemptId}: Could not snapshot clipboard.");
            return string.Empty;
        }

        var hadClipboardData = clipboardData is not null;
        var originalSequence = GetClipboardSequenceNumber();
        Log.Dbg(
            $"Selection attempt #{attemptId}: Clipboard fallback start. OriginalSequence={originalSequence}, HadExistingData={hadClipboardData}");

        try
        {
            SendCopyShortcut(attemptId);

            var stopAt = DateTime.UtcNow.AddMilliseconds(500);
            var poll = 0;
            while (DateTime.UtcNow < stopAt)
            {
                poll++;
                cancellationToken.ThrowIfCancellationRequested();

                var currentSequence = GetClipboardSequenceNumber();
                if (currentSequence != originalSequence)
                {
                    Log.Trc(
                        $"Selection attempt #{attemptId}: Clipboard sequence changed at poll {poll}: {originalSequence} -> {currentSequence}");
                    if (TryGetClipboardText(out var capturedText, attemptId))
                    {
                        if (!string.IsNullOrWhiteSpace(capturedText))
                        {
                            return capturedText.Trim();
                        }

                        Log.Wrn(
                            $"Selection attempt #{attemptId}: Clipboard changed but text payload is empty at poll {poll}.");
                    }
                    else
                    {
                        Log.Wrn($"Selection attempt #{attemptId}: Clipboard changed but text read failed at poll {poll}.");
                    }
                }
                else
                {
                    Log.Trc($"Selection attempt #{attemptId}: Poll {poll}, clipboard sequence unchanged ({currentSequence}).");
                }

                await Task.Delay(50, cancellationToken);
            }

            Log.Wrn($"Selection attempt #{attemptId}: Clipboard fallback timed out after 500ms.");
        }
        finally
        {
            if (hadClipboardData && clipboardData is not null)
            {
                var restored = TrySetClipboardDataObject(clipboardData, attemptId);
                Log.Dbg($"Selection attempt #{attemptId}: Clipboard restore {(restored ? "succeeded" : "failed")}.");
            }
            else
            {
                var cleared = TryClearClipboard(attemptId);
                Log.Dbg($"Selection attempt #{attemptId}: Clipboard clear {(cleared ? "succeeded" : "failed")}.");
            }
        }

        return string.Empty;
    }

    private static bool TryGetClipboardDataObject(out System.Windows.IDataObject? dataObject, int attemptId)
    {
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                dataObject = System.Windows.Clipboard.GetDataObject();
                Log.Trc(
                    $"Selection attempt #{attemptId}: Clipboard snapshot succeeded on attempt {attempt}. HasDataObject={dataObject is not null}");
                return true;
            }
            catch (COMException ex)
            {
                Log.Trc(
                    $"Selection attempt #{attemptId}: Clipboard snapshot COMException on attempt {attempt}. HResult=0x{ex.HResult:X8}");
                Thread.Sleep(20);
            }
        }

        dataObject = null;
        return false;
    }

    private static bool TryGetClipboardText(out string text, int attemptId)
    {
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                var containsText = System.Windows.Clipboard.ContainsText();
                if (!containsText)
                {
                    Log.Trc($"Selection attempt #{attemptId}: Clipboard contains no text (attempt {attempt}).");
                    break;
                }

                text = System.Windows.Clipboard.GetText();
                Log.Trc(
                    $"Selection attempt #{attemptId}: Clipboard text read on attempt {attempt}. Length={text.Length}, Preview='{Preview(text)}'");
                return true;
            }
            catch (COMException ex)
            {
                Log.Trc(
                    $"Selection attempt #{attemptId}: Clipboard text read COMException on attempt {attempt}. HResult=0x{ex.HResult:X8}");
                Thread.Sleep(20);
            }
        }

        text = string.Empty;
        return false;
    }

    private static bool TrySetClipboardDataObject(System.Windows.IDataObject dataObject, int attemptId)
    {
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                System.Windows.Clipboard.SetDataObject(dataObject, true);
                Log.Trc($"Selection attempt #{attemptId}: Clipboard restore succeeded on attempt {attempt}.");
                return true;
            }
            catch (COMException ex)
            {
                Log.Trc(
                    $"Selection attempt #{attemptId}: Clipboard restore COMException on attempt {attempt}. HResult=0x{ex.HResult:X8}");
                Thread.Sleep(20);
            }
        }

        return false;
    }

    private static bool TryClearClipboard(int attemptId)
    {
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                System.Windows.Clipboard.Clear();
                Log.Trc($"Selection attempt #{attemptId}: Clipboard clear succeeded on attempt {attempt}.");
                return true;
            }
            catch (COMException ex)
            {
                Log.Trc(
                    $"Selection attempt #{attemptId}: Clipboard clear COMException on attempt {attempt}. HResult=0x{ex.HResult:X8}");
                Thread.Sleep(20);
            }
        }

        return false;
    }

    private static void SendCopyShortcut(int attemptId)
    {
        var inputs = new[]
        {
            CreateKeyboardInput(VkControl, keyUp: false),
            CreateKeyboardInput(VkC, keyUp: false),
            CreateKeyboardInput(VkC, keyUp: true),
            CreateKeyboardInput(VkControl, keyUp: true)
        };

        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length)
        {
            var lastError = Marshal.GetLastWin32Error();
            Log.Wrn(
                $"Selection attempt #{attemptId}: SendInput sent {sent}/{inputs.Length} events. LastError={lastError}");
            return;
        }

        Log.Trc($"Selection attempt #{attemptId}: Ctrl+C keystrokes sent.");
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

    private static string DescribeForegroundWindow(IntPtr hwnd)
    {
        var titleBuilder = new StringBuilder(256);
        var classBuilder = new StringBuilder(128);

        _ = GetWindowText(hwnd, titleBuilder, titleBuilder.Capacity);
        _ = GetClassName(hwnd, classBuilder, classBuilder.Capacity);
        _ = GetWindowThreadProcessId(hwnd, out var processId);

        var processName = "unknown";
        if (processId != 0)
        {
            try
            {
                processName = Process.GetProcessById((int)processId).ProcessName;
            }
            catch
            {
                processName = "unavailable";
            }
        }

        return
            $"HWND=0x{hwnd.ToInt64():X}, PID={processId}, Process='{processName}', Class='{classBuilder}', Title='{Preview(titleBuilder.ToString())}'";
    }

    private static string DescribeAutomationElement(AutomationElement element)
    {
        try
        {
            var current = element.Current;
            var controlType = current.ControlType?.ProgrammaticName ?? "unknown";
            return
                $"Name='{Preview(current.Name)}', AutomationId='{Preview(current.AutomationId)}', ClassName='{Preview(current.ClassName)}', ControlType='{controlType}', LocalizedType='{Preview(current.LocalizedControlType)}', HasKeyboardFocus={current.HasKeyboardFocus}, IsEnabled={current.IsEnabled}";
        }
        catch (Exception ex)
        {
            return $"<failed to describe focused element: {ex.Message}>";
        }
    }

    private static string Preview(string? value, int maxLength = 120)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var sanitized = value.Replace("\r", "\\r").Replace("\n", "\\n").Trim();
        if (sanitized.Length <= maxLength)
        {
            return sanitized;
        }

        return sanitized[..maxLength] + "...";
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
        public MOUSEINPUT mi;

        [FieldOffset(0)]
        public KEYBDINPUT ki;

        [FieldOffset(0)]
        public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public nuint dwExtraInfo;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
}
