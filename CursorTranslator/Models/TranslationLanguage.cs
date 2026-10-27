using System.Text;

namespace CursorTranslator.Models;

/// <summary>
/// A language offered by the translator window. <see cref="Code"/> is the standard
/// code sent to HTTP profiles (mapped per profile when a service uses its own
/// codes); <see cref="PromptName"/> is the name given to AI models.
/// </summary>
public sealed record TranslationLanguage(string Code, string DisplayName, string PromptName)
{
    public const string AutoCode = "auto";
    public const string ChineseCode = "zh";
    public const string TraditionalChineseCode = "zh-TW";
    public const string EnglishCode = "en";

    public static readonly TranslationLanguage AutoDetect = new(AutoCode, "自动检测", "");
    public static readonly TranslationLanguage AutoTarget = new(AutoCode, "自动（中文 ⇄ 英语）", "");

    public static IReadOnlyList<TranslationLanguage> Languages { get; } =
    [
        new(ChineseCode, "中文（简体）", "Simplified Chinese"),
        new(TraditionalChineseCode, "中文（繁体）", "Traditional Chinese"),
        new(EnglishCode, "英语", "English"),
        new("ja", "日语", "Japanese"),
        new("ko", "韩语", "Korean"),
        new("fr", "法语", "French"),
        new("de", "德语", "German"),
        new("es", "西班牙语", "Spanish"),
        new("ru", "俄语", "Russian"),
        new("pt", "葡萄牙语", "Portuguese"),
        new("it", "意大利语", "Italian"),
        new("ar", "阿拉伯语", "Arabic"),
        new("vi", "越南语", "Vietnamese"),
        new("th", "泰语", "Thai"),
        new("id", "印尼语", "Indonesian")
    ];

    public static IReadOnlyList<TranslationLanguage> SourceOptions { get; } = [AutoDetect, .. Languages];
    public static IReadOnlyList<TranslationLanguage> TargetOptions { get; } = [AutoTarget, .. Languages];

    public bool IsAuto => Code == AutoCode;
    public bool IsChinese => Code is ChineseCode or TraditionalChineseCode;

    public static TranslationLanguage Chinese => Languages[0];
    public static TranslationLanguage English => Languages[2];

    public static TranslationLanguage? FromCode(string? code)
        => Languages.FirstOrDefault(language => string.Equals(language.Code, code, StringComparison.OrdinalIgnoreCase));

    public override string ToString() => DisplayName;

    /// <summary>
    /// Resolves the automatic target: Chinese text goes to English and everything
    /// else to Simplified Chinese. An explicit source language decides directly;
    /// otherwise the script mix of the text is inspected locally.
    /// </summary>
    public static TranslationLanguage ResolveAutoTarget(TranslationLanguage source, string text)
    {
        var sourceIsChinese = source.IsAuto ? LooksChinese(text) : source.IsChinese;
        return sourceIsChinese ? English : Chinese;
    }

    /// <summary>
    /// Han characters dominate and no kana or hangul is present. Mixed text such
    /// as "我在用 React 开发" still counts as Chinese.
    /// </summary>
    public static bool LooksChinese(string text)
    {
        int han = 0, otherLetters = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var value = rune.Value;
            if (value is >= 0x3040 and <= 0x30FF or >= 0x31F0 and <= 0x31FF   // kana
                or >= 0x1100 and <= 0x11FF or >= 0x3130 and <= 0x318F or >= 0xAC00 and <= 0xD7AF) // hangul
                return false;
            if (value is >= 0x3400 and <= 0x4DBF or >= 0x4E00 and <= 0x9FFF
                or >= 0xF900 and <= 0xFAFF or >= 0x20000 and <= 0x323AF)
                han++;
            else if (Rune.IsLetter(rune))
                otherLetters++;
        }
        return han > 0 && han * 2 >= otherLetters;
    }
}

/// <summary>An explicit language pair that overrides the configured translation direction.</summary>
public sealed record TranslationDirection(TranslationLanguage Source, TranslationLanguage Target)
{
    public string ToAiInstruction()
    {
        var from = Source.IsAuto
            ? "Detect the source language automatically and translate it"
            : $"Translate the source text from {Source.PromptName}";
        return $"{from} into {Target.PromptName}. Preserve its meaning, tone, line breaks, formatting, URLs, numbers and names. If the text is already in {Target.PromptName}, return it unchanged.";
    }
}
