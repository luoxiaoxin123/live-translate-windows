# 实时翻译（Live Translate for Windows）

[English](README.md)

基于 [Google Gemini Live Translate](https://ai.google.dev/gemini-api/docs/live-api/live-translate)（模型 `gemini-3.5-live-translate-preview`）的 **Windows 实时字幕**应用，WinUI 3 原生界面。捕获**本机正在播放的声音**、**麦克风**或两者混合，推流到 Live Translate 接口，屏幕上显示**可拖动缩放的置顶悬浮字幕**，可选**译音**并行播放，停止后可导出 Markdown。

## 功能

| 模块 | 说明 |
|------|------|
| 字幕页 | 源/目标语言（21 种）、声音来源、启动/停止、状态与实时预览 |
| 声音来源 | 媒体音 / 麦克风 / 媒体+麦克风（逐样本混音） |
| 悬浮字幕 | 置顶、不抢焦点、逐像素透明；顶部把手拖动，右下角把手缩放（字号不变）；位置尺寸记忆 |
| 显示模式 | 仅译文，或双语（原文 + 译文，中间分隔线） |
| 自动滚动 | 原文/译文各自滚动；**只有换行时才滚动**，避免字幕抖动 |
| 音频采集 | 媒体音：WASAPI **进程排除 loopback**（自动排除本应用的译音，杜绝回声循环；Win10 2004+）；不支持时回退经典 loopback + 播译音时暂停采集。麦克风：16 kHz PCM |
| Live API | WebSocket + `translationConfig`，对齐官方 Live Translate 协议 |
| 译音 | 默认关闭；与原声并行；音量最高 **200%**（数字增益） |
| 多 API Key | 最多 10 个；会话启动按顺序轮询；连接测试会逐个测试 |
| 导出 | 停止后导出本次原文/译文为 Markdown 到「下载」目录 |
| 语言 | 界面跟随系统：中文系统 → 中文界面，否则英文 |

## 运行要求

- Windows 10 2004（19041）及以上，建议 Windows 11
- [Google AI Studio](https://aistudio.google.com/) 的 API Key
- 麦克风模式需要允许桌面应用访问麦克风（系统设置 → 隐私）

## 从源码构建

需要 [.NET 10 SDK](https://dotnet.microsoft.com/download)（无需 Visual Studio）：

```powershell
dotnet build LiveTranslate.slnx            # 构建（Debug x64）
dotnet test  LiveTranslate.slnx            # 单元测试
# 运行
.\src\LiveTranslate.App\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\LiveTranslate.exe
```

### 发布（self-contained，免装 .NET）

```powershell
powershell -ExecutionPolicy Bypass -File tools\pack.ps1
```

产出 `LiveTranslate-win-x64.zip`，结构对接收者友好：

```text
实时翻译.exe    ← 双击这个启动（内置图标的小启动器）
app\            ← 程序本体（自包含，无需装 .NET）
```

## 使用

1. 打开应用 → **设置** → 填入一个或多个 API Key → **保存并测试连接**
2. **字幕**页选择目标语言与声音来源 → **启动字幕**
3. 播放外文媒体或对麦克风说话，悬浮窗出现译文
4. **停止**后可 **导出本次翻译为 Markdown**（保存到「下载」，文件名如 `7月26日-14.30-翻译结果.md`）

悬浮窗技巧：拖顶部**细横条**移动；拖右下角**把手**改大小（字号不变）；字号、背景透明度、双语开关在设置页调整，运行中即时生效。

## 项目结构

```text
src/LiveTranslate.Core/   # 无 UI 依赖的业务逻辑（可单测）
  Audio/    麦克风 / 系统音采集（进程排除 loopback COM interop）/ 混音 / 重采样 / 译音播放
  Live/     LiveTranslateClient（WebSocket 协议与状态机）
  Data/     设置(JSON) / API Key(DPAPI 加密) / 语言表
  Text/     转写累积（服务端累积重写去重）
  Export/   Markdown 导出
src/LiveTranslate.App/    # WinUI 3 应用
  Views/    主窗口、字幕页、设置页、悬浮字幕窗
  ViewModels/  Services/(会话编排)  Localization/
tests/LiveTranslate.Tests/  # xUnit 单元测试（协议 / DSP / 存储 / 导出）
```

## 隐私

- API Key 使用 Windows DPAPI（当前用户）加密保存在 `%LocalAppData%\LiveTranslate\`
- 音频只发往你配置的端点（默认 Google AI Studio Live API），无自建后端
- 诊断日志 `%LocalAppData%\LiveTranslate\session.log` 仅记录状态与少量转写片段，可随时删除

## 已知限制

- 受 DRM/独占模式保护的音频无法被 loopback 捕获 → 改用麦克风
- Live Translate 以**目标语言**为主；源语言选「自动检测」最稳妥
- 导出为「全文原文 + 全文译文」两段，非逐句对照
- 预览版模型与配额可能变化；端点与模型 ID 可在设置中修改

## 许可

[Apache License 2.0](LICENSE)
