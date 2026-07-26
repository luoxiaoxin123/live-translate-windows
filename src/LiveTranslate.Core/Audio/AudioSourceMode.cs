namespace LiveTranslate.Core.Audio;

/// <summary>Which audio the session captures and translates.</summary>
public enum AudioSourceMode
{
    Media,
    Mic,
    MediaAndMic,
}

public static class AudioSourceModeExtensions
{
    public static bool NeedsSystemAudio(this AudioSourceMode mode) =>
        mode is AudioSourceMode.Media or AudioSourceMode.MediaAndMic;

    public static bool NeedsMicrophone(this AudioSourceMode mode) =>
        mode is AudioSourceMode.Mic or AudioSourceMode.MediaAndMic;

    public static AudioSourceMode FromStorage(string? value) =>
        Enum.TryParse<AudioSourceMode>(value, ignoreCase: true, out var mode) ? mode : AudioSourceMode.Media;
}
