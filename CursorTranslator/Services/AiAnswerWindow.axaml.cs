using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace CursorTranslator.Services;

public partial class AiAnswerWindow : Window
{
    public AiAnswerWindow()
    {
        InitializeComponent();
    }

    public void ShowPending(TranslationOverlay card)
    {
        AnswerText.Text = "正在等待 AI 回答…";
        if (!IsVisible)
            Show();

        PositionNear(card);
        Dispatcher.UIThread.Post(() =>
        {
            if (IsVisible)
                PositionNear(card);
        }, DispatcherPriority.Render);
    }

    public void ShowAnswer(string answer) => AnswerText.Text = answer;

    public void ShowError(string error) => AnswerText.Text = $"AI 暂时无法回答：{error}";

    private void PositionNear(TranslationOverlay card)
    {
        var screen = Screens.ScreenFromPoint(card.Position) ?? Screens.Primary;
        var scaling = screen?.Scaling ?? 1.0;
        var work = screen?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);
        var cardHeight = (int)(card.Bounds.Height * scaling);
        var popupWidth = (int)(Bounds.Width * scaling);
        var popupHeight = (int)(Bounds.Height * scaling);
        var x = Math.Clamp(card.Position.X, work.X, Math.Max(work.X, work.Right - popupWidth));
        var below = card.Position.Y + cardHeight + popupHeight + 8 <= work.Bottom;
        var y = below
            ? card.Position.Y + cardHeight + 8
            : Math.Max(work.Y, card.Position.Y - popupHeight - 8);
        Position = new PixelPoint(x, Math.Clamp(y, work.Y, Math.Max(work.Y, work.Bottom - popupHeight)));
    }

    private void CloseButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();
}
