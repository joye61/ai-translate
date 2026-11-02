# AI Translate

基于 **.NET 10 + Avalonia 12** 的跨平台 AI 桌面词典、全局划词、截图原位翻译与 FSRS 科学记忆生词本，支持 **Windows / macOS / Linux**。

## 核心特性

- **AI 认知词典工作台**：支持 DeepSeek、OpenAI、兼容端点与本地 Ollama；提供多词性释义、词源词根拆解、AI 巧记、语法剖析、高频搭配与双语例句。
- **三大取词与翻译通道**：
  - **灵动划词速译 (`Alt + Q`)**：提取选中文字弹窗速译，自动还原系统剪贴板。
  - **XCapture 截图原位翻译 (`Alt + S`)**：离线 RapidOCR 识别 + 表格/段落分析 + 原位背景擦除排版重绘。
  - **剪贴板监听 (`Alt + W`)**：支持后台实时监听或快捷键即刻精翻。
- **KokoroSharp 神经语音与自然拼读**：集成 `KokoroSharp` (Kokoro-82M ONNX)，支持美式/英式多声线、IPA 音标直驱 (`[word](/ipa/)`)、**自然拼读音节拆解领读**、0.7x 慢速跟读与生词磨耳朵连读。
- **FSRS 超级生词本**：完整保留查词原句上下文，内置 FSRS 间隔重复算法（闪卡 / 语境完形填空 / 看义拼写 / 盲听默写），支持导出至 Anki TSV、Markdown、CSV、JSON。

## 项目结构

```text
src/
  AiTranslate.Core/              # 核心领域模型、FSRS 记忆调度算法与跨平台契约
  AiTranslate.Ai/                # AI 流式传输、区域翻译复核与认知词典服务
  AiTranslate.Ocr/               # 本地离线 RapidOCR (ONNX Runtime + SkiaSharp)
  AiTranslate.Speech/            # KokoroSharp 神经网络 TTS 与自然拼读拆解引擎
  AiTranslate.Vocabulary/        # 生词本持久化、多维检索与多格式导入导出
  AiTranslate.Platform.Windows/  # Windows 截图、全局热键、剪贴板与划词捕获
  AiTranslate.Platform.MacOS/    # macOS 屏幕捕获、pbpaste 监听与 AppleScript 划词
  AiTranslate.Platform.Linux/    # Linux (X11/Wayland) 截图与 Primary Selection 划词
  AiTranslate.Desktop/           # Avalonia Bento UI 工作台、系统托盘与截图覆盖层
tests/
  AiTranslate.Tests/             # 核心算法、FSRS 调度、翻译管线与语音拼读测试
```

## 开发与构建

### 环境要求
- [.NET 10 SDK](https://dotnet.microsoft.com/) (`10.0.100` 或更高版本)
- 支持操作系统：Windows 10/11、macOS 12+、Linux (X11 / Wayland)

### 常用命令

```bash
# 1. 还原依赖并编译全解决方案
dotnet build AiTranslate.sln

# 2. 启动桌面客户端
dotnet run --project src/AiTranslate.Desktop/AiTranslate.Desktop.csproj

# 3. 运行自动化测试套件
dotnet run --project tests/AiTranslate.Tests/AiTranslate.Tests.csproj

# 4. 发布跨平台可执行程序 (以 Windows / macOS / Linux 为例)
dotnet publish src/AiTranslate.Desktop/AiTranslate.Desktop.csproj -c Release -r win-x64
dotnet publish src/AiTranslate.Desktop/AiTranslate.Desktop.csproj -c Release -r osx-arm64
dotnet publish src/AiTranslate.Desktop/AiTranslate.Desktop.csproj -c Release -r linux-x64
```

## 默认快捷键

| 快捷键 | 功能说明 |
| :--- | :--- |
| `Alt + D` | 呼出 / 激活主词典工作台 |
| `Alt + Q` | 全局划词即时翻译弹窗 |
| `Alt + S` | 启动 XCapture 截图原位翻译 |
| `Alt + W` | 读取并翻译当前剪贴板文本 |

