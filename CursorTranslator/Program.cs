using Avalonia;
using CursorTranslator.Services;
using System.Runtime.InteropServices;

namespace CursorTranslator;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        AppLog.Initialize();
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
        {
            var exception = eventArgs.ExceptionObject as Exception;
            AppLog.Error(
                "Unhandled exception",
                $"Process-level exception; terminating={eventArgs.IsTerminating}.",
                exception);
        };
        TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
            AppLog.Error("Unobserved task", "A background task fault was not observed.", eventArgs.Exception);
        System.Windows.Forms.Application.ThreadException += (_, eventArgs) =>
            AppLog.Error("Windows Forms UI", "Unhandled UI thread exception.", eventArgs.Exception);

        using var singleInstanceMutex = new Mutex(true, @"Local\CursorTranslator.SingleInstance", out var isFirstInstance);
        if (!isFirstInstance) return;

        AppLog.Info("Application", "Process started.");
        AppLog.Info("Runtime diagnostics",
            $"Application version={UpdateService.CurrentVersionLabel}; framework={RuntimeInformation.FrameworkDescription}; " +
            $"OS={RuntimeInformation.OSDescription}; architecture={RuntimeInformation.ProcessArchitecture}; " +
            $"process64Bit={Environment.Is64BitProcess}.");
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            AppLog.Info("Application", "Process exiting.");
            singleInstanceMutex.ReleaseMutex();
        }
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
}
