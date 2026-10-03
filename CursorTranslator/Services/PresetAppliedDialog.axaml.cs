using Avalonia.Controls;
using Avalonia.Input;

namespace CursorTranslator.Services;

public partial class PresetAppliedDialog : Window
{
    public PresetAppliedDialog() => InitializeComponent();

    public PresetAppliedDialog(string message) : this()
    {
        MessageText.Text = message;
    }

    private void Confirm_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => Close();

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
        => WindowChrome.BeginMoveDrag(this, e);
}
