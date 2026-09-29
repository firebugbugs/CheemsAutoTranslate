using System.IO;
using System.Text.Json;
using CursorTranslator.Models;

namespace CursorTranslator.Services;

public sealed class CardAppearanceStore
{
    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CursorTranslator",
        "card-appearance.json");

    public CardAppearanceSettings Load()
    {
        try
        {
            if (!File.Exists(_path)) return new CardAppearanceSettings();
            var settings = JsonSerializer.Deserialize<CardAppearanceSettings>(File.ReadAllText(_path))
                ?? new CardAppearanceSettings();
            settings.Opacity = Math.Clamp(settings.Opacity, 0.25, 1.0);
            if (settings.Theme is not ("Dark" or "Light" or "Blue" or "Green"))
                settings.Theme = "Dark";
            return settings;
        }
        catch
        {
            return new CardAppearanceSettings();
        }
    }

    public void Save(CardAppearanceSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
    }
}
