# <img src="src/AiTranslate.Desktop/Assets/Brand/ai-translate.svg" width="48" height="48" align="absmiddle" alt="" /> AI Translate

基于 **.NET 10 + Avalonia 12** 的跨平台 AI 桌面词典、全局划词、截图原位翻译与 FSRS 科学记忆生词本，支持 **Windows / macOS / Linux**。

## 核心特性

- **AI 认知词典工作台**：支持 DeepSeek、OpenAI、兼容端点与本地 Ollama；提供多词性释义、词源词根拆解、AI 巧记、语法剖析、高频搭配与双语例句。
- **三大取词与翻译通道**：
  - **灵动划词速译 (`Alt + Q`)**：提取选中文字弹窗速译，自动还原系统剪贴板。
  - **XCapture 截图原位翻译 (`Alt + S`)**：离线 RapidOCR 识别 + 表格/段落分析 + 原位背景擦除排版重绘。
  - **剪贴板监听 (`Alt + W`)**：支持后台实时监听或快捷键即刻精翻。
- **神经语音引擎**在软件启动时后台预加载离线模型。支持英美多声线、IPA 音标朗读、自然拼读、慢速跟读和生词卡片连读。
- **语法精解**面向学生：输入语法点（任何说法都行，如“过去进行时”“was/were 加 ing 怎么用”），或粘贴一个句子，AI 自己判断要讲什么——前者给出完整讲解（核心概念、构成公式、逐条用法与例句、标志词、变化规则、易混对比、特殊情况、常见错误、考点技巧、口诀、可自测的小练习、相关语法），后者找出句中的语法点逐项解析，并可一键深入精解。讲过的语法存入本地语法库，不同问法落到同一个规范名称，再次询问直接命中、不消耗 AI。翻译结果的「关键语法」会和语法库双向关联，点「精解」即可进入。
- **生词本**把主动收藏的单词、句子和段落按分组管理。每个分组一个列表，可排序、导出（Anki TSV、Markdown、CSV、JSON）。列表只显示词条与释义，点开后与查词结果一致。「复习」按分组逐张出题：先看词，选「认识 / 模糊 / 不认识」，再看释义，不认识的词稍后会再出现。评分由 FSRS 安排下次复习时间。

## 项目结构

```text
src/
  AiTranslate.Core/              # 核心领域模型、FSRS 记忆调度算法与跨平台契约
  AiTranslate.Ai/                # AI 流式传输、区域翻译复核与认知词典服务
  AiTranslate.Ocr/               # 本地离线 RapidOCR (ONNX Runtime + SkiaSharp)
  AiTranslate.Speech/            # KokoroSharp 神经网络 TTS 与自然拼读拆解引擎
  AiTranslate.Vocabulary/        # 生词本分组与词条持久化、复习调度记录与多格式导出
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
dotnet test tests/AiTranslate.Tests/AiTranslate.Tests.csproj

# 4. 发布跨平台可执行程序 (以 Windows / macOS / Linux 为例)
dotnet publish src/AiTranslate.Desktop/AiTranslate.Desktop.csproj -c Release -r win-x64
dotnet publish src/AiTranslate.Desktop/AiTranslate.Desktop.csproj -c Release -r osx-arm64
dotnet publish src/AiTranslate.Desktop/AiTranslate.Desktop.csproj -c Release -r linux-x64
```

### 桌面图标与发布

Windows 可执行文件内嵌多尺寸图标。macOS 发布目录中的 `AI Translate.app` 可整体移入「应用程序」，Finder 和 Dock 使用包内的 ICNS，菜单栏使用跟随系统明暗的模板图标。

Linux 发布目录包含应用菜单条目和 hicolor 图标。在 Linux 上将发布目录放到固定位置后，可以在该目录执行以下命令，为当前用户注册桌面入口。`~/.local/bin` 需要在桌面会话的 `PATH` 中。

```sh
data_home="${XDG_DATA_HOME:-$HOME/.local/share}"
mkdir -p "$HOME/.local/bin" "$data_home/applications" "$data_home/icons"
ln -sfn "$PWD/AiTranslate.Desktop" "$HOME/.local/bin/AiTranslate.Desktop"
install -m 644 share/applications/ai-translate.desktop "$data_home/applications/"
cp -R share/icons/hicolor "$data_home/icons/"
```

