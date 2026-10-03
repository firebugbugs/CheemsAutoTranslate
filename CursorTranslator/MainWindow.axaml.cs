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
    private readonly UsageStatisticsStore _usageStatisticsStore = new();
    private readonly TranslationService _translation = new();
    private readonly SpeechSynthesisService _speechSynthesis = new();
    private readonly StartupRegistrationService _startupRegistration = new();
    private readonly DispatcherTimer _autoSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _monitorDurationTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Stopwatch _monitorDurationStopwatch = new();
    private readonly InputMonitor _monitor;
    private readonly TranslationOverlay _overlay;
    private AppSettings _settings;
    private CardAppearanceSettings _cardAppearance;
    private AiAnswerWindow? _aiAnswerWindow;
    private CancellationTokenSource? _aiQuestionCancellation;
    private long _latestAiQuestion;
    private bool _isMonitoring;
    private string _monitorStateLabel = "已退出";
    private bool _allowClose;
    private bool _updatingTriggerControls;
    private bool _normalizingMaximumTranslationCharacters;
    private bool _apiKeyVisible;
    private bool _speechApiKeyVisible;
    private bool _updatingTranslationProviderControls;
    private bool _updatingSpeechProviderControls = true;
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
        HttpTranslationProfileComboBox.ItemsSource = _settings.TranslationHttpProfiles;
        HttpTranslationProfileComboBox.SelectedItem = _settings.ActiveTranslationHttpProfile;
        SpeechEndpointBox.Text = _settings.SpeechEndpoint;
        SpeechModelBox.Text = _settings.SpeechModel;
        SpeechApiKeyBox.Text = _settings.SpeechApiKey;
        SpeechVoiceBox.Text = _settings.SpeechVoice;
        HttpSpeechProfileComboBox.ItemsSource = _settings.SpeechHttpProfiles;
        HttpSpeechProfileComboBox.SelectedItem = _settings.ActiveSpeechHttpProfile;
        _updatingSpeechProviderControls = true;
        EnableSpeechAiSwitch.IsChecked = _settings.SpeechProvider == SpeechProviderKind.OpenAiCompatible;
        EnableSpeechHttpSwitch.IsChecked = _settings.SpeechProvider == SpeechProviderKind.GenericHttp;
        SpeechProviderTabs.SelectedItem = _settings.SpeechProvider == SpeechProviderKind.GenericHttp
            ? SpeechHttpProviderTab
            : SpeechAiProviderTab;
        UpdateSpeechProviderTabIndicators();
        _updatingSpeechProviderControls = false;
        RealTimeSpeechCheckBox.IsChecked = _settings.RealTimeSpeechEnabled;
        SystemPromptBox.Text = _settings.SystemPrompt;
        _updatingTranslationProviderControls = true;
        EnableAiProviderSwitch.IsChecked = _settings.TranslationProvider == TranslationProviderKind.OpenAiCompatible;
        EnableHttpProviderSwitch.IsChecked = _settings.TranslationProvider == TranslationProviderKind.HttpTranslation;
        TranslationProviderTabs.SelectedItem = _settings.TranslationProvider == TranslationProviderKind.HttpTranslation
            ? HttpProviderTab
            : AiProviderTab;
        UpdateTranslationProviderTabIndicators();
        _updatingTranslationProviderControls = false;
        InitializeTriggerControls();
        MaximumTranslationCharactersBox.Text = Math.Clamp(
            _settings.MaximumTranslationCharacters,
            AppSettings.MinimumMaximumTranslationCharacters,
            AppSettings.MaximumMaximumTranslationCharacters).ToString(CultureInfo.InvariantCulture);
        UpdateStartupButton();
        SetStatus(_settings.IsConfigured ? "就绪" : "设置翻译接口");
        _cardAppearance = _cardAppearanceStore.Load();
        _overlay = new TranslationOverlay();
        _overlay.ApplyAppearance(_cardAppearance);
        _overlay.AppearanceChanged += OnCardAppearanceChanged;
        _overlay.AiQuestionRequested += OnAiQuestionRequested;
        _overlay.SpeechRequested += OnSpeechRequested;
        _monitor = new InputMonitor();
        _overlay.NativeWindowHandleAvailable += _monitor.SetOverlayWindowHandle;
        _overlay.UserInteraction += _monitor.PreserveTargetForOverlayInteraction;
        ApplyTranslationTriggerSettings();
        _monitor.TextCommitted += OnTextCommitted;
        _monitor.InputCleared += OnInputCleared;
        _monitor.Diagnostic += message => Dispatcher.UIThread.Post(() =>
        {
            if (message.Contains("异常", StringComparison.Ordinal)
                || message.Contains("失败", StringComparison.Ordinal)
                || message.Contains("错误", StringComparison.Ordinal))
                AppLog.Warning("Input monitor", message);
            UpdateMonitorStateText(_isMonitoring ? "监控中" : "已退出");
            SetStatus(message);
        });
        _monitor.Error += message => Dispatcher.UIThread.Post(() =>
        {
            AppLog.Error("Input monitor", message);
            UpdateMonitorStateText("监控异常");
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
        _monitorDurationTimer.Tick += (_, _) => UpdateMonitorStateText();
        AttachAutoSaveHandlers();
        _ = InitializeUsageStatisticsStoreAsync();
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
        if (!_settings.IsAiConfigured)
        {
            popup.ShowPending(_overlay);
            popup.ShowError("AI 释义需要配置 AI 接口地址、模型名称和系统提示词。");
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
            AppLog.Error("AI explanation", "AI explanation request failed.", ex);
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
            TranslationProvider = EnableHttpProviderSwitch.IsChecked == true
                ? TranslationProviderKind.HttpTranslation
                : TranslationProviderKind.OpenAiCompatible,
            TranslationHttpProfiles = _settings.TranslationHttpProfiles,
            ActiveTranslationHttpProfileId = (HttpTranslationProfileComboBox.SelectedItem as TranslationHttpProfile)?.Id ?? "",
            SpeechEndpoint = SpeechEndpointBox.Text?.Trim() ?? "",
            SpeechProvider = EnableSpeechHttpSwitch.IsChecked == true
                ? SpeechProviderKind.GenericHttp
                : SpeechProviderKind.OpenAiCompatible,
            SpeechModel = SpeechModelBox.Text?.Trim() ?? "",
            SpeechApiKey = SpeechApiKeyBox.Text?.Trim() ?? "",
            SpeechVoice = SpeechVoiceBox.Text?.Trim() ?? "",
            HttpSpeechEndpoint = "",
            HttpSpeechModel = "",
            HttpSpeechApiKey = "",
            HttpSpeechVoice = "",
            SpeechHttpProfiles = _settings.SpeechHttpProfiles,
            ActiveSpeechHttpProfileId = (HttpSpeechProfileComboBox.SelectedItem as SpeechHttpProfile)?.Id ?? "",
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
            validationIssues.Add(_settings.SpeechProvider == SpeechProviderKind.GenericHttp
                ? "实时语音请配置有效的 HTTP 接口档案"
                : "实时语音请配置语音 API 地址、模型名称和音色");
        SetStatus(validationIssues.Count == 0
            ? (_settings.IsConfigured ? "设置已自动保存" : "设置已保存 · 当前接口待配置")
            : $"其他设置已自动保存；{string.Join("；", validationIssues)}。");
    }

    private void AttachAutoSaveHandlers()
    {
        EndpointBox.TextChanged += SettingsText_Changed;
        ModelBox.TextChanged += SettingsText_Changed;
        ApiKeyBox.TextChanged += SettingsText_Changed;
        HttpTranslationProfileComboBox.SelectionChanged += HttpTranslationProfile_Changed;
        SpeechEndpointBox.TextChanged += SettingsText_Changed;
        SpeechModelBox.TextChanged += SettingsText_Changed;
        SpeechApiKeyBox.TextChanged += SettingsText_Changed;
        SpeechVoiceBox.TextChanged += SettingsText_Changed;
        HttpSpeechProfileComboBox.SelectionChanged += HttpSpeechProfile_Changed;
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

    private void HttpSpeechProfile_Changed(object? sender, SelectionChangedEventArgs e)
    {
        ScheduleAutoSave();
    }

    private void HttpTranslationProfile_Changed(object? sender, SelectionChangedEventArgs e)
        => ScheduleAutoSave();

    private async void ManageTranslationHttpProfiles_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        SaveSettings();
        var editor = new TranslationHttpProfilesWindow(
            _settings.TranslationHttpProfiles,
            _settings.ActiveTranslationHttpProfileId);
        var saved = await editor.ShowDialog<bool>(this);
        if (!saved) return;

        _settings.TranslationHttpProfiles = editor.Profiles.Select(profile => profile.Copy()).ToList();
        _settings.ActiveTranslationHttpProfileId = editor.SelectedProfileId;
        HttpTranslationProfileComboBox.ItemsSource = null;
        HttpTranslationProfileComboBox.ItemsSource = _settings.TranslationHttpProfiles;
        HttpTranslationProfileComboBox.SelectedItem = _settings.ActiveTranslationHttpProfile;
        SaveSettings();
    }

    private async void ManageSpeechHttpProfiles_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        SaveSettings();
        var editor = new SpeechHttpProfilesWindow(_settings.SpeechHttpProfiles, _settings.ActiveSpeechHttpProfileId);
        var saved = await editor.ShowDialog<bool>(this);
        if (!saved) return;

        _settings.SpeechHttpProfiles = editor.Profiles.Select(profile => profile.Copy()).ToList();
        _settings.ActiveSpeechHttpProfileId = editor.SelectedProfileId;
        HttpSpeechProfileComboBox.ItemsSource = null;
        HttpSpeechProfileComboBox.ItemsSource = _settings.SpeechHttpProfiles;
        HttpSpeechProfileComboBox.SelectedItem = _settings.ActiveSpeechHttpProfile;
        SaveSettings();
    }

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

    private void TranslationProviderSwitch_Changed(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_updatingTranslationProviderControls) return;

        var currentProvider = _settings.TranslationProvider;
        var requestedProvider = sender == EnableHttpProviderSwitch
            ? TranslationProviderKind.HttpTranslation
            : TranslationProviderKind.OpenAiCompatible;
        var requestedSwitch = requestedProvider == TranslationProviderKind.HttpTranslation
            ? EnableHttpProviderSwitch
            : EnableAiProviderSwitch;
        var selectedProvider = requestedSwitch.IsChecked == true
            ? requestedProvider
            : currentProvider == requestedProvider
                ? requestedProvider == TranslationProviderKind.HttpTranslation
                    ? TranslationProviderKind.OpenAiCompatible
                    : TranslationProviderKind.HttpTranslation
                : currentProvider;

        _updatingTranslationProviderControls = true;
        EnableAiProviderSwitch.IsChecked = selectedProvider == TranslationProviderKind.OpenAiCompatible;
        EnableHttpProviderSwitch.IsChecked = selectedProvider == TranslationProviderKind.HttpTranslation;
        TranslationProviderTabs.SelectedItem = selectedProvider == TranslationProviderKind.HttpTranslation
            ? HttpProviderTab
            : AiProviderTab;
        UpdateTranslationProviderTabIndicators();
        _updatingTranslationProviderControls = false;

        _autoSaveTimer.Stop();
        SaveSettings();
    }

    private void UpdateTranslationProviderTabIndicators()
    {
        var httpTranslationSelected = EnableHttpProviderSwitch.IsChecked == true;
        AiProviderCheckMark.IsVisible = !httpTranslationSelected;
        HttpProviderCheckMark.IsVisible = httpTranslationSelected;
        TestProviderLabel.Text = httpTranslationSelected ? "当前：HTTP 接入" : "当前：AI 接入";
    }

    private void SpeechProviderSwitch_Changed(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_updatingSpeechProviderControls) return;

        var currentProvider = _settings.SpeechProvider;
        var requestedProvider = sender == EnableSpeechHttpSwitch
            ? SpeechProviderKind.GenericHttp
            : SpeechProviderKind.OpenAiCompatible;
        var requestedSwitch = requestedProvider == SpeechProviderKind.GenericHttp
            ? EnableSpeechHttpSwitch
            : EnableSpeechAiSwitch;
        var selectedProvider = requestedSwitch.IsChecked == true
            ? requestedProvider
            : currentProvider == requestedProvider
                ? requestedProvider == SpeechProviderKind.GenericHttp
                    ? SpeechProviderKind.OpenAiCompatible
                    : SpeechProviderKind.GenericHttp
                : currentProvider;

        _updatingSpeechProviderControls = true;
        EnableSpeechAiSwitch.IsChecked = selectedProvider == SpeechProviderKind.OpenAiCompatible;
        EnableSpeechHttpSwitch.IsChecked = selectedProvider == SpeechProviderKind.GenericHttp;
        SpeechProviderTabs.SelectedItem = selectedProvider == SpeechProviderKind.GenericHttp
            ? SpeechHttpProviderTab
            : SpeechAiProviderTab;
        UpdateSpeechProviderTabIndicators();
        _updatingSpeechProviderControls = false;

        _autoSaveTimer.Stop();
        SaveSettings();
    }

    private void UpdateSpeechProviderTabIndicators()
    {
        var httpSpeechSelected = EnableSpeechHttpSwitch.IsChecked == true;
        SpeechAiProviderCheckMark.IsVisible = !httpSpeechSelected;
        SpeechHttpProviderCheckMark.IsVisible = httpSpeechSelected;
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
            AppLog.Error("Startup registration", "Failed to update Windows startup registration.", ex);
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
        => WindowChrome.BeginMoveDrag(this, e);

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

    private async void ShowStatistics_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await new DailyStatisticsWindow(_usageStatisticsStore).ShowDialog(this);

    private async void CopyRecentLogs_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null) throw new InvalidOperationException("当前窗口没有可用的剪贴板。");

            var logs = await Task.Run(() => AppLog.GetRecentLogsText());
            await clipboard.SetTextAsync(logs);
            SetStatus($"已复制最近日志 · {logs.Length:N0} 字符");
        }
        catch (Exception ex)
        {
            AppLog.Error("Application logs", "Failed to copy recent application logs to the clipboard.", ex);
            SetStatus($"复制日志失败：{ex.Message}");
        }
    }

    private async Task InitializeUsageStatisticsStoreAsync()
    {
        try
        {
            await _usageStatisticsStore.InitializeAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("Usage statistics", "Failed to initialize the statistics database.", ex);
        }
    }

    private async Task RecordCompletedTranslationAsync(string submittedText, TranslationProviderKind provider)
    {
        try
        {
            await _usageStatisticsStore.RecordCompletedTranslationAsync(submittedText, provider);
        }
        catch (Exception ex)
        {
            AppLog.Error("Usage statistics", "Failed to record a completed translation.", ex);
        }
    }

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
            TranslateOnTextChangeCheckBox.IsChecked = true;

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
        var settings = _settings;
        var providerName = settings.TranslationProvider == TranslationProviderKind.HttpTranslation
            ? "HTTP"
            : "AI";
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
            var submittedText = TranslationService.GetSubmittedText(
                settings,
                text,
                applyMaximumTranslationLimit: false);
            var translated = await _translation.TranslateAsync(
                settings,
                text,
                CancellationToken.None,
                applyMaximumTranslationLimit: false);
            if (!string.IsNullOrWhiteSpace(translated))
                await RecordCompletedTranslationAsync(submittedText, settings.TranslationProvider);
            TestResultText.Text = translated;
            TestSpeakButton.IsEnabled = !string.IsNullOrWhiteSpace(translated);
            stopwatch.Stop();
            SetStatus($"试译完成 · {providerName} 接口 · {stopwatch.Elapsed.TotalMilliseconds:F0} ms");
            if (RealTimeSpeechCheckBox.IsChecked == true)
                StartSpeechPlayback(translated);
        }
        catch (Exception ex)
        {
            AppLog.Error("Translation test", "Test translation request failed.", ex);
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
        AppLog.Info("Input monitor", "Input monitoring started.");
        _monitorDurationStopwatch.Restart();
        _monitorDurationTimer.Start();
        UpdateMonitorStateText("监控中");
        SetStatus(_settings.IsConfigured ? "就绪" : "设置翻译接口");
    }

    private void StopMonitoring()
    {
        var wasMonitoring = _isMonitoring;
        _isMonitoring = false;
        _monitorDurationTimer.Stop();
        _monitorDurationStopwatch.Reset();
        CancelTranslationQueue();
        _monitor.Stop();
        if (wasMonitoring)
            AppLog.Info("Input monitor", "Input monitoring stopped.");
        _overlay.HideOverlay();
        UpdateMonitorStateText("已退出");
    }

    private void UpdateMonitorStateText(string? stateOverride = null)
    {
        if (stateOverride is not null)
            _monitorStateLabel = stateOverride;

        if (!_isMonitoring)
        {
            MonitorStateText.Text = "已退出";
            return;
        }

        var elapsed = _monitorDurationStopwatch.Elapsed;
        MonitorStateText.Text = $"{_monitorStateLabel} · {(long)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
    }

    private void OnSpeechRequested(string text)
        => Dispatcher.UIThread.Post(() => StartSpeechPlayback(text), DispatcherPriority.Background);

    private void StartSpeechPlayback(string text)
    {
        CancelSpeechPlayback();
        var settings = new AppSettings
        {
            SpeechEndpoint = SpeechEndpointBox.Text?.Trim() ?? "",
            SpeechProvider = EnableSpeechHttpSwitch.IsChecked == true
                ? SpeechProviderKind.GenericHttp
                : SpeechProviderKind.OpenAiCompatible,
            SpeechModel = SpeechModelBox.Text?.Trim() ?? "",
            SpeechApiKey = SpeechApiKeyBox.Text?.Trim() ?? "",
            SpeechVoice = SpeechVoiceBox.Text?.Trim() ?? "",
            HttpSpeechEndpoint = "",
            HttpSpeechModel = "",
            HttpSpeechApiKey = "",
            HttpSpeechVoice = "",
            SpeechHttpProfiles = _settings.SpeechHttpProfiles,
            ActiveSpeechHttpProfileId = (HttpSpeechProfileComboBox.SelectedItem as SpeechHttpProfile)?.Id ?? ""
        };
        if (!settings.IsSpeechConfigured)
        {
            SetStatus(settings.SpeechProvider == SpeechProviderKind.GenericHttp
                ? "请先配置有效的通用 HTTP 语音接口档案"
                : "请先配置语音模型地址、模型名称和音色");
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
            AppLog.Error("Speech synthesis", "Speech synthesis or playback failed.", ex);
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
        // Do not let a translation from the previous input target re-open the
        // overlay after focus has moved away or its text has been cleared.
        Interlocked.Increment(ref _translationGeneration);
        _queuedTranslation = null;
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
            SetStatus("请配置当前翻译接口");
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
                    var translationSettings = _settings;
                    var submittedText = TranslationService.GetSubmittedText(translationSettings, text.Text);
                    var translated = await _translation.TranslateAsync(translationSettings, text.Text, cancellationToken);
                    stopwatch.Stop();
                    var isCurrentRequest = generation == Interlocked.Read(ref _translationGeneration)
                        && _queuedTranslation is null
                        && _isMonitoring
                        && !cancellationToken.IsCancellationRequested;
                    if (isCurrentRequest && !string.IsNullOrWhiteSpace(translated))
                        await RecordCompletedTranslationAsync(submittedText, translationSettings.TranslationProvider);

                    if (generation == Interlocked.Read(ref _translationGeneration)
                        && _queuedTranslation is null && _isMonitoring
                        && !cancellationToken.IsCancellationRequested)
                    {
                        _overlay.ShowAt(text.Bounds, translated);
                        SetStatus($"翻译完成 · 接口耗时：{stopwatch.Elapsed.TotalMilliseconds:F0} ms");
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
                    AppLog.Error("Translation monitor", "Monitored translation request failed.", ex);
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
