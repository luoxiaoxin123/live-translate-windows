namespace LiveTranslate.Core.Data;

public sealed record LanguageOption(string Code, string EnglishName, string ChineseName)
{
    public string DisplayName(bool chinese) => chinese ? ChineseName : EnglishName;
}

/// <summary>The language catalog offered by the app (same set as the Android version).</summary>
public static class Languages
{
    public static readonly LanguageOption AutoDetect = new("auto", "Auto-detect", "自动检测");

    public static readonly IReadOnlyList<LanguageOption> TargetOptions = new List<LanguageOption>
    {
        new("zh-Hans", "Chinese (Simplified)", "中文（简体）"),
        new("zh-Hant", "Chinese (Traditional)", "中文（繁体）"),
        new("en", "English", "英语"),
        new("ja", "Japanese", "日语"),
        new("ko", "Korean", "韩语"),
        new("es", "Spanish", "西班牙语"),
        new("fr", "French", "法语"),
        new("de", "German", "德语"),
        new("ru", "Russian", "俄语"),
        new("pt-BR", "Portuguese (Brazil)", "葡萄牙语（巴西）"),
        new("pt-PT", "Portuguese (Portugal)", "葡萄牙语（葡萄牙）"),
        new("it", "Italian", "意大利语"),
        new("ar", "Arabic", "阿拉伯语"),
        new("hi", "Hindi", "印地语"),
        new("th", "Thai", "泰语"),
        new("vi", "Vietnamese", "越南语"),
        new("id", "Indonesian", "印尼语"),
        new("tr", "Turkish", "土耳其语"),
        new("pl", "Polish", "波兰语"),
        new("nl", "Dutch", "荷兰语"),
        new("uk", "Ukrainian", "乌克兰语"),
    };

    public static readonly IReadOnlyList<LanguageOption> SourceOptions =
        new List<LanguageOption> { AutoDetect }.Concat(TargetOptions).ToList();

    public static LanguageOption FindTarget(string code) =>
        TargetOptions.FirstOrDefault(o => o.Code == code) ?? TargetOptions[0];

    public static LanguageOption FindSource(string code) =>
        SourceOptions.FirstOrDefault(o => o.Code == code) ?? AutoDetect;
}
