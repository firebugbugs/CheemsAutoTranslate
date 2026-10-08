using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CursorTranslator.Models;
using System.Globalization;

namespace CursorTranslator.Services;

public partial class SettingsWindow : Window
{
    private readonly DispatcherTimer _autoSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool _updatingTriggerControls;
    private bool _normalizingMaximumTranslationCharacters;
    private decimal _lastValidInactivityDelaySeconds;
    private int _lastValidMaximumTranslationCharacters;
    private int _lastValidOverlayFocusLossCloseDelaySeconds;

    public event Action<bool, bool, bool, bool, decimal, int, bool, int>? PreferencesChanged;

    public SettingsWindow() : this(new AppSettings()) { }

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        _lastValidInactivityDelaySeconds = settings.InactivityDelaySeconds;
        _lastValidMaximumTranslationCharacters = settings.MaximumTranslationCharacters;
        _lastValidOverlayFocusLossCloseDelaySeconds = Math.Clamp(
            settings.OverlayFocusLossCloseDelaySeconds,
            AppSettings.MinimumOverlayFocusLossDelaySeconds,
            AppSettings.MaximumOverlayFocusLossDelaySeconds);

        _updatingTriggerControls = true;
        TranslateOnTextChangeCheckBox.IsChecked = settings.TranslateOnTextChange;
        TranslateOnSentenceEndCheckBox.IsChecked = settings.TranslateOnSentenceEnd;
        TranslateAfterCopyCheckBox.IsChecked = settings.TranslateAfterCopy;
        TranslateAfterInactivityCheckBox.IsChecked = settings.TranslateAfterInactivity;
        OverlayFocusLossCloseDelayCheckBox.IsChecked = settings.OverlayFocusLossCloseDelayEnabled;
        InactivityDelaySecondsBox.Text = Math.Clamp(
            settings.InactivityDelaySeconds,
            AppSettings.MinimumInactivityDelaySeconds,
            AppSettings.MaximumInactivityDelaySeconds).ToString("0.##", CultureInfo.InvariantCulture);
        MaximumTranslationCharactersBox.Text = Math.Clamp(
            settings.MaximumTranslationCharacters,
            AppSettings.MinimumMaximumTranslationCharacters,
            AppSettings.MaximumMaximumTranslationCharacters).ToString(CultureInfo.InvariantCulture);
        OverlayFocusLossCloseDelaySecondsBox.Text = _lastValidOverlayFocusLossCloseDelaySeconds.ToString(CultureInfo.InvariantCulture);

        if (TranslateOnTextChangeCheckBox.IsChecked != true
            && TranslateOnSentenceEndCheckBox.IsChecked != true
            && TranslateAfterCopyCheckBox.IsChecked != true
            && TranslateAfterInactivityCheckBox.IsChecked != true)
            TranslateOnTextChangeCheckBox.IsChecked = true;

        if (TranslateOnTextChangeCheckBox.IsChecked == true)
        {
            TranslateOnSentenceEndCheckBox.IsChecked = false;
            TranslateAfterInactivityCheckBox.IsChecked = false;
        }

