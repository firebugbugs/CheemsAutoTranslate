using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Platform;
using Avalonia.VisualTree;
using System.Runtime.InteropServices;

namespace CursorTranslator.Services;

public static class WindowChrome
{
    public static void BeginMoveDrag(Window window, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(window).Properties.IsLeftButtonPressed
            || e.Source is not Avalonia.Controls.Control source
            || IsInteractiveControl(source))
            return;

        window.BeginMoveDrag(e);
        e.Handled = true;
    }

    private static bool IsInteractiveControl(Avalonia.Controls.Control source)
        => source.FindAncestorOfType<Avalonia.Controls.Button>(includeSelf: true) is not null
            || source.FindAncestorOfType<Avalonia.Controls.CheckBox>(includeSelf: true) is not null
            || source.FindAncestorOfType<Avalonia.Controls.RadioButton>(includeSelf: true) is not null
            || source.FindAncestorOfType<ToggleButton>(includeSelf: true) is not null
            || source.FindAncestorOfType<Avalonia.Controls.TextBox>(includeSelf: true) is not null
            || source.FindAncestorOfType<Avalonia.Controls.ComboBox>(includeSelf: true) is not null
            || source.FindAncestorOfType<Slider>(includeSelf: true) is not null
            || source.FindAncestorOfType<Avalonia.Controls.NumericUpDown>(includeSelf: true) is not null
            || source.FindAncestorOfType<Avalonia.Controls.ListBox>(includeSelf: true) is not null
            || source.FindAncestorOfType<Avalonia.Controls.ListBoxItem>(includeSelf: true) is not null
            || source.FindAncestorOfType<Avalonia.Controls.ScrollViewer>(includeSelf: true) is not null
            || source.FindAncestorOfType<Avalonia.Controls.Primitives.ScrollBar>(includeSelf: true) is not null
            || source.FindAncestorOfType<Avalonia.Controls.TabControl>(includeSelf: true) is not null
            || source.FindAncestorOfType<Avalonia.Controls.TabItem>(includeSelf: true) is not null
            || source.FindAncestorOfType<Avalonia.Controls.MenuItem>(includeSelf: true) is not null;
}

public sealed class BorderlessWindowResizeSession
{
    private const uint SetWindowPosNoZOrder = 0x0004;
    private const uint SetWindowPosNoActivate = 0x0010;
    private readonly Window _window;
    private readonly IntPtr _handle;
    private readonly ResizeEdges _edges;
    private readonly NativePoint _startPointer;
    private readonly NativeRect _startWindow;

    private BorderlessWindowResizeSession(
        Window window,
        IntPtr handle,
        ResizeEdges edges,
        NativePoint startPointer,
        NativeRect startWindow)
    {
        _window = window;
        _handle = handle;
        _edges = edges;
        _startPointer = startPointer;
        _startWindow = startWindow;
    }

    public static BorderlessWindowResizeSession? TryBegin(
        Window window,
        PointerPressedEventArgs e,
        string direction)
    {
        if (!OperatingSystem.IsWindows()
            || !window.CanResize
            || !e.GetCurrentPoint(window).Properties.IsLeftButtonPressed)
            return null;

        var edges = direction switch
        {
            "Left" => new ResizeEdges(true, false, false, false),
            "Right" => new ResizeEdges(false, true, false, false),
            "Top" => new ResizeEdges(false, false, true, false),
            "Bottom" => new ResizeEdges(false, false, false, true),
            "TopLeft" => new ResizeEdges(true, false, true, false),
            "TopRight" => new ResizeEdges(false, true, true, false),
            "BottomLeft" => new ResizeEdges(true, false, false, true),
            "BottomRight" => new ResizeEdges(false, true, false, true),
            _ => default
        };
        if (!edges.IsResize)
            return null;

        var handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero
            || !GetCursorPos(out var pointer)
            || !GetWindowRect(handle, out var bounds))
            return null;

        return new BorderlessWindowResizeSession(window, handle, edges, pointer, bounds);
    }

    public void Update()
    {
        if (!GetCursorPos(out var pointer)) return;

        var dx = pointer.X - _startPointer.X;
        var dy = pointer.Y - _startPointer.Y;
        var left = _startWindow.Left;
        var top = _startWindow.Top;
        var right = _startWindow.Right;
        var bottom = _startWindow.Bottom;
        var minWidth = (int)Math.Ceiling(_window.MinWidth * _window.RenderScaling);
        var minHeight = (int)Math.Ceiling(_window.MinHeight * _window.RenderScaling);

        if (_edges.Left) left = Math.Min(_startWindow.Left + dx, right - minWidth);
        if (_edges.Right) right = Math.Max(_startWindow.Right + dx, left + minWidth);
        if (_edges.Top) top = Math.Min(_startWindow.Top + dy, bottom - minHeight);
        if (_edges.Bottom) bottom = Math.Max(_startWindow.Bottom + dy, top + minHeight);

        SetWindowPos(_handle, IntPtr.Zero, left, top, right - left, bottom - top,
            SetWindowPosNoZOrder | SetWindowPosNoActivate);
    }

    private readonly record struct ResizeEdges(bool Left, bool Right, bool Top, bool Bottom)
    {
        public bool IsResize => Left || Right || Top || Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr handle, out NativeRect rect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr handle,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);
}
