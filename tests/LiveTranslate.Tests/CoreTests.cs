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

        // Resumable from the first connection, with sliding-window context compression.
        Assert.Empty(setup.GetProperty("sessionResumption").EnumerateObject());
        Assert.Equal(JsonValueKind.Object,
            setup.GetProperty("contextWindowCompression").GetProperty("slidingWindow").ValueKind);
    }

    [Fact]
    public void SetupMessage_CarriesResumptionHandle()
    {
        var json = LiveTranslateClient.BuildSetupMessage("m", "en", true, resumptionHandle: "handle-123");
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("handle-123", doc.RootElement.GetProperty("setup")
            .GetProperty("sessionResumption").GetProperty("handle").GetString());
    }

    [Fact]
    public void SessionResumptionUpdate_KeepsLatestResumableHandle()
    {
        var client = new LiveTranslateClient();
        var raised = new List<string>();
        client.ResumptionHandleUpdated += raised.Add;

        client.HandleMessage("""{"sessionResumptionUpdate":{"newHandle":"h1","resumable":true}}""");
        client.HandleMessage("""{"sessionResumptionUpdate":{"resumable":false}}""");            // not resumable now
        client.HandleMessage("""{"sessionResumptionUpdate":{"newHandle":"","resumable":true}}"""); // empty handle
        client.HandleMessage("""{"session_resumption_update":{"new_handle":"h2","resumable":true}}""");

        Assert.Equal(new[] { "h1", "h2" }, raised);
        Assert.Equal("h2", client.LastResumptionHandle);
    }

    [Fact]
    public void SessionResumptionUpdate_AndGoAwayInOneFrame_BothHandled()
    {
        var client = new LiveTranslateClient();
        TimeSpan? goAway = null;
        client.GoAwayReceived += timeLeft => goAway = timeLeft;

        client.HandleMessage("""{"sessionResumptionUpdate":{"newHandle":"h9","resumable":true},"goAway":{"timeLeft":"5s"}}""");

        Assert.Equal("h9", client.LastResumptionHandle);
        Assert.Equal(TimeSpan.FromSeconds(5), goAway);
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
    public void SetupMessage_WritesEchoFlag_And_SessionConfigDefaultsOn()
    {
        var json = LiveTranslateClient.BuildSetupMessage("m", "en", echoTargetLanguage: false);
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.GetProperty("setup").GetProperty("generationConfig")
            .GetProperty("translationConfig").GetProperty("echoTargetLanguage").GetBoolean());
        // Echo stays on so same-language input still produces captions.
        Assert.True(new SessionConfig("wss://x", "k", "m", "en").EchoTargetLanguage);
    }

    [Fact]
    public void Transcriptions_CarryDetectedLanguageCode()
    {
        var client = new LiveTranslateClient();
        var inputs = new List<(string Text, string? Code)>();
        var outputs = new List<(string Text, string? Code)>();
        client.InputTranscript += (text, code) => inputs.Add((text, code));
        client.OutputTranscript += (text, code) => outputs.Add((text, code));

        client.HandleMessage("""{"serverContent":{"inputTranscription":{"text":"你好","languageCode":"zh-CN"},"outputTranscription":{"text":"hello","language_code":"en"}}}""");
        client.HandleMessage("""{"server_content":{"input_transcription":{"text":"hi"}}}""");
        client.HandleMessage("""{"serverContent":{"inputTranscription":{"text":"yo","languageCode":"  "}}}""");

        Assert.Equal(new (string, string?)[] { ("你好", "zh-CN"), ("hi", null), ("yo", null) }, inputs);
        Assert.Equal(new (string, string?)[] { ("hello", "en") }, outputs);
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

    [Theory]
    [InlineData("""{"goAway":{"timeLeft":"10s"}}""", true, 10.0)]
    [InlineData("""{"go_away":{"time_left":{"seconds":10}}}""", true, 10.0)]
    [InlineData("""{"goAway":{"timeLeft":{"seconds":5,"nanos":500000000}}}""", true, 5.5)]
    [InlineData("""{"goAway":{}}""", true, null)]
    [InlineData("""{"setupComplete":{}}""", false, null)]
    [InlineData("""{"serverContent":{"inputTranscription":{"text":"hi"}}}""", false, null)]
    [InlineData("not-json", false, null)]
    public void TryParseGoAway_AcceptsCamelAndSnakeCase(string json, bool expected, double? seconds)
    {
        var parsed = LiveTranslateClient.TryParseGoAway(json, out var timeLeft);
        Assert.Equal(expected, parsed);
        if (seconds is null)
        {
            Assert.Null(timeLeft);
        }
        else
        {
            Assert.NotNull(timeLeft);
            Assert.Equal(seconds.Value, timeLeft!.Value.TotalSeconds, precision: 3);
        }
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("Server closed the connection (PolicyViolation): Connection aborted because the client failed to close the connection after receiving a GoAway signal", true)]
    [InlineData("Connection lost: Unable to read data from the transport connection", true)]
    [InlineData("Server closed the connection (InternalServerError): Thread was cancelled", true)]
    [InlineData("Audio send failed: The remote party closed the WebSocket connection", true)]
    [InlineData("API key is empty.", false)]
    [InlineData("Endpoint is empty.", false)]
    [InlineData("Server error: code 400 — INVALID_ARGUMENT — bad model", false)]
    [InlineData("Server error: UNAUTHENTICATED — API key not valid", false)]
    [InlineData("PERMISSION_DENIED", false)]
    [InlineData("API_KEY_INVALID", false)]
    public void IsReconnectableFailure_ClassifiesReasons(string? reason, bool expected)
    {
        Assert.Equal(expected, LiveTranslateClient.IsReconnectableFailure(reason));
    }

    [Theory]
    [InlineData("Server error: code 429 — RESOURCE_EXHAUSTED — quota exceeded", true)]
    [InlineData("Connection lost: timeout", false)]
    [InlineData(null, false)]
    public void IsQuotaError_DetectsRateLimits(string? reason, bool expected)
    {
        Assert.Equal(expected, LiveTranslateClient.IsQuotaError(reason));
    }
}

