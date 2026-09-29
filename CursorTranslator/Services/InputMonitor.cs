using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using CursorTranslator.Models;

namespace CursorTranslator.Services;

/// <summary>
/// Windows UI Automation watcher. It reads the focused control and its nearby UIA
/// ancestors, including the native focused HWND fallback used by custom desktop apps.
/// It never installs a keyboard hook or stores text on disk.
/// </summary>
public sealed class InputMonitor : IDisposable
{
    private const int HotkeyId = 0x4354;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint VkSpace = 0x20;
    private const uint WmHotkey = 0x0312;
    private const uint GcsCompStr = 0x0008;
    private Thread? _thread;
    private volatile bool _running;
    private volatile bool _enabled;
    private string? _elementId;
    private string _baseline = "";
    private string _lastCommittedText = "";
    private DateTime _lastChangeUtc;
    private DateTime? _unreadableSinceUtc;
    private string? _lastDiagnostic;
    private static readonly TimeSpan UnreadableTargetGrace = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan PunctuationStability = TimeSpan.FromMilliseconds(250);

    public event Action<MonitoredText>? TextCommitted;
    public event Action? InputCleared;
    public event Action<bool>? StateChanged;
    public event Action<string>? Error;
    public event Action<string>? Diagnostic;
    public event Action? TargetLost;

