# AGENTS.md — AI Translate (有道级 AI 桌面词典与截图翻译客户端)

## 1. 项目定位与阶段目标
本项目是一个采用 **Avalonia UI (.NET 10)** 构建的现代化 AI 桌面词典与翻译工具（对标有道桌面词典 + Bob + 欧路词典 / Anki 级生词本）：
1. **Avalonia 技术栈**：采用 `.NET 10` + `Avalonia 12` 跨平台桌面技术栈，高 DPI 自适应、清爽浅色「暖白 + 珊瑚橙」视觉风格（详见第 4 节 UI 设计约定）。
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

---

## 4. UI 设计约定（强制要求）
视觉风格为 **「暖白纸感 + 珊瑚橙」清爽浅色主题**：干净、克制、留白充足，中性色为底，单一主色点缀。所有令牌集中定义在 `src/AiTranslate.Desktop/AppTheme.cs`，全局按钮状态与 Fluent 控件覆盖集中在 `App.axaml`。

### 4.1 色彩（只用 `AppTheme` 令牌，禁止在视图里硬编码颜色）
- **主题变体**：`RequestedThemeVariant="Light"`，当前不提供深色主题，不得引入深色表面（截图遮罩等覆盖层除外）。
- **表面层级**：`WindowBg` `#FAF8F5` → `SidebarBg` `#F3EFEA`（侧栏）→ `CardBg` `#FFFFFF`（卡片）→ `InputBg` `#F8F5F1`（输入框/信息块内嵌底）→ `CardElevatedBg` `#F4F0EB`（标签/胶囊/键帽）。
- **唯一主色**：珊瑚橙 `Accent` `#FF7A59`（悬停 `AccentHover`、按下 `AccentPressed`、淡底 `AccentSoft`）。主色只用于：品牌标识、当前选中导航、主按钮、开关/焦点环、核心译义、未读数徽章。
- **主色上的文字/图标**：浅底上的主色文字与图标用 `AccentBright`（`#D6482A`，保证可读性），不要直接用 `Accent` 着色小字。
- **AI 洞察标记**：词源/语法/语境义/近义辨析等 AI 生成内容统一用 `Insight` / `InsightSoft`（= `Hues.Violet`），与品牌珊瑚橙区分「操作」与「AI 解析」。
- **语义色（有含义）**：`Emerald` 表示「已收录/已掌握/Good」；`Amber` 表示「注意/待办/助记/Hard」；`Danger` 仅表示错误、破坏性操作与「Again」。
- **点缀色（`AppTheme.Hues`）**：低饱和的 `Sky`（蓝）/ `Jade`（青绿）/ `Violet`（紫）/ `Honey`（琥珀）/ `Rose`（玫红）/ `Sage`（绿）/ `Coral`，每个 `Hue` 提供 `Ink`（文字/图标，AA 可读）、`Soft`（12% 淡底）、`Solid`（圆点/色条）。点缀色只用于**给类别编码**，且必须经 `Controls/HueMap.cs` 统一映射，保证同一概念在所有页面同色：
  - 记忆阶段：新词 Sky · 学习中 Honey · 复习中 Violet · 已掌握 Sage（`StudioUi.CreateDotLabel`）。
  - 条目类型：单词 Sky · 短语 Jade · 习语 Rose · 术语 Violet · 句子/段落 Honey（`StudioUi.CreateChip(text, hue)`）。
  - 词性：n. Sky · v. Jade · adj. Violet · adv. Honey · 短语/习语 Rose，其余虚词保持中性灰。
  - 复习评分：Again Danger · Hard Honey · Good Sage · Easy Sky。
  - 设置卡片标题图标、快捷捕获图标、词本图标（`HueMap.ForKey` 按 id 稳定取色）。词典结果的分区标题直接与正文左边缘对齐，不加前置图标。
