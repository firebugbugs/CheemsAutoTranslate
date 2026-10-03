using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using CursorTranslator.Services;
using Forms = System.Windows.Forms;
using System.IO;

namespace CursorTranslator;

public partial class App : Avalonia.Application
{
    private Forms.NotifyIcon? _trayIcon;
    private System.Drawing.Icon? _trayIconImage;
    private Stream? _trayIconStream;

    public override void Initialize()
    {
        Dispatcher.UIThread.UnhandledException += (_, eventArgs) =>
            AppLog.Error("Avalonia UI", "Unhandled dispatcher exception.", eventArgs.Exception);
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnExplicitShutdown;
            var showSettingsOnStartup = Environment.GetCommandLineArgs()
                .Skip(1)
                .Any(arg => string.Equals(arg, "--show-settings", StringComparison.OrdinalIgnoreCase));

            var mainWindow = new MainWindow
            {
                ShowInTaskbar = showSettingsOnStartup,
                WindowState = showSettingsOnStartup
                    ? Avalonia.Controls.WindowState.Normal
                    : Avalonia.Controls.WindowState.Minimized
            };
            var hideOnFirstOpen = !showSettingsOnStartup;
            mainWindow.Opened += (_, _) =>
            {
                if (!hideOnFirstOpen) return;
                hideOnFirstOpen = false;
                mainWindow.Hide();
            };

            var menu = new Forms.ContextMenuStrip();
            var openSettingsItem = new Forms.ToolStripMenuItem("打开设置");
            var exitItem = new Forms.ToolStripMenuItem("退出");
            menu.Items.Add(openSettingsItem);
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(exitItem);

            _trayIconStream = AssetLoader.Open(new Uri("avares://CursorTranslator/Assets/translator_icon.ico"));
            _trayIconImage = new System.Drawing.Icon(_trayIconStream);
            _trayIcon = new Forms.NotifyIcon
            {
                Icon = _trayIconImage,
                Text = "Cheems翻译",
                ContextMenuStrip = menu,
                Visible = true
            };
            _trayIcon.DoubleClick += (_, _) => Dispatcher.UIThread.Post(mainWindow.ShowSettingsWindow);
            openSettingsItem.Click += (_, _) => Dispatcher.UIThread.Post(mainWindow.ShowSettingsWindow);
            exitItem.Click += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                mainWindow.CloseForExit();
                desktop.Shutdown();
            });

            desktop.MainWindow = mainWindow;
            mainWindow.StartMonitoring();
            desktop.Exit += (_, _) =>
            {
                AppLog.Info("Application", "Desktop lifetime exited.");
                if (_trayIcon is not null)
                {
                    _trayIcon.Visible = false;
                    _trayIcon.Dispose();
                    _trayIcon = null;
                }
                _trayIconImage?.Dispose();
                _trayIconImage = null;
                _trayIconStream?.Dispose();
                _trayIconStream = null;
                menu.Dispose();
            };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
