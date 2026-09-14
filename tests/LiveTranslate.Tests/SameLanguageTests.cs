using LiveTranslate.Core.Data;
using LiveTranslate.Core.Text;
using Xunit;

namespace LiveTranslate.Tests;

public class LanguageCodesTests
{
    [Theory]
    [InlineData("zh-Hans", "zh-Hans")]
    [InlineData("en", "en")]
    [InlineData("ja", "ja")]
    [InlineData("zh", "zh-Hans")]
    [InlineData("zh-CN", "zh-Hans")]
    [InlineData("cmn", "zh-Hans")]
    [InlineData("zh_hans", "zh-Hans")]
    [InlineData("zh-Hant", "zh-Hant")]
    [InlineData("zh-TW", "zh-Hant")]
    [InlineData("zh-HK", "zh-Hant")]
    [InlineData("nb", "no")]
    [InlineData("nn", "no")]
    [InlineData("pt-BR", "pt-BR")]
    [InlineData("pt", "pt-BR")]   // no region on detection → compatible with either variant
    [InlineData("pt", "pt-PT")]
    [InlineData("en-US", "en")]   // no region on the target → any region matches
    [InlineData("en-GB", "en")]
    public void MatchesTarget_SameLanguage(string detected, string target)
    {
        Assert.True(LanguageCodes.MatchesTarget(detected, target));
    }

    [Theory]
    [InlineData("zh-Hans", "zh-Hant")]
    [InlineData("zh-CN", "zh-Hant")]
    [InlineData("zh-TW", "zh-Hans")]
    [InlineData("en", "zh-Hans")]
    [InlineData("ja", "ko")]
    [InlineData("pt-BR", "pt-PT")]  // distinct target options must not collapse
    [InlineData("pt-PT", "pt-BR")]
    [InlineData(null, "zh-Hans")]
    [InlineData("", "zh-Hans")]
    public void MatchesTarget_DifferentLanguage(string? detected, string target)
    {
        Assert.False(LanguageCodes.MatchesTarget(detected, target));
    }

    [Fact]
    public void TextLooksLikeTarget_UsesOnlyHighConfidenceScripts()
    {
        const string chinese = "今天天气很好我们一起去公园走走吧";
        const string english = "Hello everyone this is an English sentence";
        const string japanese = "こんにちは、今日はいい天気ですね私達は公園へ行きます";

        Assert.True(LanguageCodes.TextLooksLikeTarget(chinese, "zh-Hans"));
        Assert.False(LanguageCodes.TextLooksLikeTarget(english, "zh-Hans"));
        Assert.True(LanguageCodes.TextLooksLikeTarget(english, "en"));
        Assert.False(LanguageCodes.TextLooksLikeTarget(chinese, "en"));
        Assert.False(LanguageCodes.TextLooksLikeTarget(chinese, "ja"));   // Han without kana isn't Japanese
        Assert.True(LanguageCodes.TextLooksLikeTarget("こんにちは、今日はいい天気ですね", "ja"));
        Assert.False(LanguageCodes.TextLooksLikeTarget(japanese, "zh-Hans"));
        Assert.False(LanguageCodes.TextLooksLikeTarget("你好啊", "zh-Hans")); // too short to guess
        Assert.False(LanguageCodes.TextLooksLikeTarget("Bonjour tout le monde", "fr")); // Latin languages aren't guessed
    }
}

public class SameLanguageCaptionModeTests
{
    [Fact]
    public void EntersOnFirstMatchingLanguageCode()
    {
        var mode = new SameLanguageCaptionMode();
        Assert.Equal(true, mode.OnDetectedInputLanguage("zh-Hans", "zh-Hans"));
        Assert.True(mode.Enabled);
        Assert.Null(mode.OnDetectedInputLanguage("zh", "zh-Hans"));
        Assert.True(mode.Enabled);
    }

