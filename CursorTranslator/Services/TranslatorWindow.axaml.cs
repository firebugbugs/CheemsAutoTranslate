using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CursorTranslator.Models;
using KeyEventArgs = Avalonia.Input.KeyEventArgs;

namespace CursorTranslator.Services;

/// <summary>
/// Classic two-pane translator opened from the tray. It uses the configured AI or
/// HTTP translation service but chooses its own language pair, independent of the
/// monitoring prompt.
/// </summary>
public partial class TranslatorWindow : Window
{
    private static readonly TimeSpan AutoTranslateDelay = TimeSpan.FromMilliseconds(900);
    private readonly Func<AppSettings> _getSettings;
    private readonly TranslationService _translation;
    private readonly Func<string, TranslationProviderKind, Task> _recordUsage;
    private readonly Action<string> _speak;
    private readonly Action<string, string, bool> _savePreferences;
    private readonly DispatcherTimer _autoTranslateTimer = new() { Interval = AutoTranslateDelay };
    private CancellationTokenSource? _translationCancellation;
    private long _translationGeneration;
    private string _lastTranslationKey = "";
    private TranslationLanguage? _lastResolvedTarget;
    private bool _loading;

    public TranslatorWindow()
        : this(() => new AppSettings(), new TranslationService(), (_, _) => Task.CompletedTask, _ => { }, (_, _, _) => { })
    {
    }

    public TranslatorWindow(
        Func<AppSettings> getSettings,
        TranslationService translation,
        Func<string, TranslationProviderKind, Task> recordUsage,
        Action<string> speak,
        Action<string, string, bool> savePreferences)
    {
        InitializeComponent();
        _getSettings = getSettings;
        _translation = translation;
        _recordUsage = recordUsage;
        _speak = speak;
        _savePreferences = savePreferences;

        var settings = getSettings();
        _loading = true;
        SourceLanguageComboBox.ItemsSource = TranslationLanguage.SourceOptions;
        TargetLanguageComboBox.ItemsSource = TranslationLanguage.TargetOptions;
        SourceLanguageComboBox.SelectedItem = FindOption(TranslationLanguage.SourceOptions, settings.TranslatorSourceLanguage);
        TargetLanguageComboBox.SelectedItem = FindOption(TranslationLanguage.TargetOptions, settings.TranslatorTargetLanguage);
        AutoTranslateCheckBox.IsChecked = settings.TranslatorAutoTranslate;
        _loading = false;

        _autoTranslateTimer.Tick += (_, _) => TranslateNow(force: false);
        Opened += (_, _) =>
        {
            RefreshEngineText();
            RefreshDirectionHints();
            SourceTextBox.Focus();
        };
        // Settings can change in the main window while this one stays open.
        Activated += (_, _) => RefreshEngineText();
        Closed += (_, _) =>
        {
            _autoTranslateTimer.Stop();
            CancelTranslation();
        };
    }

    private TranslationLanguage SelectedSource
        => SourceLanguageComboBox.SelectedItem as TranslationLanguage ?? TranslationLanguage.AutoDetect;

    private TranslationLanguage SelectedTarget
        => TargetLanguageComboBox.SelectedItem as TranslationLanguage ?? TranslationLanguage.AutoTarget;

    private static TranslationLanguage FindOption(IReadOnlyList<TranslationLanguage> options, string? code)
        => options.FirstOrDefault(option => string.Equals(option.Code, code, StringComparison.OrdinalIgnoreCase))
            ?? options[0];

    private void WindowSurface_PointerPressed(object? sender, PointerPressedEventArgs e)
        => WindowChrome.BeginMoveDrag(this, e);

