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
    private CancellationTokenSource? _deepAnalysisCancellation;
    private long _latestDeepAnalysis;
    private bool _isMonitoring;
    private string _monitorStateLabel = "已退出";
    private bool _allowClose;
    private bool _updatingTriggerControls;
    private bool _normalizingMaximumTranslationCharacters;
    private BorderlessWindowResizeSession? _windowResizeSession;
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
        TranslationAiProfileComboBox.ItemsSource = _settings.AiProfiles;
        TranslationAiProfileComboBox.SelectedItem = _settings.ActiveAiProfile;
        AnalysisAiProfileComboBox.ItemsSource = _settings.AiProfiles;
        AnalysisAiProfileComboBox.SelectedItem = _settings.ActiveAnalysisAiProfile;
        TranslationPromptProfileComboBox.ItemsSource = _settings.TranslationPromptProfiles;
        TranslationPromptProfileComboBox.SelectedItem = _settings.ActiveTranslationPromptProfile;
        AnalysisPromptProfileComboBox.ItemsSource = _settings.AnalysisPromptProfiles;
        AnalysisPromptProfileComboBox.SelectedItem = _settings.ActiveAnalysisPromptProfile;
        HttpTranslationProfileComboBox.ItemsSource = _settings.TranslationHttpProfiles;
        HttpTranslationProfileComboBox.SelectedItem = _settings.ActiveTranslationHttpProfile;
        SpeechAiProfileComboBox.ItemsSource = _settings.SpeechAiProfiles;
        SpeechAiProfileComboBox.SelectedItem = _settings.ActiveSpeechAiProfile;
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
        _overlay.DeepAnalysisRequested += OnDeepAnalysisRequested;
        _overlay.DeepAnalysisDismissed += OnDeepAnalysisDismissed;
        _overlay.SpeechRequested += OnSpeechRequested;
        UpdateDeepAnalysisAvailability();
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
        _deepAnalysisCancellation?.Cancel();
        Close();
    }

    private void OnCardAppearanceChanged(CardAppearanceSettings settings)
    {
        _cardAppearance = settings;
        _cardAppearanceStore.Save(settings);
    }

    private void OnDeepAnalysisRequested(string selectedText, string context)
        => Dispatcher.UIThread.Post(() => _ = ShowDeepAnalysisAsync(selectedText, context), DispatcherPriority.Background);

    private void OnDeepAnalysisDismissed()
    {
        Interlocked.Increment(ref _latestDeepAnalysis);
        _deepAnalysisCancellation?.Cancel();
    }

    private async Task ShowDeepAnalysisAsync(string selectedText, string context)
    {
        if (string.IsNullOrWhiteSpace(selectedText)) return;

        SaveSettings();
        _deepAnalysisCancellation?.Cancel();
        var requestId = Interlocked.Increment(ref _latestDeepAnalysis);
        _overlay.ShowDeepAnalysisPending();
        if (!_settings.IsDeepAnalysisConfigured)
        {
            _overlay.ShowDeepAnalysisError("请为详解选择并配置 AI 接口地址、模型名称和深度分析指令。");
            return;
        }

        var cancellation = new CancellationTokenSource();
        _deepAnalysisCancellation = cancellation;
        try
        {
            var answer = await _translation.ExplainMeaningAsync(
                _settings,
                _settings.ActiveAnalysisAiProfile!,
                selectedText,
                context,
                cancellation.Token);
            if (requestId == Interlocked.Read(ref _latestDeepAnalysis))
                _overlay.ShowDeepAnalysisResult(answer);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            AppLog.Error("AI deep analysis", "AI deep analysis request failed.", ex);
            if (requestId == Interlocked.Read(ref _latestDeepAnalysis))
                _overlay.ShowDeepAnalysisError($"AI 暂时无法完成分析：{ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_deepAnalysisCancellation, cancellation))
                _deepAnalysisCancellation = null;
            cancellation.Dispose();
        }
    }

    private void SaveSettings()
    {
        var translationPromptProfile = TranslationPromptProfileComboBox.SelectedItem as PromptProfile
            ?? _settings.ActiveTranslationPromptProfile;
        var analysisPromptProfile = AnalysisPromptProfileComboBox.SelectedItem as PromptProfile
            ?? _settings.ActiveAnalysisPromptProfile;
        var speechAiProfile = SpeechAiProfileComboBox.SelectedItem as SpeechAiConnectionProfile
            ?? _settings.ActiveSpeechAiProfile;
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
            Endpoint = (TranslationAiProfileComboBox.SelectedItem as AiConnectionProfile)?.Endpoint ?? "",
            Model = (TranslationAiProfileComboBox.SelectedItem as AiConnectionProfile)?.Model ?? "",
            ApiKey = (TranslationAiProfileComboBox.SelectedItem as AiConnectionProfile)?.ApiKey ?? "",
            AiProfiles = _settings.AiProfiles,
            ActiveAiProfileId = (TranslationAiProfileComboBox.SelectedItem as AiConnectionProfile)?.Id ?? "",
            ActiveAnalysisAiProfileId = (AnalysisAiProfileComboBox.SelectedItem as AiConnectionProfile)?.Id ?? "",
            TranslationPromptProfiles = _settings.TranslationPromptProfiles,
            ActiveTranslationPromptProfileId = translationPromptProfile?.Id ?? "",
            AnalysisPromptProfiles = _settings.AnalysisPromptProfiles,
            ActiveAnalysisPromptProfileId = analysisPromptProfile?.Id ?? "",
            TranslationProvider = EnableHttpProviderSwitch.IsChecked == true
                ? TranslationProviderKind.HttpTranslation
                : TranslationProviderKind.OpenAiCompatible,
            TranslationHttpProfiles = _settings.TranslationHttpProfiles,
            ActiveTranslationHttpProfileId = (HttpTranslationProfileComboBox.SelectedItem as TranslationHttpProfile)?.Id ?? "",
            SpeechEndpoint = speechAiProfile?.Endpoint ?? "",
            SpeechProvider = EnableSpeechHttpSwitch.IsChecked == true
                ? SpeechProviderKind.GenericHttp
                : SpeechProviderKind.OpenAiCompatible,
            SpeechModel = speechAiProfile?.Model ?? "",
            SpeechApiKey = speechAiProfile?.ApiKey ?? "",
            SpeechVoice = speechAiProfile?.Voice ?? "",
            SpeechAiProfiles = _settings.SpeechAiProfiles,
            ActiveSpeechAiProfileId = speechAiProfile?.Id ?? "",
            HttpSpeechEndpoint = "",
            HttpSpeechModel = "",
            HttpSpeechApiKey = "",
            HttpSpeechVoice = "",
            SpeechHttpProfiles = _settings.SpeechHttpProfiles,
            ActiveSpeechHttpProfileId = (HttpSpeechProfileComboBox.SelectedItem as SpeechHttpProfile)?.Id ?? "",
            RealTimeSpeechEnabled = RealTimeSpeechCheckBox.IsChecked == true,
            SystemPrompt = translationPromptProfile?.Prompt ?? "",
            DeepAnalysisPrompt = analysisPromptProfile?.Prompt ?? "",
            TranslateOnTextChange = TranslateOnTextChangeCheckBox.IsChecked == true,
            TranslateOnSentenceEnd = TranslateOnSentenceEndCheckBox.IsChecked == true,
            TranslateAfterInactivity = TranslateAfterInactivityCheckBox.IsChecked == true,
            InactivityDelaySeconds = inactivityDelaySeconds,
            MaximumTranslationCharacters = maximumTranslationCharacters
        };
        UpdateDeepAnalysisAvailability();
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
        TranslationAiProfileComboBox.SelectionChanged += TranslationAiProfile_Changed;
        AnalysisAiProfileComboBox.SelectionChanged += AnalysisAiProfile_Changed;
        TranslationPromptProfileComboBox.SelectionChanged += TranslationPromptProfile_Changed;
        AnalysisPromptProfileComboBox.SelectionChanged += AnalysisPromptProfile_Changed;
        HttpTranslationProfileComboBox.SelectionChanged += HttpTranslationProfile_Changed;
        SpeechAiProfileComboBox.SelectionChanged += SpeechAiProfile_Changed;
        HttpSpeechProfileComboBox.SelectionChanged += HttpSpeechProfile_Changed;
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

    private void TranslationAiProfile_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (TranslationAiProfileComboBox.SelectedItem is AiConnectionProfile selected)
        {
            _settings.ActiveAiProfileId = selected.Id;
            _settings.Endpoint = selected.Endpoint;
            _settings.Model = selected.Model;
            _settings.ApiKey = selected.ApiKey;
        }
        ScheduleAutoSave();
    }

    private void AnalysisAiProfile_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (AnalysisAiProfileComboBox.SelectedItem is AiConnectionProfile selected)
            _settings.ActiveAnalysisAiProfileId = selected.Id;
        UpdateDeepAnalysisAvailability();
        ScheduleAutoSave();
    }

    private void SpeechAiProfile_Changed(object? sender, SelectionChangedEventArgs e)
        => ScheduleAutoSave();

    private void TranslationPromptProfile_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (TranslationPromptProfileComboBox.SelectedItem is PromptProfile selected)
        {
            _settings.ActiveTranslationPromptProfileId = selected.Id;
            _settings.SystemPrompt = selected.Prompt;
        }
        ScheduleAutoSave();
    }

    private void AnalysisPromptProfile_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (AnalysisPromptProfileComboBox.SelectedItem is PromptProfile selected)
        {
            _settings.ActiveAnalysisPromptProfileId = selected.Id;
            _settings.DeepAnalysisPrompt = selected.Prompt;
        }
        UpdateDeepAnalysisAvailability();
        ScheduleAutoSave();
    }

    private async void ManageAiProfiles_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        SaveSettings();
        var editor = new AiConnectionProfilesWindow(_settings.AiProfiles, _settings.ActiveAiProfileId);
        var saved = await editor.ShowDialog<bool>(this);
        if (!saved) return;

        var translationProfileId = _settings.ActiveAiProfileId;
        var analysisProfileId = _settings.ActiveAnalysisAiProfileId;
        _settings.AiProfiles = editor.Profiles.Select(profile => profile.Copy()).ToList();
        if (!_settings.AiProfiles.Any(profile => profile.Id == translationProfileId))
            translationProfileId = editor.SelectedProfileId;
        if (!_settings.AiProfiles.Any(profile => profile.Id == analysisProfileId))
            analysisProfileId = translationProfileId;
        _settings.ActiveAiProfileId = translationProfileId;
        _settings.ActiveAnalysisAiProfileId = analysisProfileId;
        TranslationAiProfileComboBox.ItemsSource = null;
        TranslationAiProfileComboBox.ItemsSource = _settings.AiProfiles;
        TranslationAiProfileComboBox.SelectedItem = _settings.ActiveAiProfile;
        AnalysisAiProfileComboBox.ItemsSource = null;
        AnalysisAiProfileComboBox.ItemsSource = _settings.AiProfiles;
        AnalysisAiProfileComboBox.SelectedItem = _settings.ActiveAnalysisAiProfile;
        SaveSettings();
    }

    private async void ManagePromptProfiles_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        SaveSettings();
        var editor = new PromptProfilesWindow(
            _settings.TranslationPromptProfiles,
            _settings.ActiveTranslationPromptProfileId,
            _settings.AnalysisPromptProfiles,
            _settings.ActiveAnalysisPromptProfileId);
        var saved = await editor.ShowDialog<bool>(this);
        if (!saved) return;

        var translationPromptId = _settings.ActiveTranslationPromptProfileId;
        var analysisPromptId = _settings.ActiveAnalysisPromptProfileId;
        _settings.TranslationPromptProfiles = editor.TranslationProfiles.Select(profile => profile.Copy()).ToList();
        _settings.AnalysisPromptProfiles = editor.AnalysisProfiles.Select(profile => profile.Copy()).ToList();
        if (!_settings.TranslationPromptProfiles.Any(profile => profile.Id == translationPromptId))
            translationPromptId = _settings.TranslationPromptProfiles[0].Id;
        if (!_settings.AnalysisPromptProfiles.Any(profile => profile.Id == analysisPromptId))
            analysisPromptId = _settings.AnalysisPromptProfiles[0].Id;
        _settings.ActiveTranslationPromptProfileId = translationPromptId;
        _settings.ActiveAnalysisPromptProfileId = analysisPromptId;

        TranslationPromptProfileComboBox.ItemsSource = null;
        TranslationPromptProfileComboBox.ItemsSource = _settings.TranslationPromptProfiles;
        TranslationPromptProfileComboBox.SelectedItem = _settings.ActiveTranslationPromptProfile;
        AnalysisPromptProfileComboBox.ItemsSource = null;
        AnalysisPromptProfileComboBox.ItemsSource = _settings.AnalysisPromptProfiles;
        AnalysisPromptProfileComboBox.SelectedItem = _settings.ActiveAnalysisPromptProfile;
        SaveSettings();
    }

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

    private async void ManageSpeechAiProfiles_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        SaveSettings();
        var editor = new SpeechAiConnectionProfilesWindow(
            _settings.SpeechAiProfiles,
            _settings.ActiveSpeechAiProfileId);
        var saved = await editor.ShowDialog<bool>(this);
        if (!saved) return;

        var activeProfileId = _settings.ActiveSpeechAiProfileId;
        _settings.SpeechAiProfiles = editor.Profiles.Select(profile => profile.Copy()).ToList();
        if (!_settings.SpeechAiProfiles.Any(profile => profile.Id == activeProfileId))
            activeProfileId = editor.SelectedProfileId;
        _settings.ActiveSpeechAiProfileId = activeProfileId;
        SpeechAiProfileComboBox.ItemsSource = null;
        SpeechAiProfileComboBox.ItemsSource = _settings.SpeechAiProfiles;
        SpeechAiProfileComboBox.SelectedItem = _settings.ActiveSpeechAiProfile;
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

    private void UpdateDeepAnalysisAvailability()
        => _overlay.SetDeepAnalysisAvailable(_settings.IsDeepAnalysisConfigured);

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

    private void ResizeGrip_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Avalonia.Controls.Control { Tag: string direction }) return;
        _windowResizeSession = BorderlessWindowResizeSession.TryBegin(this, e, direction);
        if (_windowResizeSession is null) return;

        e.Pointer.Capture(this);
        e.Handled = true;
    }

    private void WindowSurface_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_windowResizeSession is null) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _windowResizeSession = null;
            return;
        }

        _windowResizeSession.Update();
    }

    private void WindowSurface_PointerReleased(object? sender, PointerReleasedEventArgs e)
        => _windowResizeSession = null;

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

    private static string GetLatestSentence(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;

        var sentenceStart = 0;
        string? latestSentence = null;
        for (var i = 0; i < text.Length; i++)
        {
            if (!IsSpeechSentenceBoundary(text, i)) continue;

            var sentenceEnd = i + 1;
            while (sentenceEnd < text.Length
                   && (IsSpeechSentencePunctuation(text[sentenceEnd])
                       || IsClosingSentenceQuote(text[sentenceEnd])))
                sentenceEnd++;

            var completedSentence = text[sentenceStart..sentenceEnd].Trim();
            if (completedSentence.Length > 0) latestSentence = completedSentence;
            sentenceStart = sentenceEnd;
            i = sentenceEnd - 1;
        }

        var trailingText = text[sentenceStart..].Trim();
        return trailingText.Length > 0 ? trailingText : latestSentence ?? text.Trim();
    }

    private static bool IsSpeechSentenceBoundary(string text, int index)
    {
        var character = text[index];
        if (character is '\r' or '\n' or '，' or ',' or '：' or ':' or '。' or '！' or '？' or '!' or '?' or '…')
            return true;
        if (character != '.') return false;

        var next = index + 1;
        while (next < text.Length && IsClosingSentenceQuote(text[next])) next++;
        return next == text.Length || char.IsWhiteSpace(text[next]);
    }

    private static bool IsSpeechSentencePunctuation(char character)
        => character is ',' or '，' or ':' or '：' or '.' or '。' or '！' or '？' or '!' or '?' or '…' or '\r' or '\n';

    private static bool IsClosingSentenceQuote(char character)
        => character is '"' or '\'' or '”' or '’' or '」' or '』' or '）' or ')' or '】' or ']';

    private void OnSpeechRequested(string text)
        => Dispatcher.UIThread.Post(() => StartSpeechPlayback(text), DispatcherPriority.Background);

    private void StartSpeechPlayback(string text)
    {
        CancelSpeechPlayback();
        var speechAiProfile = SpeechAiProfileComboBox.SelectedItem as SpeechAiConnectionProfile
            ?? _settings.ActiveSpeechAiProfile;
        var settings = new AppSettings
        {
            SpeechEndpoint = speechAiProfile?.Endpoint ?? "",
            SpeechProvider = EnableSpeechHttpSwitch.IsChecked == true
                ? SpeechProviderKind.GenericHttp
                : SpeechProviderKind.OpenAiCompatible,
            SpeechModel = speechAiProfile?.Model ?? "",
            SpeechApiKey = speechAiProfile?.ApiKey ?? "",
            SpeechVoice = speechAiProfile?.Voice ?? "",
            SpeechAiProfiles = _settings.SpeechAiProfiles,
            ActiveSpeechAiProfileId = speechAiProfile?.Id ?? "",
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
                            StartSpeechPlayback(GetLatestSentence(translated));
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