public class PendingAudioTests
{
    private static byte[] Chunk(byte value, int size) => Enumerable.Repeat(value, size).ToArray();

    [Fact]
    public void Drain_CoalescesChunksInOrder()
    {
        var buffer = new PendingAudioBuffer(maxBytes: 100, maxFrameBytes: 10);
        buffer.Add(Chunk(1, 4));
        buffer.Add(Chunk(2, 4));
        buffer.Add(Chunk(3, 4));

        var frames = buffer.Drain();

        Assert.Equal(2, frames.Count);
        Assert.Equal(Chunk(1, 4).Concat(Chunk(2, 4)), frames[0]);
        Assert.Equal(Chunk(3, 4), frames[1]);
        Assert.Equal(0, buffer.SizeBytes);
    }

    [Fact]
    public void Add_DropsOldestChunksBeyondCapacity()
    {
        var buffer = new PendingAudioBuffer(maxBytes: 8, maxFrameBytes: 100);
        buffer.Add(Chunk(1, 4));
        buffer.Add(Chunk(2, 4));
        buffer.Add(Chunk(3, 4));

        Assert.Equal(8, buffer.SizeBytes);
        Assert.Equal(4, buffer.DroppedBytes);
        Assert.Equal(Chunk(2, 4).Concat(Chunk(3, 4)), Assert.Single(buffer.Drain()));
        Assert.Equal(0, buffer.DroppedBytes);
    }

    [Fact]
    public void Prepend_PutsCarriedAudioFirstAndKeepsNewestWithinCapacity()
    {
        var buffer = new PendingAudioBuffer(maxBytes: 8, maxFrameBytes: 100);
        buffer.Add(Chunk(3, 4));
        buffer.Prepend([Chunk(1, 4), Chunk(2, 4)]);

        Assert.Equal(Chunk(2, 4).Concat(Chunk(3, 4)), Assert.Single(buffer.Drain()));
    }

