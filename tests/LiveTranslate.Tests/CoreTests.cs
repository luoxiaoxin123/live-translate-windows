using System.Text;
using System.Text.Json;
using LiveTranslate.Core.Audio;
using LiveTranslate.Core.Data;
using LiveTranslate.Core.Export;
using LiveTranslate.Core.Live;
using LiveTranslate.Core.Text;
using Xunit;

namespace LiveTranslate.Tests;

public class LiveProtocolTests
{
    [Fact]
    public void SetupMessage_HasExactWireShape()
    {
        var json = LiveTranslateClient.BuildSetupMessage("gemini-3.5-live-translate-preview", "zh-Hans", true);
        using var doc = JsonDocument.Parse(json);
        var setup = doc.RootElement.GetProperty("setup");

        Assert.Equal("models/gemini-3.5-live-translate-preview", setup.GetProperty("model").GetString());

        var generation = setup.GetProperty("generationConfig");
        Assert.Equal("AUDIO", generation.GetProperty("responseModalities")[0].GetString());

        var translation = generation.GetProperty("translationConfig");
        Assert.Equal("zh-Hans", translation.GetProperty("targetLanguageCode").GetString());
        Assert.True(translation.GetProperty("echoTargetLanguage").GetBoolean());

        Assert.Equal(JsonValueKind.Object, setup.GetProperty("inputAudioTranscription").ValueKind);
        Assert.Equal(JsonValueKind.Object, setup.GetProperty("outputAudioTranscription").ValueKind);
    }

    [Theory]
    [InlineData("models/foo", "models/foo")]
    [InlineData("foo", "models/foo")]
    [InlineData("  models/foo  ", "models/foo")]
    public void SetupMessage_NormalizesModelId(string input, string expected)
    {
        var json = LiveTranslateClient.BuildSetupMessage(input, "en", true);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(expected, doc.RootElement.GetProperty("setup").GetProperty("model").GetString());
    }

    [Fact]
    public void SetupMessage_BlankTargetFallsBackToSimplifiedChinese()
    {
        var json = LiveTranslateClient.BuildSetupMessage("m", "  ", true);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("zh-Hans", doc.RootElement.GetProperty("setup").GetProperty("generationConfig")
            .GetProperty("translationConfig").GetProperty("targetLanguageCode").GetString());
    }

    [Fact]
    public void RealtimeAudioMessage_EncodesBase64PcmWithMime()
    {
        var pcm = new byte[] { 1, 2, 3, 4 };
        var bytes = LiveTranslateClient.BuildRealtimeAudioMessage(pcm);
        using var doc = JsonDocument.Parse(bytes);
        var audio = doc.RootElement.GetProperty("realtimeInput").GetProperty("audio");
        Assert.Equal(Convert.ToBase64String(pcm), audio.GetProperty("data").GetString());
        Assert.Equal("audio/pcm;rate=16000", audio.GetProperty("mimeType").GetString());
    }

    [Fact]
    public void BuildUrl_AppendsOrReplacesKey()
    {
        Assert.Equal("wss://host/path?key=K1", LiveTranslateClient.BuildUrl("wss://host/path", "K1"));
        Assert.Equal("wss://host/path?a=1&key=K1", LiveTranslateClient.BuildUrl("wss://host/path?a=1", "K1"));
        Assert.Equal("wss://host/path?key=NEW&b=2", LiveTranslateClient.BuildUrl("wss://host/path?key=OLD&b=2", "NEW"));
        Assert.Equal("wss://host/path?key=K1", LiveTranslateClient.BuildUrl("  \"wss://host/path\"  ", " K1 "));
    }

    [Fact]
    public void RedactKey_MasksOnlyTheKeyValue()
    {
        Assert.Equal("wss://host/path?key=***&b=2", LiveTranslateClient.RedactKey("wss://host/path?key=SECRET&b=2"));
    }

    [Fact]
    public void BuildUrl_KeyWithDollarSignsSurvivesReplacement()
    {
        Assert.Equal("wss://host/path?key=sk$1$&abc&b=2", LiveTranslateClient.BuildUrl("wss://host/path?key=OLD&b=2", "sk$1$&abc"));
    }
}

public class AudioDspTests
{
    [Fact]
    public void Resampler_44100To16000_ProducesExpectedTotalLength()
    {
        var resampler = new PcmResampler(44100, 16000);
        var chunk = new short[4410]; // 100 ms
        var totalBytes = 0;
        for (var i = 0; i < 10; i++) // 1 s total
        {
            totalBytes += resampler.Resample(chunk, chunk.Length).Length;
        }
        var totalSamples = totalBytes / 2;
        Assert.InRange(totalSamples, 15990, 16000); // ~16000 samples out of 1 s input
    }

