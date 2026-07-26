namespace LiveTranslate.Core.Live;

/// <summary>Parameters for one Live Translate session.</summary>
public sealed record SessionConfig(
    string Endpoint,
    string ApiKey,
    string ModelId,
    string TargetLanguageCode,
    bool EchoTargetLanguage = true);
