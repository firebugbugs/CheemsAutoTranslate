using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using CursorTranslator.Models;
using CursorTranslator.Services;

namespace CursorTranslator;

public partial class MainWindow : Window
{
    private readonly SettingsStore _settingsStore = new();
    private readonly CardAppearanceStore _cardAppearanceStore = new();
    private readonly TranslationService _translation = new();
    private readonly InputMonitor _monitor;
    private readonly TranslationOverlay _overlay;
    private AppSettings _settings;
    private CardAppearanceSettings _cardAppearance;
    private CardSettingsWindow? _cardSettingsWindow;
    private AiAnswerWindow? _aiAnswerWindow;
    private CancellationTokenSource? _aiQuestionCancellation;
    private long _latestAiQuestion;
    private bool _isMonitoring;
    private bool _allowClose;
    private long _latestCommittedTranslation;
    private CancellationTokenSource? _translationCancellation;
    private DateTimeOffset _lastTranslationStartedAt = DateTimeOffset.MinValue;
    private MonitoredText? _pendingTranslation;
    private bool _translationWorkerRunning;
    private static readonly TimeSpan MinimumTranslationInterval = TimeSpan.FromSeconds(2);

    public MainWindow()
    {
        InitializeComponent();
        _settings = _settingsStore.Load();
        EndpointBox.Text = _settings.Endpoint;
        ModelBox.Text = _settings.Model;
        ApiKeyBox.Text = _settings.ApiKey;
        SystemPromptBox.Text = _settings.SystemPrompt;
        _cardAppearance = _cardAppearanceStore.Load();
        _overlay = new TranslationOverlay();
        _overlay.ApplyAppearance(_cardAppearance);
        _overlay.SettingsRequested += ShowCardSettings;
        _overlay.AiQuestionRequested += OnAiQuestionRequested;
        _monitor = new InputMonitor();
        _monitor.TextCommitted += OnTextCommitted;
        _monitor.InputCleared += OnInputCleared;
        _monitor.StateChanged += OnMonitorStateChanged;
        _monitor.TargetLost += OnTargetLost;
        _monitor.Diagnostic += message => Dispatcher.UIThread.Post(() => StatusText.Text = message);
        _monitor.Error += message => Dispatcher.UIThread.Post(() => StatusText.Text = message);
        Closed += (_, _) => StopMonitoring();
        Closing += (_, e) =>
        {
            if (_allowClose) return;
            e.Cancel = true;
            ShowInTaskbar = false;
            Hide();
        };
    }

    public event Action<bool>? MonitoringChanged;

    public void ShowSettingsWindow()
    {
        ShowInTaskbar = true;
        if (!IsVisible) Show();
        WindowState = Avalonia.Controls.WindowState.Normal;
        Activate();
    }

    public void ToggleMonitoring()
    {
        if (_isMonitoring)
            StopMonitoring();
        else
            StartMonitoring();
    }

    public void CloseForExit()
    {
        _allowClose = true;
        StopMonitoring();
        _cardSettingsWindow?.Close();
        _aiQuestionCancellation?.Cancel();
        _aiAnswerWindow?.Close();
        Close();
    }

    private void ShowCardSettings()
    {
        if (_cardSettingsWindow is { IsVisible: true } existing)
        {
            existing.Topmost = true;
            existing.Activate();
            return;
        }

        try
        {
            // The translation card is always-on-top and does not activate itself.
            // Lower it while the dedicated settings window is open so it cannot cover it.
            _overlay.Topmost = false;
            var settingsWindow = new CardSettingsWindow(_cardAppearance)
            {
                ShowActivated = true,
                ShowInTaskbar = true,
                Topmost = true
            };
            _cardSettingsWindow = settingsWindow;
            settingsWindow.SettingsApplied += settings =>
            {
                _cardAppearance = settings;
                _cardAppearanceStore.Save(settings);
                _overlay.ApplyAppearance(settings);
            };
            settingsWindow.Closed += (_, _) =>
            {
                _overlay.Topmost = true;
                if (ReferenceEquals(_cardSettingsWindow, settingsWindow))
                    _cardSettingsWindow = null;
            };
            settingsWindow.Show();
            settingsWindow.Activate();
        }
        catch (Exception ex)
        {
            _overlay.Topmost = true;
            _cardSettingsWindow = null;
            StatusText.Text = $"无法打开翻译卡片设置：{ex.Message}";
        }
    }

    private void OnAiQuestionRequested(string text)
        => Dispatcher.UIThread.Post(() => _ = AskAiAboutTextAsync(text), DispatcherPriority.Background);

