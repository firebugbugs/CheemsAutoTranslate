using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Forms = System.Windows.Forms;

namespace CursorTranslator;

public partial class App : Avalonia.Application
{
    private Forms.NotifyIcon? _trayIcon;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnExplicitShutdown;

            var mainWindow = new MainWindow
            {
                ShowInTaskbar = false,
                WindowState = Avalonia.Controls.WindowState.Minimized
            };
            var hideOnFirstOpen = true;
            mainWindow.Opened += (_, _) =>
            {
                if (!hideOnFirstOpen) return;
                hideOnFirstOpen = false;
                mainWindow.Hide();
            };

            var menu = new Forms.ContextMenuStrip();
            var openSettingsItem = new Forms.ToolStripMenuItem("打开设置");
            var monitoringItem = new Forms.ToolStripMenuItem("暂停监听");
            var exitItem = new Forms.ToolStripMenuItem("退出");
            menu.Items.Add(openSettingsItem);
            menu.Items.Add(monitoringItem);
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(exitItem);

            _trayIcon = new Forms.NotifyIcon
            {
                Icon = System.Drawing.SystemIcons.Application,
                Text = "光标翻译",
                ContextMenuStrip = menu,
                Visible = true
            };
            _trayIcon.DoubleClick += (_, _) => Dispatcher.UIThread.Post(mainWindow.ShowSettingsWindow);
            openSettingsItem.Click += (_, _) => Dispatcher.UIThread.Post(mainWindow.ShowSettingsWindow);
            monitoringItem.Click += (_, _) => Dispatcher.UIThread.Post(mainWindow.ToggleMonitoring);
            exitItem.Click += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                mainWindow.CloseForExit();
                desktop.Shutdown();
            });
            mainWindow.MonitoringChanged += enabled => Dispatcher.UIThread.Post(() =>
                monitoringItem.Text = enabled ? "暂停监听" : "恢复监听");

            desktop.MainWindow = mainWindow;
            mainWindow.StartMonitoring();
            desktop.Exit += (_, _) =>
            {
                if (_trayIcon is not null)
                {
                    _trayIcon.Visible = false;
                    _trayIcon.Dispose();
                    _trayIcon = null;
                }
                menu.Dispose();
            };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