    [Fact]
    public void Resampler_SameRate_PassesThrough()
    {
        var resampler = new PcmResampler(16000, 16000);
        var chunk = new short[] { 100, -200, 300 };
        var output = resampler.Resample(chunk, chunk.Length);
        Assert.Equal(6, output.Length);
        Assert.Equal(100, BitConverter.ToInt16(output, 0));
        Assert.Equal(-200, BitConverter.ToInt16(output, 2));
        Assert.Equal(300, BitConverter.ToInt16(output, 4));
    }

    [Fact]
    public void Resampler_PreservesConstantSignal()
    {
        var resampler = new PcmResampler(48000, 16000);
        var chunk = new short[4800];
        Array.Fill(chunk, (short)1000);
        var output = resampler.Resample(chunk, chunk.Length);
        for (var i = 0; i + 1 < output.Length; i += 2)
        {
            Assert.Equal(1000, BitConverter.ToInt16(output, i));
        }
    }

    [Fact]
    public void Mixer_AveragesOverlappingSamples()
    {
        var mixed = new List<byte>();
        var mixer = new PcmMixer(bytes => mixed.AddRange(bytes));

        mixer.OfferMedia(PcmBytes(1000, 2000));
        Assert.Empty(mixed); // nothing until both sides have data

        mixer.OfferMic(PcmBytes(3000, -2000));
        Assert.Equal(4, mixed.Count);
        Assert.Equal(2000, BitConverter.ToInt16(mixed.ToArray(), 0));
        Assert.Equal(0, BitConverter.ToInt16(mixed.ToArray(), 2));
    }

    [Fact]
    public async Task Mixer_PassesMicThroughWhenMediaGoesSilent()
    {
        var mixed = new List<byte>();
        var mixer = new PcmMixer(bytes => mixed.AddRange(bytes));

        // Media never produces anything (e.g. paused video). After the stale window,
        // mic audio must flow through instead of being dropped.
        await Task.Delay(600);
        mixer.OfferMic(PcmBytes(111, 222, 333));

        Assert.Equal(6, mixed.Count);
        Assert.Equal(111, BitConverter.ToInt16(mixed.ToArray(), 0));
        Assert.Equal(333, BitConverter.ToInt16(mixed.ToArray(), 4));
    }

    [Fact]
    public void ApplyGain_ClampsInsteadOfWrapping()
    {
        var pcm = PcmBytes(30000, -30000, 1000);
        PcmConvert.ApplyGain(pcm, 2.0f);
        Assert.Equal(short.MaxValue, BitConverter.ToInt16(pcm, 0));
        Assert.Equal(short.MinValue, BitConverter.ToInt16(pcm, 2));
        Assert.Equal(2000, BitConverter.ToInt16(pcm, 4));
    }

    [Theory]
    [InlineData("audio/pcm;rate=24000", 24000)]
    [InlineData("audio/pcm;rate=8000", 8000)]
    [InlineData("audio/pcm;rate=96000", 48000)] // clamped
    [InlineData("audio/pcm", 24000)]
    [InlineData(null, 24000)]
    public void TranslatedAudioPlayer_ParsesMimeRate(string? mime, int expected)
    {
        Assert.Equal(expected, TranslatedAudioPlayer.ParseRate(mime));
    }

    private static byte[] PcmBytes(params short[] samples)
    {
        var bytes = new byte[samples.Length * 2];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        return bytes;
    }
}

public class TranscriptAccumulatorTests
{
    [Fact]
    public void CumulativeRewrite_ReplacesBuffer()
    {
        var acc = new TranscriptAccumulator(1000);
        acc.Append("Hello");
        acc.Append("Hello world");
        Assert.Equal("Hello world", acc.Text);
    }

    [Fact]
    public void DuplicateTail_IsIgnored()
    {
        var acc = new TranscriptAccumulator(1000);
        acc.Append("Hello world");
        acc.Append("world");
        Assert.Equal("Hello world", acc.Text);
    }

    [Fact]
    public void NewContent_AppendsWithSpaceBetweenLatinWords()
    {
        var acc = new TranscriptAccumulator(1000);
        acc.Append("Hello");
        acc.Append("again");
        Assert.Equal("Hello again", acc.Text);
    }

    [Fact]
    public void CjkContent_AppendsWithoutSpace()
    {
        var acc = new TranscriptAccumulator(1000);
        acc.Append("你好");
        acc.Append("世界");
        Assert.Equal("你好世界", acc.Text);
    }

    [Fact]
    public void ExceedingCap_KeepsTail()
    {
        var acc = new TranscriptAccumulator(10);
        acc.Append("0123456789ABCDEF");
        Assert.Equal("6789ABCDEF", acc.Text);
    }

