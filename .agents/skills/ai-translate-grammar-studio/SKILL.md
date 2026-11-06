---
name: ai-translate-grammar-studio
description: 新增或修改「语法精解」的讲解栏目、AI 提示词与解析、本地语法库、句子语法解析，以及翻译结果「关键语法」与语法库的关联时使用。
---

# 语法精解契约

1. 领域模型和栏目目录在 `src/AiTranslate.Core/GrammarModels.cs`：`GrammarTopic` 是一个语法点的完整讲解，`GrammarBlockCatalog` 决定栏目的顺序、标题、Core / Extended 层级和洞察标记，解析、流式预览和阅读视图共用它。新增栏目时同步改：模型字段、`GrammarJson`（`src/AiTranslate.Ai/GrammarJson.cs`）的读取、`GrammarPrompts` 的提示词，以及 `GrammarTopicView.RenderBlock` 与 `GrammarBlocks` 的渲染。旧库记录可能缺字段，读取时用空集合。
2. 讲解与具体句子无关，才能被本地库复用：不要把用户的句子、上下文写进 `GrammarTopic`。规范名称是库的键，`GrammarText.Normalize` 统一写法差异（去“用法”“讲解”后缀、`时态`→`时`、标点和空白）。别名只收录规范化后至少 3 个字符的说法，且不能抢占其他语法点的名称。
3. `AiGrammarService` 的顺序从省到费：本地精确命中（零请求）→ 小的识别请求，把库内候选和常用名称交给模型沿用，归一后再查库 → 才生成讲解。生成分核心和进阶两次，进阶失败时保存核心并标 `Extended=false`，由「补全进阶内容」继续。句子分析不入库，句中语法点的名称先按库内名称归一。所有发给模型的 JSON 保持 CJK 不转义，减少令牌。
4. 翻译结果的 `GrammarHighlight.Topic` 是规范名称，在主翻译请求中随 `known_grammar_topics`（库内常用名称）生成，不另加一次请求。旧结果没有 `Topic` 时界面用 `Label` 作为查询名。`GrammarLinks`（Desktop）由应用外壳设置，未设置时语法行只是文字，划词浮窗和测试不触发导航。
5. 界面规则见桌面 UI skill。语法栏目复用 `RichBlocks`、`StudioUi`、`UsageContrastView`、`DictionaryInsightBlocks.Mistakes`；分类颜色经 `HueMap.GrammarCategory`；公式中的角色用中性胶囊，外语词形用 Violet 胶囊；练习答题状态只在文章完成后可交互。
6. 测试用 `tests/AiTranslate.Tests` 的 `GrammarStudioTests`（库、服务、翻译关联）和 `GrammarUiTests`。验证：同一语法点的不同问法只第一次生成；未配置引擎时库仍可作答；进阶失败后可补全；流式预览先出摘要、再出用法。
