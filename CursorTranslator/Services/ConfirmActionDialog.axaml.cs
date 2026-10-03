using Avalonia.Controls;

namespace CursorTranslator.Services;

public partial class ConfirmActionDialog : Window
{
    public ConfirmActionDialog() => InitializeComponent();

    public ConfirmActionDialog(string title, string message, string confirmText) : this()
    {
        DialogTitleText.Text = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirmText;
    }

    private void Cancel_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => Close(false);

    private void Confirm_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => Close(true);
}