    [Fact]
    public void CapTrim_NeverSplitsSurrogatePairs()
    {
        var acc = new TranscriptAccumulator(3);
        acc.Append("ab\U0001F600cd"); // emoji is a surrogate pair straddling the cap boundary
        Assert.Equal("cd", acc.Text);
        Assert.False(char.IsLowSurrogate(acc.Text[0]));
    }
}

public class MarkdownExporterTests
{
    [Fact]
    public void Markdown_HasBothSectionsAndTimestamp()
    {
        var stopped = new DateTime(2026, 7, 26, 14, 30, 0);
        var md = MarkdownExporter.BuildMarkdown("source text", "translated text", stopped);
        Assert.Contains("# 翻译结果", md);
        Assert.Contains("`2026-07-26 14:30`", md);
        Assert.Contains("## 原文", md);
        Assert.Contains("source text", md);
        Assert.Contains("## 译文", md);
        Assert.Contains("translated text", md);
    }

    [Fact]
    public void Markdown_EmptySectionsUsePlaceholder()
    {
        var md = MarkdownExporter.BuildMarkdown("", "  ", DateTime.Now);
        Assert.Equal(2, md.Split("（无）").Length - 1);
    }

    [Fact]
    public void FileName_ContainsNoNtfsIllegalCharacters()
    {
        var name = MarkdownExporter.FileName(new DateTime(2026, 7, 26, 14, 30, 0));
        Assert.Equal("7月26日-14.30-翻译结果.md", name);
        Assert.DoesNotContain(name, n => Path.GetInvalidFileNameChars().Contains(n));
    }

    [Fact]
    public void SaveToDownloads_WritesFileAndResolvesCollisions()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lt-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var stopped = new DateTime(2026, 7, 26, 14, 30, 0);
            var first = MarkdownExporter.SaveToDownloads("one", stopped, dir);
            var second = MarkdownExporter.SaveToDownloads("two", stopped, dir);
            Assert.True(File.Exists(first));
            Assert.True(File.Exists(second));
            Assert.NotEqual(first, second);
            Assert.Contains("(2)", second);
            Assert.Equal("one", File.ReadAllText(first, Encoding.UTF8));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

public class DataStoreTests
{
    [Fact]
    public void ApiKeyStore_DpapiRoundTrip_And_Rotation()
    {
        var path = Path.Combine(Path.GetTempPath(), "lt-test-" + Guid.NewGuid().ToString("N"), "keys.dat");
        try
        {
            var store = new ApiKeyStore(path);
            Assert.False(store.HasKeys());
            Assert.Null(store.NextRotatedKey());

            store.SaveKeys(new[] { " k1 ", "", "k2", "k3" });
            Assert.Equal(new[] { "k1", "k2", "k3" }, store.GetKeys());

            // The file on disk must not contain plaintext keys.
            var raw = File.ReadAllBytes(path);
            Assert.DoesNotContain("k1", Encoding.UTF8.GetString(raw));

            // A fresh instance reads the same keys back (decryption round trip).
            var reloaded = new ApiKeyStore(path);
            Assert.Equal(new[] { "k1", "k2", "k3" }, reloaded.GetKeys());

            Assert.Equal("k1", reloaded.NextRotatedKey());
            Assert.Equal("k2", reloaded.NextRotatedKey());
            Assert.Equal("k3", reloaded.NextRotatedKey());
            Assert.Equal("k1", reloaded.NextRotatedKey());
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void ApiKeyStore_CapsAtTenKeys()
    {
        var path = Path.Combine(Path.GetTempPath(), "lt-test-" + Guid.NewGuid().ToString("N"), "keys.dat");
        try
        {
            var store = new ApiKeyStore(path);
            store.SaveKeys(Enumerable.Range(1, 15).Select(i => $"key{i}"));
            Assert.Equal(ApiKeyStore.MaxKeys, store.GetKeys().Count);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void SettingsRepository_RoundTrips_And_SurvivesCorruptFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "lt-test-" + Guid.NewGuid().ToString("N"), "settings.json");
        try
        {
            var repo = new UserSettingsRepository(path);
            Assert.Equal(UserSettings.DefaultEndpoint, repo.Current.Endpoint);

            repo.Update(s => s with { TargetLanguageCode = "ja", FontSize = 24, Bilingual = true, AudioSourceMode = AudioSourceMode.MediaAndMic });

            var reloaded = new UserSettingsRepository(path);
            Assert.Equal("ja", reloaded.Current.TargetLanguageCode);
            Assert.Equal(24, reloaded.Current.FontSize);
            Assert.True(reloaded.Current.Bilingual);
            Assert.Equal(AudioSourceMode.MediaAndMic, reloaded.Current.AudioSourceMode);

            File.WriteAllText(path, "{corrupt");
            var recovered = new UserSettingsRepository(path);
            Assert.Equal(UserSettings.DefaultEndpoint, recovered.Current.Endpoint);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }
}
