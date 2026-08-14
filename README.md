# Live Translate for Windows

[简体中文](README.zh-CN.md)

Android version: [Live Translate for Android](https://github.com/luoxiaoxin123/live-translate)

A **real-time subtitle** app for Windows with a native WinUI 3 interface, powered by [Google Gemini Live Translate](https://ai.google.dev/gemini-api/docs/live-api/live-translate) (model `gemini-3.5-live-translate-preview`). It captures **what your PC is playing**, the **microphone**, or both mixed, streams the audio to the Live Translate API, and shows the translation in a **draggable, resizable always-on-top floating subtitle window** — optionally speaking the translation out loud alongside the original audio. After stopping, the session can be exported as Markdown.

## Features

| Module | Description |
|--------|-------------|
| Subtitles page | Target language (70+ official Live Translate languages; source is auto-detected), audio source, start/stop, status and live preview |
| Audio source | System audio / microphone / both (per-sample mixing) |
| Floating subtitles | Always on top, never steals focus, per-pixel transparency; drag by the top handle, resize by the bottom-right handle (font size unchanged); position and size remembered |
| Display modes | Translation only, or bilingual (source + translation with a divider) |
| Auto-scroll | Each pane scrolls independently, and **only when a new line wraps** — no jitter while a line is still filling in |
| Audio capture | System audio via WASAPI **process-exclude loopback** (the app's own translated voice is excluded automatically, so no feedback loop; Windows 10 2004+), falling back to classic loopback with capture paused while the translated voice plays. Microphone at 16 kHz PCM |
| Live API | WebSocket + `translationConfig`, aligned with the official Live Translate protocol; closes on GoAway and reconnects automatically |
| Translated voice | Off by default; plays alongside the original audio; volume up to **200%** (digital gain) |
| Multiple API keys | Up to 10; sessions rotate through them; the connection test checks every key |
| Export | After stopping, export the session transcript as Markdown to Downloads |
| UI language | Follows the system: Chinese system → Chinese UI, otherwise English |

## Requirements

- Windows 10 2004 (build 19041) or later, Windows 11 recommended
- An API key from [Google AI Studio](https://aistudio.google.com/)
- Microphone mode needs desktop apps allowed to access the microphone (Settings → Privacy)

## Download (for everyone else)

1. Open the [Releases](https://github.com/luoxiaoxin123/live-translate-windows/releases) page
2. Download **LiveTranslate-Setup-x64.exe**
3. Double-click it → Next → install (no administrator account needed)
4. Open **实时翻译** from the desktop or Start menu

No .NET runtime to install. If SmartScreen says Windows protected your PC: **More info** → **Run anyway**.

## Building from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download) (no Visual Studio needed):

```powershell
dotnet build LiveTranslate.slnx            # build (Debug x64)
dotnet test  LiveTranslate.slnx            # unit tests
# run
.\src\LiveTranslate.App\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\LiveTranslate.exe
```

## Usage

1. Open the app → **Settings** → enter one or more API keys → **Save and test connection**
2. On the **Subtitles** page pick the target language and audio source → **Start subtitles**
3. Play foreign-language media or speak into the microphone; the translation appears in the floating window
4. After **stopping**, export the session as Markdown (saved to Downloads, e.g. `7月26日-14.30-翻译结果.md`)

Floating window tips: drag the **thin bar** at the top to move; drag the **bottom-right handle** to resize (font size stays); font size, background opacity and bilingual mode are in Settings and apply live.

## Project layout

```text
src/LiveTranslate.Core/   # UI-free business logic (unit-testable)
  Audio/    mic / system-audio capture (process-exclude loopback COM interop) / mixing / resampling / translated-voice playback
  Live/     LiveTranslateClient (WebSocket protocol and state machine)
  Data/     settings (JSON) / API keys (DPAPI-encrypted) / language catalog
  Text/     transcript accumulation (dedupes the server's cumulative rewrites)
  Export/   Markdown export
src/LiveTranslate.App/    # WinUI 3 app
  Views/    main window, subtitles page, settings page, floating subtitle window
  ViewModels/  Services/ (session orchestration)  Localization/
tests/LiveTranslate.Tests/  # xUnit unit tests (protocol / DSP / storage / export)
```

## Privacy

- API keys are stored DPAPI-encrypted (current user) under `%LocalAppData%\LiveTranslate\`
- Audio is sent only to the endpoint you configure (Google AI Studio Live API by default); there is no backend of ours
- The diagnostic log `%LocalAppData%\LiveTranslate\session.log` records status and a few transcript snippets; delete it anytime

## Known limitations

- DRM-protected or exclusive-mode audio cannot be captured by loopback → use the microphone instead
- Live Translate only accepts a **target** language; the model detects the source itself
- Each Live WebSocket lasts about 10 minutes; the app closes on the server's GoAway and opens a fresh connection, keeping the overlay and transcript
- Exports are two full sections (source + translation), not sentence-aligned pairs
- The preview model and its quota may change; the endpoint and model ID are editable in Settings

## License

[Apache License 2.0](LICENSE)
