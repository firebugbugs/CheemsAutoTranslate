using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace CursorTranslator.Models;

public sealed class SpeechHttpProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "通用 HTTP 接口";
    public string Model { get; set; } = "";
    public string Voice { get; set; } = "";
    public SpeechHttpRequestProfile Request { get; set; } = new();
    public SpeechHttpAuthProfile Auth { get; set; } = new();
    public SpeechHttpWorkflowProfile Workflow { get; set; } = new();
    public SpeechHttpResponseProfile Response { get; set; } = new();

    [JsonIgnore]
    public string ApiKey { get; set; } = "";

    [JsonIgnore]
    public string ApiSecret { get; set; } = "";

    public SpeechHttpProfile Copy() => new()
    {
        Id = Id,
        Name = Name,
        Model = Model,
        Voice = Voice,
        Request = Request.Copy(),
        Auth = Auth.Copy(),
        Workflow = Workflow.Copy(),
        Response = Response.Copy(),
        ApiKey = ApiKey,
        ApiSecret = ApiSecret
    };

    public override string ToString() => Name;

    public static SpeechHttpProfile CreateDefault() => new()
    {
        Request = new SpeechHttpRequestProfile
        {
            Url = "http://127.0.0.1:8000/tts",
            Method = "POST",
            Body = JsonNode.Parse("""{"text":"{{text}}"}""")
        },
        Auth = new SpeechHttpAuthProfile { Type = "Bearer" },
        Response = new SpeechHttpResponseProfile { Type = "RawAudio", Format = "Wav" }
    };
}

public sealed class SpeechHttpRequestProfile
{
    public string Url { get; set; } = "";
    public string Method { get; set; } = "POST";
    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Query { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public JsonNode? Body { get; set; }
    public int TimeoutSeconds { get; set; } = 90;

    public SpeechHttpRequestProfile Copy() => new()
    {
        Url = Url,
        Method = Method,
        Headers = new Dictionary<string, string>(Headers, StringComparer.OrdinalIgnoreCase),
        Query = new Dictionary<string, string>(Query, StringComparer.OrdinalIgnoreCase),
        Body = Body?.DeepClone(),
        TimeoutSeconds = TimeoutSeconds
    };
}

public sealed class SpeechHttpAuthProfile
{
    // None, Bearer, ApiKeyHeader, ApiKeyQuery, Basic, HmacSha256
    public string Type { get; set; } = "None";
    public string HeaderName { get; set; } = "Authorization";
    public string QueryName { get; set; } = "api_key";
    public string Username { get; set; } = "";
    public string SignatureInput { get; set; } = "";
    public string SignatureEncoding { get; set; } = "Base64";
    public string SignatureHeader { get; set; } = "Authorization";
    public string SignatureTemplate { get; set; } = "{{signature}}";
    public string DateHeader { get; set; } = "";

    public SpeechHttpAuthProfile Copy() => (SpeechHttpAuthProfile)MemberwiseClone();
}

public sealed class SpeechHttpWorkflowProfile
{
    // Sync, AsyncTask
    public string Type { get; set; } = "Sync";
    public string TaskIdJsonPath { get; set; } = "";
    public string StatusJsonPath { get; set; } = "";
    public string SuccessStatus { get; set; } = "";
    public string ErrorStatuses { get; set; } = "";
    public int PollIntervalSeconds { get; set; } = 2;
    public SpeechHttpRequestProfile QueryRequest { get; set; } = new();

    public SpeechHttpWorkflowProfile Copy() => new()
    {
        Type = Type,
        TaskIdJsonPath = TaskIdJsonPath,
        StatusJsonPath = StatusJsonPath,
        SuccessStatus = SuccessStatus,
        ErrorStatuses = ErrorStatuses,
        PollIntervalSeconds = PollIntervalSeconds,
        QueryRequest = QueryRequest.Copy()
    };
}

public sealed class SpeechHttpResponseProfile
{
    // RawAudio, JsonBase64, JsonUrl, JsonBase64OrUrl
    public string Type { get; set; } = "RawAudio";
    public string JsonPath { get; set; } = "";
    // Wav, Pcm, Mp3
    public string Format { get; set; } = "Wav";
    public int SampleRate { get; set; } = 24000;
    public int Channels { get; set; } = 1;
    public int BitsPerSample { get; set; } = 16;

    public SpeechHttpResponseProfile Copy() => (SpeechHttpResponseProfile)MemberwiseClone();
}

public sealed record SpeechHttpProfileCredentials(string ApiKey, string ApiSecret);
