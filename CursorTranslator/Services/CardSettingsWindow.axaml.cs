using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using CursorTranslator.Models;

namespace CursorTranslator.Services;

public partial class CardSettingsWindow : Window
{
    public event Action<CardAppearanceSettings>? SettingsApplied;

    public CardSettingsWindow() : this(new CardAppearanceSettings()) { }

    public CardSettingsWindow(CardAppearanceSettings settings)
    {
        InitializeComponent();
        OpacitySlider.Value = Math.Clamp(settings.Opacity, 0.25, 1.0);
        ThemeBox.SelectedIndex = settings.Theme switch
        {
            "Light" => 1,
            "Blue" => 2,
            "Green" => 3,
            _ => 0
        };
        UpdateOpacityLabel();
    }

    private void OpacitySlider_ValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
        => UpdateOpacityLabel();

    private void UpdateOpacityLabel()
        => OpacityValueText.Text = $"{Math.Round(OpacitySlider.Value * 100)}%";

    private void Apply_Click(object? sender, RoutedEventArgs e)
    {
        var theme = (ThemeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Dark";
        SettingsApplied?.Invoke(new CardAppearanceSettings
        {
            Opacity = OpacitySlider.Value,
            Theme = theme
        });
        Close();
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close();
}