- **禁止**：点缀色用于大面积表面、正文文字或作为第二品牌色；在视图中直接写 `Hues.*` 而绕过 `HueMap` 的类别映射（设置/侧栏等固定标题图标除外）；发光/高饱和渐变。音频胶囊、键帽、普通标签与次要按钮保持中性暖灰（`CardElevatedBg` + `TextSecondary`）。
- **文字**：`TextPrimary` `#26211C`（标题/关键信息）、`TextSecondary` `#5E564D`（正文/次要）、`TextMuted` `#8F867C`（说明/辅助）。

### 4.2 排版、间距、圆角
- 字号使用 `AppTheme.Type`（Caption 11.5 / Small 12.5 / Body 13.5 / BodyLarge 15 / Title 18 / Headline 22 / Display 30），正文行高约 1.5–1.6；词条标题 30 Bold，卡片标题 15 SemiBold，辅助信息 11–12 弱灰。
- 间距使用 `AppTheme.Space`（4px 基准栅格，8 的倍数优先）；页面外边距 24，卡片间距 16，同组元素间距 8–12。
- 圆角使用 `AppTheme.Radius`：卡片 `L`=12，按钮/输入框 `M`=10，小控件 `S`=8，胶囊 `Pill`；组件圆角必须落在 8–12px。

### 4.2.1 文字层级（强制要求）
翻译产品以文字为主，文字的「主次 / 对象类型」必须靠统一的语义角色区分，禁止在视图里手写 `FontSize` / `FontWeight` / `Foreground` 组合。新文字一律经 `TextStyles.Create / Selectable / Apply(…, TextRole.X)`（`src/AiTranslate.Desktop/TextStyles.cs`）创建：

| 角色 `TextRole` | 规格 | 用途 |
|---|---|---|
| `Display` | 30 Bold · 主文字 · 行高 38 | 词条标题 |
| `PageTitle` | 18 SemiBold · 主文字 | 页面标题、句段「译文」标题、浮窗词头 |
| `SectionTitle` | 15 SemiBold · 主文字 | 章节 / 卡片标题 |
| `Translation` | 18 SemiBold · `AccentBright` | 单词 / 短语的核心译义（全界面唯一的大号主色文字） |
| `PassageTranslation` | 15 Regular · 主文字 · 行高 26 | 句子 / 段落译文与原文 |
| `Definition` | 15 Medium · 主文字 | 义项中文释义 |
| `Term` | 14 SemiBold · 主文字 | 原文表达：搭配、词族、变形、对照形式 |
| `Body` | 13.5 Regular · 主文字 · 行高 22 | 正文、释义说明、搭配中文义 |
| `Secondary` | 13.5 Regular · 次文字 | 场景、用法、提示等解释性文字 |
| `Gloss` | 13 Italic · 次文字 | 英文释义（只有英文释义用斜体） |
| `ExampleSource` / `ExampleTarget` | 13.5 主文字 / 13 次文字 | 双语例句原句 / 译句 |
| `Label` | 12 SemiBold · 弱文字 | 键值行的字段名（用法 / 场景 / 近义词 / 使用提示） |
| `FieldLabel` | 12.5 Medium · 次文字 | 表单输入框上方的字段名 |
| `PartOfSpeech` | 12 SemiBold 等宽 · 次文字 | 词性缩写（放在 `StudioUi.CreateTag(text, role, HueMap.PartOfSpeech(...))` 词性色胶囊内） |
| `Numeral` | 12 SemiBold 等宽 · 弱文字 | 义项编号 |
| `Phonetic` | 12.5 等宽 · 次文字 | 音标、发音胶囊 |
| `Meta` | 11.5 Regular · 弱文字 | 计数、时间、来源、提示 |

- **行内强调只改颜色不改字重**：例句中的目标词、原句中的查询词用 `AccentBright` 着色（+ 可点击时下划线）。Avalonia 中粗体 Run 之后的中文 Run 会被渲染成粗体，因此不同字重必须拆成独立 TextBlock。
- **键值行**：标签 + 内容统一用 `StudioUi.CreateKeyValue`，标签列为 `Auto` 并通过 `Grid.IsSharedSizeScope` 在同组内对齐，禁止用固定宽度（如 84px）留出大片空白。

