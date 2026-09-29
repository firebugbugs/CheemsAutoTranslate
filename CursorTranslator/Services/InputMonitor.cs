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
    private const uint GcsCompStr = 0x0008;
    private const uint ObjIdClient = 0xFFFFFFFC;
    private const uint GaRoot = 2;
    private const uint GwOwner = 4;
    private const int RoleSystemText = 0x2A;
    private const int StateSystemUnavailable = 0x00000001;
    private const int StateSystemFocused = 0x00000004;
    private const int StateSystemReadOnly = 0x00000040;
    private const int StateSystemFocusable = 0x00100000;
    private const int StateSystemProtected = 0x20000000;
    private const string PasswordControlDiagnostic = "检测到受保护的密码输入框，已跳过";
    private Thread? _thread;
    private volatile bool _running;
    private string? _elementId;
    private IntPtr _targetRootWindow;
    private IntPtr _overlayWindowHandle;
    private int _overlayInteractionActive;
    private string _baseline = "";
    private bool _hasPendingText;
    private DateTime _lastChangeUtc;
    private DateTime? _unreadableSinceUtc;
    private string? _lastDiagnostic;
    private string? _lastLoopError;
    private static readonly TimeSpan UnreadableTargetGrace = TimeSpan.FromMilliseconds(600);
    private TriggerConfiguration _triggerConfiguration = new(
        false,
        true,
        false,
        (int)(AppSettings.DefaultInactivityDelaySeconds * 1000));

    public void ConfigureTranslationTriggers(
        bool onTextChange,
        bool onSentenceEnd,
        bool afterInactivity,
        int inactivityDelayMilliseconds)
    {
        if (onTextChange)
        {
            onSentenceEnd = false;
            afterInactivity = false;
        }

        Volatile.Write(ref _triggerConfiguration, new TriggerConfiguration(
            onTextChange,
            onSentenceEnd,
            afterInactivity,
            Math.Max(1, inactivityDelayMilliseconds)));
    }

    public event Action<MonitoredText>? TextCommitted;
    public event Action? InputCleared;
    public event Action<string>? Error;
    public event Action<string>? Diagnostic;

    public void SetOverlayWindowHandle(IntPtr handle)
        => Interlocked.Exchange(ref _overlayWindowHandle, handle);

    public void PreserveTargetForOverlayInteraction()
        => Interlocked.Exchange(ref _overlayInteractionActive, 1);

    public void Start()
    {
        if (_running) return;
        _running = true;
        _thread = new Thread(Run) { IsBackground = true, Name = "CursorTranslator UIA watcher" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        if (_thread is { IsAlive: true } thread && thread != Thread.CurrentThread)
            thread.Join(TimeSpan.FromSeconds(1));
        _thread = null;
    }

    private void Run()
    {
        while (_running)
        {
            try
            {
                PollFocusedElement();
                _lastLoopError = null;
            }
            catch (Exception ex)
            {
                if (ShouldPreserveTargetDuringOverlayInteraction())
                {
                    ReportDiagnostic("翻译框交互中，已保留当前输入目标。");
                }
                else
                {
                    var message = $"监控异常，正在重试：{ex.Message}";
                    if (message != _lastLoopError)
                    {
                        _lastLoopError = message;
                        Error?.Invoke(message);
                    }
                    LostTarget();
                }
            }

            Thread.Sleep(180);
        }
    }

    private void PollFocusedElement()
    {
        var triggers = Volatile.Read(ref _triggerConfiguration);
        var foregroundWindow = GetForegroundWindow();
        var foregroundRoot = foregroundWindow == IntPtr.Zero
            ? IntPtr.Zero
            : GetAncestor(foregroundWindow, GaRoot);

        // Mouse interaction can activate the non-activating overlay or one of
        // its native popups. Keep the current source target while the user is
        // interacting with that card; switching to any other app still clears it.
        if (IsOverlayWindowOrOwnedPopup(foregroundWindow, foregroundRoot))
            return;

        // The overlay belongs to the input window that produced it. Clear it as
        // soon as the user switches to another top-level window, even if that
        // window has no readable/editable UI Automation element.
        if (_targetRootWindow != IntPtr.Zero && foregroundRoot != _targetRootWindow)
            LostTarget();

        var nativeFocusHandle = GetNativeFocusHandle();
        if (HasActiveImeComposition(nativeFocusHandle))
        {
            ReportDiagnostic("检测到输入法正在组合文字，等候选字上屏后再翻译。");
            return;
        }

        AutomationElement? element;
        try { element = AutomationElement.FocusedElement; }
        catch (ElementNotAvailableException)
        {
            HandleUnreadableTarget("焦点输入控件暂时不可读，正在等待其文本接口恢复。");
            return;
        }
        catch (COMException)
        {
            HandleUnreadableTarget("焦点输入控件暂时不可读，正在等待其文本接口恢复。");
            return;
        }

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
            if (uiAutomationDescription == PasswordControlDiagnostic)
            {
                HandleUnreadableTarget(uiAutomationDescription);
                return;
            }

            if (!TryReadNativeEdit(nativeFocusHandle, out value, out id, out bounds, out description))
            {
                var nativeDescription = description;
                if (nativeDescription == PasswordControlDiagnostic)
                {
                    HandleUnreadableTarget(nativeDescription);
                    return;
                }

                if (!TryReadLegacyAccessible(nativeFocusHandle, out value, out id, out bounds, out description))
                {
                    if (OfficeDocumentInputSource.TryReadFocusedParagraph(
                            element, nativeFocusHandle, out value, out id, out bounds, out var officeDescription))
                    {
                        description = officeDescription;
                    }
                    else
                    {
                        var inputDescription = !string.IsNullOrWhiteSpace(nativeDescription)
                            ? nativeDescription
                            : uiAutomationDescription;
                        description = $"{inputDescription}；{officeDescription}";
                        HandleUnreadableTarget(description);
                        return;
                    }
                }
            }
        }

        if (string.IsNullOrEmpty(id))
        {
            HandleUnreadableTarget(description);
            return;
        }

        Interlocked.Exchange(ref _overlayInteractionActive, 0);
        _unreadableSinceUtc = null;

        if (id != _elementId)
        {
            _elementId = id;
            _targetRootWindow = foregroundRoot;
            _baseline = value;
            _lastChangeUtc = DateTime.UtcNow;
            _hasPendingText = !string.IsNullOrWhiteSpace(value);
            ReportDiagnostic($"已连接：{description}");
            if (string.IsNullOrWhiteSpace(value))
            {
                InputCleared?.Invoke();
            }
            else if (triggers.OnTextChange)
            {
                Commit(value, bounds);
            }
            else if (triggers.OnSentenceEnd && EndsWithSentenceTerminator(value))
            {
                Commit(value, bounds);
            }
            return;
        }

        var previousValue = _baseline;
        var changed = !previousValue.Equals(value, StringComparison.Ordinal);
        if (changed)
        {
            _baseline = value;
            _lastChangeUtc = DateTime.UtcNow;
            _hasPendingText = !string.IsNullOrWhiteSpace(value);
            ReportDiagnostic($"输入中 · {value.Length} 字");
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            _hasPendingText = false;
            InputCleared?.Invoke();
            return;
        }

        if (changed && triggers.OnTextChange)
        {
            Commit(value, bounds);
            return;
        }

        if (!_hasPendingText) return;

        if (changed && triggers.OnSentenceEnd
            && (ContainsSentenceTerminator(GetChangedSegment(previousValue, value))
                || EndsWithSentenceTerminator(value)))
        {
            Commit(value, bounds);
            return;
        }

        if (triggers.AfterInactivity
            && (!triggers.OnSentenceEnd || !EndsWithSentenceTerminator(value))
            && DateTime.UtcNow - _lastChangeUtc >= TimeSpan.FromMilliseconds(triggers.InactivityDelayMilliseconds))
        {
            Commit(value, bounds);
        }
    }

    private bool IsOverlayWindowOrOwnedPopup(IntPtr foregroundWindow, IntPtr foregroundRoot)
    {
        var overlayHandle = Interlocked.CompareExchange(ref _overlayWindowHandle, IntPtr.Zero, IntPtr.Zero);
        if (overlayHandle == IntPtr.Zero || foregroundWindow == IntPtr.Zero)
            return false;

        var overlayRoot = GetAncestor(overlayHandle, GaRoot);
        if (overlayRoot == IntPtr.Zero)
            overlayRoot = overlayHandle;
        if (foregroundRoot == overlayRoot)
            return true;

        var current = foregroundWindow;
        for (var depth = 0; current != IntPtr.Zero && depth < 16; depth++)
        {
            if (current == overlayHandle || GetAncestor(current, GaRoot) == overlayRoot)
                return true;
            current = GetWindow(current, GwOwner);
        }

        return false;
    }

    private void Commit(string text, PixelRect bounds)
    {
        Emit(text, bounds);
        _hasPendingText = false;
        ReportDiagnostic("已提交");
    }

    private static string GetChangedSegment(string previous, string current)
    {
        var prefixLength = 0;
        var commonLength = Math.Min(previous.Length, current.Length);
        while (prefixLength < commonLength && previous[prefixLength] == current[prefixLength])
            prefixLength++;

        var previousEnd = previous.Length;
        var currentEnd = current.Length;
        while (previousEnd > prefixLength && currentEnd > prefixLength
            && previous[previousEnd - 1] == current[currentEnd - 1])
        {
            previousEnd--;
            currentEnd--;
        }

        return current[prefixLength..currentEnd];
    }

    private static bool ContainsSentenceTerminator(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is '\r' or '\n' or '。' or '！' or '？' or '!' or '?' or ';' or '；'
                or ',' or '，' or '、' or ':' or '：' or '…')
                return true;
            if (text[i] == '.' && (i + 1 == text.Length || char.IsWhiteSpace(text[i + 1])))
                return true;
        }
        return false;
    }

    private static bool EndsWithSentenceTerminator(string text)
    {
        for (var i = text.Length - 1; i >= 0; i--)
        {
            if (char.IsWhiteSpace(text[i])) continue;
            return text[i] is '\r' or '\n' or '。' or '！' or '？' or '!' or '?' or ';' or '；'
                or ',' or '，' or '、' or ':' or '：' or '…'
                || text[i] == '.';
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
            description = PasswordControlDiagnostic;
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

            description = $"{description} 未暴露可读的 UI Automation 文本模式；已检查焦点控件及其父级控件";
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
                value = valueInfo.Value ?? "";

                // Some custom UIA providers report ValuePattern on every node, including
                // non-editable containers. An empty value from such a node is not evidence
                // that it is an editable text target; otherwise it masks the Win32/MSAA
                // fallbacks and leaves monitoring connected to a permanently empty value.
                if (value.Length == 0 && info.ControlType != ControlType.Edit)
                    return false;

                pattern = valuePattern;
            }
            else if (candidate.TryGetCurrentPattern(TextPattern.Pattern, out var textPattern))
            {
                if (!IsSameElement(candidate, focusedElement)
                    && info.ControlType != ControlType.Edit
                    && info.ControlType != ControlType.Document)
                    return false;
                pattern = textPattern;
                value = ((TextPattern)textPattern).DocumentRange.GetText(-1);
                if (string.IsNullOrEmpty(value)
                    && info.ControlType != ControlType.Edit
                    && info.ControlType != ControlType.Document)
                    return false;
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

    private static bool TryReadNativeEdit(IntPtr handle, out string value, out string id, out PixelRect bounds, out string description)
    {
        value = "";
        id = "";
        bounds = default;
        description = "";

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
            description = PasswordControlDiagnostic;
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

    private static bool TryReadLegacyAccessible(IntPtr handle, out string value, out string id, out PixelRect bounds, out string description)
    {
        value = "";
        id = "";
        bounds = default;
        description = "";

        if (handle == IntPtr.Zero) return false;

        GetWindowThreadProcessId(handle, out var processId);
        if (processId == Environment.ProcessId) return false;

        var interfaceId = typeof(Accessibility.IAccessible).GUID;
        if (AccessibleObjectFromWindow(handle, ObjIdClient, ref interfaceId, out var root) < 0 || root is null)
            return false;

        try
        {
            Accessibility.IAccessible focused = root;
            object childId = 0;
            for (var depth = 0; depth < 8; depth++)
            {
                object? focusedChild;
                try { focusedChild = focused.accFocus; }
                catch (COMException) { break; }

                if (focusedChild is Accessibility.IAccessible focusedObject)
                {
                    focused = focusedObject;
                    childId = 0;
                    continue;
                }

                if (focusedChild is not null && focusedChild is not DBNull
                    && TryConvertAccessibleChildId(focusedChild, out var focusedChildId))
                    childId = focusedChildId;
                break;
            }

            var role = Convert.ToInt32(focused.accRole[childId]);
            var state = Convert.ToInt32(focused.accState[childId]);
            if (role != RoleSystemText
                || (state & (StateSystemUnavailable | StateSystemReadOnly | StateSystemProtected)) != 0
                || (state & (StateSystemFocusable | StateSystemFocused)) == 0)
                return false;

            value = focused.accValue[childId] ?? "";
            id = $"{processId}:msaa:{handle.ToInt64():X}:{childId}";

            try
            {
                focused.accLocation(out var left, out var top, out var width, out var height, childId);
                if (width > 0 && height > 0)
                    bounds = new PixelRect(left, top, width, height);
            }
            catch (COMException) { }

            if (bounds.Width == 0 || bounds.Height == 0)
            {
                var rect = new NativeRect();
                if (GetWindowRect(handle, out rect))
                    bounds = new PixelRect(rect.Left, rect.Top,
                        Math.Max(1, rect.Right - rect.Left), Math.Max(1, rect.Bottom - rect.Top));
            }

            description = "MSAA editable text";
            return true;
        }
        catch (COMException) { return false; }
        catch (InvalidCastException) { return false; }
        catch (FormatException) { return false; }
        catch (OverflowException) { return false; }
        catch (Exception) { return false; }
    }

    private static bool TryConvertAccessibleChildId(object value, out object childId)
    {
        try
        {
            var numericId = Convert.ToInt32(value);
            if (numericId >= 0)
            {
                childId = numericId;
                return true;
            }
        }
        catch (FormatException) { }
        catch (InvalidCastException) { }
        catch (OverflowException) { }

        childId = 0;
        return false;
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
        var hadTarget = _elementId is not null;
        ResetTarget();
        if (hadTarget)
            InputCleared?.Invoke();
    }

    private void HandleUnreadableTarget(string message)
    {
        if (ShouldPreserveTargetDuringOverlayInteraction())
        {
            _unreadableSinceUtc = null;
            ReportDiagnostic("翻译框交互中，已保留当前输入目标。");
            return;
        }

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

    private bool ShouldPreserveTargetDuringOverlayInteraction()
    {
        if (Volatile.Read(ref _overlayInteractionActive) == 0 || _targetRootWindow == IntPtr.Zero)
            return false;

        var foregroundWindow = GetForegroundWindow();
        var foregroundRoot = foregroundWindow == IntPtr.Zero
            ? IntPtr.Zero
            : GetAncestor(foregroundWindow, GaRoot);
        return foregroundRoot == _targetRootWindow;
    }

    private void ResetTarget()
    {
        _elementId = null;
        _targetRootWindow = IntPtr.Zero;
        Interlocked.Exchange(ref _overlayInteractionActive, 0);
        _baseline = "";
        _hasPendingText = false;
        _unreadableSinceUtc = null;
    }

    private sealed record TriggerConfiguration(
        bool OnTextChange,
        bool OnSentenceEnd,
        bool AfterInactivity,
        int InactivityDelayMilliseconds);

    public void Dispose() => Stop();

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetWindow(IntPtr hWnd, uint command);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);
    [DllImport("oleacc.dll", PreserveSig = true)] private static extern int AccessibleObjectFromWindow(IntPtr hwnd, uint objectId, ref Guid interfaceId, [MarshalAs(UnmanagedType.Interface)] out Accessibility.IAccessible accessibleObject);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)] private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint message, IntPtr wParam, StringBuilder lParam, uint flags, uint timeout, out IntPtr result);
    [DllImport("imm32.dll", SetLastError = true)] private static extern IntPtr ImmGetContext(IntPtr hWnd);
    [DllImport("imm32.dll", EntryPoint = "ImmGetCompositionStringW", SetLastError = true)] private static extern int ImmGetCompositionString(IntPtr context, uint index, IntPtr buffer, uint bufferLength);
    [DllImport("imm32.dll", SetLastError = true)] private static extern bool ImmReleaseContext(IntPtr hWnd, IntPtr context);
}
