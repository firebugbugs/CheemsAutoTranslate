namespace CursorTranslator.Models;

public sealed class AppSettings
{
    public const string DefaultSystemPrompt = "Translate the source text into natural English. Preserve its meaning, URLs, numbers, names, punctuation, and formatting.";
    public const string DefaultHttpTranslationEndpoint = "https://api.niutrans.com/NiuTransServer/translation";
    public const decimal DefaultInactivityDelaySeconds = 2m;
    public const decimal MinimumInactivityDelaySeconds = 0.5m;
    public const decimal MaximumInactivityDelaySeconds = 60m;
    public const int DefaultMaximumTranslationCharacters = 10;
    public const int MinimumMaximumTranslationCharacters = 5;
    public const int MaximumMaximumTranslationCharacters = 1_000;

    public AppSettings()
    {
        var defaultTranslationProfile = TranslationHttpProfile.CreateNiuTrans();
        TranslationHttpProfiles = [defaultTranslationProfile];
        ActiveTranslationHttpProfileId = defaultTranslationProfile.Id;
        var defaultProfile = SpeechHttpProfile.CreateDefault();
        SpeechHttpProfiles = [defaultProfile];
        ActiveSpeechHttpProfileId = defaultProfile.Id;
    }

    public string Endpoint { get; set; } = "https://api.openai.com/v1";
    public string Model { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public TranslationProviderKind TranslationProvider { get; set; } = TranslationProviderKind.OpenAiCompatible;
    public string HttpTranslationEndpoint { get; set; } = DefaultHttpTranslationEndpoint;
    public string HttpTranslationApiKey { get; set; } = "";
    public string HttpTranslationSourceLanguage { get; set; } = "auto";
    public string HttpTranslationTargetLanguage { get; set; } = "en";
    public string ActiveTranslationHttpProfileId { get; set; } = "";
    public List<TranslationHttpProfile> TranslationHttpProfiles { get; set; }
    public string SpeechEndpoint { get; set; } = "";
    public string SpeechModel { get; set; } = "";
    public string SpeechApiKey { get; set; } = "";
    public string SpeechVoice { get; set; } = "alloy";
    public SpeechProviderKind SpeechProvider { get; set; } = SpeechProviderKind.OpenAiCompatible;
    public string HttpSpeechEndpoint { get; set; } = "";
    public string HttpSpeechModel { get; set; } = "";
    public string HttpSpeechApiKey { get; set; } = "";
    public string HttpSpeechVoice { get; set; } = "";
    public string ActiveSpeechHttpProfileId { get; set; } = "";
    public List<SpeechHttpProfile> SpeechHttpProfiles { get; set; }
    public bool RealTimeSpeechEnabled { get; set; }
    public string SystemPrompt { get; set; } = DefaultSystemPrompt;
    public bool TranslateOnTextChange { get; set; } = true;
    public bool TranslateOnSentenceEnd { get; set; }
    public bool TranslateAfterInactivity { get; set; }
    public decimal InactivityDelaySeconds { get; set; } = DefaultInactivityDelaySeconds;
    public int MaximumTranslationCharacters { get; set; } = DefaultMaximumTranslationCharacters;

    public bool IsAiConfigured => Uri.TryCreate(Endpoint, UriKind.Absolute, out _)
        && !string.IsNullOrWhiteSpace(Model)
        && !string.IsNullOrWhiteSpace(SystemPrompt);

    public TranslationHttpProfile? ActiveTranslationHttpProfile => TranslationHttpProfiles.FirstOrDefault(profile =>
        string.Equals(profile.Id, ActiveTranslationHttpProfileId, StringComparison.Ordinal));

    public bool IsHttpTranslationConfigured => ActiveTranslationHttpProfile is { } profile
        && Uri.TryCreate(profile.Request.Url, UriKind.Absolute, out var httpUri)
        && (httpUri.Scheme == Uri.UriSchemeHttp || httpUri.Scheme == Uri.UriSchemeHttps)
        && !string.IsNullOrWhiteSpace(profile.SourceLanguage)
        && !string.IsNullOrWhiteSpace(profile.TargetLanguage)
        && (!TranslationProfileNeedsApiKey(profile) || !string.IsNullOrWhiteSpace(profile.ApiKey))
        && (!profile.Auth.Type.Equals("HmacSha256", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrWhiteSpace(profile.ApiSecret));

    public bool IsConfigured => TranslationProvider == TranslationProviderKind.HttpTranslation
        ? IsHttpTranslationConfigured
        : IsAiConfigured;

    public SpeechHttpProfile? ActiveSpeechHttpProfile => SpeechHttpProfiles.FirstOrDefault(profile =>
        string.Equals(profile.Id, ActiveSpeechHttpProfileId, StringComparison.Ordinal));

    public bool IsSpeechConfigured => SpeechProvider == SpeechProviderKind.GenericHttp
        ? ActiveSpeechHttpProfile is { } profile
            && (profile.IsEdgeTts
                ? !string.IsNullOrWhiteSpace(profile.Voice)
                : Uri.TryCreate(profile.Request.Url, UriKind.Absolute, out var httpSpeechUri)
                    && (httpSpeechUri.Scheme == Uri.UriSchemeHttp || httpSpeechUri.Scheme == Uri.UriSchemeHttps))
        : Uri.TryCreate(SpeechEndpoint, UriKind.Absolute, out var speechUri)
            && (speechUri.Scheme == Uri.UriSchemeHttp || speechUri.Scheme == Uri.UriSchemeHttps)
            && !string.IsNullOrWhiteSpace(SpeechModel)
            && !string.IsNullOrWhiteSpace(SpeechVoice);

    private static bool TranslationProfileNeedsApiKey(TranslationHttpProfile profile)
    {
        if (profile.Auth.Type.Equals("Bearer", StringComparison.OrdinalIgnoreCase)
            || profile.Auth.Type.Equals("ApiKeyHeader", StringComparison.OrdinalIgnoreCase)
            || profile.Auth.Type.Equals("ApiKeyQuery", StringComparison.OrdinalIgnoreCase)
            || profile.Auth.Type.Equals("Basic", StringComparison.OrdinalIgnoreCase))
            return true;

        var requestJson = System.Text.Json.JsonSerializer.Serialize(profile.Request);
        var workflowJson = System.Text.Json.JsonSerializer.Serialize(profile.Workflow.QueryRequest);
        var authJson = System.Text.Json.JsonSerializer.Serialize(profile.Auth);
        return requestJson.Contains("{{apiKey}}", StringComparison.Ordinal)
            || authJson.Contains("{{apiKey}}", StringComparison.Ordinal)
            || workflowJson.Contains("{{apiKey}}", StringComparison.Ordinal);
    }
}

public sealed record MonitoredText(string Text, Avalonia.PixelRect Bounds);
