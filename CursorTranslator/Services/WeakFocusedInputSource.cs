using System.Runtime.InteropServices;
using Avalonia;
using UIA = Interop.UIAutomationClient;

namespace CursorTranslator.Services;

/// <summary>
/// Reads focused inputs that UI Automation reports with a generic control type
/// (Custom, Group or Pane) instead of Edit/Document. Such nodes are accepted only
/// with independent evidence of editing: a writable value or text-edit pattern,
/// no static-text or read-only MSAA role, and an active caret inside the node.
/// <see cref="InputMonitor"/> additionally keeps these targets under observation
/// until their text changes in response to user input.
/// </summary>
internal static class WeakFocusedInputSource
{
    private const int UiaValuePatternId = 10002;
    private const int UiaLegacyIAccessiblePatternId = 10018;
    private const int UiaTextPattern2Id = 10024;
    private const int UiaTextEditPatternId = 10032;
    private const int UiaIsReadOnlyAttributeId = 40015;
    private const int UiaCustomControlTypeId = 50025;
    private const int UiaGroupControlTypeId = 50026;
    private const int UiaPaneControlTypeId = 50033;
    private const int RoleSystemStaticText = 0x29;
    private const int StateSystemUnavailable = 0x00000001;
    private const int StateSystemReadOnly = 0x00000040;
    private const int StateSystemProtected = 0x20000000;
    private const uint ObjIdCaret = 0xFFFFFFF8;
    private const int CaretTolerancePixels = 8;
    private const int MaximumCompositionCharacters = 256;
    // UIA's default 20 s transaction timeout would stall the watcher thread when a
    // target process hangs; property reads normally finish in a few milliseconds.
    private const uint UiaTimeoutMilliseconds = 1000;

    [ThreadStatic] private static UIA.IUIAutomation? _automation;
    [ThreadStatic] private static bool _automationUnavailable;

    // Without UIA3 (no COM support, broken registration) the monitor keeps its
    // other sources; it must not turn every poll into a monitor error.
    private static UIA.IUIAutomation? Automation
    {
        get
        {
            if (_automation is null && !_automationUnavailable)
            {
                try { _automation = CreateAutomation(); }
                catch (Exception ex) when (ex is COMException or NotSupportedException or InvalidCastException)
                {
                    _automationUnavailable = true;
                }
            }
            return _automation;
        }
    }

    private static UIA.IUIAutomation CreateAutomation()
    {
        var automation = new UIA.CUIAutomation8();
        if (automation is UIA.IUIAutomation2 timeouts)
        {
            timeouts.ConnectionTimeout = UiaTimeoutMilliseconds;
            timeouts.TransactionTimeout = UiaTimeoutMilliseconds;
        }
        return automation;
    }

    public static UIA.IUIAutomationElement? GetFocusedElement()
    {
        try { return Automation?.GetFocusedElement(); }
        catch (COMException) { return null; }
        catch (InvalidCastException) { return null; }
    }

