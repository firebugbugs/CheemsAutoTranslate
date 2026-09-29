using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CursorTranslator.Models;
using CursorTranslator.Services;
using System.Diagnostics;
using System.Globalization;

namespace CursorTranslator;

public partial class MainWindow : Window
{
    private readonly SettingsStore _settingsStore = new();
    private readonly CardAppearanceStore _cardAppearanceStore = new();
    private readonly TranslationService _translation = new();
    private readonly SpeechSynthesisService _speechSynthesis = new();
    private readonly StartupRegistrationService _startupRegistration = new();
    private readonly DispatcherTimer _autoSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly InputMonitor _monitor;
    private readonly TranslationOverlay _overlay;
    private AppSettings _settings;
    private CardAppearanceSettings _cardAppearance;
    private AiAnswerWindow? _aiAnswerWindow;
    private CancellationTokenSource? _aiQuestionCancellation;
    private long _latestAiQuestion;
    private bool _isMonitoring;
    private bool _allowClose;
    private bool _updatingTriggerControls;
    private bool _normalizingMaximumTranslationCharacters;
    private bool _apiKeyVisible;
    private bool _speechApiKeyVisible;
    private long _translationGeneration;
    private long _speechGeneration;
    private CancellationTokenSource? _translationCancellation;
    private CancellationTokenSource? _speechCancellation;
    private MonitoredText? _queuedTranslation;
    private bool _translationWorkerRunning;

    public MainWindow()
    {
        InitializeComponent();
        _settings = _settingsStore.Load();
        EndpointBox.Text = _settings.Endpoint;
        ModelBox.Text = _settings.Model;
        ApiKeyBox.Text = _settings.ApiKey;
        SpeechEndpointBox.Text = _settings.SpeechEndpoint;
        SpeechModelBox.Text = _settings.SpeechModel;
        SpeechApiKeyBox.Text = _settings.SpeechApiKey;
        SpeechVoiceBox.Text = _settings.SpeechVoice;
        RealTimeSpeechCheckBox.IsChecked = _settings.RealTimeSpeechEnabled;
        SystemPromptBox.Text = _settings.SystemPrompt;
        InitializeTriggerControls();
        MaximumTranslationCharactersBox.Text = Math.Clamp(
            _settings.MaximumTranslationCharacters,
            AppSettings.MinimumMaximumTranslationCharacters,
            AppSettings.MaximumMaximumTranslationCharacters).ToString(CultureInfo.InvariantCulture);
        UpdateStartupButton();
        SetStatus(_settings.IsConfigured ? "就绪" : "设置模型");
        _cardAppearance = _cardAppearanceStore.Load();
        _overlay = new TranslationOverlay();
        _overlay.ApplyAppearance(_cardAppearance);
        _overlay.AppearanceChanged += OnCardAppearanceChanged;
        _overlay.AiQuestionRequested += OnAiQuestionRequested;
        _overlay.SpeechRequested += OnSpeechRequested;
        _monitor = new InputMonitor();
        ApplyTranslationTriggerSettings();
        _monitor.TextCommitted += OnTextCommitted;
        _monitor.InputCleared += OnInputCleared;
        _monitor.Diagnostic += message => Dispatcher.UIThread.Post(() =>
        {
            MonitorStateText.Text = _isMonitoring ? "监控中" : "已退出";
            SetStatus(message);
        });
        _monitor.Error += message => Dispatcher.UIThread.Post(() =>
        {
            MonitorStateText.Text = "监控异常";
            SetStatus(message);
        });
        Closed += (_, _) => StopMonitoring();
        Closing += (_, e) =>
        {
            _autoSaveTimer.Stop();
            SaveSettings();
            if (_allowClose) return;
            e.Cancel = true;
            ShowInTaskbar = false;
            Hide();
        };
        _autoSaveTimer.Tick += AutoSaveTimer_Tick;
        AttachAutoSaveHandlers();
    }

    public void ShowSettingsWindow()
    {
        ShowInTaskbar = true;
        if (!IsVisible) Show();
        WindowState = Avalonia.Controls.WindowState.Normal;
        Activate();
    }

