# 控件与状态

## 颜色与层级

- 使用 Light 暖白纸感主题，截图遮罩是深色覆盖层的例外。窗口、侧栏、卡片、嵌入信息、标签依次使用 `WindowBg`、`SidebarBg`、`CardBg`、`InputBg`、`CardElevatedBg`。标题、正文说明、辅助信息从 `TextPrimary / TextSecondary / TextMuted` 按角色取色。
- 珊瑚橙用于品牌、当前导航、主操作、焦点、核心译义和计数徽章。浅底小字和图标用 `AccentBright`。AI 解析用 `Insight / InsightSoft`，成功或掌握用 `Emerald`，提醒与助记用 `Amber`，错误、破坏性操作和 Again 用 `Danger`。
- 阶段、条目类型、词性、评分分别用 `HueMap.Stage / ItemType / PartOfSpeech / Rating`。生词本分组是中性文件夹行，不再按 ID 取色。类别标签用 Ink 文字与 Soft 淡底，避免大面积点缀色、发光和高饱和渐变。音频胶囊、键帽和次要按钮使用中性暖灰。
- 以 4 DIP 为基准，优先 8 的倍数。常规页面外边距 24、卡间距 16、组内距 8–12。卡片用 `Radius.L`，按钮和输入框用 `M`，小控件用 `S`，胶囊用 `Pill`。

## 布局与交互

- 卡片复用 `StudioUi.CreateBentoCard / CreateHeroCard` 和 `CardShadow`。信息块用 `CreateInfoTile` 的嵌入底色。划词浮窗和截图工具栏用半透明白底与 `PopupShadow`，主窗口保留系统窗口边框行为。
- 页面内容顶部对齐。主按钮在标题行右侧，次操作与说明共行。工具栏可换行，并列统计用同一卡片等分列，快捷键等短行用双列。窄窗下标签、按钮和内容仍须能对应。
- 每个按钮声明角色 class，悬停和按下态集中在 `App.axaml`。主操作用 `primary`，次操作、图标按钮和胶囊用 `ghost`，淡主色操作用 `tint`，导航用 `nav` 配合 `active`，成功反馈用 `success`。生词本按钮显示下一步操作：“加入生词表”用 `primary`，“取消收录”用暖灰 `ghost`，悬停时保持文案稳定。
- 输入框和下拉框统一使用 Fluent 资源中的 `BorderStrong` 和主色焦点环，关闭的开关用暖灰色。快捷捕获用安静的列表行，左侧类别图标、右侧快捷键帽，计数徽章用珊瑚底白字。
- 可用操作悬停显示手形，禁用态不提示可点击。输入框和普通可选文字保留文本光标。左键点选区外清除旧选区，右键菜单执行复制或查词前保留选区。
- 列表行用 `entry-row`：整行悬停浮起 `InputBg`，行内次要操作（`row-action`）收在行尾右对齐并始终显示，删除类加 `destructive` 悬停转红（不要复用实心的 `danger`）。多词性词条每个词性占一行，词性标签列对齐。生词本的“隐藏释义”是按本保存的自测开关，开启后每行多一个眼睛按钮临时显示该条释义。下拉列表项、菜单项统一圆角与暖灰悬停，选中为淡珊瑚底，文字垂直居中。
- 对话框按钮右对齐，取消在前、主操作在右；删除确认用 `StudioUi.CreateDangerButton` 的实心红按钮并默认聚焦取消，Esc 关闭。空状态与完成状态用 `StudioUi.CreateEmptyState`，复习评分按钮用 `answer` 加评级 class，悬停显示对应评级色。