        _updatingTriggerControls = false;
        RefreshTriggerControls();
        AttachAutoSaveHandlers();
        _autoSaveTimer.Tick += AutoSaveTimer_Tick;
        Closed += (_, _) =>
        {
            _autoSaveTimer.Stop();
            SavePreferences();
        };
    }

    private void AttachAutoSaveHandlers()
    {
        InactivityDelaySecondsBox.TextChanged += SettingsText_Changed;
        OverlayFocusLossCloseDelaySecondsBox.TextChanged += SettingsText_Changed;
        OverlayFocusLossCloseDelaySecondsBox.TextInput += OverlayFocusLossCloseDelaySecondsBox_TextInput;
        OverlayFocusLossCloseDelaySecondsBox.LostFocus += OverlayFocusLossCloseDelaySecondsBox_LostFocus;
        MaximumTranslationCharactersBox.TextChanged += MaximumTranslationCharactersBox_TextChanged;
        MaximumTranslationCharactersBox.TextInput += MaximumTranslationCharactersBox_TextInput;
        MaximumTranslationCharactersBox.LostFocus += MaximumTranslationCharactersBox_LostFocus;
    }

    private void TranslationTriggerOption_Changed(object? sender, RoutedEventArgs e)
    {
        if (_updatingTriggerControls) return;

        _updatingTriggerControls = true;
        if (ReferenceEquals(sender, TranslateOnTextChangeCheckBox)
            && TranslateOnTextChangeCheckBox.IsChecked == true)
        {
            TranslateOnSentenceEndCheckBox.IsChecked = false;
            TranslateAfterInactivityCheckBox.IsChecked = false;
        }
        else if (ReferenceEquals(sender, TranslateAfterCopyCheckBox))
        {
            // Clipboard monitoring is independent and may be enabled alongside
            // any input-field trigger, including immediate text-change mode.
        }
        else if (sender is Avalonia.Controls.CheckBox changedCheckBox && changedCheckBox.IsChecked == true)
        {
            TranslateOnTextChangeCheckBox.IsChecked = false;
        }

        if (TranslateOnTextChangeCheckBox.IsChecked != true
            && TranslateOnSentenceEndCheckBox.IsChecked != true
            && TranslateAfterCopyCheckBox.IsChecked != true
            && TranslateAfterInactivityCheckBox.IsChecked != true)
            TranslateOnSentenceEndCheckBox.IsChecked = true;

        _updatingTriggerControls = false;
        RefreshTriggerControls();
        ScheduleAutoSave();
    }

    private void RefreshTriggerControls()
    {
        var immediateMode = TranslateOnTextChangeCheckBox.IsChecked == true;
        ConditionalTriggerStatusText.Text = immediateMode ? "实时模式下暂停" : "可组合";
        ConditionalTriggersPanel.Opacity = immediateMode ? 0.68 : 1;
        TranslateOnSentenceEndCheckBox.IsEnabled = !immediateMode;
        TranslateAfterCopyCheckBox.IsEnabled = true;
        TranslateAfterInactivityCheckBox.IsEnabled = !immediateMode;
        InactivityDelaySecondsBox.IsEnabled = !immediateMode && TranslateAfterInactivityCheckBox.IsChecked == true;
        OverlayFocusLossCloseDelaySecondsBox.IsEnabled = OverlayFocusLossCloseDelayCheckBox.IsChecked == true;
    }

    private void OverlayFocusLossCloseDelay_Changed(object? sender, RoutedEventArgs e)
    {
        if (_updatingTriggerControls) return;
        RefreshTriggerControls();
        ScheduleAutoSave();
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

    private void MaximumTranslationCharactersBox_LostFocus(object? sender, RoutedEventArgs e)
    {
        var maximumCharacters = int.TryParse(
                MaximumTranslationCharactersBox.Text?.Trim(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var parsedMaximumCharacters)
            ? parsedMaximumCharacters
            : _lastValidMaximumTranslationCharacters;
        MaximumTranslationCharactersBox.Text = Math.Clamp(
            maximumCharacters,
            AppSettings.MinimumMaximumTranslationCharacters,
            AppSettings.MaximumMaximumTranslationCharacters).ToString(CultureInfo.InvariantCulture);
    }

    private void SettingsText_Changed(object? sender, TextChangedEventArgs e)
        => ScheduleAutoSave();

    private void OverlayFocusLossCloseDelaySecondsBox_TextInput(object? sender, TextInputEventArgs e)
    {
        if (e.Text is { } input && input.Any(character => !char.IsAsciiDigit(character)))
            e.Handled = true;
    }

    private void OverlayFocusLossCloseDelaySecondsBox_LostFocus(object? sender, RoutedEventArgs e)
    {
        var delaySeconds = int.TryParse(
                OverlayFocusLossCloseDelaySecondsBox.Text?.Trim(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var parsedDelay)
            ? parsedDelay
            : _lastValidOverlayFocusLossCloseDelaySeconds;
        OverlayFocusLossCloseDelaySecondsBox.Text = Math.Clamp(
            delaySeconds,
            AppSettings.MinimumOverlayFocusLossDelaySeconds,
            AppSettings.MaximumOverlayFocusLossDelaySeconds).ToString(CultureInfo.InvariantCulture);
    }

    private void ScheduleAutoSave()
    {
        _autoSaveTimer.Stop();
        _autoSaveTimer.Start();
    }

    private void AutoSaveTimer_Tick(object? sender, EventArgs e)
    {
        _autoSaveTimer.Stop();
        SavePreferences();
    }

    private void SavePreferences()
    {
        var inactivityDelaySeconds = decimal.TryParse(
                InactivityDelaySecondsBox.Text?.Trim(),
                NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out var parsedDelay)
            && parsedDelay >= AppSettings.MinimumInactivityDelaySeconds
            && parsedDelay <= AppSettings.MaximumInactivityDelaySeconds
                ? _lastValidInactivityDelaySeconds = parsedDelay
                : _lastValidInactivityDelaySeconds;
        var maximumTranslationCharacters = int.TryParse(
                MaximumTranslationCharactersBox.Text?.Trim(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var parsedMaximumCharacters)
            && parsedMaximumCharacters >= AppSettings.MinimumMaximumTranslationCharacters
            && parsedMaximumCharacters <= AppSettings.MaximumMaximumTranslationCharacters
                ? _lastValidMaximumTranslationCharacters = parsedMaximumCharacters
                : _lastValidMaximumTranslationCharacters;
        var overlayFocusLossCloseDelaySeconds = int.TryParse(
                OverlayFocusLossCloseDelaySecondsBox.Text?.Trim(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var parsedOverlayFocusLossCloseDelay)
            && parsedOverlayFocusLossCloseDelay >= AppSettings.MinimumOverlayFocusLossDelaySeconds
            && parsedOverlayFocusLossCloseDelay <= AppSettings.MaximumOverlayFocusLossDelaySeconds
                ? _lastValidOverlayFocusLossCloseDelaySeconds = parsedOverlayFocusLossCloseDelay
                : _lastValidOverlayFocusLossCloseDelaySeconds;

        PreferencesChanged?.Invoke(
            TranslateOnTextChangeCheckBox.IsChecked == true,
            TranslateOnSentenceEndCheckBox.IsChecked == true,
            TranslateAfterCopyCheckBox.IsChecked == true,
            TranslateAfterInactivityCheckBox.IsChecked == true,
            inactivityDelaySeconds,
            maximumTranslationCharacters,
            OverlayFocusLossCloseDelayCheckBox.IsChecked == true,
            overlayFocusLossCloseDelaySeconds);
    }
}
