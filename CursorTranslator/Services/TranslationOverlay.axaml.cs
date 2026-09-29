using System.Runtime.InteropServices;
using System.Drawing.Text;
using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CursorTranslator.Models;

namespace CursorTranslator.Services;

public partial class TranslationOverlay : Window
{
    private const double MinTranslationViewportHeight = 38;
    private const double MaxTranslationViewportHeight = 420;
    private const double LayoutTransitionDurationMilliseconds = 120;
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private bool _windowStyleConfigured;
    private bool _positionLocked;
    private bool _hasLockedPosition;
    private PixelPoint _lockedPosition;
    private bool _dragCandidate;
    private bool _updatingAppearanceControls;
    private bool _appearanceControlsInitialized;
    private PixelRect? _lastCaret;
    private readonly HashSet<string> _availableFontFamilies = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _layoutAnimationTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly DispatcherTimer _layoutCorrectionTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly DispatcherTimer _positionSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly Stopwatch _layoutAnimationClock = new();
    private double _animationStartHeight;
    private double _animationTargetHeight;
    private double _animationStartViewportHeight;
    private double _animationTargetViewportHeight;
    private PixelPoint _animationStartPosition;
    private PixelPoint _animationTargetPosition;

    private const string SystemDefaultFontDisplayName = "系统默认";

    public event Action<IntPtr>? NativeWindowHandleAvailable;
    public event Action? UserInteraction;
    public event Action<CardAppearanceSettings>? AppearanceChanged;
    public event Action<string>? AiQuestionRequested;
    public event Action<string>? SpeechRequested;

    public TranslationOverlay()
    {
        InitializeComponent();
        AddHandler(
            Avalonia.Input.InputElement.PointerPressedEvent,
            (_, e) =>
            {
                if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                    UserInteraction?.Invoke();
            },
            Avalonia.Interactivity.RoutingStrategies.Tunnel,
            handledEventsToo: true);
        InitializeFontFamilies();
        _appearanceControlsInitialized = true;
        _layoutAnimationTimer.Tick += LayoutAnimationTimer_Tick;
        _layoutCorrectionTimer.Tick += LayoutCorrectionTimer_Tick;
        _positionSaveTimer.Tick += (_, _) => SaveLockedPosition();
        Opened += (_, _) => ConfigureWindowStyle();
        PositionChanged += (_, _) =>
        {
            if (!_dragCandidate || !_positionLocked) return;
            _lockedPosition = Position;
            _hasLockedPosition = true;
            _positionSaveTimer.Stop();
            _positionSaveTimer.Start();
        };
    }

    public void ShowAt(PixelRect caret, string text)
    {
        _lastCaret = caret;
        TranslationRun.Text = text;
        var target = MeasureContentLayout();
        if (!IsVisible)
        {
            StopLayoutAnimation();
            TranslationScrollViewer.Height = target.ViewportHeight;
            Height = target.WindowHeight;
            if (_positionLocked && _hasLockedPosition)
                Position = _lockedPosition;
            else
            {
                Position = GetPositionAtCaret(caret, target.WindowHeight);
                if (_positionLocked)
                {
                    _lockedPosition = Position;
                    _hasLockedPosition = true;
                    SaveLockedPosition();
                }
            }
            Show();
            ConfigureWindowStyle();
            ScheduleLayoutCorrection();
            return;
        }

        var targetPosition = _positionLocked
            ? Position
            : GetPositionAtCaret(caret, target.WindowHeight);
        AnimateLayout(target.ViewportHeight, target.WindowHeight, targetPosition);
        ConfigureWindowStyle();
        ScheduleLayoutCorrection();
    }