品牌母版和分平台资源位于 [Assets/Brand](src/AiTranslate.Desktop/Assets/Brand)。修改 SVG 后，可用安装了 `resvg-py` 和 `Pillow` 的 Python 运行 `python tools/generate-icons.py`，重建 ICO、ICNS、PNG 和 symbolic SVG。界面内的功能图标只用 [Font Awesome Free](https://fontawesome.com/icons)（许可证见 `LICENSE-FONTAWESOME.txt`），增删图标见 `tools/generate-fontawesome-icons.mjs`。

## 缓存、术语参考与离线模型

工作台默认使用「自动 · 系统语言」。外语译为当前系统显示语言，例如简体中文系统下的日文会译为简体中文。输入与系统语言相同时译为英语，英文系统下的英文则译为简体中文。释义与译法辨析使用系统语言。可以在输入框下方手动指定目标语言，划词和剪贴板快译共用这一选择。截图原位翻译单独选择目标语言。

划词后，点击浮窗的「加入生词表」才会将词条收录到默认生词本。

输入语言在本地识别。没有上下文的单字和多种语言共用的词可能有歧义，可手动指定目标语言。自动识别使用 fastText 的 `lid.176.ftz` 模型。[模型说明](https://fasttext.cc/docs/en/language-identification.html) · [CC BY-SA 3.0](https://creativecommons.org/licenses/by-sa/3.0/)

单词结果把双语例句放在对应的句型或搭配下面。读长句时，将鼠标移到原文的语法高亮上可看解释。点击带下划线的表达，或选中原文、译文、例句中的文字并右键查词，可以带着所在句查询。联查页的「返回上一级」会恢复之前的结果与滚动位置。

有些表达两种说法都成立，意思却不同。「用法对照」会并列说明两种说法各在什么场景使用，例如 `stop doing` 和 `stop to do`。「词形变化」列出过去式、复数等形式，「词族」列出可以继续点查的派生词。句段结果中的「译法与依据」引用原文和主译文中的实际片段，遇到歧义时说明另一种译法成立的条件。

划词与剪贴板速译先给当前语境的主译，简单内容只调用一次 AI。遇到否定范围、数字、占位符缺失、专业术语等风险时会追加一次质量审校。工作台查词和复杂句段会先显示基础译文，再按需补充学习解析，最后独立核对主译与各栏目。正常一次查询最多经过三个角色，短暂的供应商限流或服务不可用可共用一次重试，总计最多四次请求。英语学习词条可参考应用内置的 WordNet 3.0 线索。[WordNet 项目](https://wordnet.princeton.edu/)

工作台可点击「核对整条」，扩展中断时可点「补全并核对」从已保存的译文继续。审校同时修正有关联的栏目。主译或义项发生变化时，清除已经失效的引文和解释。无法消歧时会标明有待确认。各栏目仍可点击「智能复核」深入检查，缺少双语例句的句型和搭配可点「补全例句」。同一内容和语境已完成的整条或单栏核对可以直接复用。通过校验的更改写入本地缓存并同步到已收录的词条，词本写入失败时会在下次读取缓存时重试。

查词、划词和剪贴板翻译结果自动保存到本机用户数据目录的 `AiTranslate/dictionary-cache.sqlite3`。缓存区分原文、语境、目标语、说明语言、术语参考与查询用途。速译可复用同一输入的完整词条，打开词典详情时加载学习版本。输入补全只索引适合单独查询的短词和表达，已保存的词汇和句段都可通过完整原文离线重开。

截图每次都将本地 OCR 识别的文字发给所选 AI 引擎翻译。截图和译文仅用于本次操作，点击「保存译文图片」或「加入生词本」可以主动留存。升级时会清理旧版自动保存的截图翻译缓存。

生词本独立保存在同目录的 `vocabulary-store.sqlite3`。词本、卡片和复习日志分别存储，每次评分在同一个事务中更新卡片并追加日志。升级前请退出旧版应用。首次启动新版时会读取旧 `vocabulary-store.json` 并迁移，原文件保留作备份。备份新版数据时请先退出应用，再复制两个 SQLite 文件。

语法精解的讲解保存在同目录的 `grammar-library.sqlite3`，每个语法点只存一份，以规范名称为键，另有别名表记录学生用过的各种说法。提问时先在本地按名称和别名精确查找，命中即返回，不调用 AI；未命中时用一次很小的识别请求把问题归一成规范名称（并把本地库里已有的名称一并提供，让 AI 沿用），归一后再查一次库，仍未命中才撰写讲解。讲解分「核心」和「进阶」两次生成，进阶部分失败时核心讲解仍会保存，之后可点「补全进阶内容」。句子分析不入库，只把句中语法点按库内名称标出，点开某个语法点才讲解或从库里读取。翻译结果的「关键语法」同样沿用库内名称，并标出「已收录」。库的内容可用讲解页的「重新用 AI 讲解」刷新。

词典中的「重新用 AI 翻译」成功后更新对应记录，失败或取消会保留之前的成功结果。「补全例句」和「补查常用义与短语」会把新增内容写回对应词条。截图工具栏的「重新识别并翻译」会重新执行 OCR 并请求 AI。

翻译服务可读取当前原文命中的词本术语、短语和习语，最多八条，只将这些词的原文与译法随同本次请求发送给所选模型。界面列出实际提供的术语参考。

构建时会准备并校验固定版本的 Kokoro ONNX 权重。离线构建可将 v0.2.0 的 `kokoro.onnx` 放在仓库根目录，或指定 `-p:KokoroModelPath=<绝对路径>`。发布时请分发整个 publish 目录，其中包含 `KokoroModels/`、`voices/`、`espeak/` 和 `OcrModels/`。语音和 OCR 在本地运行，首次使用无需下载权重。首次 AI 翻译使用配置的供应商或本地 Ollama。

## 默认快捷键

| 快捷键 | 功能说明 |
| :--- | :--- |
| `Alt + D` | 呼出 / 激活主词典工作台 |
| `Alt + Q` | 全局划词即时翻译弹窗 |
| `Alt + S` | 启动 XCapture 截图原位翻译 |
| `Alt + W` | 读取并翻译当前剪贴板文本 |

