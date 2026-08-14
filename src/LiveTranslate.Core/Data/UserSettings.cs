using LiveTranslate.Core.Audio;

namespace LiveTranslate.Core.Data;

/// <summary>All persisted user settings.</summary>
public sealed record UserSettings
{
    public const string DefaultEndpoint =
        "wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent";

    public const string DefaultModelId = "gemini-3.5-live-translate-preview";

    public string Endpoint { get; init; } = DefaultEndpoint;
    public string ModelId { get; init; } = DefaultModelId;
    public string TargetLanguageCode { get; init; } = "zh-Hans";

    /// <summary>
    /// Official default is false: stay silent when input is already the target language.
    /// True makes the model parrot that audio (and can pull BGM into the translated voice).
    /// </summary>
    public bool EchoTargetLanguage { get; init; }

    public double FontSize { get; init; } = 18;
    public double BackgroundOpacity { get; init; } = 0.65;
    public bool Bilingual { get; init; }

    public bool PlayTranslatedAudio { get; init; }
    public double TranslatedVolume { get; init; } = 0.8;

    /// <summary>Overlay geometry in physical pixels; -1 means "not placed yet" (auto-position).</summary>
    public int OverlayX { get; init; } = -1;
    public int OverlayY { get; init; } = -1;
    public int OverlayWidth { get; init; } = -1;
    public int OverlayHeight { get; init; } = -1;

    public AudioSourceMode AudioSourceMode { get; init; } = AudioSourceMode.Media;

    public UserSettings ResetSubtitleAppearance() => this with
    {
        FontSize = 18,
        BackgroundOpacity = 0.65,
        Bilingual = false,
    };
}
