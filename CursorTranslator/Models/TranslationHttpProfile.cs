using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace CursorTranslator.Models;

public sealed class TranslationHttpProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "通用 HTTP 翻译接口";
    public string SourceLanguage { get; set; } = "auto";
    public string TargetLanguage { get; set; } = "en";
    public SpeechHttpRequestProfile Request { get; set; } = new();
    public SpeechHttpAuthProfile Auth { get; set; } = new();
    public SpeechHttpWorkflowProfile Workflow { get; set; } = new();
    public TranslationHttpResponseProfile Response { get; set; } = new();

    [JsonIgnore]
    public string ApiKey { get; set; } = "";

    [JsonIgnore]
    public string ApiSecret { get; set; } = "";

    public TranslationHttpProfile Copy() => new()
    {
        Id = Id,
        Name = Name,
        SourceLanguage = SourceLanguage,
        TargetLanguage = TargetLanguage,
        Request = Request.Copy(),
        Auth = Auth.Copy(),
        Workflow = Workflow.Copy(),
        Response = Response.Copy(),
        ApiKey = ApiKey,
        ApiSecret = ApiSecret
    };

    public override string ToString() => Name;

    public static TranslationHttpProfile CreateDefault() => new()
    {
        Name = "通用 HTTP 接口",
        Request = new SpeechHttpRequestProfile
        {
            Url = "http://127.0.0.1:8000/translate",
            Method = "POST",
            Body = JsonNode.Parse("""{"text":"{{text}}","source":"{{sourceLanguage}}","target":"{{targetLanguage}}"}""")
        },
        Auth = new SpeechHttpAuthProfile { Type = "None" },
        Workflow = new SpeechHttpWorkflowProfile(),
        Response = new TranslationHttpResponseProfile { Type = "JsonText", JsonPath = "" }
    };

    public static TranslationHttpProfile CreateNiuTrans() => new()
    {
        Name = "小牛翻译",
        Request = new SpeechHttpRequestProfile
        {
            Url = AppSettings.DefaultHttpTranslationEndpoint,
            Method = "POST",
            Body = JsonNode.Parse("""{"from":"{{sourceLanguage}}","to":"{{targetLanguage}}","apikey":"{{apiKey}}","src_text":"{{text}}"}""")
        },
        Auth = new SpeechHttpAuthProfile { Type = "None" },
        Workflow = new SpeechHttpWorkflowProfile(),
        Response = new TranslationHttpResponseProfile { Type = "JsonText", JsonPath = "tgt_text" }
    };
}

public sealed class TranslationHttpResponseProfile
{
    // JsonText, RawText
    public string Type { get; set; } = "JsonText";
    public string JsonPath { get; set; } = "";

    public TranslationHttpResponseProfile Copy() => (TranslationHttpResponseProfile)MemberwiseClone();
}

public sealed record TranslationHttpProfileCredentials(string ApiKey, string ApiSecret);
