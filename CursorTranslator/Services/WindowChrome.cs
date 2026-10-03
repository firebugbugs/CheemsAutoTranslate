using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.VisualTree;

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
            || source.FindAncestorOfType<ToggleButton>(includeSelf: true) is not null
            || source.FindAncestorOfType<Avalonia.Controls.TextBox>(includeSelf: true) is not null
            || source.FindAncestorOfType<Avalonia.Controls.ComboBox>(includeSelf: true) is not null
            || source.FindAncestorOfType<Slider>(includeSelf: true) is not null
            || source.FindAncestorOfType<Avalonia.Controls.NumericUpDown>(includeSelf: true) is not null;
}
