using Avalonia;
using Avalonia.Controls;

namespace CursorTranslator.Services;

public static class WindowFrameHelper
{
    public static void Track(Window window, Border surface)
    {
        void UpdateSurface()
        {
            var maximized = window.WindowState == WindowState.Maximized;
            surface.Margin = maximized ? new Thickness(0) : new Thickness(8);
            surface.CornerRadius = maximized ? new CornerRadius(0) : new CornerRadius(14);
            surface.BorderThickness = maximized ? new Thickness(0) : new Thickness(1);
        }

        window.PropertyChanged += (_, args) =>
        {
            if (args.Property == Window.WindowStateProperty)
                UpdateSurface();
        };

        UpdateSurface();
    }
}
