using Avalonia;

namespace CursorTranslator;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        using var singleInstanceMutex = new Mutex(true, @"Local\CursorTranslator.SingleInstance", out var isFirstInstance);
        if (!isFirstInstance) return;

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            singleInstanceMutex.ReleaseMutex();
        }
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
}
