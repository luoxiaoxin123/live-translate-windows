namespace LiveTranslate.Core.Live;

/// <summary>
/// Parameters for one Live Translate session. <see cref="EchoTargetLanguage"/> stays on so input
/// already in the target language still produces captions (e.g. a Chinese video with a Chinese
/// target); the session layer then collapses them to one line and mutes the echoed voice.
/// A non-null <see cref="ResumptionHandle"/> (issued by the server) continues the previous
/// session on the new connection instead of starting over.
/// </summary>
public sealed record SessionConfig(
    string Endpoint,
    string ApiKey,
    string ModelId,
    string TargetLanguageCode,
    bool EchoTargetLanguage = true,
    string? ResumptionHandle = null);
