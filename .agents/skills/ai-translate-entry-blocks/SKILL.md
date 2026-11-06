---
name: ai-translate-entry-blocks
description: 新增或修改词典结构化栏目、AI 输出字段、解析与生词本数据映射时使用。已有字段仅改样式或交互时使用桌面 UI skill。
---

# 修改词典栏目契约

1. 在 `src/AiTranslate.Core/EntryBlocks.cs` 的 `EntryBlockCatalog` 确定适用条目类型、顺序、标题、Core / Extended 层级及 Insight 标记。解析与渲染共用这个目录。基础栏目保留该类型必备结构，扩展栏目只有在内容有价值时输出，缺省即隐藏。
2. 按字段实际去向更新领域模型、`AiDictionaryService` 的提示词及 `ReadEntry`（含分文件解析逻辑）。同时考虑流式预览和完整结果。旧缓存、生词卡或未完成的 JSON 可能缺少新字段，读取时采用可兼容的空值或空集合。
3. 说明字段可由 `InlineMarkup` 解析强调、原文词形和外文词标记。译文、释义、例句与引文锚点保持纯文本。涉及引文的解析应核对原文或译文中的实际片段。
4. 修改需收录的字段时，核对写入 `VocabularyEntry` 和 `VocabularyEntry.ToDictionaryResult()` 的往返映射。查词与生词本详情共用 `DictionaryResultView`。涉及 CSV 或 Anki TSV 的说明字段用 `InlineMarkup.Strip` 清除行内标记。
5. 选用 `tests/AiTranslate.Tests` 已有的结构化栏目、缓存、阅读或导出测试。用一条有字段和一条缺字段的词条验证适用类型与阅读顺序。涉及存储时验证收录再打开，涉及预览时验证字段逐步到达。
6. `grammarHighlights` 的 `topic` 是与「语法精解」共用的规范名称，改动它的生成或读取时同时遵循 [语法精解契约 skill](../ai-translate-grammar-studio/SKILL.md)。