    public void Start()
    {
        if (_running) return;
        _running = true;
        _enabled = true;
        _thread = new Thread(Run) { IsBackground = true, Name = "CursorTranslator UIA watcher" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        StateChanged?.Invoke(true);
    }

    public void Stop()
    {
        _running = false;
        _enabled = false;
        if (_thread is { IsAlive: true } thread && thread != Thread.CurrentThread)
            thread.Join(TimeSpan.FromSeconds(1));
        _thread = null;
        StateChanged?.Invoke(false);
    }

    private void Run()
    {
        var hotkeyRegistered = RegisterHotKey(IntPtr.Zero, HotkeyId, ModControl | ModShift, VkSpace);
        if (!hotkeyRegistered)
            Error?.Invoke("无法注册 Ctrl+Shift+Space 暂停热键，可能已被其他程序占用；仍可用窗口按钮暂停。");

        try
        {
            while (_running)
            {
                while (PeekMessage(out var msg, IntPtr.Zero, WmHotkey, WmHotkey, 1))
                {
                    if (msg.WParam.ToInt32() == HotkeyId && _running)
                    {
                        _enabled = !_enabled;
                        ResetTarget();
                        StateChanged?.Invoke(_enabled);
                    }
                }

                if (_enabled)
                    PollFocusedElement();
                Thread.Sleep(180);
            }
        }
        catch (Exception ex)
        {
            Error?.Invoke($"输入监控已停止：{ex.Message}");
            _running = false;
            _enabled = false;
            StateChanged?.Invoke(false);
        }
        finally
        {
            if (hotkeyRegistered) UnregisterHotKey(IntPtr.Zero, HotkeyId);
            TargetLost?.Invoke();
        }
    }

    private void PollFocusedElement()
    {
        var nativeFocusHandle = GetNativeFocusHandle();
        if (HasActiveImeComposition(nativeFocusHandle))
        {
            ReportDiagnostic("检测到输入法正在组合文字，等候选字上屏后再翻译。");
            return;
        }

        AutomationElement? element;
        try { element = AutomationElement.FocusedElement; }
        catch (ElementNotAvailableException) { LostTarget(); return; }
        catch (COMException) { LostTarget(); return; }

        var nativeFocus = GetNativeFocusedElement(nativeFocusHandle);
        if (element is null)
            element = nativeFocus;

        if (element is null)
        {
            HandleUnreadableTarget("当前没有可监控的输入控件；请将光标放进其他应用中可编辑的输入框。");
            return;
        }

        if (!TryReadEditable(element, nativeFocus, out var value, out var id, out var bounds, out var description))
        {
            var uiAutomationDescription = description;
            if (!TryReadNativeEdit(out value, out id, out bounds, out description))
            {
                if (string.IsNullOrWhiteSpace(description))
                    description = uiAutomationDescription;
                HandleUnreadableTarget(description);
                return;
            }
        }

        if (string.IsNullOrEmpty(id))
        {
            HandleUnreadableTarget(description);
            return;
        }

        _unreadableSinceUtc = null;

        if (id != _elementId)
        {
            _elementId = id;
            _baseline = value;
            _lastCommittedText = value;
            _lastChangeUtc = DateTime.UtcNow;
            ReportDiagnostic($"已连接输入控件：{description}。输入内容只在本机暂存到句末或短暂停顿。");
            TargetLost?.Invoke();
            if (string.IsNullOrWhiteSpace(value))
            {
                InputCleared?.Invoke();
            }
            else
            {
                Emit(value, bounds);
                ReportDiagnostic($"发现已有输入内容，已提交完整内容（{value.Trim().Length} 个字符）。");
                _lastChangeUtc = DateTime.UtcNow;
            }
            return;
        }

        if (!_baseline.Equals(value, StringComparison.Ordinal))
        {
            _baseline = value;
            _lastChangeUtc = DateTime.UtcNow;
            ReportDiagnostic($"检测到输入变化（当前内容 {value.Length} 个字符），等待句末或停顿…");
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            _lastCommittedText = "";
            InputCleared?.Invoke();
            return;
        }

        if (value.Equals(_lastCommittedText, StringComparison.Ordinal)) return;

        // Detect punctuation only in newly appended text. The request itself always carries
        // the complete current field, so multi-sentence input is translated as one passage.
        var appendedText = value.StartsWith(_lastCommittedText, StringComparison.Ordinal)
            ? value[_lastCommittedText.Length..]
            : "";

        if (ContainsSentenceTerminator(appendedText)
            && DateTime.UtcNow - _lastChangeUtc >= PunctuationStability)
        {
            Emit(value, bounds);
            ReportDiagnostic($"完整输入已提交（{value.Trim().Length} 个字符）。");
            _lastCommittedText = value;
            _lastChangeUtc = DateTime.UtcNow;
            return;
        }

        if (value.Trim().Length >= 2 && DateTime.UtcNow - _lastChangeUtc >= TimeSpan.FromMilliseconds(850))
        {
            Emit(value, bounds);
            ReportDiagnostic($"完整输入已提交（{value.Trim().Length} 个字符）。");
            _lastCommittedText = value;
            _lastChangeUtc = DateTime.UtcNow;
        }
    }

    private static bool ContainsSentenceTerminator(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is '\r' or '\n' or '。' or '！' or '？' or '!' or '?' or ';' or '；')
                return true;
            if (text[i] == '.' && (i + 1 == text.Length || char.IsWhiteSpace(text[i + 1])))
                return true;
        }
        return false;
    }

    private void Emit(string text, PixelRect bounds)
    {
        text = text.Trim();
        if (text.Length == 0) return;
        TextCommitted?.Invoke(new MonitoredText(text, bounds));
    }

    private static bool TryReadEditable(
        AutomationElement focusedElement,
        AutomationElement? nativeFocus,
        out string value,
        out string id,
        out PixelRect bounds,
        out string description)
    {
        value = "";
        id = "";
        bounds = default;
        description = "";
        try
        {
            var focusedInfo = focusedElement.Current;
            description = FormatDescription(focusedInfo);
            if (focusedInfo.ProcessId == Environment.ProcessId)
            {
                description = "焦点仍在翻译器窗口内；请将光标移到其他应用的输入框";
                return false;
            }
            if (focusedInfo.IsPassword)
            {
                description = "检测到受保护的密码输入框，已跳过";
                return false;
            }

            var candidates = new List<AutomationElement> { focusedElement };

            // Custom editors such as WeChat's may focus a child UIA node that exposes
            // no text pattern while the containing Edit/Document node does.
            var walker = TreeWalker.RawViewWalker;
            var current = focusedElement;
            for (var depth = 0; depth < 12; depth++)
            {
                var parent = walker.GetParent(current);
                if (parent is null) break;
                var parentInfo = parent.Current;
                if (parentInfo.ProcessId != focusedInfo.ProcessId) break;
                if (candidates.All(candidate => !IsSameElement(candidate, parent)))
                    candidates.Add(parent);
                current = parent;
            }

            if (nativeFocus is not null && candidates.All(candidate => !IsSameElement(candidate, nativeFocus)))
                candidates.Add(nativeFocus);

            var orderedCandidates = candidates
                .OrderBy(candidate => GetCandidatePriority(candidate, focusedElement))
                .ToArray();
            foreach (var candidate in orderedCandidates)
            {
                if (TryReadCandidate(candidate, focusedElement, focusedInfo.ProcessId, out value, out id, out bounds, out var sourceDescription))
                {
                    description = sourceDescription;
                    return true;
                }
            }

            description = $"{description} 未暴露可读的 UI Automation 文本模式（微信等自绘输入框可能如此）；已检查焦点控件及其父级控件";
            return false;
        }
        catch (ElementNotAvailableException) { description = "焦点控件刚刚关闭，请重新聚焦输入框"; return false; }
        catch (InvalidOperationException ex) { description = $"UI Automation 暂时无法读取焦点控件：{ex.Message}"; return false; }
        catch (COMException ex) { description = $"UI Automation 读取失败（HRESULT 0x{ex.HResult:X8}）"; return false; }
        catch (Exception ex) { description = $"读取焦点控件失败：{ex.Message}"; return false; }
    }

    private static bool TryReadCandidate(
        AutomationElement candidate,
        AutomationElement focusedElement,
        int focusedProcessId,
        out string value,
        out string id,
        out PixelRect bounds,
        out string description)
    {
        value = "";
        id = "";
        bounds = default;
        description = "";
        try
        {
            var info = candidate.Current;
            if (info.ProcessId == Environment.ProcessId || info.ProcessId != focusedProcessId || info.IsPassword)
                return false;

            object? pattern = null;
            if (candidate.TryGetCurrentPattern(ValuePattern.Pattern, out var valuePattern))
            {
                var valueInfo = ((ValuePattern)valuePattern).Current;
                if (valueInfo.IsReadOnly) return false;
                pattern = valuePattern;
                value = valueInfo.Value ?? "";
            }
            else if (candidate.TryGetCurrentPattern(TextPattern.Pattern, out var textPattern))
            {
                if (!IsSameElement(candidate, focusedElement)
                    && info.ControlType != ControlType.Edit
                    && info.ControlType != ControlType.Document)
                    return false;
                pattern = textPattern;
                value = ((TextPattern)textPattern).DocumentRange.GetText(-1);
            }
            else
            {
                return false;
            }

            if (IsPlaceholderText(value, info.Name, info.HelpText))
                value = "";

            var runtimeId = candidate.GetRuntimeId();
            id = info.ProcessId + ":" + string.Join('.', runtimeId);
            var caretRect = focusedElement.Current.BoundingRectangle;
            var rect = IsUsableRectangle(caretRect) ? caretRect : info.BoundingRectangle;
            bounds = ToPixelRect(rect);

            if (pattern is TextPattern text)
            {
                try
                {
                    var selection = text.GetSelection();
                    if (selection.Length > 0)
                    {
                        var rangeBounds = selection[0].GetBoundingRectangles();
                        if (rangeBounds.Length > 0 && rangeBounds[0].Width > 0 && rangeBounds[0].Height > 0)
                        {
                            var caret = rangeBounds[0];
                            bounds = ToPixelRect(caret);
                        }
                    }
                }
                catch (InvalidOperationException) { }
                catch (COMException) { }
            }

            description = FormatDescription(info);
            if (!IsSameElement(candidate, focusedElement))
                description += "（使用焦点控件父级文本）";
            return true;
        }
        catch (ElementNotAvailableException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (COMException) { return false; }
        catch (Exception) { return false; }
    }

    private static AutomationElement? GetNativeFocusedElement(IntPtr focus)
    {
        if (focus == IntPtr.Zero) return null;

        try { return AutomationElement.FromHandle(focus); }
        catch (ElementNotAvailableException) { return null; }
        catch (COMException) { return null; }
    }

    private static bool TryReadNativeEdit(out string value, out string id, out PixelRect bounds, out string description)
    {
        value = "";
        id = "";
        bounds = default;
        description = "";

        var handle = GetNativeFocusHandle();
        if (handle == IntPtr.Zero) return false;

        GetWindowThreadProcessId(handle, out var processId);
        if (processId == Environment.ProcessId)
        {
            description = "焦点仍在翻译器窗口内；请将光标移到其他应用的输入框";
            return false;
        }

        var className = new StringBuilder(256);
        if (GetClassName(handle, className, className.Capacity) == 0)
            return false;

        var classText = className.ToString();
        var isNativeEditor = classText.Equals("Edit", StringComparison.OrdinalIgnoreCase)
            || classText.StartsWith("RichEdit", StringComparison.OrdinalIgnoreCase)
            || classText.StartsWith("RICHEDIT", StringComparison.OrdinalIgnoreCase)
            || classText.Contains(".EDIT.", StringComparison.OrdinalIgnoreCase);
        if (!isNativeEditor)
            return false;

        const int GwlStyle = -16;
        const long EsPassword = 0x0020;
        const uint WmGetTextLength = 0x000E;
        const uint WmGetText = 0x000D;
        const uint SmtoAbortIfHung = 0x0002;
        const uint SmtoBlock = 0x0001;
        const uint MessageTimeoutMs = 100;
        if ((GetWindowLongPtr(handle, GwlStyle).ToInt64() & EsPassword) != 0)
        {
            description = "检测到受保护的密码输入框，已跳过";
            return false;
        }

        if (SendMessageTimeout(handle, WmGetTextLength, IntPtr.Zero, IntPtr.Zero,
                SmtoAbortIfHung | SmtoBlock, MessageTimeoutMs, out var lengthResult) == IntPtr.Zero)
            return false;

        var length = Math.Clamp(lengthResult.ToInt64(), 0, 100_000);
        var buffer = new StringBuilder((int)length + 1);
        if (SendMessageTimeout(handle, WmGetText, new IntPtr(buffer.Capacity), buffer,
                SmtoAbortIfHung | SmtoBlock, MessageTimeoutMs, out _) == IntPtr.Zero)
            return false;

        value = buffer.ToString();
        var windowRect = new NativeRect();
        if (GetWindowRect(handle, out windowRect))
            bounds = new PixelRect(windowRect.Left, windowRect.Top,
                Math.Max(1, windowRect.Right - windowRect.Left), Math.Max(1, windowRect.Bottom - windowRect.Top));

        id = $"{processId}:hwnd:{handle.ToInt64():X}";
        description = $"Win32 {classText}";
        return true;
    }

    private static IntPtr GetNativeFocusHandle()
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero) return IntPtr.Zero;
        var threadId = GetWindowThreadProcessId(foreground, out _);
        if (threadId == 0) return IntPtr.Zero;

        var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
        if (!GetGUIThreadInfo(threadId, ref info) || info.Focus == IntPtr.Zero)
            return IntPtr.Zero;
        return info.Focus;
    }

    private static bool HasActiveImeComposition(IntPtr focus)
    {
        if (focus == IntPtr.Zero) return false;
        var context = ImmGetContext(focus);
        if (context == IntPtr.Zero) return false;
        try
        {
            // The result is a byte count; reading the actual pre-edit string is unnecessary.
            return ImmGetCompositionString(context, GcsCompStr, IntPtr.Zero, 0) > 0;
        }
        finally
        {
            ImmReleaseContext(focus, context);
        }
    }

    private static bool IsSameElement(AutomationElement first, AutomationElement second)
    {
        try
        {
            var firstId = first.GetRuntimeId();
            var secondId = second.GetRuntimeId();
            return first.Current.ProcessId == second.Current.ProcessId && firstId.SequenceEqual(secondId);
        }
        catch (ElementNotAvailableException) { return false; }
        catch (COMException) { return false; }
    }

    private static int GetCandidatePriority(AutomationElement candidate, AutomationElement focusedElement)
    {
        try
        {
            var controlType = candidate.Current.ControlType;
            if (controlType == ControlType.Edit) return 0;
            if (IsSameElement(candidate, focusedElement)) return 1;
            if (controlType == ControlType.Document) return 2;
        }
        catch (ElementNotAvailableException) { }
        catch (COMException) { }
        return 3;
    }

    private static string FormatDescription(AutomationElement.AutomationElementInformation info)
        => $"{info.ControlType.ProgrammaticName.Replace("ControlType.", "")}/{(string.IsNullOrWhiteSpace(info.ClassName) ? "未命名控件" : info.ClassName)}";

    private static bool IsUsableRectangle(System.Windows.Rect rectangle)
        => rectangle.Width > 0 && rectangle.Height > 0
            && !double.IsInfinity(rectangle.Left) && !double.IsInfinity(rectangle.Top)
            && !double.IsNaN(rectangle.Left) && !double.IsNaN(rectangle.Top);

    private static PixelRect ToPixelRect(System.Windows.Rect rect)
        => new((int)rect.Left, (int)rect.Top, Math.Max(1, (int)rect.Width), Math.Max(1, (int)rect.Height));

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public uint Size;
        public uint Flags;
        public IntPtr Active;
        public IntPtr Focus;
        public IntPtr Capture;
        public IntPtr MenuOwner;
        public IntPtr MoveSize;
        public IntPtr Caret;
        public NativeRect CaretRect;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private static bool IsPlaceholderText(string value, string? name, string? helpText)
    {
        var candidate = value.Trim();
        return candidate.Length > 0
            && ((!string.IsNullOrWhiteSpace(name) && candidate.Equals(name.Trim(), StringComparison.Ordinal))
                || (!string.IsNullOrWhiteSpace(helpText) && candidate.Equals(helpText.Trim(), StringComparison.Ordinal)));
    }

    private void ReportDiagnostic(string message)
    {
        if (message == _lastDiagnostic) return;
        _lastDiagnostic = message;
        Diagnostic?.Invoke(message);
    }

    private void LostTarget()
    {
        if (_elementId is not null) TargetLost?.Invoke();
        ResetTarget();
    }

    private void HandleUnreadableTarget(string message)
    {
        var now = DateTime.UtcNow;
        _unreadableSinceUtc ??= now;
        if (_elementId is not null && now - _unreadableSinceUtc < UnreadableTargetGrace)
        {
            ReportDiagnostic("焦点输入控件暂时不可读，正在等待其文本接口恢复。");
            return;
        }

        ReportDiagnostic(message);
        LostTarget();
    }

    private void ResetTarget()
    {
        _elementId = null;
        _baseline = "";
        _lastCommittedText = "";
        _unreadableSinceUtc = null;
    }

    public void Dispose() => Stop();

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr HWnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X;
        public int Y;
        public uint Private;
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint virtualKey);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] private static extern bool PeekMessage(out NativeMessage message, IntPtr hWnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", SetLastError = true)] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)] private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint message, IntPtr wParam, StringBuilder lParam, uint flags, uint timeout, out IntPtr result);
    [DllImport("imm32.dll", SetLastError = true)] private static extern IntPtr ImmGetContext(IntPtr hWnd);
    [DllImport("imm32.dll", EntryPoint = "ImmGetCompositionStringW", SetLastError = true)] private static extern int ImmGetCompositionString(IntPtr context, uint index, IntPtr buffer, uint bufferLength);
    [DllImport("imm32.dll", SetLastError = true)] private static extern bool ImmReleaseContext(IntPtr hWnd, IntPtr context);
}