### 4.2.2 排版密度（反「宁可换行」）
- 横向优先：词头与音标同行、主操作按钮放在标题行右上、提示文字与次级操作共用一行；短表达与其释义同行（`WrapPanel`，放不下再自然换行）。
- 不允许单个元素因宽度不足孤零零换到新行：并列的统计数据放进同一张卡片等分列；成组的短行（快捷键等）用两列网格。
- 内容卡片在页面内顶部对齐，不要垂直居中留出大片空白。

### 4.3 组件规范
- **卡片**：统一经 `StudioUi.CreateBentoCard` / `CreateHeroCard` 创建（白底 + `BorderSubtle` 发丝边 + `AppTheme.CardShadow` 软阴影）。信息块 `CreateInfoTile` 用 `InputBg` 底色分组，不加边框；用留白和分组代替分割线，避免卡片套卡片与多余边框。
- **浮层**（划词气泡、截图工具栏）：半透明白底 + `AppTheme.PopupShadow`。
- **富文本（词典结果）**：
  - 核心译义用 `TextRole.Translation`（`AccentBright` SemiBold，全界面唯一的大号主色文字），不再额外加竖条等强调。
  - **序号统一**：所有有序序列（义项、搭配、例句、要点、对照表行号）一律用 `StudioUi.CreateOrdinal`（两位等宽数字 + 暖灰圆角块，高 22）或表格内的两位 `Numeral` 文本；序号列用 `SharedSizeGroup="Ordinal"` 预留宽度，同一栏目内单义项（无序号）的词性组也必须与有序号行的正文左缘对齐。
  - **对齐**：表格与列表单元一律顶对齐（标签/徽章加 1px 上边距对齐首行文字）；键值行的补充说明也要放进同一标签列（如语气栏的「说明」），不得出现游离在标签列左侧的孤立文字。
  - **边界**：可独立成立的「对照/判断」条目（常见错误、译法与依据）每条一个白底发丝边块，块内次级信息用 `InputBg`。
  - **窄宽度**：例句块宽度 < 520 时，跟读/自测操作换行到文字下方，禁止挤压正文列。
  - 可点击表达用 `DictionaryReadingText.LinkUnderline()`（1.2px、下移 3px 的浅暖灰细线；例句高亮词用 `AccentUnderline`），禁止默认黑色粗下划线。
  - 例句块用 `InputBg` 圆角底 + 前置引号图标；常见错误、译法与依据等判断类条目用白底 + `BorderSubtle` 发丝边；中性提示块用 `CardElevatedBg`。
  - 行内 `code` 用 `CodeBg` 底；强调（Strong）只改颜色为 `Insight`，不改字重（CJK 混排字重 bug）。
  - 结构化块按语义上色：构词（前缀 Sky / 词根 Violet / 后缀 Jade）、用法对照左右两卡（Sky vs Honey）、正确写法 Sage、错误写法 Danger 删除线、表格表头 `InputBg`、本词行 `AccentFaint`、语域/多译法/语法标签用 Tag。
- **按钮角色与状态**：Fluent 模板会覆盖本地 `Background`，所以每个按钮必须声明角色 class，由 `App.axaml` 提供悬停/按下态，禁止依赖默认悬停色：
  - `primary`：主按钮（`StudioUi.CreatePrimaryButton`，主色渐变，白字）。
  - `success`：用于成功反馈（`Emerald` 底）。生词本按钮始终显示下一步操作：“加入生词表”用 `primary`，“取消收录”用暖灰色 `ghost`，悬停时文案保持一致。
  - `ghost`：次要按钮、胶囊、标签、图标按钮（白/暖灰底，悬停 `HoverBg`，按下 `PressedBg`）。
  - `nav` + `active`：侧栏导航与快捷捕获行（激活态用 `AccentSoft` 底 + `AccentBright` 文字，悬停保持激活色）。
  - `tint`：主色淡底按钮的悬停态。
