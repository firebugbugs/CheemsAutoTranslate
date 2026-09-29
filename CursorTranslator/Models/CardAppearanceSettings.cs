namespace CursorTranslator.Models;

public sealed class CardAppearanceSettings
{
    public double Opacity { get; set; } = 0.8;
    public string Theme { get; set; } = "Light";
    public double FontSize { get; set; } = 17;
    public string FontFamily { get; set; } = "";
    public bool IsBold { get; set; }
}
