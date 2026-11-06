# 查词与生词本的阅读版式

- 查词正文与底部输入框共用居中单列，上限 760 DIP。单词和短语先呈现主译义，词头与音标同行。句段依次呈现译文、原文、解析。生成中输入框用 `FlowingBorderEffect`，正文用行状骨架并随字段到达逐节展示。
- 栏目使用 `DictionarySection` 的细顶线和留白，标题与正文左缘对齐，取消标题前置图标。分析栏目按 `EntryBlockSpec.Insight` 在标题后显示洞察标记。生词本详情通过 `VocabularyEntry.ToDictionaryResult()` 使用同一个渲染器。
- 文字规格以 `TextStyles.cs` 为准。词头用 `Display`，词条主译义用 `Translation`（唯一大号主色文字，无额外竖条），句段原文和译文用 `PassageTranslation`。义项用 `Definition`，原文表达用 `Term`，仅英文释义用斜体 `Gloss`。例句用 `ExampleSource / ExampleTarget`，词性标签用 `PartOfSpeech`，音标用 `Phonetic`。
- 同类内容按列比较，表格和列表单元顶对齐，标签与首行文字对齐。序号统一用 `StudioUi.CreateOrdinal`（两位等宽数字、暖灰底、高 22）或表内两位 `Numeral`。序号列共享 `Ordinal` 宽度，无编号的义项也要和同栏正文对齐。键值行用 `StudioUi.CreateKeyValue` 和共享尺寸域，标签列为 Auto，补充说明对齐内容列。
- 核心释义始终可见。例句缩进到对应义项下，使用 `InputBg` 圆角块和引号图标。窄栏中例句与跟读按钮无法同行时，按钮移到文字下方。短表达与释义尽量同行，展开、收起只影响当前条目。
- 译法辨析以候选译文为主，含义另起一行，使用「含义」标签和 `Annotation` 小号说明文字；选用条件放在独立列。紧随正文的附注沿用 `Annotation`。双语例句和逐句对照按源语、目标语分别使用 `ExampleSource / ExampleTarget`，译向改变时仍按角色选样式。
- 用 `RichBlocks` 和 `DictionaryInsightBlocks` 表达结构。对比用表格、层级用缩进树、要点用编号列表、语域用标签。表头用 `InputBg`，本词行用 `AccentFaint`。复用 `RichBlocks.TableFrame`，行底色沿边框内缘裁剪，检查首末行的四个圆角。
- 语法精解的文章版式由 `GrammarTopicView` 和 `GrammarBlocks` 负责，栏目顺序与标题取自 `GrammarBlockCatalog`。头部依次是分类、学习阶段、来源标签，然后是标题、原语术语与别名，再接 Insight 提示块的“一句话讲清”。公式用“角色（中性）+ 词形（Violet）”胶囊相加，示例紧随其下。用法逐条编号并用细线分隔，标志词用 Honey 胶囊，变化规则用表格，易混对比复用 Sky 与 Honey 并列块，练习卡可自测。本地库已有的语法点旁显示 Sage 对勾“已收录”。句子分析把句中语法片段用 Insight 淡底标出并加上角标序号，序号对应下方卡片。
- 常见错误、译法与依据每条一个白底细边块，次级解释用 `InputBg`。错误用 Danger 删除线、正确形式用 Sage，搭配对应的 Font Awesome 标记。用法对照左右用 Sky 与 Honey，前缀、词根、后缀用 Sky、Violet、Jade。助记用 Amber 提示块，文化背景用中性提示块。
- 说明性富文本复用 `RichText`。行内代码用 `CodeBg`，强调仅改为 `Insight` 色。CJK 混排需要不同字重时拆成独立 TextBlock。例句目标词和原句查询词用 `AccentBright`，可点击表达用 `DictionaryReadingText.LinkUnderline()` 的浅暖灰细线，例句高亮词用 `AccentUnderline`。
