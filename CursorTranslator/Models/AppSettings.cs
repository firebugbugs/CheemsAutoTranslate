namespace CursorTranslator.Models;

public sealed class AppSettings
{
    public const string DefaultSystemPrompt = "Translate the source text into natural English. Preserve its meaning, URLs, numbers, names, punctuation, and formatting.";
    public const decimal DefaultInactivityDelaySeconds = 2m;
    public const decimal MinimumInactivityDelaySeconds = 0.5m;
    public const decimal MaximumInactivityDelaySeconds = 60m;
    public const int DefaultMaximumTranslationCharacters = 10;
    public const int MinimumMaximumTranslationCharacters = 5;
    public const int MaximumMaximumTranslationCharacters = 1_000;

    public string Endpoint { get; set; } = "https://api.openai.com/v1";
    public string Model { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string SpeechEndpoint { get; set; } = "";
    public string SpeechModel { get; set; } = "";
    public string SpeechApiKey { get; set; } = "";
    public string SpeechVoice { get; set; } = "alloy";
    public bool RealTimeSpeechEnabled { get; set; }
    public string SystemPrompt { get; set; } = DefaultSystemPrompt;
    public bool TranslateOnTextChange { get; set; } = true;
    public bool TranslateOnSentenceEnd { get; set; }
    public bool TranslateAfterInactivity { get; set; }
    public decimal InactivityDelaySeconds { get; set; } = DefaultInactivityDelaySeconds;
    public int MaximumTranslationCharacters { get; set; } = DefaultMaximumTranslationCharacters;

    public bool IsConfigured => Uri.TryCreate(Endpoint, UriKind.Absolute, out _)
        && !string.IsNullOrWhiteSpace(Model)
        && !string.IsNullOrWhiteSpace(SystemPrompt);

    public bool IsSpeechConfigured => Uri.TryCreate(SpeechEndpoint, UriKind.Absolute, out var speechUri)
        && (speechUri.Scheme == Uri.UriSchemeHttp || speechUri.Scheme == Uri.UriSchemeHttps)
        && !string.IsNullOrWhiteSpace(SpeechModel)
        && !string.IsNullOrWhiteSpace(SpeechVoice);
}

public sealed record MonitoredText(string Text, Avalonia.PixelRect Bounds);
