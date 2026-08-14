namespace LiveTranslate.Core.Data;

public sealed record LanguageOption(string Code, string EnglishName, string ChineseName)
{
    public string DisplayName(bool chinese) => chinese ? ChineseName : EnglishName;
}

/// <summary>
/// Official Live Translate target-language catalog
/// (https://ai.google.dev/gemini-api/docs/live-api/live-translate#supported-languages).
/// Frequent targets stay at the front; the rest follow in English-name order.
/// </summary>
public static class Languages
{
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
        new("af", "Afrikaans", "南非荷兰语"),
        new("ak", "Akan", "阿肯语"),
        new("sq", "Albanian", "阿尔巴尼亚语"),
        new("am", "Amharic", "阿姆哈拉语"),
        new("hy", "Armenian", "亚美尼亚语"),
        new("az", "Azerbaijani", "阿塞拜疆语"),
        new("eu", "Basque", "巴斯克语"),
        new("be", "Belarusian", "白俄罗斯语"),
        new("bn", "Bengali", "孟加拉语"),
        new("bg", "Bulgarian", "保加利亚语"),
        new("my", "Burmese", "缅甸语"),
        new("ca", "Catalan", "加泰罗尼亚语"),
        new("hr", "Croatian", "克罗地亚语"),
        new("cs", "Czech", "捷克语"),
        new("da", "Danish", "丹麦语"),
        new("et", "Estonian", "爱沙尼亚语"),
        new("fil", "Filipino", "菲律宾语"),
        new("fi", "Finnish", "芬兰语"),
        new("gl", "Galician", "加利西亚语"),
        new("ka", "Georgian", "格鲁吉亚语"),
        new("el", "Greek", "希腊语"),
        new("gu", "Gujarati", "古吉拉特语"),
        new("ha", "Hausa", "豪萨语"),
        new("he", "Hebrew", "希伯来语"),
        new("hu", "Hungarian", "匈牙利语"),
        new("is", "Icelandic", "冰岛语"),
        new("jv", "Javanese", "爪哇语"),
        new("kn", "Kannada", "卡纳达语"),
        new("kk", "Kazakh", "哈萨克语"),
        new("km", "Khmer", "高棉语"),
        new("rw", "Kinyarwanda", "卢旺达语"),
        new("lo", "Lao", "老挝语"),
        new("lv", "Latvian", "拉脱维亚语"),
        new("lt", "Lithuanian", "立陶宛语"),
        new("mk", "Macedonian", "马其顿语"),
        new("ms", "Malay", "马来语"),
        new("ml", "Malayalam", "马拉雅拉姆语"),
        new("mr", "Marathi", "马拉地语"),
        new("mn", "Mongolian", "蒙古语"),
        new("ne", "Nepali", "尼泊尔语"),
        new("no", "Norwegian", "挪威语"),
        new("fa", "Persian", "波斯语"),
        new("pa", "Punjabi", "旁遮普语"),
        new("ro", "Romanian", "罗马尼亚语"),
        new("sr", "Serbian", "塞尔维亚语"),
        new("sd", "Sindhi", "信德语"),
        new("si", "Sinhala", "僧伽罗语"),
        new("sk", "Slovak", "斯洛伐克语"),
        new("sl", "Slovenian", "斯洛文尼亚语"),
        new("su", "Sundanese", "巽他语"),
        new("sw", "Swahili", "斯瓦希里语"),
        new("sv", "Swedish", "瑞典语"),
        new("ta", "Tamil", "泰米尔语"),
        new("te", "Telugu", "泰卢固语"),
        new("ur", "Urdu", "乌尔都语"),
        new("uz", "Uzbek", "乌兹别克语"),
        new("zu", "Zulu", "祖鲁语"),
    };

    public static LanguageOption FindTarget(string code) =>
        TargetOptions.FirstOrDefault(o => o.Code == code) ?? TargetOptions[0];
}