    [Fact]
    public void LeavesOnlyAfterTwoConsecutiveMismatches()
    {
        var mode = new SameLanguageCaptionMode();
        mode.OnDetectedInputLanguage("zh-Hans", "zh-Hans");

        Assert.Null(mode.OnDetectedInputLanguage("en", "zh-Hans"));
        Assert.True(mode.Enabled);
        Assert.Null(mode.OnDetectedInputLanguage("zh", "zh-Hans"));   // a match resets the streak
        Assert.Null(mode.OnDetectedInputLanguage("en", "zh-Hans"));
        Assert.True(mode.Enabled);

        Assert.Equal(false, mode.OnDetectedInputLanguage("en", "zh-Hans"));
        Assert.False(mode.Enabled);
    }

    [Fact]
    public void BlankLanguageCodeIsIgnored()
    {
        var mode = new SameLanguageCaptionMode();
        Assert.Null(mode.OnDetectedInputLanguage(null, "zh-Hans"));
        Assert.Null(mode.OnDetectedInputLanguage("  ", "zh-Hans"));
        Assert.False(mode.Enabled);
    }

    [Fact]
    public void ScriptFallback_OnlyWhileNoTranslationArrived()
    {
        const string chinese = "今天天气很好我们一起去公园走走吧";

        var late = new SameLanguageCaptionMode();
        Assert.Null(late.OnInputTextFallback(chinese, "zh-Hans", outputEmpty: false));
        Assert.False(late.Enabled);

        var early = new SameLanguageCaptionMode();
        Assert.Equal(true, early.OnInputTextFallback(chinese, "zh-Hans", outputEmpty: true));
        Assert.True(early.Enabled);
    }

    [Fact]
    public void TranslatedAudio_FollowsUserSettingUntilSameLanguage()
    {
        var mode = new SameLanguageCaptionMode();
        Assert.True(mode.ShouldPlayTranslatedAudio(userEnabled: true));
        Assert.False(mode.ShouldPlayTranslatedAudio(userEnabled: false));

        mode.OnDetectedInputLanguage("zh-Hans", "zh-Hans");
        Assert.False(mode.ShouldPlayTranslatedAudio(userEnabled: true));
        Assert.False(mode.ShouldPlayTranslatedAudio(userEnabled: false));
    }

    [Fact]
    public void TranslatedAudio_ResumesAfterLeavingSameLanguage_Or_Reset()
    {
        var mode = new SameLanguageCaptionMode();
        mode.OnDetectedInputLanguage("zh-Hans", "zh-Hans");
        mode.OnDetectedInputLanguage("en", "zh-Hans");
        Assert.False(mode.ShouldPlayTranslatedAudio(userEnabled: true));
        mode.OnDetectedInputLanguage("en", "zh-Hans");
        Assert.True(mode.ShouldPlayTranslatedAudio(userEnabled: true));

        mode.OnDetectedInputLanguage("zh", "zh-Hans");
        mode.Reset();
        Assert.False(mode.Enabled);
        Assert.True(mode.ShouldPlayTranslatedAudio(userEnabled: true));
    }
}

public class CaptionScrollPolicyTests
{
    [Theory]
    [InlineData(5, 4, 200, CaptionScrollAction.ShowLastLine)]  // a new line wrapped
    [InlineData(4, 4, 200, CaptionScrollAction.HoldSteady)]    // same line still filling in
    [InlineData(8, 10, 600, CaptionScrollAction.ShowLastLine)] // head trimmed on a long session: stay at the bottom
    [InlineData(3, 4, 150, CaptionScrollAction.ShowLastLine)]
    [InlineData(2, 10, 40, CaptionScrollAction.ShowLastLine)]  // two lines left is still a trim, not a reset
    [InlineData(0, 10, 0, CaptionScrollAction.ResetToTop)]     // cleared
    [InlineData(1, 10, 10, CaptionScrollAction.ResetToTop)]    // replaced by one short line
    public void Decide(int lines, int previous, int textLength, CaptionScrollAction expected)
    {
        Assert.Equal(expected, CaptionScrollPolicy.Decide(lines, previous, textLength));
    }
}
