# AGENTS.md — AI Translate (有道级 AI 桌面词典与截图翻译客户端)

## 1. 项目定位与阶段目标
本项目是一个采用 **Avalonia UI (.NET 10)** 构建的现代化 AI 桌面词典与翻译工具（对标有道桌面词典 + Bob + 欧路词典 / Anki 级生词本）：
1. **Avalonia 技术栈**：采用 `.NET 10` + `Avalonia 12` 跨平台桌面技术栈，高 DPI 自适应、现代深浅色/专业深色视觉风格。
2. **全量 AI 驱动翻译**：所有词典查询、长句精翻、语境解析、语法拆解、双语例句、近反义词与记忆助记（词根词缀/联想）以及截图原位重绘翻译均由 AI 驱动。
3. **三大输入捕获通道**：
   - **剪贴板监听翻译（Clipboard Monitor）**：自动监听或快捷键读取系统剪贴板文本进行即时翻译/查词。
   - **全局划词翻译（Selection / Pop-up Translate）**：全局监听鼠标划词或快捷键（模拟 `Ctrl+C` + UIAutomation / Win32 剪贴板快照恢复），弹出灵动取词气泡或划词悬浮窗，支持一键加入生词本。
   - **截图原位翻译（Screenshot In-Place Translation）**：完整复刻 `D:\github\x-capture` 的截图翻译管线（多显示器物理像素捕获 + RapidOCR 离线 ONNX 文字检测与识别 + 表格线栅格检测 + 段落合并与重排 + AI 区域翻译与独立语义复核 + 原位擦除背景采样与译文排版重绘 + 原图/译文切换、复制译文、保存/复制译文图片，并增强“一键摘录生词/句子到单词本”）。
4. **核心增强 —— 同类最强生词本（Vocabulary Notebook & Spaced Repetition System）**：
   - 对标并超越主流词典（欧路词典、有道词典、Anki、墨墨背单词、Readwise）的生词本体系。
   - 支持**词汇（Word）、短语（Phrase）、长句（Sentence）、专业术语（Term）**全类型收录。
   - **上下文溯源（Context Preservation）**：自动保存查词/划词/截图时的原句上下文、来源应用/窗口标题、截图局部原图或来源标签。
   - **AI 深度赋能卡片（AI Enrichment）**：音标、多词性精准释义、当前语境义、词形变化（过去式/复数等）、词根词缀拆解、AI 谐音/故事助记法、高频搭配、正宗双语例句。
   - **FSRS / SM-2 科学间隔重复算法（Spaced Repetition）**：内置记忆状态机（New 新词 / Learning 学习中 / Review 复习中 / Mastered 已掌握），支持 `Again (重来)` / `Hard (困难)` / `Good (良好)` / `Easy (简单)` 四级记忆反馈，动态计算下次复习时间（NextReviewAt）、记忆稳定度（Stability）、难度（Difficulty）与熟练度星级（Mastery Level 0–5）。
   - **多维复习模式**：闪卡翻转模式（Flashcard）、语境完形填空（Cloze Deletion）、看义拼写/默写模式（Spelling Quiz）、错题强化。
   - **分组管理与导入导出**：支持自定义词书/分组（Notebooks）、多标签（Tags）、星标收藏、多维筛选排序、艾宾浩斯/FSRS 学习统计看板，以及 JSON / CSV / Anki TSV / Markdown 导入导出。

---

## 2. 架构与模块划分规范（高内聚、低耦合）
项目严格遵循分层与模块化架构，位于 `src/` 与 `tests/` 下：
- `AiTranslate.Core`：核心领域模型、接口契约、几何原语（`RectD`/`PointD`）、OCR 模型契约、翻译与词典领域模型、生词本领域实体与 FSRS/SM-2 间隔重复调度算法（零 UI、零平台耦合）。
- `AiTranslate.Ai`：AI 传输层（OpenAI 兼容流式 SSE 协议、DeepSeek / OpenAI / Ollama / 自定义端点）、截图区域翻译服务（含独立复核防漏译机制）、AI 深度词典解析与生词卡片增强引擎（结构化 JSON 解析）。
- `AiTranslate.Ocr`：离线 ONNX 文字检测与识别引擎（基于 RapidOcrNet + SkiaSharp，分块检测、行碎片拼接、阅读顺序恢复）。
- `AiTranslate.Speech`：基于 `KokoroSharp` (Kokoro-82M ONNX) 的神经网络语音与自然拼读引擎（支持美音/英音多声线、IPA 音标直驱、音节自然拼读拆解领读、0.7x 慢读与生词磨耳朵连读，及 Windows/macOS/Linux 跨平台语音兜底）。
- `AiTranslate.Vocabulary`：生词本持久化仓储、多维查询、分组与标签管理、FSRS 复习计划引擎、统计分析服务、多格式导入导出器（Anki/CSV/JSON/Markdown）。
- `AiTranslate.Platform.Windows` / `AiTranslate.Platform.MacOS` / `AiTranslate.Platform.Linux`：三大桌面操作系统原生平台能力实现（屏幕捕获、全局热键、剪贴板监听、全局鼠标划词捕获与剪贴板恢复）。
- `AiTranslate.Desktop`：Avalonia 桌面客户端主程序（按 `Views/`、`Controls/`、`Screenshot/`、`Tray/` 模块化拆分，单个文件严禁臃肿过长）。
- `tests/AiTranslate.Tests`：核心算法、FSRS 间隔重复调度、翻译版面合并、AI 协议解析、生词本仓储与导入导出的自动化单元测试。

---

## 3. 图标规范（强制要求）
- **所有图标必须且只能取自 Material Design Icons (MDI) 官方图标库：https://pictogrammers.com/library/mdi/**
- 在项目中统一通过 `MdiIcons` / `MdiIcon` 矢量图标控件（基于 MDI 官方 24x24 ViewBox SVG Path 数据）渲染，禁止混用其他图标库或硬编码字符表情代替 UI 图标。
- 新增或修改任何按钮、导航栏、工具栏、状态指示图标时，必须从 Pictogrammers MDI 库中选取对应语义的标准 `mdi-*` 图标名称与 SVG Path。