    public void CloseForExit()
    {
        _allowClose = true;
        StopMonitoring();
        CancelSpeechPlayback();
        _speechSynthesis.Dispose();
        _aiQuestionCancellation?.Cancel();
        _aiAnswerWindow?.Close();
        Close();
    }

    private void OnCardAppearanceChanged(CardAppearanceSettings settings)
    {
        _cardAppearance = settings;
        _cardAppearanceStore.Save(settings);
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
            popup.ShowError("请先配置模型地址、模型名称和系统提示词。");
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

    private void SaveSettings()
    {
        var inactivityDelaySeconds = _settings.InactivityDelaySeconds;
        var hasValidDelay = decimal.TryParse(
                InactivityDelaySecondsBox.Text?.Trim(),
                NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out var parsedDelay)
            && parsedDelay >= AppSettings.MinimumInactivityDelaySeconds
            && parsedDelay <= AppSettings.MaximumInactivityDelaySeconds;
        if (hasValidDelay)
        {
            inactivityDelaySeconds = parsedDelay;
        }

        var maximumTranslationCharacters = _settings.MaximumTranslationCharacters;
        var hasValidMaximumCharacters = int.TryParse(
                MaximumTranslationCharactersBox.Text?.Trim(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var parsedMaximumCharacters)
            && parsedMaximumCharacters >= AppSettings.MinimumMaximumTranslationCharacters
            && parsedMaximumCharacters <= AppSettings.MaximumMaximumTranslationCharacters;
        if (hasValidMaximumCharacters)
            maximumTranslationCharacters = parsedMaximumCharacters;

        _settings = new AppSettings
        {
            Endpoint = EndpointBox.Text?.Trim() ?? "",
            Model = ModelBox.Text?.Trim() ?? "",
            ApiKey = ApiKeyBox.Text?.Trim() ?? "",
            SpeechEndpoint = SpeechEndpointBox.Text?.Trim() ?? "",
            SpeechModel = SpeechModelBox.Text?.Trim() ?? "",
            SpeechApiKey = SpeechApiKeyBox.Text?.Trim() ?? "",
            SpeechVoice = SpeechVoiceBox.Text?.Trim() ?? "",
            RealTimeSpeechEnabled = RealTimeSpeechCheckBox.IsChecked == true,
            SystemPrompt = SystemPromptBox.Text?.Trim() ?? "",
            TranslateOnTextChange = TranslateOnTextChangeCheckBox.IsChecked == true,
            TranslateOnSentenceEnd = TranslateOnSentenceEndCheckBox.IsChecked == true,
            TranslateAfterInactivity = TranslateAfterInactivityCheckBox.IsChecked == true,
            InactivityDelaySeconds = inactivityDelaySeconds,
            MaximumTranslationCharacters = maximumTranslationCharacters
        };
        ApplyTranslationTriggerSettings();
        _settingsStore.Save(_settings);
        var validationIssues = new List<string>();
        if (!hasValidDelay && TranslateAfterInactivityCheckBox.IsChecked == true)
            validationIssues.Add("停顿秒数请输入 0.5 到 60 之间的数字");
        if (!hasValidMaximumCharacters)
            validationIssues.Add("最多翻译请输入 5 到 1000 之间的整数");
        if (_settings.RealTimeSpeechEnabled && !_settings.IsSpeechConfigured)
            validationIssues.Add("实时语音请配置语音 API 地址、模型名称和音色");
        SetStatus(validationIssues.Count == 0
            ? "设置已自动保存"
            : $"其他设置已自动保存；{string.Join("；", validationIssues)}。");
    }

    private void AttachAutoSaveHandlers()
    {
        EndpointBox.TextChanged += SettingsText_Changed;
        ModelBox.TextChanged += SettingsText_Changed;
        ApiKeyBox.TextChanged += SettingsText_Changed;
        SpeechEndpointBox.TextChanged += SettingsText_Changed;
        SpeechModelBox.TextChanged += SettingsText_Changed;
        SpeechApiKeyBox.TextChanged += SettingsText_Changed;
        SpeechVoiceBox.TextChanged += SettingsText_Changed;
        SystemPromptBox.TextChanged += SettingsText_Changed;
        InactivityDelaySecondsBox.TextChanged += SettingsText_Changed;
        MaximumTranslationCharactersBox.TextChanged += MaximumTranslationCharactersBox_TextChanged;
        MaximumTranslationCharactersBox.TextInput += MaximumTranslationCharactersBox_TextInput;
        MaximumTranslationCharactersBox.LostFocus += MaximumTranslationCharactersBox_LostFocus;
    }

    private void MaximumTranslationCharactersBox_TextInput(object? sender, TextInputEventArgs e)
    {
        if (e.Text is { } input && input.Any(character => !char.IsAsciiDigit(character)))
            e.Handled = true;
    }

    private void MaximumTranslationCharactersBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_normalizingMaximumTranslationCharacters) return;

        var currentText = MaximumTranslationCharactersBox.Text ?? "";
        var caretIndex = Math.Clamp(MaximumTranslationCharactersBox.CaretIndex, 0, currentText.Length);
        var sanitizedText = new string(currentText.Where(char.IsAsciiDigit).Take(4).ToArray());
        var sanitizedCaretIndex = currentText[..caretIndex].Count(char.IsAsciiDigit);
        if (int.TryParse(sanitizedText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            && parsed > AppSettings.MaximumMaximumTranslationCharacters)
        {
            sanitizedText = AppSettings.MaximumMaximumTranslationCharacters.ToString(CultureInfo.InvariantCulture);
            sanitizedCaretIndex = Math.Min(sanitizedCaretIndex, sanitizedText.Length);
        }

        if (!string.Equals(currentText, sanitizedText, StringComparison.Ordinal))
        {
            _normalizingMaximumTranslationCharacters = true;
            MaximumTranslationCharactersBox.Text = sanitizedText;
            MaximumTranslationCharactersBox.CaretIndex = Math.Min(sanitizedCaretIndex, sanitizedText.Length);
            _normalizingMaximumTranslationCharacters = false;
        }

        ScheduleAutoSave();
    }

    private void MaximumTranslationCharactersBox_LostFocus(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var maximumCharacters = int.TryParse(
                MaximumTranslationCharactersBox.Text?.Trim(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var parsedMaximumCharacters)
            ? parsedMaximumCharacters
            : _settings.MaximumTranslationCharacters;
        MaximumTranslationCharactersBox.Text = Math.Clamp(
            maximumCharacters,
            AppSettings.MinimumMaximumTranslationCharacters,
            AppSettings.MaximumMaximumTranslationCharacters).ToString(CultureInfo.InvariantCulture);
    }

    private void SettingsText_Changed(object? sender, TextChangedEventArgs e)
        => ScheduleAutoSave();

    private void ScheduleAutoSave()
    {
        _autoSaveTimer.Stop();
        _autoSaveTimer.Start();
    }

    private void AutoSaveTimer_Tick(object? sender, EventArgs e)
    {
        _autoSaveTimer.Stop();
        SaveSettings();
    }

    private void ToggleApiKeyVisibility_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _apiKeyVisible = !_apiKeyVisible;
        ApiKeyBox.PasswordChar = _apiKeyVisible ? '\0' : '●';
        ApiKeyEyeSlash.IsVisible = _apiKeyVisible;
        Avalonia.Controls.ToolTip.SetTip(
            ApiKeyVisibilityButton,
            _apiKeyVisible ? "隐藏 API Key" : "显示 API Key");
    }

    private void ToggleSpeechApiKeyVisibility_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _speechApiKeyVisible = !_speechApiKeyVisible;
        SpeechApiKeyBox.PasswordChar = _speechApiKeyVisible ? '\0' : '●';
        SpeechApiKeyEyeSlash.IsVisible = _speechApiKeyVisible;
        Avalonia.Controls.ToolTip.SetTip(
            SpeechApiKeyVisibilityButton,
            _speechApiKeyVisible ? "隐藏 API Key" : "显示 API Key");
    }

