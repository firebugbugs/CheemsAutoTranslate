using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using CursorTranslator.Models;

namespace CursorTranslator.Services;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions ProfileJsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly string _path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CursorTranslator", "settings.json");

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_path)) return new AppSettings();
            using var json = JsonDocument.Parse(File.ReadAllText(_path));
            var root = json.RootElement;
            var settings = new AppSettings
            {
                TranslationProvider = ReadTranslationProvider(root),
                Endpoint = root.TryGetProperty("endpoint", out var endpoint) ? endpoint.GetString() ?? "" : "",
                Model = root.TryGetProperty("model", out var model) ? model.GetString() ?? "" : "",
                HttpTranslationEndpoint = ReadString(root, "httpTranslationEndpoint", "niuTransEndpoint")
                    ?? AppSettings.DefaultHttpTranslationEndpoint,
                HttpTranslationSourceLanguage = ReadString(root, "httpTranslationSourceLanguage", "niuTransSourceLanguage") ?? "auto",
                HttpTranslationTargetLanguage = ReadString(root, "httpTranslationTargetLanguage", "niuTransTargetLanguage") ?? "en",
                ActiveTranslationHttpProfileId = ReadString(root, "activeTranslationHttpProfileId") ?? "",
                TranslationHttpProfiles = ReadTranslationProfiles(root),
                SpeechEndpoint = root.TryGetProperty("speechEndpoint", out var speechEndpoint)
                    ? speechEndpoint.GetString() ?? ""
                    : "",
                SpeechProvider = ReadSpeechProvider(root),
                SpeechModel = root.TryGetProperty("speechModel", out var speechModel)
                    ? speechModel.GetString() ?? ""
                    : "",
                SpeechVoice = root.TryGetProperty("speechVoice", out var speechVoice)
                    ? speechVoice.GetString() ?? "alloy"
                    : "alloy",
                HttpSpeechEndpoint = ReadString(root, "httpSpeechEndpoint") ?? "",
                HttpSpeechModel = ReadString(root, "httpSpeechModel") ?? "",
                HttpSpeechVoice = ReadString(root, "httpSpeechVoice") ?? "",
                ActiveSpeechHttpProfileId = ReadString(root, "activeSpeechHttpProfileId") ?? "",
                SpeechHttpProfiles = ReadProfiles(root),
                RealTimeSpeechEnabled = root.TryGetProperty("realTimeSpeechEnabled", out var realTimeSpeechEnabled)
                    && realTimeSpeechEnabled.GetBoolean(),
                SystemPrompt = root.TryGetProperty("systemPrompt", out var systemPrompt)
                    ? systemPrompt.GetString() ?? AppSettings.DefaultSystemPrompt
                    : AppSettings.DefaultSystemPrompt,
                TranslateOnTextChange = root.TryGetProperty("translateOnTextChange", out var translateOnTextChange)
                    ? translateOnTextChange.GetBoolean()
                    : true,
                TranslateOnSentenceEnd = root.TryGetProperty("translateOnSentenceEnd", out var translateOnSentenceEnd)
                    && translateOnSentenceEnd.GetBoolean(),
                TranslateAfterInactivity = root.TryGetProperty("translateAfterInactivity", out var translateAfterInactivity)
                    && translateAfterInactivity.GetBoolean(),
                InactivityDelaySeconds = root.TryGetProperty("inactivityDelaySeconds", out var inactivityDelaySeconds)
                    ? Math.Clamp(inactivityDelaySeconds.GetDecimal(), AppSettings.MinimumInactivityDelaySeconds, AppSettings.MaximumInactivityDelaySeconds)
                    : AppSettings.DefaultInactivityDelaySeconds,
                MaximumTranslationCharacters = root.TryGetProperty("maximumTranslationCharacters", out var maximumTranslationCharacters)
                    ? Math.Clamp(maximumTranslationCharacters.GetInt32(), AppSettings.MinimumMaximumTranslationCharacters, AppSettings.MaximumMaximumTranslationCharacters)
                    : AppSettings.DefaultMaximumTranslationCharacters
            };
            if (!settings.TranslateOnTextChange && !settings.TranslateOnSentenceEnd && !settings.TranslateAfterInactivity)
                settings.TranslateOnTextChange = true;
            if (root.TryGetProperty("protectedKey", out var protectedKey))
            {
                var cipher = Convert.FromBase64String(protectedKey.GetString() ?? "");
                settings.ApiKey = System.Text.Encoding.UTF8.GetString(ProtectedData.Unprotect(cipher, null, DataProtectionScope.CurrentUser));
            }
            var protectedHttpKey = ReadElement(root, "protectedHttpTranslationKey", "protectedNiuTransKey");
            if (protectedHttpKey.ValueKind == JsonValueKind.String)
            {
                var cipher = Convert.FromBase64String(protectedHttpKey.GetString() ?? "");
                settings.HttpTranslationApiKey = System.Text.Encoding.UTF8.GetString(ProtectedData.Unprotect(cipher, null, DataProtectionScope.CurrentUser));
            }
            if (root.TryGetProperty("protectedSpeechKey", out var protectedSpeechKey))
            {
                var cipher = Convert.FromBase64String(protectedSpeechKey.GetString() ?? "");
                settings.SpeechApiKey = System.Text.Encoding.UTF8.GetString(ProtectedData.Unprotect(cipher, null, DataProtectionScope.CurrentUser));
            }
            var protectedHttpSpeechKey = ReadElement(root, "protectedHttpSpeechKey");
            if (protectedHttpSpeechKey.ValueKind == JsonValueKind.String)
            {
                var cipher = Convert.FromBase64String(protectedHttpSpeechKey.GetString() ?? "");
                settings.HttpSpeechApiKey = System.Text.Encoding.UTF8.GetString(ProtectedData.Unprotect(cipher, null, DataProtectionScope.CurrentUser));
            }

            var protectedProfileSecrets = ReadElement(root, "protectedSpeechHttpProfileSecrets");
            if (protectedProfileSecrets.ValueKind == JsonValueKind.String)
            {
                var cipher = Convert.FromBase64String(protectedProfileSecrets.GetString() ?? "");
                var secretsJson = System.Text.Encoding.UTF8.GetString(
                    ProtectedData.Unprotect(cipher, null, DataProtectionScope.CurrentUser));
                var credentials = JsonSerializer.Deserialize<Dictionary<string, SpeechHttpProfileCredentials>>(secretsJson);
                if (credentials is not null)
                {
                    foreach (var profile in settings.SpeechHttpProfiles)
                    {
                        if (!credentials.TryGetValue(profile.Id, out var secret)) continue;
                        profile.ApiKey = secret.ApiKey;
                        profile.ApiSecret = secret.ApiSecret;
                    }
                }
            }

            var protectedTranslationProfileSecrets = ReadElement(root, "protectedTranslationHttpProfileSecrets");
            if (protectedTranslationProfileSecrets.ValueKind == JsonValueKind.String)
            {
                var cipher = Convert.FromBase64String(protectedTranslationProfileSecrets.GetString() ?? "");
                var secretsJson = System.Text.Encoding.UTF8.GetString(
                    ProtectedData.Unprotect(cipher, null, DataProtectionScope.CurrentUser));
                var credentials = JsonSerializer.Deserialize<Dictionary<string, TranslationHttpProfileCredentials>>(secretsJson);
                if (credentials is not null)
                {
                    foreach (var profile in settings.TranslationHttpProfiles)
                    {
                        if (!credentials.TryGetValue(profile.Id, out var secret)) continue;
                        profile.ApiKey = secret.ApiKey;
                        profile.ApiSecret = secret.ApiSecret;
                    }
                }
            }

            // Migrate the original fixed NiuTrans settings into a reusable profile.
            if (settings.TranslationHttpProfiles.Count == 0)
            {
                var legacyProfile = TranslationHttpProfile.CreateNiuTrans();
                legacyProfile.Request.Url = settings.HttpTranslationEndpoint;
                legacyProfile.SourceLanguage = settings.HttpTranslationSourceLanguage;
                legacyProfile.TargetLanguage = settings.HttpTranslationTargetLanguage;
                legacyProfile.ApiKey = settings.HttpTranslationApiKey;
                settings.TranslationHttpProfiles.Add(legacyProfile);
            }
            if (!settings.TranslationHttpProfiles.Any(profile => profile.Id == settings.ActiveTranslationHttpProfileId))
                settings.ActiveTranslationHttpProfileId = settings.TranslationHttpProfiles[0].Id;

            // Migrate the original single-endpoint HTTP settings to a reusable profile.
            if (settings.SpeechHttpProfiles.Count == 0 && !string.IsNullOrWhiteSpace(settings.HttpSpeechEndpoint))
            {
                var legacyProfile = SpeechHttpProfile.CreateDefault();
                legacyProfile.Name = "默认 HTTP 接口";
                legacyProfile.Request.Url = settings.HttpSpeechEndpoint;
                legacyProfile.Model = settings.HttpSpeechModel;
                legacyProfile.Voice = settings.HttpSpeechVoice;
                legacyProfile.Request.Body = new System.Text.Json.Nodes.JsonObject
                {
                    ["text"] = "{{text}}"
                };
                if (!string.IsNullOrWhiteSpace(settings.HttpSpeechModel))
                    legacyProfile.Request.Body["model"] = settings.HttpSpeechModel;
                if (!string.IsNullOrWhiteSpace(settings.HttpSpeechVoice))
                    legacyProfile.Request.Body["voice"] = settings.HttpSpeechVoice;
                legacyProfile.ApiKey = settings.HttpSpeechApiKey;
                settings.SpeechHttpProfiles.Add(legacyProfile);
            }
            if (settings.SpeechHttpProfiles.Count == 0)
                settings.SpeechHttpProfiles.Add(SpeechHttpProfile.CreateDefault());
            if (!settings.SpeechHttpProfiles.Any(profile => profile.Id == settings.ActiveSpeechHttpProfileId))
                settings.ActiveSpeechHttpProfileId = settings.SpeechHttpProfiles[0].Id;

            return settings;
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var values = new Dictionary<string, object?>
        {
            ["translationProvider"] = settings.TranslationProvider.ToString(),
            ["endpoint"] = settings.Endpoint,
            ["model"] = settings.Model,
            ["httpTranslationEndpoint"] = settings.HttpTranslationEndpoint,
            ["httpTranslationSourceLanguage"] = settings.HttpTranslationSourceLanguage,
            ["httpTranslationTargetLanguage"] = settings.HttpTranslationTargetLanguage,
            ["activeTranslationHttpProfileId"] = settings.ActiveTranslationHttpProfileId,
            ["translationHttpProfiles"] = settings.TranslationHttpProfiles,
            ["speechEndpoint"] = settings.SpeechEndpoint,
            ["speechProvider"] = settings.SpeechProvider.ToString(),
            ["speechModel"] = settings.SpeechModel,
            ["speechVoice"] = settings.SpeechVoice,
            ["httpSpeechEndpoint"] = settings.HttpSpeechEndpoint,
            ["httpSpeechModel"] = settings.HttpSpeechModel,
            ["httpSpeechVoice"] = settings.HttpSpeechVoice,
            ["activeSpeechHttpProfileId"] = settings.ActiveSpeechHttpProfileId,
            ["speechHttpProfiles"] = settings.SpeechHttpProfiles,
            ["realTimeSpeechEnabled"] = settings.RealTimeSpeechEnabled,
            ["systemPrompt"] = settings.SystemPrompt,
            ["translateOnTextChange"] = settings.TranslateOnTextChange,
            ["translateOnSentenceEnd"] = settings.TranslateOnSentenceEnd,
            ["translateAfterInactivity"] = settings.TranslateAfterInactivity,
            ["inactivityDelaySeconds"] = settings.InactivityDelaySeconds,
            ["maximumTranslationCharacters"] = settings.MaximumTranslationCharacters
        };
        if (!string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            var cipher = ProtectedData.Protect(
                System.Text.Encoding.UTF8.GetBytes(settings.ApiKey.Trim()),
                null,
                DataProtectionScope.CurrentUser);
            values["protectedKey"] = Convert.ToBase64String(cipher);
        }
        if (!string.IsNullOrWhiteSpace(settings.HttpTranslationApiKey))
        {
            var cipher = ProtectedData.Protect(
                System.Text.Encoding.UTF8.GetBytes(settings.HttpTranslationApiKey.Trim()),
                null,
                DataProtectionScope.CurrentUser);
            values["protectedHttpTranslationKey"] = Convert.ToBase64String(cipher);
        }
        if (!string.IsNullOrWhiteSpace(settings.SpeechApiKey))
        {
            var cipher = ProtectedData.Protect(
                System.Text.Encoding.UTF8.GetBytes(settings.SpeechApiKey.Trim()),
                null,
                DataProtectionScope.CurrentUser);
            values["protectedSpeechKey"] = Convert.ToBase64String(cipher);
        }
        if (!string.IsNullOrWhiteSpace(settings.HttpSpeechApiKey))
        {
            var cipher = ProtectedData.Protect(
                System.Text.Encoding.UTF8.GetBytes(settings.HttpSpeechApiKey.Trim()),
                null,
                DataProtectionScope.CurrentUser);
            values["protectedHttpSpeechKey"] = Convert.ToBase64String(cipher);
        }
        var profileCredentials = settings.SpeechHttpProfiles
            .Where(profile => !string.IsNullOrWhiteSpace(profile.ApiKey) || !string.IsNullOrWhiteSpace(profile.ApiSecret))
            .ToDictionary(profile => profile.Id, profile => new SpeechHttpProfileCredentials(profile.ApiKey, profile.ApiSecret));
        if (profileCredentials.Count > 0)
        {
            var cipher = ProtectedData.Protect(
                System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(profileCredentials)),
                null,
                DataProtectionScope.CurrentUser);
            values["protectedSpeechHttpProfileSecrets"] = Convert.ToBase64String(cipher);
        }
        var translationProfileCredentials = settings.TranslationHttpProfiles
            .Where(profile => !string.IsNullOrWhiteSpace(profile.ApiKey) || !string.IsNullOrWhiteSpace(profile.ApiSecret))
            .ToDictionary(profile => profile.Id, profile => new TranslationHttpProfileCredentials(profile.ApiKey, profile.ApiSecret));
        if (translationProfileCredentials.Count > 0)
        {
            var cipher = ProtectedData.Protect(
                System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(translationProfileCredentials)),
                null,
                DataProtectionScope.CurrentUser);
            values["protectedTranslationHttpProfileSecrets"] = Convert.ToBase64String(cipher);
        }

        var json = JsonSerializer.Serialize(values, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_path, json);
    }

    private static TranslationProviderKind ReadTranslationProvider(JsonElement root)
    {
        if (!root.TryGetProperty("translationProvider", out var provider)
            || provider.ValueKind != JsonValueKind.String)
            return TranslationProviderKind.OpenAiCompatible;

        var value = provider.GetString();
        if (string.Equals(value, "NiuTransHttp", StringComparison.OrdinalIgnoreCase))
            return TranslationProviderKind.HttpTranslation;
        return Enum.TryParse<TranslationProviderKind>(value, true, out var parsed)
            && Enum.IsDefined(typeof(TranslationProviderKind), parsed)
                ? parsed
                : TranslationProviderKind.OpenAiCompatible;
    }

    private static SpeechProviderKind ReadSpeechProvider(JsonElement root)
    {
        if (!root.TryGetProperty("speechProvider", out var provider)
            || provider.ValueKind != JsonValueKind.String)
            return SpeechProviderKind.OpenAiCompatible;

        return Enum.TryParse<SpeechProviderKind>(provider.GetString(), true, out var parsed)
            && Enum.IsDefined(typeof(SpeechProviderKind), parsed)
                ? parsed
                : SpeechProviderKind.OpenAiCompatible;
    }

    private static string? ReadString(JsonElement root, string currentName, string legacyName)
    {
        var value = ReadElement(root, currentName, legacyName);
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static string? ReadString(JsonElement root, string name)
    {
        var value = ReadElement(root, name);
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static JsonElement ReadElement(JsonElement root, string currentName, string legacyName)
    {
        if (root.TryGetProperty(currentName, out var currentValue)) return currentValue;
        return root.TryGetProperty(legacyName, out var legacyValue) ? legacyValue : default;
    }

    private static JsonElement ReadElement(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) ? value : default;

    private static List<SpeechHttpProfile> ReadProfiles(JsonElement root)
    {
        var profiles = ReadElement(root, "speechHttpProfiles");
        if (profiles.ValueKind != JsonValueKind.Array) return [];
        var result = JsonSerializer.Deserialize<List<SpeechHttpProfile>>(profiles.GetRawText(), ProfileJsonOptions) ?? [];
        foreach (var profile in result)
        {
            if (string.IsNullOrWhiteSpace(profile.Id)) profile.Id = Guid.NewGuid().ToString("N");
            profile.Name ??= "通用 HTTP 接口";
            profile.Model ??= "";
            profile.Voice ??= "";
            profile.Request ??= new SpeechHttpRequestProfile();
            profile.Request.Url ??= "";
            profile.Request.Method ??= "POST";
            profile.Request.Headers ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            profile.Request.Query ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            profile.Auth ??= new SpeechHttpAuthProfile();
            profile.Auth.Type ??= "None";
            profile.Workflow ??= new SpeechHttpWorkflowProfile();
            profile.Workflow.Type ??= "Sync";
            profile.Workflow.QueryRequest ??= new SpeechHttpRequestProfile();
            profile.Workflow.QueryRequest.Url ??= "";
            profile.Workflow.QueryRequest.Method ??= "POST";
            profile.Response ??= new SpeechHttpResponseProfile();
            profile.Response.Type ??= "RawAudio";
            profile.Response.Format ??= "Wav";
        }
        return result;
    }

    private static List<TranslationHttpProfile> ReadTranslationProfiles(JsonElement root)
    {
        var profiles = ReadElement(root, "translationHttpProfiles");
        if (profiles.ValueKind != JsonValueKind.Array) return [];
        var result = JsonSerializer.Deserialize<List<TranslationHttpProfile>>(profiles.GetRawText(), ProfileJsonOptions) ?? [];
        foreach (var profile in result)
        {
            if (string.IsNullOrWhiteSpace(profile.Id)) profile.Id = Guid.NewGuid().ToString("N");
            profile.Name ??= "通用 HTTP 翻译接口";
            profile.SourceLanguage ??= "auto";
            profile.TargetLanguage ??= "en";
            profile.Request ??= new SpeechHttpRequestProfile();
            profile.Request.Url ??= "";
            profile.Request.Method ??= "POST";
            profile.Request.Headers ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            profile.Request.Query ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            profile.Auth ??= new SpeechHttpAuthProfile();
            profile.Auth.Type ??= "None";
            profile.Workflow ??= new SpeechHttpWorkflowProfile();
            profile.Workflow.Type ??= "Sync";
            profile.Workflow.QueryRequest ??= new SpeechHttpRequestProfile();
            profile.Workflow.QueryRequest.Url ??= "";
            profile.Workflow.QueryRequest.Method ??= "POST";
            profile.Workflow.QueryRequest.Headers ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            profile.Workflow.QueryRequest.Query ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            profile.Response ??= new TranslationHttpResponseProfile();
            profile.Response.Type ??= "JsonText";
            profile.Response.JsonPath ??= "";
        }
        return result;
    }
}
