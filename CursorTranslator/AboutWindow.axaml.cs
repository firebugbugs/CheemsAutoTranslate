using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace CursorTranslator;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
    }

    private void Surface_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            || e.Source is not Avalonia.Controls.Control source
            || source.FindAncestorOfType<Avalonia.Controls.Button>(includeSelf: true) is not null)
            return;

        BeginMoveDrag(e);
        e.Handled = true;
    }

    private void Close_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => Close();

    private static void OpenLink_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Avalonia.Controls.Button { Tag: string url } button)
            return;

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            Avalonia.Controls.ToolTip.SetTip(button, "无法打开网页，请检查默认浏览器设置。");
        }
    }
}
