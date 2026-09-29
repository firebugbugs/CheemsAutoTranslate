namespace CursorTranslator.Models;

public sealed class AppSettings
{
    public const string DefaultSystemPrompt = "Translate the source text into natural English. Preserve its meaning, URLs, numbers, names, punctuation, and formatting.";

    public string Endpoint { get; set; } = "https://api.openai.com/v1";
    public string Model { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string SystemPrompt { get; set; } = DefaultSystemPrompt;
    public bool IsConfigured => Uri.TryCreate(Endpoint, UriKind.Absolute, out _)
        && !string.IsNullOrWhiteSpace(Model)
        && !string.IsNullOrWhiteSpace(ApiKey)
        && !string.IsNullOrWhiteSpace(SystemPrompt);
}

public sealed record MonitoredText(string Text, Avalonia.PixelRect Bounds);
