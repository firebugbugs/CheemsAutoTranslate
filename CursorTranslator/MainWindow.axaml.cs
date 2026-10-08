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
using System.Text;

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
    private readonly DispatcherTimer _overlayCloseDelayTimer = new();
    private readonly Stopwatch _monitorDurationStopwatch = new();
    private readonly InputMonitor _monitor;
    private readonly TranslationOverlay _overlay;
    private AppSettings _settings;
    private CardAppearanceSettings _cardAppearance;
    private CancellationTokenSource? _deepAnalysisCancellation;
    private SettingsWindow? _translationSettingsWindow;
    private long _latestDeepAnalysis;
    private bool _isMonitoring;
    private string _monitorStateLabel = "已退出";
    private bool _allowClose;
    private BorderlessWindowResizeSession? _windowResizeSession;
    private bool _updatingTranslationProviderControls;
    private bool _updatingSpeechProviderControls = true;
    private long _translationGeneration;
    private long _speechGeneration;
    private CancellationTokenSource? _translationCancellation;
    private CancellationTokenSource? _speechCancellation;
    private bool _speechPlaybackFromOverlay;
    private MonitoredText? _queuedTranslation;
    private bool _translationWorkerRunning;
    private string _latestTestTranslation = "";

    public MainWindow()
    {
        InitializeComponent();
        WindowFrameHelper.Track(this, WindowSurface);
        _settings = _settingsStore.Load();
        _overlayCloseDelayTimer.Tick += (_, _) =>
        {
            _overlayCloseDelayTimer.Stop();
            _overlay.HideOverlay();
        };
        TranslationAiProfileComboBox.ItemsSource = _settings.AiProfiles;
        TranslationAiProfileComboBox.SelectedItem = _settings.ActiveAiProfile;
        AnalysisAiProfileComboBox.ItemsSource = _settings.AiProfiles;
        AnalysisAiProfileComboBox.SelectedItem = _settings.ActiveAnalysisAiProfile;
        TranslationPromptProfileComboBox.ItemsSource = _settings.TranslationPromptProfiles;
        TranslationPromptProfileComboBox.SelectedItem = _settings.ActiveTranslationPromptProfile;
        AnalysisPromptProfileComboBox.ItemsSource = _settings.AnalysisPromptProfiles;
        AnalysisPromptProfileComboBox.SelectedItem = _settings.ActiveAnalysisPromptProfile;
        SelectedTranslationTermPromptProfileComboBox.ItemsSource = _settings.SelectedTranslationTermPromptProfiles;
        SelectedTranslationTermPromptProfileComboBox.SelectedItem = _settings.ActiveSelectedTranslationTermPromptProfile;
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
        UpdateStartupButton();
        SetStatus(_settings.IsConfigured ? "就绪" : "设置翻译接口");
        _cardAppearance = _cardAppearanceStore.Load();
        _overlay = new TranslationOverlay();
        _overlay.ApplyAppearance(_cardAppearance);
        _overlay.AppearanceChanged += OnCardAppearanceChanged;
        _overlay.SourceTranslationAnalysisRequested += OnSourceTranslationAnalysisRequested;
        _overlay.SelectedTranslationTermAnalysisRequested += OnDeepAnalysisRequested;
        _overlay.DeepAnalysisDismissed += OnDeepAnalysisDismissed;
        _overlay.SpeechRequested += OnSpeechRequested;
        _overlay.SpeechStopRequested += OnOverlaySpeechStopRequested;
        _overlay.MainPageRequested += () =>
        {
            _overlay.HideOverlay();
            ShowSettingsWindow();
        };
        UpdateDeepAnalysisAvailability();
        UpdateSpeechAvailability();
        _monitor = new InputMonitor();
        _overlay.NativeWindowHandleAvailable += _monitor.SetOverlayWindowHandle;
        _overlay.UserInteraction += _monitor.PreserveTargetForOverlayInteraction;
        ApplyTranslationTriggerSettings();
        _monitor.TextCommitted += OnTextCommitted;
        _monitor.InputCleared += OnInputCleared;
        _monitor.FocusLost += OnInputFocusLost;
        _monitor.TargetFound += OnInputTargetFound;
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

    private void OnSourceTranslationAnalysisRequested(string sourceText, string translation)
        => Dispatcher.UIThread.Post(
            () => _ = ShowDeepAnalysisAsync(sourceText, translation, sourceText, analyzeSourceTranslation: true),
            DispatcherPriority.Background);

    private void OnDeepAnalysisRequested(string selectedText, string translationContext, string sourceText)
        => Dispatcher.UIThread.Post(
            () => _ = ShowDeepAnalysisAsync(selectedText, translationContext, sourceText, analyzeSourceTranslation: false),
            DispatcherPriority.Background);

    private void OnDeepAnalysisDismissed()
    {
        Interlocked.Increment(ref _latestDeepAnalysis);
        _deepAnalysisCancellation?.Cancel();
    }

    private async Task ShowDeepAnalysisAsync(
        string analysisTarget,
        string translationContext,
        string sourceText,
        bool analyzeSourceTranslation)
    {
        if (analyzeSourceTranslation
                ? string.IsNullOrWhiteSpace(sourceText)
                : string.IsNullOrWhiteSpace(analysisTarget))
            return;

        SaveSettings();
        _deepAnalysisCancellation?.Cancel();
        var requestId = Interlocked.Increment(ref _latestDeepAnalysis);
        _overlay.ShowDeepAnalysisPending();
        var isAnalysisConfigured = analyzeSourceTranslation
            ? _settings.IsDeepAnalysisConfigured
            : _settings.IsSelectedTranslationTermAnalysisConfigured;
        if (!isAnalysisConfigured)
        {
            _overlay.ShowDeepAnalysisError(analyzeSourceTranslation
                ? "请为原文解析选择并配置 AI 接口地址、模型名称和解析提示词。"
                : "请为选词解析选择并配置 AI 接口地址、模型名称和选词提示词。" );
            return;
        }

        var cancellation = new CancellationTokenSource();
        _deepAnalysisCancellation = cancellation;
        int? reasoningTokens = null;
        using var liveOutput = new UiStreamBuffer(
            result =>
            {
                if (requestId == Interlocked.Read(ref _latestDeepAnalysis))
                    _overlay.ShowDeepAnalysisStreamingResult(result);
            },
            result =>
            {
                if (requestId == Interlocked.Read(ref _latestDeepAnalysis))
                    _overlay.ShowDeepAnalysisResult(result);
            });
        try
        {
            var analysisProfile = _settings.ActiveAnalysisAiProfile!;
            var answer = analyzeSourceTranslation
                ? await _translation.ExplainSourceTranslationAsync(
                    _settings,
                    analysisProfile,
                    sourceText,
                    translationContext,
                    cancellation.Token,
                    onDelta: liveOutput.Append,
                    onProgress: progress =>
                    {
                        if (progress.Phase == AiStreamPhase.Completed)
                            reasoningTokens = progress.ReasoningTokens;
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (requestId == Interlocked.Read(ref _latestDeepAnalysis))
                                _overlay.ShowDeepAnalysisProgress(progress);
                        });
                    })
                : await _translation.ExplainSelectedTranslationTermAsync(
                    _settings,
                    analysisProfile,
                    analysisTarget,
                    translationContext,
                    sourceText,
                    cancellation.Token,
                    onDelta: liveOutput.Append,
                    onProgress: progress =>
                    {
                        if (progress.Phase == AiStreamPhase.Completed)
                            reasoningTokens = progress.ReasoningTokens;
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (requestId == Interlocked.Read(ref _latestDeepAnalysis))
                                _overlay.ShowDeepAnalysisProgress(progress);
                        });
                    });
            liveOutput.Complete(answer);
            _overlay.ShowDeepAnalysisProgress(new AiStreamProgress(AiStreamPhase.Completed, reasoningTokens));
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
        var selectedTranslationTermPromptProfile = SelectedTranslationTermPromptProfileComboBox.SelectedItem as PromptProfile
            ?? _settings.ActiveSelectedTranslationTermPromptProfile;
        var speechAiProfile = SpeechAiProfileComboBox.SelectedItem as SpeechAiConnectionProfile
            ?? _settings.ActiveSpeechAiProfile;
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
            SelectedTranslationTermPromptProfiles = _settings.SelectedTranslationTermPromptProfiles,
            ActiveSelectedTranslationTermPromptProfileId = selectedTranslationTermPromptProfile?.Id ?? "",
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
            SelectedTranslationTermPrompt = selectedTranslationTermPromptProfile?.Prompt
                ?? AppSettings.DefaultSelectedTranslationTermPrompt,
            TranslateOnTextChange = _settings.TranslateOnTextChange,
            TranslateOnSentenceEnd = _settings.TranslateOnSentenceEnd,
            TranslateAfterCopy = _settings.TranslateAfterCopy,
            TranslateAfterInactivity = _settings.TranslateAfterInactivity,
            InactivityDelaySeconds = _settings.InactivityDelaySeconds,
            MaximumTranslationCharacters = _settings.MaximumTranslationCharacters,
            OverlayFocusLossCloseDelayEnabled = _settings.OverlayFocusLossCloseDelayEnabled,
            OverlayFocusLossCloseDelaySeconds = _settings.OverlayFocusLossCloseDelaySeconds
        };
        UpdateDeepAnalysisAvailability();
        UpdateSpeechAvailability();
        ApplyTranslationTriggerSettings();
        _settingsStore.Save(_settings);
        var validationIssues = new List<string>();
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
        SelectedTranslationTermPromptProfileComboBox.SelectionChanged += SelectedTranslationTermPromptProfile_Changed;
        HttpTranslationProfileComboBox.SelectionChanged += HttpTranslationProfile_Changed;
        SpeechAiProfileComboBox.SelectionChanged += SpeechAiProfile_Changed;
        HttpSpeechProfileComboBox.SelectionChanged += HttpSpeechProfile_Changed;
    }

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

    private void SelectedTranslationTermPromptProfile_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (SelectedTranslationTermPromptProfileComboBox.SelectedItem is PromptProfile selected)
        {
            _settings.ActiveSelectedTranslationTermPromptProfileId = selected.Id;
            _settings.SelectedTranslationTermPrompt = selected.Prompt;
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
            _settings.ActiveAnalysisPromptProfileId,
            _settings.SelectedTranslationTermPromptProfiles,
            _settings.ActiveSelectedTranslationTermPromptProfileId);
        var saved = await editor.ShowDialog<bool>(this);
        if (!saved) return;

        var translationPromptId = _settings.ActiveTranslationPromptProfileId;
        var analysisPromptId = _settings.ActiveAnalysisPromptProfileId;
        var selectedTranslationTermPromptId = _settings.ActiveSelectedTranslationTermPromptProfileId;
        _settings.TranslationPromptProfiles = editor.TranslationProfiles.Select(profile => profile.Copy()).ToList();
        _settings.AnalysisPromptProfiles = editor.AnalysisProfiles.Select(profile => profile.Copy()).ToList();
        _settings.SelectedTranslationTermPromptProfiles = editor.SelectedTranslationTermProfiles
            .Select(profile => profile.Copy()).ToList();
        if (!_settings.TranslationPromptProfiles.Any(profile => profile.Id == translationPromptId))
            translationPromptId = _settings.TranslationPromptProfiles[0].Id;
        if (!_settings.AnalysisPromptProfiles.Any(profile => profile.Id == analysisPromptId))
            analysisPromptId = _settings.AnalysisPromptProfiles[0].Id;
        if (!_settings.SelectedTranslationTermPromptProfiles.Any(profile => profile.Id == selectedTranslationTermPromptId))
            selectedTranslationTermPromptId = _settings.SelectedTranslationTermPromptProfiles[0].Id;
        _settings.ActiveTranslationPromptProfileId = translationPromptId;
        _settings.ActiveAnalysisPromptProfileId = analysisPromptId;
        _settings.ActiveSelectedTranslationTermPromptProfileId = selectedTranslationTermPromptId;

        TranslationPromptProfileComboBox.ItemsSource = null;
        TranslationPromptProfileComboBox.ItemsSource = _settings.TranslationPromptProfiles;
        TranslationPromptProfileComboBox.SelectedItem = _settings.ActiveTranslationPromptProfile;
        AnalysisPromptProfileComboBox.ItemsSource = null;
        AnalysisPromptProfileComboBox.ItemsSource = _settings.AnalysisPromptProfiles;
        AnalysisPromptProfileComboBox.SelectedItem = _settings.ActiveAnalysisPromptProfile;
        SelectedTranslationTermPromptProfileComboBox.ItemsSource = null;
        SelectedTranslationTermPromptProfileComboBox.ItemsSource = _settings.SelectedTranslationTermPromptProfiles;
        SelectedTranslationTermPromptProfileComboBox.SelectedItem = _settings.ActiveSelectedTranslationTermPromptProfile;
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
        => _overlay.SetDeepAnalysisAvailability(
            _settings.IsDeepAnalysisConfigured,
            _settings.IsSelectedTranslationTermAnalysisConfigured);

    private void UpdateSpeechAvailability()
        => _overlay.SetSpeechAvailability(_settings.IsSpeechConfigured);

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

    private void SetAiProgressStatus(AiStreamProgress progress)
    {
        if (progress.Phase == AiStreamPhase.Completed) return;
        SetStatus(progress.Phase switch
        {
            AiStreamPhase.WaitingForResponse => "AI 已发送请求，等待模型响应…",
            AiStreamPhase.Thinking => "AI 正在深度思考…",
            AiStreamPhase.Generating => "AI 正在生成结果…",
            _ => "AI 正在处理…"
        });
    }

    private void SetTestResult(string markdown)
        => MarkdownTextRenderer.Render(
            markdown,
            TestResultContent,
            new SolidColorBrush(Avalonia.Media.Color.Parse("#51463F")));

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

    private void OpenTranslationSettings_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_translationSettingsWindow is { IsVisible: true })
        {
            _translationSettingsWindow.Activate();
            return;
        }

        var settingsWindow = new SettingsWindow(_settings);
        _translationSettingsWindow = settingsWindow;
        settingsWindow.PreferencesChanged += OnTranslationPreferencesChanged;
        settingsWindow.Closed += (_, _) =>
        {
            if (ReferenceEquals(_translationSettingsWindow, settingsWindow))
                _translationSettingsWindow = null;
        };
        settingsWindow.Show(this);
    }

    private void OnTranslationPreferencesChanged(
        bool translateOnTextChange,
        bool translateOnSentenceEnd,
        bool translateAfterCopy,
        bool translateAfterInactivity,
        decimal inactivityDelaySeconds,
        int maximumTranslationCharacters,
        bool overlayFocusLossCloseDelayEnabled,
        int overlayFocusLossCloseDelaySeconds)
    {
        var closeDelayChanged = _settings.OverlayFocusLossCloseDelayEnabled != overlayFocusLossCloseDelayEnabled
            || _settings.OverlayFocusLossCloseDelaySeconds != overlayFocusLossCloseDelaySeconds;
        _settings.TranslateOnTextChange = translateOnTextChange;
        _settings.TranslateOnSentenceEnd = translateOnSentenceEnd;
        _settings.TranslateAfterCopy = translateAfterCopy;
        _settings.TranslateAfterInactivity = translateAfterInactivity;
        _settings.InactivityDelaySeconds = inactivityDelaySeconds;
        _settings.MaximumTranslationCharacters = maximumTranslationCharacters;
        _settings.OverlayFocusLossCloseDelayEnabled = overlayFocusLossCloseDelayEnabled;
        _settings.OverlayFocusLossCloseDelaySeconds = overlayFocusLossCloseDelaySeconds;
        ApplyTranslationTriggerSettings();
        if (closeDelayChanged && _overlayCloseDelayTimer.IsEnabled)
        {
            _overlayCloseDelayTimer.Stop();
            if (!overlayFocusLossCloseDelayEnabled)
                _overlay.HideOverlay();
            else
            {
                _overlayCloseDelayTimer.Interval = TimeSpan.FromSeconds(overlayFocusLossCloseDelaySeconds);
                _overlayCloseDelayTimer.Start();
            }
        }
        _settingsStore.Save(_settings);
        SetStatus("翻译设置已自动保存");
    }

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

    private void ApplyTranslationTriggerSettings()
    {
        _monitor.ConfigureTranslationTriggers(
            _settings.TranslateOnTextChange,
            _settings.TranslateOnSentenceEnd,
            _settings.TranslateAfterCopy,
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
        if (TranslationService.IsUrl(text))
        {
            _latestTestTranslation = "";
            SetTestResult("");
            TestSpeakButton.IsEnabled = false;
            SetStatus("网址不会翻译");
            return;
        }

        TestButton.IsEnabled = false;
        TestAnalyzeButton.IsEnabled = false;
        TestSpeakButton.IsEnabled = false;
        _latestTestTranslation = "";
        SetTestResult("正在翻译…");
        var stopwatch = Stopwatch.StartNew();
        using var liveOutput = new UiStreamBuffer(SetTestResult);
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
                applyMaximumTranslationLimit: false,
                onDelta: liveOutput.Append,
                onProgress: progress => Dispatcher.UIThread.Post(() => SetAiProgressStatus(progress)));
            liveOutput.Complete(translated);
            if (!string.IsNullOrWhiteSpace(translated))
                await RecordCompletedTranslationAsync(submittedText, settings.TranslationProvider);
            _latestTestTranslation = translated;
            TestSpeakButton.IsEnabled = !string.IsNullOrWhiteSpace(translated);
            stopwatch.Stop();
            SetStatus($"试译完成 · {providerName} 接口 · {stopwatch.Elapsed.TotalMilliseconds:F0} ms");
            if (RealTimeSpeechCheckBox.IsChecked == true)
                StartSpeechPlayback(translated);
        }
        catch (Exception ex)
        {
            AppLog.Error("Translation test", "Test translation request failed.", ex);
            SetTestResult($"翻译失败：{ex.Message}");
            SetStatus($"翻译失败：{ex.Message}");
        }
        finally
        {
            TestButton.IsEnabled = true;
            TestAnalyzeButton.IsEnabled = true;
        }
    }

    private async void AnalyzeTest_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        SaveSettings();
        var settings = _settings;
        var profile = settings.ActiveAnalysisAiProfile;
        var text = TestInputBox.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            SetStatus("请先输入要解析的文本");
            return;
        }

        if (!settings.IsDeepAnalysisConfigured || profile is null)
        {
            SetStatus("请先配置解析 AI 接口和解析提示词");
            return;
        }

        TestButton.IsEnabled = false;
        TestAnalyzeButton.IsEnabled = false;
        TestSpeakButton.IsEnabled = false;
        SetTestResult("正在解析…");
        var stopwatch = Stopwatch.StartNew();
        using var liveOutput = new UiStreamBuffer(SetTestResult);
        try
        {
            var analysis = await _translation.ExplainSourceTranslationAsync(
                settings,
                profile,
                text,
                existingTranslation: null,
                cancellationToken: CancellationToken.None,
                onDelta: liveOutput.Append,
                onProgress: progress => Dispatcher.UIThread.Post(() => SetAiProgressStatus(progress)));
            liveOutput.Complete(analysis);
            stopwatch.Stop();
            SetStatus($"试译解析完成 · AI · {stopwatch.Elapsed.TotalMilliseconds:F0} ms");
        }
        catch (Exception ex)
        {
            AppLog.Error("Translation test analysis", "Test analysis request failed.", ex);
            SetTestResult($"解析失败：{ex.Message}");
            SetStatus($"解析失败：{ex.Message}");
        }
        finally
        {
            TestButton.IsEnabled = true;
            TestAnalyzeButton.IsEnabled = true;
        }
    }

    private void TestSpeak_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var translated = _latestTestTranslation;
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
        _overlayCloseDelayTimer.Stop();
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
        => Dispatcher.UIThread.Post(() =>
        {
            if (_overlay.IsVisible)
                StartSpeechPlayback(text, fromOverlay: true);
        }, DispatcherPriority.Background);

    private void StartSpeechPlayback(string text, bool fromOverlay = false)
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
        _speechPlaybackFromOverlay = fromOverlay;
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
        _speechPlaybackFromOverlay = false;
        cancellation?.Cancel();
        _speechSynthesis.Stop();
    }

    private void OnOverlaySpeechStopRequested()
    {
        if (_speechPlaybackFromOverlay)
            CancelSpeechPlayback();
    }

    private void OnInputCleared() => Dispatcher.UIThread.Post(() =>
    {
        // Do not let a translation from the previous input target re-open the
        // overlay after focus has moved away or its text has been cleared.
        _overlayCloseDelayTimer.Stop();
        Interlocked.Increment(ref _translationGeneration);
        _queuedTranslation = null;
        _overlay.HideOverlay();
    });

    private void OnInputFocusLost() => Dispatcher.UIThread.Post(() =>
    {
        Interlocked.Increment(ref _translationGeneration);
        _queuedTranslation = null;
        if (!_isMonitoring || !_settings.OverlayFocusLossCloseDelayEnabled)
        {
            _overlayCloseDelayTimer.Stop();
            _overlay.HideOverlay();
            return;
        }

        _overlayCloseDelayTimer.Stop();
        _overlayCloseDelayTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(
            _settings.OverlayFocusLossCloseDelaySeconds,
            AppSettings.MinimumOverlayFocusLossDelaySeconds,
            AppSettings.MaximumOverlayFocusLossDelaySeconds));
        _overlayCloseDelayTimer.Start();
    });

    private void OnInputTargetFound() => Dispatcher.UIThread.Post(() => _overlayCloseDelayTimer.Stop());

    private void CancelTranslationQueue()
    {
        Interlocked.Increment(ref _translationGeneration);
        _queuedTranslation = null;
        _translationCancellation?.Cancel();
    }

    private void OnTextCommitted(MonitoredText text) => Dispatcher.UIThread.Post(() =>
    {
        _overlayCloseDelayTimer.Stop();
        if (TranslationService.IsUrl(text.Text)) return;

        if (!_settings.IsConfigured)
        {
            SetStatus("请配置当前翻译接口");
            return;
        }

        _monitor.SetOverlaySourceRootWindowHandle(text.SourceRootWindow);

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
                    var applyMaximumTranslationLimit = !text.IsCopyTriggered;
                    var submittedText = TranslationService.GetSubmittedText(
                        translationSettings,
                        text.Text,
                        applyMaximumTranslationLimit);
                    using var liveOutput = new UiStreamBuffer(
                        output =>
                        {
                            if (generation == Interlocked.Read(ref _translationGeneration)
                                && _queuedTranslation is null && _isMonitoring)
                                _overlay.ShowAt(text.Bounds, output, submittedText);
                        },
                        output => _overlay.ShowAt(text.Bounds, output, submittedText));
                    var translated = await _translation.TranslateAsync(
                        translationSettings,
                        text.Text,
                        cancellationToken,
                        applyMaximumTranslationLimit,
                        onDelta: liveOutput.Append,
                        onProgress: progress => Dispatcher.UIThread.Post(() =>
                        {
                            if (generation == Interlocked.Read(ref _translationGeneration)
                                && _queuedTranslation is null && _isMonitoring)
                                SetAiProgressStatus(progress);
                        }));
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
                        liveOutput.Complete(translated);
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

    private sealed class UiStreamBuffer : IDisposable
    {
        private readonly object _sync = new();
        private readonly StringBuilder _content = new();
        private readonly Action<string> _onUpdate;
        private readonly Action<string>? _onComplete;
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(70) };
        private bool _dirty;
        private int _disposed;

        public UiStreamBuffer(Action<string> onUpdate, Action<string>? onComplete = null)
        {
            _onUpdate = onUpdate;
            _onComplete = onComplete;
            _timer.Tick += OnTimerTick;
            _timer.Start();
        }

        public void Append(string delta)
        {
            if (string.IsNullOrEmpty(delta) || Volatile.Read(ref _disposed) != 0) return;
            lock (_sync)
            {
                if (_disposed != 0) return;
                _content.Append(delta);
                _dirty = true;
            }
        }

        public void Complete(string finalContent)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            lock (_sync)
            {
                if (_disposed != 0) return;
                _content.Clear();
                _content.Append(finalContent);
                _dirty = false;
            }

            RunOnUiThread(() =>
            {
                _timer.Stop();
                try
                {
                    if (_onComplete is not null) _onComplete(finalContent);
                    else _onUpdate(finalContent);
                }
                catch (Exception ex)
                {
                    AppLog.Error("Streaming output", "Could not render completed stream output.", ex);
                }
            });
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            RunOnUiThread(() =>
            {
                _timer.Stop();
                _timer.Tick -= OnTimerTick;
            });
        }

        private void OnTimerTick(object? sender, EventArgs e)
        {
            string snapshot;
            lock (_sync)
            {
                if (!_dirty || _disposed != 0) return;
                snapshot = _content.ToString();
                _dirty = false;
            }

            try
            {
                _onUpdate(snapshot);
            }
            catch (Exception ex)
            {
                AppLog.Error("Streaming output", "Could not render partial stream output.", ex);
            }
        }

        private static void RunOnUiThread(Action action)
        {
            if (Dispatcher.UIThread.CheckAccess()) action();
            else Dispatcher.UIThread.Post(action, DispatcherPriority.Render);
        }
    }
}
