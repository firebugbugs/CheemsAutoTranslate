using System.Text.Json.Serialization;

namespace CursorTranslator.Models;

public sealed class SpeechAiConnectionProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "默认语音 AI 接口";
    public string Endpoint { get; set; } = "";
    public string Model { get; set; } = "";
    public string Voice { get; set; } = "alloy";

    [JsonIgnore]
    public string ApiKey { get; set; } = "";

    public SpeechAiConnectionProfile Copy() => new()
    {
        Id = Id,
        Name = Name,
        Endpoint = Endpoint,
        Model = Model,
        Voice = Voice,
        ApiKey = ApiKey
    };

    public override string ToString() => Name;
}

public sealed record SpeechAiConnectionProfileCredentials(string ApiKey);