    private async Task AskAiAboutTextAsync(string text)
    {
        if (_aiAnswerWindow is null)
        {
            var answerWindow = new AiAnswerWindow();
            _aiAnswerWindow = answerWindow;
            answerWindow.Closed += (_, _) =>
            {
                if (!ReferenceEquals(_aiAnswerWindow, answerWindow)) return;
                _aiAnswerWindow = null;
                _aiQuestionCancellation?.Cancel();
            };
        }

        var popup = _aiAnswerWindow;
        if (!_settings.IsConfigured)
        {
            popup.ShowPending(_overlay);
            popup.ShowError("请先配置模型地址、模型名称和 API Key。");
            return;
        }

        _aiQuestionCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _aiQuestionCancellation = cancellation;
        var requestId = Interlocked.Increment(ref _latestAiQuestion);
        popup.ShowPending(_overlay);

        try
        {
            var answer = await _translation.ExplainMeaningAsync(_settings, text, cancellation.Token);
            if (requestId == Interlocked.Read(ref _latestAiQuestion)
                && ReferenceEquals(_aiAnswerWindow, popup) && popup.IsVisible)
                popup.ShowAnswer(answer);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (requestId == Interlocked.Read(ref _latestAiQuestion)
                && ReferenceEquals(_aiAnswerWindow, popup) && popup.IsVisible)
                popup.ShowError(ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_aiQuestionCancellation, cancellation))
                _aiQuestionCancellation = null;
            cancellation.Dispose();
        }
    }

    private void SaveSettings_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _settings = new AppSettings
        {
            Endpoint = EndpointBox.Text?.Trim() ?? "",
            Model = ModelBox.Text?.Trim() ?? "",
            ApiKey = ApiKeyBox.Text ?? "",
            SystemPrompt = SystemPromptBox.Text?.Trim() ?? ""
        };
        _settingsStore.Save(_settings);
        StatusText.Text = "连接设置已保存。API Key 已使用当前 Windows 用户的 DPAPI 保护。";
    }

    private async void TranslateTest_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        SaveSettings_Click(sender, e);
        var text = TestInputBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            StatusText.Text = "先输入一段要翻译的中文。";
            return;
        }

        TestButton.IsEnabled = false;
        try
        {
            TestResultText.Text = await _translation.TranslateAsync(_settings, text, CancellationToken.None);
            StatusText.Text = "连接测试完成。";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"翻译失败：{ex.Message}";
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }

    private void Monitor_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_isMonitoring)
            StopMonitoring();
        else
            StartMonitoring();
    }

    public void StartMonitoring()
    {
        SaveSettings_Click(this, new Avalonia.Interactivity.RoutedEventArgs());
        _isMonitoring = true;
        _monitor.Start();
        MonitorButton.Content = "暂停监控";
        MonitorStateText.Text = "● 正在监控（Ctrl+Shift+Space 暂停）";
        MonitoringChanged?.Invoke(true);
        StatusText.Text = _settings.IsConfigured
            ? "监控当前焦点输入框；译文显示在光标附近。"
            : "全局监听已启动；请从托盘打开设置并填写模型连接信息。";
    }

    private void StopMonitoring()
    {
        _isMonitoring = false;
        InvalidatePendingTranslation();
        _monitor.Stop();
        _overlay.Hide();
        MonitoringChanged?.Invoke(false);
        if (MonitorButton is not null)
        {
            MonitorButton.Content = "开始监控";
            MonitorStateText.Text = "● 已暂停";
        }
    }

    private void OnMonitorStateChanged(bool enabled) => Dispatcher.UIThread.Post(() =>
    {
        _isMonitoring = enabled;
        if (!enabled) InvalidatePendingTranslation();
        MonitorButton.Content = enabled ? "暂停监控" : "开始监控";
        MonitorStateText.Text = enabled ? "● 正在监控（Ctrl+Shift+Space 暂停）" : "● 已暂停";
        MonitoringChanged?.Invoke(enabled);
        if (!enabled) _overlay.Hide();
    });

    private void OnInputCleared() => Dispatcher.UIThread.Post(() =>
    {
        InvalidatePendingTranslation();
        _overlay.Hide();
    });

    private void OnTargetLost() => Dispatcher.UIThread.Post(() =>
    {
        InvalidatePendingTranslation();
    });

    private void InvalidatePendingTranslation()
    {
        Interlocked.Increment(ref _latestCommittedTranslation);
        _pendingTranslation = null;
        _translationCancellation?.Cancel();
    }

    private void OnTextCommitted(MonitoredText text) => Dispatcher.UIThread.Post(() =>
    {
        if (!_settings.IsConfigured)
        {
            StatusText.Text = "监听已开启；请从主窗口配置 API 地址、模型、密钥和系统提示词。";
            return;
        }

        _pendingTranslation = text;
        Interlocked.Increment(ref _latestCommittedTranslation);
        StartTranslationWorker();
    });

    private void StartTranslationWorker()
    {
        if (_translationWorkerRunning || _pendingTranslation is null || !_isMonitoring)
            return;

        _translationWorkerRunning = true;
        var cancellation = new CancellationTokenSource();
        _translationCancellation = cancellation;
        _ = ProcessTranslationQueueAsync(cancellation);
    }

    private async Task ProcessTranslationQueueAsync(CancellationTokenSource cancellation)
    {
        var cancellationToken = cancellation.Token;
        try
        {
            while (_pendingTranslation is not null && _isMonitoring)
            {
                var nextAllowedStart = _lastTranslationStartedAt + MinimumTranslationInterval;
                var delay = nextAllowedStart - DateTimeOffset.UtcNow;
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, cancellationToken);

                cancellationToken.ThrowIfCancellationRequested();
                if (!_isMonitoring) break;

                // Coalesce commits received during the throttle window to the latest text.
                var text = _pendingTranslation;
                _pendingTranslation = null;
                if (text is null) continue;

                var requestId = Interlocked.Read(ref _latestCommittedTranslation);
                _lastTranslationStartedAt = DateTimeOffset.UtcNow;
                try
                {
                    var translated = await _translation.TranslateAsync(_settings, text.Text, cancellationToken);
                    if (requestId == Interlocked.Read(ref _latestCommittedTranslation)
                        && _pendingTranslation is null && _isMonitoring)
                    {
                        _overlay.ShowAt(text.Bounds, translated);
                        StatusText.Text = "译文已显示在输入框附近。";
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (requestId == Interlocked.Read(ref _latestCommittedTranslation)
                        && _pendingTranslation is null)
                        StatusText.Text = $"翻译失败：{ex.Message}";
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(_translationCancellation, cancellation))
                _translationCancellation = null;
            cancellation.Dispose();
            _translationWorkerRunning = false;
            if (_pendingTranslation is not null && _isMonitoring)
                StartTranslationWorker();
        }
    }
}
