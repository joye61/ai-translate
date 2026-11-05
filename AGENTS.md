# 项目更新约束

- 保持现有分层。领域模型和契约放在 Core，保持与 UI、平台无关。AI、OCR、语音、持久化和平台调用归各自项目，Desktop 负责展示与编排。新增视图按职责拆入 Views、Controls 或 Screenshot。
- 改划词捕获时验证剪贴板恢复。改截图翻译时保留多屏物理像素定位、表格与段落版面及独立译后复核。改生词本持久化时保留已有条目的来源上下文和复习进度。
- 修改或审查桌面界面、布局、样式及交互前，必须读取并遵循 [桌面 UI skill](.agents/skills/ai-translate-desktop-ui/SKILL.md)。
- 新增或修改词典栏目、AI 输出字段及其解析、收录或导出映射前，必须读取并遵循 [词典栏目契约 skill](.agents/skills/ai-translate-entry-blocks/SKILL.md)。同时改动界面时，两项都适用。
- 仅加载当前任务需要的 skill 和参考页。改动完成后构建受影响项目，运行相关测试。完整测试命令为 `dotnet test tests/AiTranslate.Tests/AiTranslate.Tests.csproj`。