    [Fact]
    public void Client_BuffersAudioUntilSetupAndHandsItToReplacement()
    {
        var old = new LiveTranslateClient();
        old.SendPcm16Le(Chunk(1, 320));
        old.HandleMessage("""{"goAway":{"timeLeft":"5s"}}""");
        old.SendPcm16Le(Chunk(2, 320));
        Assert.Equal(640, old.PendingAudioBytes);

        var replacement = new LiveTranslateClient();
        replacement.SendPcm16Le(Chunk(3, 320));
        replacement.TakePendingAudioFrom(old);

        Assert.Equal(0, old.PendingAudioBytes);
        Assert.Equal(960, replacement.PendingAudioBytes);
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
    public void MixAverage_TakesTheMeanWithoutOverflow()
    {
        var output = new byte[8];
        PcmMixer.MixAverage(
            PcmBytes(1000, -1000, short.MaxValue, short.MinValue),
            PcmBytes(3000, 1000, short.MaxValue, short.MinValue),
            output);
        Assert.Equal(new short[] { 2000, 0, short.MaxValue, short.MinValue }, Samples(output));
    }

    [Fact]
    public void Mixer_MixesAcrossChunkBoundaries()
    {
        var mixed = new List<byte>();
        var mixer = new PcmMixer(bytes => mixed.AddRange(bytes));

        mixer.OfferMedia(PcmBytes(100, 200, 300));
        mixer.OfferMic(PcmBytes(0));
        mixer.OfferMic(PcmBytes(20, 40));

        Assert.Equal(new short[] { 50, 110, 170 }, Samples(mixed.ToArray()));
    }

    [Fact]
    public void Mixer_OddTrailingBytesDoNotStall()
    {
        var mixed = new List<byte>();
        var mixer = new PcmMixer(bytes => mixed.AddRange(bytes));

        mixer.OfferMedia(new byte[] { 10, 0, 99 }); // one sample plus half of another
        mixer.OfferMic(PcmBytes(10, 30));
        Assert.Equal(new short[] { 10 }, Samples(mixed.ToArray()));

        // Lone bytes are dropped; the leftover mic sample (30) then pairs with the next media sample.
        mixer.OfferMedia(new byte[] { 1 });
        mixer.OfferMic(new byte[] { 2 });
        mixer.OfferMedia(PcmBytes(50, 20));
        Assert.Equal(new short[] { 10, 40 }, Samples(mixed.ToArray()));
    }

    [Fact]
    public void Mixer_QueueCapKeepsNewestAudio()
    {
        var mixed = new List<byte>();
        var mixer = new PcmMixer(bytes => mixed.AddRange(bytes));

        // 2 s of media in 100 ms chunks while the mic is still "alive" (no pass-through):
        // only the newest 1.5 s may survive.
        for (short chunk = 0; chunk < 20; chunk++)
        {
            mixer.OfferMedia(PcmBytes(Enumerable.Repeat((short)(chunk * 100), 1600).ToArray()));
        }
        mixer.OfferMic(new byte[64_000]);

        var samples = Samples(mixed.ToArray());
        Assert.Equal(24_000, samples.Length);
        Assert.Equal(250, samples[0]);      // chunk 5 is the oldest kept (500 / 2)
        Assert.Equal(950, samples[^1]);     // chunk 19 is the newest (1900 / 2)
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

    private static short[] Samples(byte[] pcm)
    {
        var samples = new short[pcm.Length / 2];
        Buffer.BlockCopy(pcm, 0, samples, 0, samples.Length * 2);
        return samples;
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

    [Fact]
    public void CapTrim_DropsWholeSentences()
    {
        const string sentences = "这是第一句话。这是第二句话。这是第三句话。这是第四句话。";
        var acc = new TranscriptAccumulator(40);
        acc.Append(sentences + sentences);
        Assert.Equal("这是第四句话。这是第一句话。这是第二句话。这是第三句话。这是第四句话。", acc.Text);
    }

    [Fact]
    public void AlignCut_MovesPastTheNextTerminatorAndCloser()
    {
        const string text = "你好这是一个完整句子。”然后是下一句";
        Assert.Equal("你好这是一个完整句子。”".Length, TranscriptAccumulator.AlignCutToSentenceBoundary(text, 4));
        Assert.Equal("One. ".Length, TranscriptAccumulator.AlignCutToSentenceBoundary("One. Two", 1));
    }

    [Fact]
    public void AlignCut_KeepsCutWhenNoTerminatorIsNear()
    {
        Assert.Equal(6, TranscriptAccumulator.AlignCutToSentenceBoundary("一段没有任何句号的长文本继续继续继续继续继续", 6));
    }

    [Fact]
    public void CollapseCjkInteriorSpaces_OnlyBetweenHanOrKana()
    {
        Assert.Equal("做成任何其他东西", TranscriptAccumulator.CollapseCjkInteriorSpaces("做 成任何其他东西"));
        Assert.Equal("做成", TranscriptAccumulator.CollapseCjkInteriorSpaces("做  成"));
        Assert.Equal("こんにちは世界", TranscriptAccumulator.CollapseCjkInteriorSpaces("こんにちは 世界"));
        Assert.Equal("hello world", TranscriptAccumulator.CollapseCjkInteriorSpaces("hello world"));
        Assert.Equal("用 hello", TranscriptAccumulator.CollapseCjkInteriorSpaces("用 hello"));
        // Korean separates words with spaces — keep them.
        Assert.Equal("안녕하세요 여러분", TranscriptAccumulator.CollapseCjkInteriorSpaces("안녕하세요 여러분"));
    }

    [Fact]
    public void SentenceLineBreaker_StripsCjkInteriorSpaces()
    {
        const string clean = "好的。嗯。然后他就走了。后来我们去了公园。下一段从这里开始。";
        const string gapped = "好的。嗯。然后他就走 了。后来我们去了公园。下一段从这里开始。";
        Assert.Equal(SentenceLineBreaker.Format(clean), SentenceLineBreaker.Format(gapped));
    }
}

public class SentenceLineBreakerTests
{
    [Fact]
    public void LongChineseSentence_BreaksAfterIdeographicPeriod()
    {
        var text = "这是一句足够长的中文翻译内容。下一句从这里开始";
        Assert.Equal("这是一句足够长的中文翻译内容。\n下一句从这里开始", SentenceLineBreaker.Format(text, minCharsPerLine: 12));
    }

    [Fact]
    public void LongEnglishSentence_BreaksAfterPeriod()
    {
        var text = "This is a reasonably long sentence. The next one starts here";
        Assert.Equal("This is a reasonably long sentence.\nThe next one starts here", SentenceLineBreaker.Format(text, minCharsPerLine: 16));
    }

    [Fact]
    public void ShortSentences_StayOnTheSameLine()
    {
        Assert.Equal("好的。我知道了。", SentenceLineBreaker.Format("好的。我知道了。", minCharsPerLine: 16));
        Assert.Equal("OK. I see.", SentenceLineBreaker.Format("OK. I see.", minCharsPerLine: 16));
    }

    [Fact]
    public void DecimalPoint_IsNotABreak()
    {
        Assert.Equal("The value is 3.14 today.", SentenceLineBreaker.Format("The value is 3.14 today.", minCharsPerLine: 8));
    }

    [Fact]
    public void ClosingQuoteStaysOnTheSameLine()
    {
        var text = "他说这是一句足够长的话。”下一句";
        Assert.Equal("他说这是一句足够长的话。”\n下一句", SentenceLineBreaker.Format(text, minCharsPerLine: 8));
    }

    [Fact]
    public void EllipsisAndFullwidthTerminators_Break()
    {
        Assert.Equal("这是一句足够长的内容…\n下一句", SentenceLineBreaker.Format("这是一句足够长的内容…下一句", minCharsPerLine: 8));
        Assert.Equal("这是一句足够长的内容！\n下一句", SentenceLineBreaker.Format("这是一句足够长的内容！下一句", minCharsPerLine: 8));
        Assert.Equal("Wait, this is long enough...\nNext", SentenceLineBreaker.Format("Wait, this is long enough...Next", minCharsPerLine: 8));
    }

    [Fact]
    public void TrailingTerminator_DoesNotAddABlankLine()
    {
        Assert.Equal("这是一句足够长的中文翻译内容。", SentenceLineBreaker.Format("这是一句足够长的中文翻译内容。", minCharsPerLine: 8));
    }

    [Fact]
    public void GrowingStream_IsStable()
    {
        const int min = 8;
        var prefixes = new[]
        {
            "这是一句",
            "这是一句足够长的内容",
            "这是一句足够长的内容。",
            "这是一句足够长的内容。下一句也变长了。",
            "这是一句足够长的内容。下一句也变长了。再往后",
        };

        string? previous = null;
        foreach (var prefix in prefixes)
        {
            var formatted = SentenceLineBreaker.Format(prefix, min);
            if (previous != null)
            {
                // Newline positions already decided must not move or disappear.
                var prevLines = previous.Split('\n');
                var nextLines = formatted.Split('\n');
                for (var i = 0; i < prevLines.Length - 1; i++)
                {
                    Assert.True(i < nextLines.Length);
                    Assert.Equal(prevLines[i], nextLines[i]);
                }
                Assert.StartsWith(prevLines[^1], nextLines[prevLines.Length - 1]);
            }
            previous = formatted;
        }

        Assert.Equal("这是一句足够长的内容。\n下一句也变长了。\n再往后", previous);
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
    public void EnglishExport_UsesEnglishHeadingsAndFileName()
    {
        var stopped = new DateTime(2026, 7, 6, 9, 5, 0);
        var md = MarkdownExporter.BuildMarkdown("", "translated text", stopped, chinese: false);
        Assert.Contains("# Translation", md);
        Assert.Contains("- Stopped at: `2026-07-06 09:05`", md);
        Assert.Contains("## Source", md);
        Assert.Contains("(empty)", md);
        Assert.DoesNotContain("译文", md);

        var name = MarkdownExporter.FileName(stopped, chinese: false);
        Assert.Equal("Translation-0706-09.05.md", name);
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

            repo.Update(s => s with { TargetLanguageCode = "ja", FontSize = 24, Bilingual = true, AudioSourceMode = AudioSourceMode.MediaAndMic, BackgroundOpacity = 0 });
            repo.Flush();

            var reloaded = new UserSettingsRepository(path);
            Assert.Equal("ja", reloaded.Current.TargetLanguageCode);
            Assert.Equal(24, reloaded.Current.FontSize);
            Assert.True(reloaded.Current.Bilingual);
            Assert.Equal(AudioSourceMode.MediaAndMic, reloaded.Current.AudioSourceMode);
            Assert.Equal(0, reloaded.Current.BackgroundOpacity);

            // Files from older versions still carry the removed echoTargetLanguage toggle.
            File.WriteAllText(path, """{"targetLanguageCode":"ko","echoTargetLanguage":false,"bilingual":true}""");
            var legacy = new UserSettingsRepository(path);
            Assert.Equal("ko", legacy.Current.TargetLanguageCode);
            Assert.True(legacy.Current.Bilingual);

            File.WriteAllText(path, "{corrupt");
            var recovered = new UserSettingsRepository(path);
            Assert.Equal(UserSettings.DefaultEndpoint, recovered.Current.Endpoint);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void LanguageCatalog_MatchesOfficialLiveTranslateCodes()
    {
        var codes = Languages.TargetOptions.Select(o => o.Code).ToList();
        Assert.Equal(codes.Count, codes.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("zh-Hans", codes);
        Assert.Contains("no", codes);
        Assert.Contains("fil", codes);
        Assert.Contains("he", codes);
        Assert.Equal("zh-Hans", Languages.FindTarget("missing").Code);
        Assert.Equal(78, codes.Count);
    }

    [Theory]
    [InlineData("ja", "ja")]
    [InlineData(" ZH-hant ", "zh-Hant")]
    [InlineData("pt-br", "pt-BR")]
    [InlineData("nb", "no")]
    [InlineData("NN", "no")]
    [InlineData("xx-unknown", "zh-Hans")]
    [InlineData("", "zh-Hans")]
    [InlineData(null, "zh-Hans")]
    public void LanguageCatalog_CanonicalOrDefault(string? stored, string expected)
    {
        Assert.Equal(expected, Languages.CanonicalOrDefault(stored));
    }
}