    private (double ViewportHeight, double WindowHeight) MeasureContentLayout()
    {
        var previousViewportHeight = TranslationScrollViewer.Height;
        try
        {
            // Measure the text itself with the width it actually receives. Measuring
            // ScrollViewer.DesiredSize is unreliable here: an unbounded-height
            // ScrollViewer may report only its minimum viewport, leaving the Window
            // at its minimum height even while the text overflows visibly.
            var textWidth = GetTranslationTextMeasureWidth();
            TranslationText.InvalidateMeasure();
            TranslationText.Measure(new Avalonia.Size(textWidth, double.PositiveInfinity));
            var naturalViewportHeight = Math.Max(
                MinTranslationViewportHeight,
                Math.Ceiling(TranslationText.DesiredSize.Height));

            // Measure the fixed card chrome separately at the minimum viewport,
            // then use it to determine how much room remains below the Window cap.
            TranslationScrollViewer.Height = MinTranslationViewportHeight;
            CardBorder.InvalidateMeasure();
            var measureSize = new Avalonia.Size(Width, double.PositiveInfinity);
            CardBorder.Measure(measureSize);
            var nonTranslationHeight = Math.Max(
                0,
                CardBorder.DesiredSize.Height - TranslationScrollViewer.DesiredSize.Height);
            var heightAvailableWithinWindow = Math.Max(
                MinTranslationViewportHeight,
                MaxHeight - nonTranslationHeight);
            var viewportHeight = Math.Clamp(
                Math.Ceiling(naturalViewportHeight),
                MinTranslationViewportHeight,
                Math.Min(MaxTranslationViewportHeight, heightAvailableWithinWindow));

            TranslationScrollViewer.Height = viewportHeight;
            CardBorder.InvalidateMeasure();
            CardBorder.Measure(measureSize);
            var windowHeight = Math.Clamp(
                Math.Ceiling(CardBorder.DesiredSize.Height),
                MinHeight,
                MaxHeight);

            return (viewportHeight, windowHeight);
        }
        finally
        {
            TranslationScrollViewer.Height = previousViewportHeight;
        }
    }

    private double GetTranslationTextMeasureWidth()
    {
        // Bounds is the arranged text surface, excluding its Margin. Add the
        // margins back because Measure receives the full allocated slot.
        if (TranslationText.Bounds.Width > 0)
            return TranslationText.Bounds.Width
                + TranslationText.Margin.Left + TranslationText.Margin.Right;
        if (TranslationScrollViewer.Bounds.Width > 0)
            return TranslationScrollViewer.Bounds.Width;

        // The window width is fixed, so this fallback is only needed on its first
        // show, before the Grid has arranged its columns.
        ActionButtonsPanel.Measure(new Avalonia.Size(double.PositiveInfinity, double.PositiveInfinity));
        var horizontalChrome = CardBorder.Padding.Left + CardBorder.Padding.Right
            + CardBorder.BorderThickness.Left + CardBorder.BorderThickness.Right
            + ActionButtonsPanel.DesiredSize.Width;
        return Math.Max(1, Width - horizontalChrome);
    }