    private void RealTimeSpeech_Changed(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ScheduleAutoSave();
        if (RealTimeSpeechCheckBox.IsChecked != true)
            CancelSpeechPlayback();
    }

    private void Startup_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var enable = StartupToggle.IsChecked == true;
            _startupRegistration.SetEnabled(enable);
            UpdateStartupButton();
            SetStatus(enable ? "已启用随系统启动" : "已关闭随系统启动");
        }
        catch (Exception ex)
        {
            UpdateStartupButton();
            SetStatus($"启动项设置失败：{ex.Message}");
        }
    }

    private void UpdateStartupButton()
    {
        try
        {
            var enabled = _startupRegistration.IsEnabled;
            StartupToggle.IsChecked = enabled;
            StartupToggleTrack.Background = new SolidColorBrush(Avalonia.Media.Color.Parse(enabled ? "#B65D3B" : "#E8DED4"));
            StartupToggleTrack.BorderBrush = new SolidColorBrush(Avalonia.Media.Color.Parse(enabled ? "#A95032" : "#D7C8BA"));
            StartupToggleThumb.HorizontalAlignment = enabled
                ? Avalonia.Layout.HorizontalAlignment.Right
                : Avalonia.Layout.HorizontalAlignment.Left;
            StartupStateText.Text = enabled ? "开" : "关";
            StartupStateText.Foreground = new SolidColorBrush(Avalonia.Media.Color.Parse(enabled ? "#A95032" : "#756B63"));
        }
        catch
        {
            StartupToggle.IsChecked = false;
            StartupToggleTrack.Background = new SolidColorBrush(Avalonia.Media.Color.Parse("#E8DED4"));
            StartupToggleTrack.BorderBrush = new SolidColorBrush(Avalonia.Media.Color.Parse("#D7C8BA"));
            StartupToggleThumb.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
            StartupStateText.Text = "—";
            StartupStateText.Foreground = new SolidColorBrush(Avalonia.Media.Color.Parse("#756B63"));
        }
    }

    private void SetStatus(string message)
    {
        StatusText.Text = message;
        Avalonia.Controls.ToolTip.SetTip(StatusText, message);
    }

    private void WindowSurface_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            || e.Source is not Avalonia.Controls.Control source
            || IsInteractiveControl(source))
            return;

        BeginMoveDrag(e);
        e.Handled = true;
    }

    private static bool IsInteractiveControl(Avalonia.Controls.Control source)
        => source.FindAncestorOfType<Avalonia.Controls.Button>(includeSelf: true) is not null
            || source.FindAncestorOfType<ToggleButton>(includeSelf: true) is not null
            || source.FindAncestorOfType<Avalonia.Controls.TextBox>(includeSelf: true) is not null
            || source.FindAncestorOfType<Avalonia.Controls.ComboBox>(includeSelf: true) is not null
            || source.FindAncestorOfType<Slider>(includeSelf: true) is not null
            || source.FindAncestorOfType<Avalonia.Controls.NumericUpDown>(includeSelf: true) is not null;

    private void MinimizeWindow_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => WindowState = Avalonia.Controls.WindowState.Minimized;

    private void ToggleWindowState_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        WindowState = WindowState == Avalonia.Controls.WindowState.Maximized
            ? Avalonia.Controls.WindowState.Normal
            : Avalonia.Controls.WindowState.Maximized;
        var maximized = WindowState == Avalonia.Controls.WindowState.Maximized;
        MaximizeIcon.IsVisible = !maximized;
        RestoreIcon.IsVisible = maximized;
        Avalonia.Controls.ToolTip.SetTip(
            MaximizeButton,
            maximized ? "还原" : "最大化");
    }

    private void CloseWindow_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => Close();

    private async void ShowAbout_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await new AboutWindow().ShowDialog(this);

    private void InitializeTriggerControls()
    {
        _updatingTriggerControls = true;
        TranslateOnTextChangeCheckBox.IsChecked = _settings.TranslateOnTextChange;
        TranslateOnSentenceEndCheckBox.IsChecked = _settings.TranslateOnSentenceEnd;
        TranslateAfterInactivityCheckBox.IsChecked = _settings.TranslateAfterInactivity;
        InactivityDelaySecondsBox.Text = Math.Clamp(
            _settings.InactivityDelaySeconds,
            AppSettings.MinimumInactivityDelaySeconds,
            AppSettings.MaximumInactivityDelaySeconds).ToString("0.##", CultureInfo.InvariantCulture);

        if (!TranslateOnTextChangeCheckBox.IsChecked.GetValueOrDefault()
            && !TranslateOnSentenceEndCheckBox.IsChecked.GetValueOrDefault()
            && !TranslateAfterInactivityCheckBox.IsChecked.GetValueOrDefault())
            TranslateOnSentenceEndCheckBox.IsChecked = true;

        if (TranslateOnTextChangeCheckBox.IsChecked == true)
        {
            TranslateOnSentenceEndCheckBox.IsChecked = false;
            TranslateAfterInactivityCheckBox.IsChecked = false;
        }
        _updatingTriggerControls = false;
        RefreshTriggerControls();
    }

    private void TranslationTriggerOption_Changed(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_updatingTriggerControls) return;

        _updatingTriggerControls = true;
        if (ReferenceEquals(sender, TranslateOnTextChangeCheckBox)
            && TranslateOnTextChangeCheckBox.IsChecked == true)
        {
            TranslateOnSentenceEndCheckBox.IsChecked = false;
            TranslateAfterInactivityCheckBox.IsChecked = false;
        }
        else if (sender is Avalonia.Controls.CheckBox changedCheckBox && changedCheckBox.IsChecked == true)
        {
            TranslateOnTextChangeCheckBox.IsChecked = false;
        }

        if (TranslateOnTextChangeCheckBox.IsChecked != true
            && TranslateOnSentenceEndCheckBox.IsChecked != true
            && TranslateAfterInactivityCheckBox.IsChecked != true)
            TranslateOnSentenceEndCheckBox.IsChecked = true;

        _updatingTriggerControls = false;
        RefreshTriggerControls();
        ScheduleAutoSave();
    }

    private void RefreshTriggerControls()
    {
        var immediateMode = TranslateOnTextChangeCheckBox.IsChecked == true;
        TranslateOnSentenceEndCheckBox.IsEnabled = !immediateMode;
        TranslateAfterInactivityCheckBox.IsEnabled = !immediateMode;
        InactivityDelaySecondsBox.IsEnabled = !immediateMode && TranslateAfterInactivityCheckBox.IsChecked == true;
    }

    private void ApplyTranslationTriggerSettings()
    {
        _settings.TranslateOnTextChange = TranslateOnTextChangeCheckBox.IsChecked == true;
        _settings.TranslateOnSentenceEnd = TranslateOnSentenceEndCheckBox.IsChecked == true;
        _settings.TranslateAfterInactivity = TranslateAfterInactivityCheckBox.IsChecked == true;
        _monitor.ConfigureTranslationTriggers(
            _settings.TranslateOnTextChange,
            _settings.TranslateOnSentenceEnd,
            _settings.TranslateAfterInactivity,
            (int)(_settings.InactivityDelaySeconds * 1000m));
    }

    private async void TranslateTest_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        SaveSettings();
        var text = TestInputBox.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            SetStatus("请先输入文本");
            return;
        }

        TestButton.IsEnabled = false;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var translated = await _translation.TranslateAsync(
                _settings,
                text,
                CancellationToken.None,
                applyMaximumTranslationLimit: false);
            TestResultText.Text = translated;
            TestSpeakButton.IsEnabled = !string.IsNullOrWhiteSpace(translated);
            stopwatch.Stop();
            SetStatus($"试译完成 · AI计算耗时：{stopwatch.Elapsed.TotalMilliseconds:F0} ms");
            if (RealTimeSpeechCheckBox.IsChecked == true)
                StartSpeechPlayback(translated);
        }
        catch (Exception ex)
        {
            SetStatus($"翻译失败：{ex.Message}");
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }

    private void TestSpeak_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var translated = TestResultText.Text;
        if (!string.IsNullOrWhiteSpace(translated))
            StartSpeechPlayback(translated);
    }

    public void StartMonitoring()
    {
        if (_isMonitoring) return;
        SaveSettings();
        _isMonitoring = true;
        _monitor.Start();
        MonitorStateText.Text = "监控中";
        SetStatus(_settings.IsConfigured ? "就绪" : "设置模型");
    }

    private void StopMonitoring()
    {
        _isMonitoring = false;
        CancelTranslationQueue();
        _monitor.Stop();
        _overlay.HideOverlay();
        MonitorStateText.Text = "已退出";
    }

    private void OnSpeechRequested(string text)
        => Dispatcher.UIThread.Post(() => StartSpeechPlayback(text), DispatcherPriority.Background);

    private void StartSpeechPlayback(string text)
    {
        CancelSpeechPlayback();
        var settings = new AppSettings
        {
            SpeechEndpoint = SpeechEndpointBox.Text?.Trim() ?? "",
            SpeechModel = SpeechModelBox.Text?.Trim() ?? "",
            SpeechApiKey = SpeechApiKeyBox.Text?.Trim() ?? "",
            SpeechVoice = SpeechVoiceBox.Text?.Trim() ?? ""
        };
        if (!settings.IsSpeechConfigured)
        {
            SetStatus("请先配置语音模型地址、模型名称和音色");
            return;
        }

        var cancellation = new CancellationTokenSource();
        _speechCancellation = cancellation;
        var generation = Interlocked.Read(ref _speechGeneration);
        _ = SynthesizeAndPlayAsync(settings, text, cancellation, generation);
    }

    private async Task SynthesizeAndPlayAsync(
        AppSettings settings,
        string text,
        CancellationTokenSource cancellation,
        long generation)
    {
        try
        {
            await _speechSynthesis.SpeakAsync(settings, text, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (generation == Interlocked.Read(ref _speechGeneration))
                Dispatcher.UIThread.Post(() =>
                {
                    if (generation == Interlocked.Read(ref _speechGeneration))
                        SetStatus($"语音播报失败：{ex.Message}");
                });
        }
        finally
        {
            if (ReferenceEquals(_speechCancellation, cancellation))
                _speechCancellation = null;
            cancellation.Dispose();
        }
    }

    private void CancelSpeechPlayback()
    {
        Interlocked.Increment(ref _speechGeneration);
        var cancellation = _speechCancellation;
        _speechCancellation = null;
        cancellation?.Cancel();
        _speechSynthesis.Stop();
    }

    private void OnInputCleared() => Dispatcher.UIThread.Post(() =>
    {
        _overlay.HideOverlay();
    });

    private void CancelTranslationQueue()
    {
        Interlocked.Increment(ref _translationGeneration);
        _queuedTranslation = null;
        _translationCancellation?.Cancel();
    }

    private void OnTextCommitted(MonitoredText text) => Dispatcher.UIThread.Post(() =>
    {
        if (!_settings.IsConfigured)
        {
            SetStatus("请配置模型连接");
            return;
        }

        // Keep at most one waiting item. A newer commit replaces the older queued text;
        // the translation already in progress is allowed to finish.
        _queuedTranslation = text;
        SetStatus(_translationWorkerRunning
            ? "排队中"
            : "正在翻译");
        StartTranslationWorker();
    });

    private void StartTranslationWorker()
    {
        if (_translationWorkerRunning || _queuedTranslation is null || !_isMonitoring)
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
            while (_queuedTranslation is not null && _isMonitoring)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!_isMonitoring) break;

                // Keep waiting input replaceable until the request actually starts.
                // While one request is in flight, incoming text replaces this single slot.
                var text = _queuedTranslation;
                _queuedTranslation = null;
                if (text is null) continue;

                var generation = Interlocked.Read(ref _translationGeneration);
                var stopwatch = Stopwatch.StartNew();
                try
                {
                    var translated = await _translation.TranslateAsync(_settings, text.Text, cancellationToken);
                    stopwatch.Stop();
                    if (generation == Interlocked.Read(ref _translationGeneration)
                        && _queuedTranslation is null && _isMonitoring)
                    {
                        _overlay.ShowAt(text.Bounds, translated);
                        SetStatus($"翻译完成 · AI计算耗时：{stopwatch.Elapsed.TotalMilliseconds:F0} ms");
                        if (RealTimeSpeechCheckBox.IsChecked == true)
                            StartSpeechPlayback(translated);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (generation == Interlocked.Read(ref _translationGeneration)
                        && _queuedTranslation is null && _isMonitoring)
                        SetStatus($"翻译失败：{ex.Message}");
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
            if (_queuedTranslation is not null && _isMonitoring)
                StartTranslationWorker();
        }
    }
}