    /// <summary>
    /// Reports an IME composition through the provider's TextEditPattern. Unlike
    /// ImmGetContext, this works across processes.
    /// </summary>
    public static bool HasActiveComposition(UIA.IUIAutomationElement? focused)
    {
        if (focused is null) return false;
        try
        {
            if (focused.GetCurrentPattern(UiaTextEditPatternId) is not UIA.IUIAutomationTextEditPattern textEdit)
                return false;
            var composition = textEdit.GetActiveComposition();
            return composition is not null
                && !string.IsNullOrEmpty(composition.GetText(MaximumCompositionCharacters));
        }
        catch (COMException) { return false; }
        catch (InvalidCastException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    public static bool TryReadFocusedInput(
        UIA.IUIAutomationElement element,
        int expectedProcessId,
        PixelRect? caretBounds,
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
            var processId = element.CurrentProcessId;
            if (processId == Environment.ProcessId || processId != expectedProcessId)
                return false;

            var controlType = element.CurrentControlType;
            if (controlType is not (UiaCustomControlTypeId or UiaGroupControlTypeId or UiaPaneControlTypeId))
                return false;
            if (element.CurrentHasKeyboardFocus == 0 || element.CurrentIsEnabled == 0)
                return false;
            if (element.CurrentIsPassword != 0)
            {
                description = InputMonitor.PasswordControlDiagnostic;
                return false;
            }

            if (element.GetCurrentPattern(UiaLegacyIAccessiblePatternId) is UIA.IUIAutomationLegacyIAccessiblePattern legacy)
            {
                var state = (int)legacy.CurrentState;
                if ((state & StateSystemProtected) != 0)
                {
                    description = InputMonitor.PasswordControlDiagnostic;
                    return false;
                }
                if (legacy.CurrentRole == RoleSystemStaticText
                    || (state & (StateSystemUnavailable | StateSystemReadOnly)) != 0)
                    return false;
            }

            var rectangle = element.CurrentBoundingRectangle;
            if (rectangle.right <= rectangle.left || rectangle.bottom <= rectangle.top)
                return false;
            var elementBounds = new PixelRect(rectangle.left, rectangle.top,
                rectangle.right - rectangle.left, rectangle.bottom - rectangle.top);

            var caretInside = caretBounds is { } caret && IsCaretInside(caret, elementBounds);
            if (!caretInside && !HasActiveTextPattern2Caret(element))
                return false;

            if (!TryReadWritableText(element, out value))
                return false;
            if (InputMonitor.IsPlaceholderText(value, element.CurrentName, element.CurrentHelpText))
                value = "";

            var runtimeId = (Array)element.GetRuntimeId();
            id = $"{processId}:weak:{string.Join('.', runtimeId.Cast<object>())}";
            bounds = caretInside ? caretBounds!.Value : elementBounds;
            var className = element.CurrentClassName;
            description = $"{GetControlTypeName(controlType)}/{(string.IsNullOrWhiteSpace(className) ? "未命名控件" : className)}";
            return true;
        }
        catch (COMException) { return false; }
        catch (InvalidCastException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    /// <summary>
    /// Returns the caret rectangle in screen pixels from the Win32 system caret or,
    /// for apps such as Chromium that expose a virtual caret, from MSAA OBJID_CARET.
    /// </summary>
    public static PixelRect? GetCaretBounds(IntPtr foregroundWindow, IntPtr focusHandle)
    {
        if (foregroundWindow != IntPtr.Zero)
        {
            var threadId = GetWindowThreadProcessId(foregroundWindow, out _);
            var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
            if (threadId != 0 && GetGUIThreadInfo(threadId, ref info) && info.Caret != IntPtr.Zero
                && info.CaretRect.Bottom > info.CaretRect.Top)
            {
                var topLeft = new NativePoint { X = info.CaretRect.Left, Y = info.CaretRect.Top };
                var bottomRight = new NativePoint { X = info.CaretRect.Right, Y = info.CaretRect.Bottom };
                if (ClientToScreen(info.Caret, ref topLeft) && ClientToScreen(info.Caret, ref bottomRight))
                    return new PixelRect(topLeft.X, topLeft.Y,
                        Math.Max(1, bottomRight.X - topLeft.X), Math.Max(1, bottomRight.Y - topLeft.Y));
            }
        }

        if (focusHandle == IntPtr.Zero) return null;
        var interfaceId = typeof(Accessibility.IAccessible).GUID;
        if (AccessibleObjectFromWindow(focusHandle, ObjIdCaret, ref interfaceId, out var caretObject) < 0
            || caretObject is null)
            return null;
        try
        {
            caretObject.accLocation(out var left, out var top, out var width, out var height, 0);
            return height > 0 ? new PixelRect(left, top, Math.Max(1, width), height) : null;
        }
        catch (COMException) { return null; }
        catch (ArgumentException) { return null; }
    }

    private static bool HasActiveTextPattern2Caret(UIA.IUIAutomationElement element)
    {
        try
        {
            if (element.GetCurrentPattern(UiaTextPattern2Id) is not UIA.IUIAutomationTextPattern2 textPattern)
                return false;
            var caretRange = textPattern.GetCaretRange(out var isActive);
            return isActive != 0 && caretRange is not null;
        }
        catch (COMException) { return false; }
        catch (InvalidCastException) { return false; }
    }

    private static bool TryReadWritableText(UIA.IUIAutomationElement element, out string value)
    {
        value = "";
        if (element.GetCurrentPattern(UiaValuePatternId) is UIA.IUIAutomationValuePattern valuePattern)
        {
            // A read-only value must not fall through to the text pattern.
            if (valuePattern.CurrentIsReadOnly != 0) return false;
            var current = valuePattern.CurrentValue ?? "";
            if (current.Length > InputMonitor.MaximumInputTextCharacters) return false;
            value = current;
            return true;
        }

        if (element.GetCurrentPattern(UiaTextEditPatternId) is not UIA.IUIAutomationTextEditPattern textEdit)
            return false;

        // Same limits as the Edit TextPattern path: inspect the read-only attribute
        // at a single insertion point and bound the text read.
        var documentRange = textEdit.DocumentRange;
        var insertionPoint = documentRange.Clone();
        insertionPoint.MoveEndpointByRange(UIA.TextPatternRangeEndpoint.TextPatternRangeEndpoint_End,
            insertionPoint, UIA.TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start);
        if (insertionPoint.GetAttributeValue(UiaIsReadOnlyAttributeId) is bool isReadOnly && isReadOnly)
            return false;

        var text = documentRange.GetText(InputMonitor.MaximumInputTextCharacters + 1) ?? "";
        if (text.Length > InputMonitor.MaximumInputTextCharacters) return false;
        value = text;
        return true;
    }

    private static bool IsCaretInside(PixelRect caret, PixelRect element)
        => caret.X + caret.Width >= element.X - CaretTolerancePixels
            && caret.X <= element.X + element.Width + CaretTolerancePixels
            && caret.Y + caret.Height >= element.Y - CaretTolerancePixels
            && caret.Y <= element.Y + element.Height + CaretTolerancePixels;

    private static string GetControlTypeName(int controlType) => controlType switch
    {
        UiaCustomControlTypeId => "Custom",
        UiaGroupControlTypeId => "Group",
        UiaPaneControlTypeId => "Pane",
        _ => controlType.ToString()
    };

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

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool ClientToScreen(IntPtr hWnd, ref NativePoint point);
    [DllImport("oleacc.dll", PreserveSig = true)] private static extern int AccessibleObjectFromWindow(IntPtr hwnd, uint objectId, ref Guid interfaceId, [MarshalAs(UnmanagedType.Interface)] out Accessibility.IAccessible accessibleObject);
}
