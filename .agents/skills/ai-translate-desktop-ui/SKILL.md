---
name: ai-translate-desktop-ui
description: 修改或审查 AI Translate 的 Avalonia 桌面 UI 时使用，涵盖工作台、生词本、设置、划词浮窗与截图工具栏的布局、样式、交互和视觉验收。
---

# 桌面 UI 更新

1. 从用户实际操作的入口找控件。查词页和生词本详情都通过 `DictionaryResultView` 渲染，核心栏目由 `DictionaryDetails` 提供。同一问题出现于多处时，先检查共用组件、模板和状态样式。
2. 让核心信息与主操作先出现。比较用表格，并列要点用列表，有顺序才编号。用缩进关联主从内容，对称布局使用相同字段顺序。组内紧凑、组间留白，需要区分独立条目时再加边界。折叠入口写明会展开什么，返回时恢复原来的阅读位置。
3. 复用 `src/AiTranslate.Desktop/` 中的设计入口。颜色、字号、间距、圆角和阴影取自 `AppTheme.cs`。文字通过 `TextStyles.Create / Selectable / Apply` 选择 `TextRole`。分类颜色经 `Controls/HueMap.cs` 映射。图标只用 [Font Awesome Free](https://fontawesome.com/icons) 的实心图标（`solid`），个别需要轻量外观的用 `regular`，默认由 `FontAwesomeIcon` 渲染。新图标在 `tools/generate-fontawesome-icons.mjs` 的 `ICONS` 列表登记（`name` 为 solid，`regular/name` 为 regular）并运行脚本，重新生成 `FontAwesomeIconPaths.cs`，不要手改该文件。界面里直接用登记的 Font Awesome 名称（如 `arrows-rotate`、`regular/copy`）引用，Pro 专属图标不可用，不混用其他图标库，也不自绘路径。此约定只管界面内的功能图标，应用图标（`Assets/Brand` 品牌母版）是独立设计，不随之改动。禁止以字符或 Emoji 充当图标。
4. 改主题、按钮、表单、卡片或导航时读 [控件与状态](references/controls.md)。改词典结果或生词本详情的阅读版式时读 [词条排版](references/reading.md)。只读本次涉及的部分。
5. 全部桌面视图统一按语义区分相邻文字。主内容与含义、译文、说明或元信息相邻时，用明显不同的字号、字重或文字颜色，并保留颜色以外的识别线索。长文换行后也应能一眼认出各字段；含义容易混淆时使用独立短标签。

## 验收

- 在仓库根目录运行 `dotnet build src/AiTranslate.Desktop/AiTranslate.Desktop.csproj`，启动本次构建目视检查。旧实例锁定输出时可用独立的 `-p:OutDir=<临时绝对目录>/ -p:UseAppHost=false` 构建，再用 `dotnet <输出目录>/AiTranslate.Desktop.dll` 启动。
- 用真实内容在常用宽度和最小窗口走一遍改动的操作。检查对比度、换行、溢出，以及相关的悬停、按下、选中、禁用和键盘状态。改描边或裁剪时也检查高 DPI。交互变更运行对应 UI 回归测试，交付时说明实测范围。