    private void Language_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        SavePreferences();
        RefreshDirectionHints();
        if (AutoTranslateCheckBox.IsChecked == true && !string.IsNullOrWhiteSpace(SourceTextBox.Text))
            TranslateNow(force: false);
    }

    private void AutoTranslate_Changed(object? sender, RoutedEventArgs e)
    {
        if (_loading) return;
        SavePreferences();
        if (AutoTranslateCheckBox.IsChecked != true) _autoTranslateTimer.Stop();
    }

    private void SourceText_Changed(object? sender, TextChangedEventArgs e)
    {
        var text = SourceTextBox.Text ?? "";
        SourceCountText.Text = $"{text.Length:N0} / {SourceTextBox.MaxLength:N0}";
        RefreshDirectionHints();
        _autoTranslateTimer.Stop();
        if (string.IsNullOrWhiteSpace(text))
        {
            CancelTranslation();
            _lastTranslationKey = "";
            SetTranslation("");
            SetStatus("");
            return;
        }
        if (AutoTranslateCheckBox.IsChecked == true)
            _autoTranslateTimer.Start();
    }

    private void SourceText_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            e.Handled = true;
            TranslateNow(force: true);
        }
    }

    private void Translate_Click(object? sender, RoutedEventArgs e) => TranslateNow(force: true);

    private void TranslateNow(bool force)
    {
        _autoTranslateTimer.Stop();
        var text = SourceTextBox.Text ?? "";
        if (string.IsNullOrWhiteSpace(text))
        {
            SetStatus("请输入要翻译的文本");
            return;
        }

        var settings = _getSettings();
        var source = SelectedSource;
        var target = SelectedTarget.IsAuto ? TranslationLanguage.ResolveAutoTarget(source, text) : SelectedTarget;
        var engineId = settings.TranslationProvider == TranslationProviderKind.HttpTranslation
            ? settings.ActiveTranslationHttpProfileId
            : settings.ActiveAiProfileId;
        var key = $"{settings.TranslationProvider}|{engineId}|{source.Code}|{target.Code}|{text}";
        // Pauses and language re-selections must not resend an unchanged request.
        if (!force && key == _lastTranslationKey) return;
        _ = TranslateAsync(settings, text, new TranslationDirection(source, target), key);
    }

    private async Task TranslateAsync(AppSettings settings, string text, TranslationDirection direction, string key)
    {
        CancelTranslation();
        RefreshEngineText();
        if (TranslationService.IsUrl(text))
        {
            SetTranslation("");
            SetStatus("网址不会翻译");
            return;
        }
        if (!settings.IsConfigured)
        {
            SetStatus(settings.TranslationProvider == TranslationProviderKind.HttpTranslation
                ? "HTTP 翻译接口尚未配置，请在托盘菜单“打开设置”中完成配置。"
                : "AI 翻译接口尚未配置，请在托盘菜单“打开设置”中填写接口地址和模型。");
            return;
        }

        var cancellation = new CancellationTokenSource();
        _translationCancellation = cancellation;
        var generation = ++_translationGeneration;
        CopyButton.IsEnabled = false;
        SpeakButton.IsEnabled = false;
        // Keep the previous translation visible, dimmed, until new text arrives.
        TargetTextBox.Opacity = 0.55;
        SetStatus($"正在翻译 · {DescribeDirection(direction)}…");
        var stopwatch = Stopwatch.StartNew();
        using var liveOutput = new UiStreamBuffer(partial =>
        {
            if (generation != _translationGeneration) return;
            TargetTextBox.Opacity = 1;
            TargetTextBox.Text = partial;
        });
        try
        {
            var translated = await _translation.TranslateAsync(
                settings,
                text,
                cancellation.Token,
                applyMaximumTranslationLimit: false,
                onDelta: liveOutput.Append,
                direction: direction);
            if (generation != _translationGeneration) return;
            liveOutput.Complete(translated);
            _lastTranslationKey = key;
            _lastResolvedTarget = direction.Target;
            SetTranslation(translated);
            stopwatch.Stop();
            SetStatus($"翻译完成 · {DescribeDirection(direction)} · {stopwatch.Elapsed.TotalMilliseconds:N0} ms");
            if (!string.IsNullOrWhiteSpace(translated))
                await _recordUsage(text, settings.TranslationProvider);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (generation != _translationGeneration) return;
            AppLog.Error("Translator window", "Translation request failed.", ex);
            TargetTextBox.Opacity = 1;
            SetStatus($"翻译失败：{ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_translationCancellation, cancellation))
                _translationCancellation = null;
            cancellation.Dispose();
        }
    }

    private void Swap_Click(object? sender, RoutedEventArgs e)
    {
        var text = SourceTextBox.Text ?? "";
        var newSource = SelectedTarget.IsAuto
            ? _lastResolvedTarget ?? (string.IsNullOrWhiteSpace(text)
                ? null
                : TranslationLanguage.ResolveAutoTarget(SelectedSource, text))
            : SelectedTarget;
        if (newSource is null)
        {
            SetStatus("请先选择具体的译文语种，或先翻译一次再交换。");
            return;
        }
        var newTarget = SelectedSource.IsAuto ? TranslationLanguage.AutoTarget : SelectedSource;

        _loading = true;
        SourceLanguageComboBox.SelectedItem = FindOption(TranslationLanguage.SourceOptions, newSource.Code);
        TargetLanguageComboBox.SelectedItem = FindOption(TranslationLanguage.TargetOptions, newTarget.Code);
        _loading = false;
        SavePreferences();

        var translation = CopyButton.IsEnabled ? TargetTextBox.Text ?? "" : "";
        if (!string.IsNullOrWhiteSpace(translation))
        {
            SetTranslation("");
            SourceTextBox.Text = translation;
        }
        RefreshDirectionHints();
        if (!string.IsNullOrWhiteSpace(SourceTextBox.Text))
            TranslateNow(force: true);
    }

    private void Clear_Click(object? sender, RoutedEventArgs e)
    {
        SourceTextBox.Text = "";
        SourceTextBox.Focus();
    }

    private async void Copy_Click(object? sender, RoutedEventArgs e)
    {
        var text = TargetTextBox.Text;
        if (string.IsNullOrWhiteSpace(text) || Clipboard is null) return;
        try
        {
            await Clipboard.SetTextAsync(text);
            SetStatus("已复制译文");
        }
        catch (Exception ex)
        {
            AppLog.Error("Translator window", "Could not copy the translation.", ex);
            SetStatus($"复制失败：{ex.Message}");
        }
    }

    private void Speak_Click(object? sender, RoutedEventArgs e)
    {
        var text = TargetTextBox.Text;
        if (string.IsNullOrWhiteSpace(text)) return;
        if (!_getSettings().IsSpeechConfigured)
        {
            SetStatus("语音模型尚未配置，请在设置的“语音模型”中完成配置。");
            return;
        }
        _speak(text);
        SetStatus("正在朗读译文");
    }

    private void SetTranslation(string text)
    {
        TargetTextBox.Opacity = 1;
        TargetTextBox.Text = text;
        var hasText = !string.IsNullOrWhiteSpace(text);
        CopyButton.IsEnabled = hasText;
        SpeakButton.IsEnabled = hasText;
        if (!hasText) _lastResolvedTarget = null;
    }

    private void CancelTranslation()
    {
        Interlocked.Increment(ref _translationGeneration);
        var cancellation = _translationCancellation;
        _translationCancellation = null;
        cancellation?.Cancel();
        TargetTextBox.Opacity = 1;
    }

    private void RefreshDirectionHints()
    {
        var text = SourceTextBox.Text ?? "";
        SourceHintText.Text = SelectedSource.IsAuto ? "由翻译服务识别语种" : "";
        TargetHintText.Text = !SelectedTarget.IsAuto
            ? ""
            : string.IsNullOrWhiteSpace(text)
                ? "中文译为英语，其他语种译为中文"
                : $"本次译为{TranslationLanguage.ResolveAutoTarget(SelectedSource, text).DisplayName}";
    }

    private void RefreshEngineText()
    {
        var settings = _getSettings();
        EngineText.Text = settings.TranslationProvider == TranslationProviderKind.HttpTranslation
            ? $"HTTP 翻译 · {settings.ActiveTranslationHttpProfile?.Name ?? "未选择档案"}"
            : $"AI 翻译 · {settings.ActiveAiProfile?.Name ?? "未选择接口"}"
                + (string.IsNullOrWhiteSpace(settings.Model) ? "" : $"（{settings.Model}）");
    }

    private static string DescribeDirection(TranslationDirection direction)
        => $"{(direction.Source.IsAuto ? "自动检测" : direction.Source.DisplayName)} → {direction.Target.DisplayName}";

    private void SavePreferences()
        => _savePreferences(SelectedSource.Code, SelectedTarget.Code, AutoTranslateCheckBox.IsChecked == true);

    private void SetStatus(string text) => StatusText.Text = text;
}
