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
                SystemPrompt = root.TryGetProperty("systemPrompt", out var systemPrompt)
                    ? systemPrompt.GetString() ?? AppSettings.DefaultSystemPrompt
                    : AppSettings.DefaultSystemPrompt
            };
            if (root.TryGetProperty("protectedKey", out var protectedKey))
            {
                var cipher = Convert.FromBase64String(protectedKey.GetString() ?? "");
                settings.ApiKey = System.Text.Encoding.UTF8.GetString(ProtectedData.Unprotect(cipher, null, DataProtectionScope.CurrentUser));
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
        var cipher = ProtectedData.Protect(System.Text.Encoding.UTF8.GetBytes(settings.ApiKey), null, DataProtectionScope.CurrentUser);
        var json = JsonSerializer.Serialize(new
        {
            endpoint = settings.Endpoint,
            model = settings.Model,
            systemPrompt = settings.SystemPrompt,
            protectedKey = Convert.ToBase64String(cipher)
        }, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_path, json);
    }
}
