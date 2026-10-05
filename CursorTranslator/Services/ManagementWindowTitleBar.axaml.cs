using Avalonia.Controls;
using Avalonia.Input;

namespace CursorTranslator.Services;

public partial class ManagementWindowTitleBar : Avalonia.Controls.UserControl
{
    private string _title = "";

    public ManagementWindowTitleBar()
    {
        InitializeComponent();
    }

    public string Title
    {
        get => _title;
        set
        {
            _title = value;
            TitleTextBlock.Text = value;
        }
    }

    private Window? OwnerWindow => TopLevel.GetTopLevel(this) as Window;

    private void Minimize_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (OwnerWindow is { } window)
            window.WindowState = WindowState.Minimized;
    }

    private void Maximize_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (OwnerWindow is not { } window) return;
        window.WindowState = window.WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
        var maximized = window.WindowState == WindowState.Maximized;
        MaximizeIcon.IsVisible = !maximized;
        RestoreIcon.IsVisible = maximized;
        Avalonia.Controls.ToolTip.SetTip(MaximizeButton, maximized ? "还原" : "最大化");
    }

    private void CloseWindow_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => OwnerWindow?.Close(false);

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (OwnerWindow is { } window)
            WindowChrome.BeginMoveDrag(window, e);
    }
}
