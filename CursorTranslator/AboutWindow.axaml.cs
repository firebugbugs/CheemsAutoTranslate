using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CursorTranslator.Services;

namespace CursorTranslator;

public partial class AboutWindow : Window
{
    private readonly UpdateService _updateService = new();
    private UpdateRelease? _latestRelease;
    private CancellationTokenSource? _downloadCancellation;
    private bool _isChecking;
    private bool _isDownloading;
    private bool _closeAfterDownloadStops;
    private bool _installerHandoffStarted;

    public AboutWindow()
    {
        InitializeComponent();
        CurrentVersionText.Text = $"当前版本 v{UpdateService.CurrentVersionLabel}";
        Opened += async (_, _) => await CheckForUpdatesAsync();
        Closing += OnClosing;
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_installerHandoffStarted || !_isDownloading)
            return;

        // Keep the dialog alive until the response and file handles are closed. Otherwise
        // reopening About can race the previous download and corrupt/reset its .part file.
        e.Cancel = true;
        _closeAfterDownloadStops = true;
        UpdateStatusText.Text = "正在暂停下载，完成后关闭窗口…";
        _downloadCancellation?.Cancel();
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

    private async void UpdateAction_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_isChecking || _isDownloading) return;
        if (_latestRelease is not { IsNewer: true })
        {
            await CheckForUpdatesAsync();
            return;
        }

        if (_latestRelease.DownloadUri is null)
        {
            OpenUrl(_latestRelease.ReleasePage, UpdateActionButton);
            return;
        }

        var state = _updateService.GetDownloadState(_latestRelease);
        if (state.CompletedInstallerPath is not null)
        {
            StartInstaller(state.CompletedInstallerPath);
            return;
        }

        await DownloadAndInstallAsync(_latestRelease);
    }

    private void PauseResume_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_isDownloading)
        {
            PauseResumeButton.IsEnabled = false;
            UpdateStatusText.Text = "正在暂停，已下载内容会保留。";
            _downloadCancellation?.Cancel();
        }
    }

    private async Task CheckForUpdatesAsync()
    {
        if (_isChecking || _isDownloading) return;

        _isChecking = true;
        UpdateActionButton.IsEnabled = false;
        UpdateActionButtonText.Text = "检查中…";
        UpdateStatusText.Text = "正在检查 Gitee 发行版…";
        try
        {
            _latestRelease = await _updateService.CheckLatestReleaseAsync(CancellationToken.None);
            if (_latestRelease is null)
            {
                UpdateStatusText.Text = "Gitee 暂无已发布版本。";
                UpdateActionButtonText.Text = "重新检查";
                UpdateProgressBar.IsVisible = false;
                DownloadProgressText.Text = "";
                PauseResumeButton.IsVisible = false;
            }
            else if (!_latestRelease.IsNewer)
            {
                UpdateStatusText.Text = $"已是最新版本（{FormatVersion(_latestRelease)}）。";
                UpdateActionButtonText.Text = "重新检查";
                UpdateProgressBar.IsVisible = false;
                DownloadProgressText.Text = "";
                PauseResumeButton.IsVisible = false;
            }
            else if (_latestRelease.DownloadUri is null)
            {
                UpdateStatusText.Text = $"发现新版本 {FormatVersion(_latestRelease)}，发行版中没有安装程序。";
                UpdateActionButtonText.Text = "查看发行版";
                UpdateProgressBar.IsVisible = false;
                DownloadProgressText.Text = "";
                PauseResumeButton.IsVisible = false;
            }
            else
            {
                var downloadState = _updateService.GetDownloadState(_latestRelease);
                if (downloadState.CompletedInstallerPath is not null)
                {
                    UpdateStatusText.Text = $"发现新版本 {FormatVersion(_latestRelease)}，安装包已下载。";
                    UpdateActionButtonText.Text = "继续安装";
                    ShowProgress(downloadState.DownloadedBytes, downloadState.TotalBytes);
                }
                else if (downloadState.HasPartial)
                {
                    UpdateStatusText.Text = $"发现新版本 {FormatVersion(_latestRelease)}，可继续上次下载。";
                    UpdateActionButtonText.Text = "继续下载";
                    ShowProgress(downloadState.DownloadedBytes, downloadState.TotalBytes);
                }
                else
                {
                    UpdateStatusText.Text = $"发现新版本 {FormatVersion(_latestRelease)}。";
                    UpdateActionButtonText.Text = "下载并安装";
                    UpdateProgressBar.IsVisible = false;
                    DownloadProgressText.Text = "";
                    PauseResumeButton.IsVisible = false;
                }
            }
        }
        catch (Exception exception)
        {
            AppLog.Error("Update check", "Failed to check for updates.", exception);
            _latestRelease = null;
            UpdateStatusText.Text = exception is HttpRequestException { StatusCode: HttpStatusCode.NotFound }
                ? "无法访问 Gitee 项目，请确认仓库已公开且地址有效。"
                : "检查更新失败，请检查网络后重试。";
            Avalonia.Controls.ToolTip.SetTip(UpdateStatusText, exception.Message);
            UpdateActionButtonText.Text = "重新检查";
            UpdateProgressBar.IsVisible = false;
            DownloadProgressText.Text = "";
            PauseResumeButton.IsVisible = false;
        }
        finally
        {
            _isChecking = false;
            UpdateActionButton.IsEnabled = true;
        }
    }

    private async Task DownloadAndInstallAsync(UpdateRelease release)
    {
        _isDownloading = true;
        _downloadCancellation = new CancellationTokenSource();
        UpdateActionButton.IsEnabled = false;
        UpdateActionButtonText.Text = "正在下载…";
        PauseResumeButton.Content = "暂停";
        PauseResumeButton.IsEnabled = true;
        PauseResumeButton.IsVisible = true;
        UpdateProgressBar.IsVisible = true;
        UpdateProgressBar.IsIndeterminate = false;

        var progress = new Progress<DownloadProgress>(item =>
        {
            ShowProgress(item.DownloadedBytes, item.TotalBytes);
            if (item.IsReplayingExistingBytes)
                UpdateStatusText.Text = "下载源不支持断点续传，正在保留进度并校验已下载部分…";
        });
        try
        {
            var installerPath = await _updateService.DownloadInstallerAsync(
                release,
                progress,
                _downloadCancellation.Token);
            UpdateProgressBar.Value = 100;
            UpdateProgressTextForComplete(new FileInfo(installerPath).Length);
            UpdateStatusText.Text = "下载完成，正在准备安装…";
            PauseResumeButton.IsVisible = false;
            UpdateActionButtonText.Text = "正在安装…";
            await Task.Delay(450, _downloadCancellation.Token);
            _downloadCancellation.Token.ThrowIfCancellationRequested();
            StartInstaller(installerPath);
        }
        catch (OperationCanceledException) when (_downloadCancellation?.IsCancellationRequested == true)
        {
            UpdateStatusText.Text = "下载已暂停，可在此处或下次打开软件后继续。";
            UpdateActionButtonText.Text = "继续下载";
            var state = _updateService.GetDownloadState(release);
            ShowProgress(state.DownloadedBytes, state.TotalBytes);
        }
        catch (Exception exception)
        {
            AppLog.Error("Update download", "Failed to download the installer.", exception);
            UpdateStatusText.Text = "下载中断，已下载内容已保留，可继续重试。";
            Avalonia.Controls.ToolTip.SetTip(UpdateStatusText, exception.Message);
            UpdateActionButtonText.Text = "继续下载";
            var state = _updateService.GetDownloadState(release);
            ShowProgress(state.DownloadedBytes, state.TotalBytes);
        }
        finally
        {
            var closeAfterDownloadStops = _closeAfterDownloadStops;
            _closeAfterDownloadStops = false;
            _downloadCancellation?.Dispose();
            _downloadCancellation = null;
            _isDownloading = false;
            UpdateActionButton.IsEnabled = true;
            PauseResumeButton.IsVisible = false;
            if (closeAfterDownloadStops && !_installerHandoffStarted)
                Dispatcher.UIThread.Post(Close);
        }
    }

    private void ShowProgress(long downloadedBytes, long? totalBytes)
    {
        UpdateProgressBar.IsVisible = true;
        UpdateProgressBar.IsIndeterminate = !totalBytes.HasValue || totalBytes.Value <= 0;
        if (totalBytes is > 0)
            UpdateProgressBar.Value = Math.Clamp(downloadedBytes * 100d / totalBytes.Value, 0, 100);

        DownloadProgressText.Text = totalBytes is > 0
            ? $"{FormatBytes(downloadedBytes)} / {FormatBytes(totalBytes.Value)}"
            : $"已下载 {FormatBytes(downloadedBytes)}";
    }

    private void UpdateProgressTextForComplete(long downloadedBytes)
    {
        UpdateProgressBar.IsIndeterminate = false;
        DownloadProgressText.Text = $"已下载 {FormatBytes(downloadedBytes)}";
    }

    private void StartInstaller(string installerPath)
    {
        try
        {
            _updateService.StartInstallerHandoff(
                installerPath,
                Environment.ProcessId,
                Environment.ProcessPath);
            _installerHandoffStarted = true;
            UpdateStatusText.Text = "更新安装程序已启动，安装完成后会重新打开 Cheems翻译。";
            UpdateActionButton.IsEnabled = false;
            Close();
            if (Owner is MainWindow mainWindow)
                mainWindow.CloseForExit();
            if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown();
        }
        catch (Exception exception)
        {
            AppLog.Error("Update install", "Failed to start the installer handoff.", exception);
            UpdateStatusText.Text = "无法启动自动安装，请从 Gitee 手动下载并安装。";
            Avalonia.Controls.ToolTip.SetTip(UpdateStatusText, exception.Message);
            UpdateActionButton.IsEnabled = true;
            UpdateActionButtonText.Text = "继续安装";
        }
    }

    private static string FormatVersion(UpdateRelease release)
        => release.TagName.StartsWith("v", StringComparison.OrdinalIgnoreCase)
            ? release.TagName
            : $"v{release.Version}";

    private static string FormatBytes(long bytes)
        => bytes >= 1024 * 1024
            ? $"{bytes / (1024d * 1024d):0.#} MB"
            : $"{bytes / 1024d:0.#} KB";

    private static void OpenUrl(Uri url, Avalonia.Controls.Button button)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
        }
        catch
        {
            Avalonia.Controls.ToolTip.SetTip(button, "无法打开网页，请检查默认浏览器设置。");
        }
    }

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
