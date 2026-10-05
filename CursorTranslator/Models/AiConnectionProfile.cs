using System.Text.Json.Serialization;

namespace CursorTranslator.Models;

public sealed class AiConnectionProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "默认 AI 接口";
    public string Endpoint { get; set; } = "https://api.openai.com/v1";
    public string Model { get; set; } = "";

    [JsonIgnore]
    public string ApiKey { get; set; } = "";

    public AiConnectionProfile Copy() => new()
    {
        Id = Id,
        Name = Name,
        Endpoint = Endpoint,
        Model = Model,
        ApiKey = ApiKey
    };

    public override string ToString() => Name;

    public static AiConnectionProfile CreateDefault() => new();
}

public sealed record AiConnectionProfileCredentials(string ApiKey);
