using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using CursorTranslator.Models;

namespace CursorTranslator.Services;

public sealed class SettingsStore
{
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
                Endpoint = root.TryGetProperty("endpoint", out var endpoint) ? endpoint.GetString() ?? "" : "",
                Model = root.TryGetProperty("model", out var model) ? model.GetString() ?? "" : "",
                SpeechEndpoint = root.TryGetProperty("speechEndpoint", out var speechEndpoint)
                    ? speechEndpoint.GetString() ?? ""
                    : "",
                SpeechModel = root.TryGetProperty("speechModel", out var speechModel)
                    ? speechModel.GetString() ?? ""
                    : "",
                SpeechVoice = root.TryGetProperty("speechVoice", out var speechVoice)
                    ? speechVoice.GetString() ?? "alloy"
                    : "alloy",
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
            if (root.TryGetProperty("protectedSpeechKey", out var protectedSpeechKey))
            {
                var cipher = Convert.FromBase64String(protectedSpeechKey.GetString() ?? "");
                settings.SpeechApiKey = System.Text.Encoding.UTF8.GetString(ProtectedData.Unprotect(cipher, null, DataProtectionScope.CurrentUser));
            }
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
            ["endpoint"] = settings.Endpoint,
            ["model"] = settings.Model,
            ["speechEndpoint"] = settings.SpeechEndpoint,
            ["speechModel"] = settings.SpeechModel,
            ["speechVoice"] = settings.SpeechVoice,
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
        if (!string.IsNullOrWhiteSpace(settings.SpeechApiKey))
        {
            var cipher = ProtectedData.Protect(
                System.Text.Encoding.UTF8.GetBytes(settings.SpeechApiKey.Trim()),
                null,
                DataProtectionScope.CurrentUser);
            values["protectedSpeechKey"] = Convert.ToBase64String(cipher);
        }

        var json = JsonSerializer.Serialize(values, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_path, json);
    }
}
