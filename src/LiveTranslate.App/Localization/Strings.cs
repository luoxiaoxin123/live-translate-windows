using System.Globalization;

namespace LiveTranslate.App.Localization;

/// <summary>
/// UI strings. Follows the system language: Chinese UI on a Chinese system, English otherwise.
/// Code-based instead of resw so the app builds with the plain dotnet CLI and no PRI tooling.
/// </summary>
public static class L
{
    public static bool IsChinese { get; } =
        CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

    private static string T(string zh, string en) => IsChinese ? zh : en;

    // App / navigation
    public static string AppTitle => T("实时翻译", "Live Translate");
    public static string NavSubtitles => T("字幕", "Subtitles");
    public static string NavSettings => T("设置", "Settings");

    // Subtitle page
    public static string SubtitlePageTitle => T("实时字幕", "Live subtitles");
    public static string StatusIdle => T("未开始", "Idle");
    public static string StatusStarting => T("正在连接…", "Connecting…");
    public static string StatusRunning => T("字幕运行中", "Subtitles running");
    public static string StatusReconnecting => T("正在重连…", "Reconnecting…");
    public static string StatusStopped => T("已停止", "Stopped");
    public static string StatusError => T("出错", "Error");
    public static string ReconnectGaveUp =>
        T("多次重连失败，已停止。请检查网络后重新启动字幕。",
          "Gave up after several reconnect attempts. Check the network and start again.");
    public static string CaptureStopped(string reason) =>
        T($"音频采集已停止：{reason}", $"Audio capture stopped: {reason}");
    public static string CaptureFailed(string reason) =>
        T($"音频采集启动失败：{reason}", $"Audio capture failed: {reason}");
    public static string StartSubtitles => T("启动字幕", "Start subtitles");
    public static string StopSubtitles => T("停止字幕", "Stop subtitles");
    public static string TargetLanguage => T("目标语言", "Target language");
    public static string TargetLanguageHint =>
        T("源语言由模型自动识别，只需选择译文语言。",
          "The model detects the source language; pick the language to translate into.");
    public static string AudioSource => T("声音来源", "Audio source");
    public static string AudioSourceMedia => T("媒体音（本机正在播放的声音）", "System audio (what this PC is playing)");
    public static string AudioSourceMic => T("麦克风", "Microphone");
    public static string AudioSourceBoth => T("媒体音 + 麦克风", "System audio + microphone");
    public static string PreviewTitle => T("实时预览", "Live preview");
    public static string PreviewSource => T("原文", "Original");
    public static string PreviewTranslation => T("译文", "Translation");
    public static string ExportMarkdown => T("导出本次翻译为 Markdown", "Export this session as Markdown");
    public static string ExportedTo(string path) => T($"已导出：{path}", $"Exported to {path}");
    public static string ExportFailed(string reason) => T($"导出失败：{reason}", $"Export failed: {reason}");
    public static string NoApiKey => T("请先在「设置」中填写 API Key", "Add an API key in Settings first");
    public static string SessionClosed(string reason) => T($"连接已断开：{reason}", $"Connection closed: {reason}");
    public static string UsingClassicLoopback =>
        T("提示：正在使用经典内录（系统不支持进程排除）。开启译音时将暂停采集以防回声。",
          "Note: classic loopback in use (process-exclude unsupported); capture pauses while translated audio plays to avoid feedback.");

    // Settings page
    public static string SettingsTitle => T("设置", "Settings");
    public static string SectionApi => T("API", "API");
    public static string EndpointLabel => T("端点（WebSocket 地址）", "Endpoint (WebSocket URL)");
    public static string ModelLabel => T("模型 ID", "Model ID");
    public static string ApiKeysLabel => T("API Key（最多 10 个）", "API keys (up to 10)");
    public static string AddKey => T("添加 Key", "Add key");
    public static string RemoveKey => T("删除", "Remove");
    public static string RevealKeys => T("显示 Key", "Show keys");
    public static string SaveAndTest => T("保存并测试连接", "Save and test connection");
    public static string SaveKeys => T("保存 API Key", "Save API keys");
    public static string Testing(int index, int total) => T($"正在测试第 {index}/{total} 个 Key…", $"Testing key {index}/{total}…");
    public static string TestSummary(int ok, int total) => T($"{ok}/{total} 个 Key 可用", $"{ok}/{total} keys OK");
    public static string KeyTooShort => T("Key 太短（少于 16 个字符）", "Key too short (under 16 characters)");
    public static string KeyOk => T("✅ 可用", "✅ OK");
    public static string Saved => T("已保存", "Saved");
    public static string SaveFailed(string reason) => T($"保存失败：{reason}", $"Save failed: {reason}");
    public static string Cancel => T("取消", "Cancel");
    public static string TestCancelled => T("已取消测试", "Test cancelled");

    public static string SectionAppearance => T("字幕外观", "Subtitle appearance");
    public static string FontSizeLabel => T("字号", "Font size");
    public static string OpacityLabel => T("背景不透明度", "Background opacity");
    public static string BilingualLabel => T("双语显示", "Bilingual display");
    public static string BilingualOn => T("显示原文 + 译文", "Show source + translation");
    public static string BilingualOff => T("仅显示译文", "Translation only");
    public static string ResetAppearance => T("重置字幕外观", "Reset subtitle appearance");

    public static string SectionVoice => T("译音", "Translated voice");
    public static string PlayVoiceLabel => T("播放译音", "Play translated voice");
    public static string PlayVoiceHint => T("与原声并行播放翻译语音", "Speak the translation alongside the original audio");
    public static string EchoTargetLabel => T("已是目标语言时仍复读", "Echo speech already in the target language");
    public static string EchoTargetHint =>
        T("关闭（推荐）：片里已是译文语言时保持静默。开启后模型会再念一遍，背景音乐可能进入译音。下次启动或重连后生效。",
          "Off (recommended): stay silent when the input is already the target language. On, the model parrots it and background music may leak into the voice. Applies on the next start or reconnect.");
    public static string VolumeLabel => T("译音音量", "Voice volume");
    public static string VolumeBoostHint => T("超过 100% 为数字增益，可能轻微失真", "Above 100% uses digital gain and may distort slightly");

    public static string SectionAbout => T("关于", "About");
    public static string VersionLabel => T("版本", "Version");
    public static string AboutDescription =>
        T("基于 Google Gemini Live Translate 的实时字幕应用。",
          "Real-time subtitles powered by Google Gemini Live Translate.");
}