- **输入框 / 下拉框**：边框 `BorderStrong`，悬停略深，焦点环为主色；这些 Fluent 资源键已在 `App.axaml` 的 `ThemeDictionaries` 覆盖，新增控件无需再逐个设置边框色。
- **开关 / 选中态**：由 `FluentTheme.Palettes` 的 `Accent` 驱动为主色；关闭态的轨道/滑块在 `App.axaml` 覆盖为暖灰（不得出现 Fluent 默认的近黑描边）。
- **侧栏**：快捷捕获项是安静的列表行（点缀色淡底小图标块 + 名称 + 右侧键帽快捷键），行本身不使用高亮色块；未读数徽章用主色 `Accent` 圆点 + 白字。

### 4.4 布局
- 重要内容优先展示，遵循 8pt 栅格与统一对齐；工具栏使用 `WrapPanel` 在窗口最小宽度下换行，避免溢出与裁切。
- 新增视图模块须拆分到 `Views/` / `Controls/`，单文件不得臃肿；复用 `StudioUi` 的卡片、徽章、按钮，不要在视图内重复造样式。
- 查词与翻译采用居中单列，结果正文和底部输入框共用 760 DIP 宽度上限，窄窗口时随可用宽度收缩。词条先呈现主译义，句段先呈现译文，再依次展示原文和解析。结果章节使用 `DictionarySection` 的细顶线、标题和留白分组，例句用缩进与次级字色关联到对应义项。
- 生成中状态：输入框使用 `FlowingBorderEffect` 珊瑚色流光边框，结果区以行状骨架占位，随内容到达逐节展示。

### 4.5 质感与验证
- 轻微磨砂/柔和阴影，层次来自「底色差 + 软阴影」，不浮夸；窗口使用真实系统边框。
- 修改 UI 后至少执行 `dotnet build src/AiTranslate.Desktop/AiTranslate.Desktop.csproj` 并启动应用目视检查：文字对比度、悬停/按下/选中状态、最小窗口尺寸下的换行与溢出。
- 若本机已有运行中的实例锁定输出文件，可改用 `-p:OutDir=<临时目录> -p:UseAppHost=false` 构建验证。

### 4.6 AI 结构化输出与富文本呈现
- **栏目目录唯一来源**：词条/译文可展示的全部栏目定义在 `AiTranslate.Core/EntryBlocks.cs` 的 `EntryBlockCatalog`，按条目类型（单词 / 短语·习语 / 术语 / 句子 / 段落）给出适用栏目、阅读顺序、标题与层级。解析器（`AiDictionaryService.ReadEntry`）与渲染器（`DictionaryDetails`）都以它为准，新增栏目必须先登记到目录。
- **基础栏目（Core）与扩展栏目（Extended）**：基础栏目是该类型必备结构（单词的音标/词性释义/词形，句段的原文）；扩展栏目由 AI 按价值判断是否输出（多种译法、要点速览、逐句对照、句子结构、语气与语域、字面与引申、文化与典故、构词拆分、近义词辨析表、常见错误等），缺省即隐藏，禁止为凑栏目编造。分析类扩展栏目标题带 `sparkles` 洞察标记（`EntryBlockSpec.Insight`）。
- **结构即版式**：按数据形态选择呈现方式，复用 `Controls/RichBlocks.cs`（列表、表格、提示块、缩进树、刻度条）与 `Controls/DictionaryInsightBlocks.cs`：对比类用表格，层级类用缩进树，要点用编号列表，错误/改正用 ✗/✓ 对照，助记用 `Amber` 提示块，文化背景用中性提示块。
- **行内富文本**：AI 说明性文字可使用 `**强调**`、`` `原文词形` `` 与 `*外文词*`，由 `InlineMarkup`（Core）解析、`RichText`（Desktop）渲染；强调只改颜色（`Insight`）不改字重。译文、释义、例句与所有引文锚点字段保持纯文本；CSV / Anki 导出用 `InlineMarkup.Strip` 去除标记。
- **一致性**：生词本详情通过 `VocabularyEntry.ToDictionaryResult()` 复用 `DictionaryDetails` 渲染，与查词结果完全一致；不得在其他视图重新手写同类栏目。
