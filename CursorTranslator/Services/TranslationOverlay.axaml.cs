using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CursorTranslator.Models;

namespace CursorTranslator.Services;

public partial class TranslationOverlay : Window
{
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private bool _windowStyleConfigured;
    private bool _positionPinned;
    private bool _dragCandidate;

    public event Action? SettingsRequested;
    public event Action<string>? AiQuestionRequested;

    public TranslationOverlay()
    {
        InitializeComponent();
        Opened += (_, _) => ConfigureWindowStyle();
        PositionChanged += (_, _) =>
        {
            if (_dragCandidate)
                _positionPinned = true;
        };
    }

    public void ShowAt(PixelRect caret, string text)
    {
        TranslationText.Text = text;
        if (!IsVisible) Show();
        ConfigureWindowStyle();

        if (_positionPinned) return;

        PositionAtCaret(caret);
        // SizeToContent updates after the text layout pass. Reposition once the new
        // card height is known so every translation stays anchored to the current input.
        Dispatcher.UIThread.Post(() =>
        {
            if (IsVisible && !_positionPinned)
                PositionAtCaret(caret);
        }, DispatcherPriority.Render);
    }

    private void PositionAtCaret(PixelRect caret)
    {
        var point = new PixelPoint(caret.X, caret.Y);
        var screen = Screens.ScreenFromPoint(point) ?? Screens.Primary;
        var scaling = screen?.Scaling ?? 1.0;
        var work = screen?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);
        var overlayWidth = (int)(Math.Min(Width, 440) * scaling);
        var overlayHeight = (int)(Height * scaling);
        var below = caret.Bottom + overlayHeight <= work.Bottom;
        var above = caret.Y - overlayHeight >= work.Y;
        var y = below ? caret.Bottom + 4 : above ? caret.Y - overlayHeight - 4 : caret.Y;
        var x = Math.Clamp(caret.X, work.X, Math.Max(work.X, work.Right - overlayWidth));
        Position = new PixelPoint(x, Math.Clamp(y, work.Y, Math.Max(work.Y, work.Bottom - overlayHeight)));
    }

    public void ApplyAppearance(CardAppearanceSettings settings)
    {
        Opacity = Math.Clamp(settings.Opacity, 0.25, 1.0);
        var (background, foreground, border) = settings.Theme switch
        {
            "Light" => ("#FFFDF7", "#172033", "#CBD5E1"),
            "Blue" => ("#0B3B5A", "#F0F9FF", "#38BDF8"),
            "Green" => ("#12372A", "#ECFDF5", "#34D399"),
            _ => ("#111827", "#FFFFFF", "#607AEE")
        };
        CardBorder.Background = new SolidColorBrush(Avalonia.Media.Color.Parse(background));
        CardBorder.BorderBrush = new SolidColorBrush(Avalonia.Media.Color.Parse(border));
        TranslationText.Foreground = new SolidColorBrush(Avalonia.Media.Color.Parse(foreground));
        var iconBrush = new SolidColorBrush(Avalonia.Media.Color.Parse(foreground));
        SettingsIcon.Fill = iconBrush;
        CloseIcon.Fill = iconBrush;
    }

    private void ConfigureWindowStyle()
    {
        if (_windowStyleConfigured || TryGetPlatformHandle() is not { } handle) return;
        var current = GetWindowLongPtr(handle.Handle, GwlExStyle).ToInt64();
        SetWindowLongPtr(handle.Handle, GwlExStyle, new IntPtr((current | WsExToolWindow | WsExNoActivate) & ~0x00000020));
        _windowStyleConfigured = true;
    }

    private void Card_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            || e.Source is Avalonia.Controls.Control source
                && (source.FindAncestorOfType<Avalonia.Controls.Button>(includeSelf: true) is not null
                    || source.FindAncestorOfType<SelectableTextBlock>(includeSelf: true) is not null))
            return;
        _dragCandidate = true;
        e.Handled = true;
        BeginMoveDrag(e);
    }

    private void Card_PointerReleased(object? sender, PointerReleasedEventArgs e)
        => _dragCandidate = false;

    private void SettingsButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // Open a second top-level window only after the non-activating overlay's
        // routed click has completed; this avoids native window activation reentrancy.
        Dispatcher.UIThread.Post(() => SettingsRequested?.Invoke(), DispatcherPriority.Background);
    }

    private async void CopyTranslation_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var text = GetSelectedOrFullTranslation();
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
            ? TranslationText.Text ?? ""
            : TranslationText.SelectedText;

    private void CloseButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _positionPinned = false;
        Hide();
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
}