    private PixelPoint GetPositionAtCaret(PixelRect caret, double windowHeight)
    {
        var point = new PixelPoint(caret.X, caret.Y);
        var screen = Screens.ScreenFromPoint(point) ?? Screens.Primary;
        var scaling = screen?.Scaling ?? 1.0;
        var work = screen?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);
        var overlayWidth = (int)(Width * scaling);
        var overlayHeight = (int)(windowHeight * scaling);
        var below = caret.Bottom + overlayHeight <= work.Bottom;
        var above = caret.Y - overlayHeight >= work.Y;
        var y = below ? caret.Bottom + 4 : above ? caret.Y - overlayHeight - 4 : caret.Y;
        var x = Math.Clamp(caret.X, work.X, Math.Max(work.X, work.Right - overlayWidth));
        return new PixelPoint(x, Math.Clamp(y, work.Y, Math.Max(work.Y, work.Bottom - overlayHeight)));
    }

    private void AnimateContentLayout(PixelRect? caret)
    {
        var target = MeasureContentLayout();
        var targetPosition = !_positionLocked && caret is { } latestCaret
            ? GetPositionAtCaret(latestCaret, target.WindowHeight)
            : Position;
        if (!IsVisible)
        {
            StopLayoutAnimation();
            TranslationScrollViewer.Height = target.ViewportHeight;
            Height = target.WindowHeight;
            if (_positionLocked && _hasLockedPosition && IsVisible)
                Position = _lockedPosition;
            else if (!_positionLocked && caret is { } hiddenCaret)
                Position = GetPositionAtCaret(hiddenCaret, target.WindowHeight);
            return;
        }

        AnimateLayout(target.ViewportHeight, target.WindowHeight, targetPosition);
    }

    private void AnimateLayout(double viewportHeight, double windowHeight, PixelPoint position)
    {
        StopLayoutAnimation();
        _animationStartHeight = Height;
        _animationTargetHeight = windowHeight;
        _animationStartViewportHeight = TranslationScrollViewer.Height;
        _animationTargetViewportHeight = viewportHeight;
        _animationStartPosition = Position;
        _animationTargetPosition = position;

        if (Math.Abs(_animationStartHeight - windowHeight) < 0.5
            && Math.Abs(_animationStartViewportHeight - viewportHeight) < 0.5
            && _animationStartPosition == position)
        {
            Height = windowHeight;
            TranslationScrollViewer.Height = viewportHeight;
            return;
        }

        _layoutAnimationClock.Restart();
        _layoutAnimationTimer.Start();
    }

    private void LayoutAnimationTimer_Tick(object? sender, EventArgs e)
    {
        var progress = Math.Clamp(
            _layoutAnimationClock.Elapsed.TotalMilliseconds / LayoutTransitionDurationMilliseconds,
            0,
            1);
        var easedProgress = 1 - Math.Pow(1 - progress, 3);
        Height = Lerp(_animationStartHeight, _animationTargetHeight, easedProgress);
        TranslationScrollViewer.Height = Lerp(
            _animationStartViewportHeight,
            _animationTargetViewportHeight,
            easedProgress);
        Position = new PixelPoint(
            (int)Math.Round(Lerp(_animationStartPosition.X, _animationTargetPosition.X, easedProgress)),
            (int)Math.Round(Lerp(_animationStartPosition.Y, _animationTargetPosition.Y, easedProgress)));

        if (progress < 1) return;
        StopLayoutAnimation();
        Height = _animationTargetHeight;
        TranslationScrollViewer.Height = _animationTargetViewportHeight;
        Position = _animationTargetPosition;
    }

    private void ScheduleLayoutCorrection()
    {
        _layoutCorrectionTimer.Stop();
        _layoutCorrectionTimer.Start();
    }

    private void LayoutCorrectionTimer_Tick(object? sender, EventArgs e)
    {
        _layoutCorrectionTimer.Stop();
        if (!IsVisible) return;

        // Run a second measurement after Avalonia has had time to settle the new
        // Run text, inline copy button, and wrapped line layout. This corrects the
        // first-pass size if the final text metrics differ from the immediate pass.
        TranslationText.InvalidateMeasure();
        TranslationScrollViewer.InvalidateMeasure();
        CardBorder.InvalidateMeasure();
        Dispatcher.UIThread.Post(() =>
        {
            if (IsVisible)
                AnimateContentLayout(_lastCaret);
        }, DispatcherPriority.Render);
    }

    private static double Lerp(double start, double end, double progress)
        => start + ((end - start) * progress);

    private void StopLayoutAnimation()
    {
        _layoutAnimationTimer.Stop();
        _layoutAnimationClock.Stop();
    }

    public void HideOverlay()
    {
        _layoutCorrectionTimer.Stop();
        StopLayoutAnimation();
        Hide();
    }

    public void ApplyAppearance(CardAppearanceSettings settings)
    {
        _updatingAppearanceControls = true;
        try
        {
            Opacity = Math.Clamp(settings.Opacity, 0.25, 1.0);
            OpacitySlider.Value = Opacity;
            _positionLocked = settings.IsPositionLocked;
            _hasLockedPosition = settings.HasLockedPosition;
            _lockedPosition = new PixelPoint(settings.LockedPositionX, settings.LockedPositionY);
            PositionLockToggle.IsChecked = _positionLocked;
            UpdatePositionLockVisualState();
            if (_positionLocked && _hasLockedPosition)
                Position = _lockedPosition;
            ThemeBox.SelectedIndex = settings.Theme switch
            {
                "Light" => 1,
                "Blue" => 2,
                "Green" => 3,
                "Dark" => 0,
                _ => 1
            };

            settings.FontSize = Math.Clamp(settings.FontSize, 10, 48);
            settings.FontFamily ??= "";
            if (settings.FontFamily.Length > 0 && !_availableFontFamilies.Contains(settings.FontFamily))
                settings.FontFamily = "";
            FontSizeSlider.Value = settings.FontSize;
            FontSizeValue.Text = settings.FontSize.ToString("0", CultureInfo.InvariantCulture);
            FontFamilyBox.SelectedItem = settings.FontFamily.Length == 0
                ? SystemDefaultFontDisplayName
                : settings.FontFamily;
            FontBoldCheckBox.IsChecked = settings.IsBold;
            TranslationText.FontSize = settings.FontSize;
            TranslationText.FontWeight = settings.IsBold ? FontWeight.Bold : FontWeight.Normal;
            if (settings.FontFamily.Length == 0)
                TranslationText.ClearValue(TextBlock.FontFamilyProperty);
            else
                TranslationText.FontFamily = new Avalonia.Media.FontFamily(settings.FontFamily);

            var (background, foreground, border) = settings.Theme switch
            {
                "Light" => ("#FAF8F5", "#302B27", "#DCD2C8"),
                "Blue" => ("#0B3B5A", "#F0F9FF", "#38BDF8"),
                "Green" => ("#12372A", "#ECFDF5", "#34D399"),
                _ => ("#111827", "#FFFFFF", "#607AEE")
            };
            CardBorder.Background = new SolidColorBrush(Avalonia.Media.Color.Parse(background));
            CardBorder.BorderBrush = new SolidColorBrush(Avalonia.Media.Color.Parse(border));
            TranslationText.Foreground = new SolidColorBrush(Avalonia.Media.Color.Parse(foreground));
            var iconBrush = new SolidColorBrush(Avalonia.Media.Color.Parse(foreground));
            SpeakerIcon.Fill = iconBrush;
            CopyIcon.Fill = iconBrush;
            SettingsIcon.Fill = iconBrush;
            PositionLockIcon.Fill = iconBrush;
            CloseIcon.Fill = iconBrush;
        }
        finally
        {
            _updatingAppearanceControls = false;
        }

        ScheduleAppearanceLayout();
    }

    private void InitializeFontFamilies()
    {
        var families = new List<string>();
        try
        {
            using var installedFonts = new InstalledFontCollection();
            families = installedFonts.Families
                .Select(family => family.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        catch
        {
            // Keep the system default available if Windows cannot enumerate fonts.
        }

        _availableFontFamilies.UnionWith(families);
        FontFamilyBox.ItemsSource = new[] { SystemDefaultFontDisplayName }.Concat(families).ToArray();
        FontFamilyBox.SelectedItem = SystemDefaultFontDisplayName;
    }

    private void ScheduleAppearanceLayout()
    {
        if (!IsVisible) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsVisible) return;
            AnimateContentLayout(_lastCaret);
        }, DispatcherPriority.Render);
    }

    private void ConfigureWindowStyle()
    {
        if (TryGetPlatformHandle() is not { } handle) return;
        NativeWindowHandleAvailable?.Invoke(handle.Handle);
        if (_windowStyleConfigured) return;
        var current = GetWindowLongPtr(handle.Handle, GwlExStyle).ToInt64();
        SetWindowLongPtr(handle.Handle, GwlExStyle, new IntPtr((current | WsExToolWindow | WsExNoActivate) & ~0x00000020));
        _windowStyleConfigured = true;
    }

    private void Card_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            || e.Source is Avalonia.Controls.Control source
                && IsInteractiveControl(source))
            return;
        StopLayoutAnimation();
        _dragCandidate = true;
        e.Handled = true;
        BeginMoveDrag(e);
    }

    private static bool IsInteractiveControl(Avalonia.Controls.Control source)
        => source.FindAncestorOfType<Avalonia.Controls.Button>(includeSelf: true) is not null
            || source.FindAncestorOfType<ToggleButton>(includeSelf: true) is not null
            || source.FindAncestorOfType<Avalonia.Controls.CheckBox>(includeSelf: true) is not null
            || source.FindAncestorOfType<Avalonia.Controls.ComboBox>(includeSelf: true) is not null
            || source.FindAncestorOfType<Avalonia.Controls.ComboBoxItem>(includeSelf: true) is not null
            || source.FindAncestorOfType<Avalonia.Controls.Slider>(includeSelf: true) is not null
            || source.FindAncestorOfType<Avalonia.Controls.TextBox>(includeSelf: true) is not null
            || source.FindAncestorOfType<SelectableTextBlock>(includeSelf: true) is not null;

    private void Card_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var wasDragging = _dragCandidate;
        _dragCandidate = false;
        if (wasDragging && _positionLocked)
            SaveLockedPosition();
    }

    private void PositionLockToggle_Changed(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_updatingAppearanceControls || !_appearanceControlsInitialized) return;
        _positionLocked = PositionLockToggle.IsChecked == true;
        UpdatePositionLockVisualState();
        if (_positionLocked)
        {
            _lockedPosition = Position;
            _hasLockedPosition = true;
        }
        else
        {
            _positionSaveTimer.Stop();
        }

        AppearanceChanged?.Invoke(CaptureAppearanceSettings());
        e.Handled = true;
    }

    private void UpdatePositionLockVisualState()
    {
        PositionLockIcon.Opacity = _positionLocked ? 1 : 0.52;
        Avalonia.Controls.ToolTip.SetTip(PositionLockToggle, _positionLocked ? "解锁浮层位置" : "锁定浮层位置");
    }

    private void SaveLockedPosition()
    {
        _positionSaveTimer.Stop();
        if (!_positionLocked || !_hasLockedPosition) return;
        _lockedPosition = Position;
        AppearanceChanged?.Invoke(CaptureAppearanceSettings());
    }

    private void SettingsButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        AppearancePanel.IsVisible = !AppearancePanel.IsVisible;
        e.Handled = true;
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsVisible) return;
            AnimateContentLayout(_lastCaret);
        }, DispatcherPriority.Render);
    }

    private void OpacitySlider_ValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
        => UpdateAppearanceFromControls();

    private void ThemeBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        => UpdateAppearanceFromControls();

    private void FontSizeSlider_ValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        FontSizeValue.Text = FontSizeSlider.Value.ToString("0", CultureInfo.InvariantCulture);
        UpdateAppearanceFromControls();
    }

    private void FontFamilyBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        => UpdateAppearanceFromControls();

    private void FontBoldCheckBox_Changed(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => UpdateAppearanceFromControls();

    private void UpdateAppearanceFromControls()
    {
        if (!_appearanceControlsInitialized || _updatingAppearanceControls) return;
        var theme = (ThemeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Light";
        var selectedFont = FontFamilyBox.SelectedItem as string;
        var settings = CaptureAppearanceSettings(theme, selectedFont);
        ApplyAppearance(settings);
        AppearanceChanged?.Invoke(settings);
    }

    private CardAppearanceSettings CaptureAppearanceSettings(string? theme = null, string? selectedFont = null)
    {
        theme ??= (ThemeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Light";
        selectedFont ??= FontFamilyBox.SelectedItem as string;
        return new CardAppearanceSettings
        {
            Opacity = OpacitySlider.Value,
            Theme = theme,
            FontSize = FontSizeSlider.Value,
            FontFamily = selectedFont == SystemDefaultFontDisplayName ? "" : selectedFont ?? "",
            IsBold = FontBoldCheckBox.IsChecked == true,
            IsPositionLocked = _positionLocked,
            HasLockedPosition = _hasLockedPosition,
            LockedPositionX = _lockedPosition.X,
            LockedPositionY = _lockedPosition.Y
        };
    }

    private async void CopyTranslation_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await CopyTextToClipboardAsync(GetSelectedOrFullTranslation());

    private void SpeakerButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var text = TranslationRun.Text ?? "";
        if (!string.IsNullOrWhiteSpace(text))
            SpeechRequested?.Invoke(text);
        e.Handled = true;
    }

    private async void CopyAllTranslation_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await CopyTextToClipboardAsync(TranslationRun.Text ?? "");

    private async Task CopyTextToClipboardAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is not null)
                await clipboard.SetTextAsync(text);
        }
        catch (Exception)
        {
            // Clipboard access can be temporarily unavailable while another app owns it.
        }
    }

    private void AskAi_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var text = GetSelectedOrFullTranslation();
        if (!string.IsNullOrWhiteSpace(text))
            AiQuestionRequested?.Invoke(text);
    }

    private string GetSelectedOrFullTranslation()
        => string.IsNullOrWhiteSpace(TranslationText.SelectedText)
            ? TranslationRun.Text ?? ""
            : TranslationText.SelectedText;

    private void CloseButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _layoutCorrectionTimer.Stop();
        StopLayoutAnimation();
        SaveLockedPosition();
        Hide();
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
}
