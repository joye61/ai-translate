using System.Text;
using AiTranslate.Core;
using AiTranslate.Desktop.Controls;
using AiTranslate.Speech;
using AiTranslate.Vocabulary;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace AiTranslate.Desktop.Views;

/// <summary>
/// Best-in-class Vocabulary Notebook View (超级生词本与记忆曲线管理中心).
/// Combines multi-notebook decks, multi-dimensional FSRS filtering, context preservation,
/// KokoroSharp Phonics & Memory Loop audio, interactive memory curve self-testing, and Anki/Markdown/CSV/JSON export.
/// </summary>
public sealed class VocabularyVaultView : UserControl
{
    private readonly IVocabularyService _vocabularyService;
    private readonly ISpeechSynthesisService _speechService;
    private readonly AppSettingsStore _settingsStore;
    private readonly Action<string, string?, TranslationTriggerSource, string> _onLookupInStudio;
    private readonly Action _onSwitchToReview;
    private readonly Action<string> _setStatus;

    private readonly StackPanel _kpiStrip;
    private readonly TextBox _searchBox;
    private readonly ComboBox _notebookCombo;
    private readonly ComboBox _stageCombo;
    private readonly ComboBox _typeCombo;
    private readonly ComboBox _sortCombo;
    private readonly CheckBox _starredOnlyCheck;
    private readonly CheckBox _dueOnlyCheck;
    private readonly ListBox _entryListBox;
    private readonly StackPanel _inspectorPanel;

    private VocabularyEntry? _selectedEntry;
    private bool _suppressFilterEvents;

    public VocabularyVaultView(
        IVocabularyService vocabularyService,
        ISpeechSynthesisService speechService,
        AppSettingsStore settingsStore,
        Action<string, string?, TranslationTriggerSource, string> onLookupInStudio,
        Action onSwitchToReview,
        Action<string> setStatus)
    {
        _vocabularyService = vocabularyService;
        _speechService = speechService;
        _settingsStore = settingsStore;
        _onLookupInStudio = onLookupInStudio;
        _onSwitchToReview = onSwitchToReview;
        _setStatus = setStatus;

        _kpiStrip = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            Margin = new Thickness(24, 18, 24, 10)
        };

        _searchBox = new TextBox
        {
            PlaceholderText = "搜索词条、释义、词根、助记、原句上下文或 #标签…",
            Width = 250,
            Background = AppTheme.InputBg,
            Foreground = AppTheme.TextPrimary,
            CornerRadius = new CornerRadius(9)
        };
        _searchBox.TextChanged += (_, _) => { if (!_suppressFilterEvents) ReloadEntries(); };

        _notebookCombo = new ComboBox { Width = 180, CornerRadius = new CornerRadius(9) };
        _notebookCombo.SelectionChanged += (_, _) => { if (!_suppressFilterEvents) ReloadEntries(); };

        _stageCombo = new ComboBox
        {
            Width = 135,
            CornerRadius = new CornerRadius(9),
            ItemsSource = new[] { "全部记忆阶段", "New · 新词", "Learning · 学习中", "Reviewing · 巩固中", "Mastered · 已掌握" },
            SelectedIndex = 0
        };
        _stageCombo.SelectionChanged += (_, _) => { if (!_suppressFilterEvents) ReloadEntries(); };

        _typeCombo = new ComboBox
        {
            Width = 120,
            CornerRadius = new CornerRadius(9),
            ItemsSource = new[] { "全部类型", "单词 Word", "短语 Phrase", "长句 Sentence", "习语 Idiom", "术语 Term" },
            SelectedIndex = 0
        };
        _typeCombo.SelectionChanged += (_, _) => { if (!_suppressFilterEvents) ReloadEntries(); };

        _sortCombo = new ComboBox
        {
            Width = 145,
            CornerRadius = new CornerRadius(9),
            ItemsSource = new[] { "最近收录优先", "待复习紧迫度", "按字母 A-Z", "查词频次最高", "记忆难度最高", "掌握度最低优先" },
            SelectedIndex = 0
        };
        _sortCombo.SelectionChanged += (_, _) => { if (!_suppressFilterEvents) ReloadEntries(); };

        _starredOnlyCheck = new CheckBox { Content = "星标重点", Foreground = AppTheme.TextSecondary, FontSize = 12.5 };
        _starredOnlyCheck.IsCheckedChanged += (_, _) => { if (!_suppressFilterEvents) ReloadEntries(); };

        _dueOnlyCheck = new CheckBox { Content = "仅待复习", Foreground = AppTheme.TextSecondary, FontSize = 12.5 };
        _dueOnlyCheck.IsCheckedChanged += (_, _) => { if (!_suppressFilterEvents) ReloadEntries(); };

        var filterBar = new WrapPanel
        {
            Margin = new Thickness(24, 2, 24, 12),
            Children =
            {
                new Border { Margin = new Thickness(0, 0, 8, 6), Child = _searchBox },
                new Border { Margin = new Thickness(0, 0, 8, 6), Child = _notebookCombo },
                new Border { Margin = new Thickness(0, 0, 8, 6), Child = _stageCombo },
                new Border { Margin = new Thickness(0, 0, 8, 6), Child = _typeCombo },
                new Border { Margin = new Thickness(0, 0, 10, 6), Child = _sortCombo },
                new Border { Margin = new Thickness(2, 0, 10, 6), Child = _starredOnlyCheck },
                new Border { Margin = new Thickness(0, 0, 12, 6), Child = _dueOnlyCheck },
                new Border { Margin = new Thickness(0, 0, 8, 6), Child = StudioUi.CreateSecondaryButton("plus", "新建词本", ShowCreateNotebookDialogAsync) },
                new Border { Margin = new Thickness(0, 0, 8, 6), Child = StudioUi.CreateSecondaryButton("export-variant", "导出 Anki/MD/CSV", ExportVocabularyAsync) },
                new Border { Margin = new Thickness(0, 0, 0, 6), Child = StudioUi.CreatePrimaryButton("brain", "启动沉浸复习", _onSwitchToReview) }
            }
        };

        _entryListBox = new ListBox
        {
            Background = AppTheme.CardBg,
            BorderBrush = AppTheme.BorderSubtle,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            ItemTemplate = new FuncDataTemplate<VocabularyEntry>((entry, _) => BuildEntryListItem(entry))
        };
        _entryListBox.SelectionChanged += (_, _) =>
        {
            if (_entryListBox.SelectedItem is VocabularyEntry selected)
            {
                _selectedEntry = selected;
                RenderInspector(selected);
            }
        };

        _inspectorPanel = new StackPanel { Spacing = 14 };
        var inspectorCard = StudioUi.CreateBentoCard(new ScrollViewer { Content = _inspectorPanel });

        var masterDetailGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("390,*"),
            ColumnSpacing = 16,
            Margin = new Thickness(24, 0, 24, 20),
            Children = { _entryListBox, inspectorCard }
        };
        Grid.SetColumn(inspectorCard, 1);

        var rootGrid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,*"),
            Children = { _kpiStrip, filterBar, masterDetailGrid }
        };
        Grid.SetRow(filterBar, 1);
        Grid.SetRow(masterDetailGrid, 2);

        Content = rootGrid;
    }

    public void RefreshAll()
    {
        var notebooks = _vocabularyService.GetNotebooks();
        var allOption = new VocabularyNotebook { Id = "all", Name = "全部生词本 (All Decks)" };
        var filterOptions = new[] { allOption }.Concat(notebooks).ToArray();

        _suppressFilterEvents = true;
        try
        {
            var prevId = (_notebookCombo.SelectedItem as VocabularyNotebook)?.Id ?? "all";
            _notebookCombo.ItemsSource = filterOptions;
            _notebookCombo.SelectedItem = filterOptions.FirstOrDefault(n => n.Id == prevId) ?? allOption;
            _notebookCombo.ItemTemplate = new FuncDataTemplate<VocabularyNotebook>((n, _) => new TextBlock { Text = n?.Name });
        }
        finally
        {
            _suppressFilterEvents = false;
        }

        var stats = _vocabularyService.GetDashboardStats();
        _kpiStrip.Children.Clear();
        _kpiStrip.Children.Add(StudioUi.CreateMetricKpiCard("词库总资产", stats.TotalEntries.ToString(), $"星标重点 {stats.StarredCount} 条", "book-open-variant", AppTheme.AccentBright));
        _kpiStrip.Children.Add(StudioUi.CreateMetricKpiCard("今日待复习", stats.DueTodayCount.ToString(), "FSRS 智能到期调度", "brain", AppTheme.Amber));
        _kpiStrip.Children.Add(StudioUi.CreateMetricKpiCard("今日已打卡", stats.ReviewedTodayCount.ToString(), $"复习中 {stats.ReviewingCount} 条", "check-all", AppTheme.Emerald));
        _kpiStrip.Children.Add(StudioUi.CreateMetricKpiCard("永久掌握", stats.MasteredCount.ToString(), "稳定度 S ≥ 45 天", "school", AppTheme.Purple));
        _kpiStrip.Children.Add(StudioUi.CreateMetricKpiCard("平均记忆保持率", $"{stats.AverageRetentionRate:F1}%", "基于艾宾浩斯遗忘曲线", "chart-timeline-variant", AppTheme.Emerald));

        ReloadEntries();
    }

    public void ReloadEntries()
    {
        var filter = BuildFilter();
        var entries = _vocabularyService.QueryEntries(filter);
        var prevId = _selectedEntry?.Id;
        _entryListBox.ItemsSource = entries;
        var next = entries.FirstOrDefault(e => e.Id == prevId) ?? entries.FirstOrDefault();
        _entryListBox.SelectedItem = next;
        if (next is not null) RenderInspector(next);
        else RenderEmptyInspector();
    }

    private VocabularyQueryFilter BuildFilter()
    {
        var nbId = (_notebookCombo.SelectedItem as VocabularyNotebook)?.Id;
        MemoryStage? stage = _stageCombo.SelectedIndex switch
        {
            1 => MemoryStage.New,
            2 => MemoryStage.Learning,
            3 => MemoryStage.Reviewing,
            4 => MemoryStage.Mastered,
            _ => null
        };
        VocabularyItemType? type = _typeCombo.SelectedIndex switch
        {
            1 => VocabularyItemType.Word,
            2 => VocabularyItemType.Phrase,
            3 => VocabularyItemType.Sentence,
            4 => VocabularyItemType.Idiom,
            5 => VocabularyItemType.TechnicalTerm,
            _ => null
        };
        var sort = _sortCombo.SelectedIndex switch
        {
            1 => VocabularySortOrder.NextReviewDue,
            2 => VocabularySortOrder.Alphabetical,
            3 => VocabularySortOrder.MostLookedUp,
            4 => VocabularySortOrder.HighestDifficulty,
            5 => VocabularySortOrder.LowestMastery,
            _ => VocabularySortOrder.RecentlyAdded
        };

        return new VocabularyQueryFilter
        {
            SearchKeyword = _searchBox.Text,
            NotebookId = nbId,
            Stage = stage,
            ItemType = type,
            SortOrder = sort,
            OnlyStarred = _starredOnlyCheck.IsChecked == true,
            OnlyDue = _dueOnlyCheck.IsChecked == true
        };
    }

    private static Control BuildEntryListItem(VocabularyEntry? entry)
    {
        if (entry is null) return new Control();
        var stars = new string('★', entry.MasteryLevel) + new string('☆', Math.Max(0, 5 - entry.MasteryLevel));
        var stageBrush = entry.Stage switch
        {
            MemoryStage.Mastered => AppTheme.Emerald,
            MemoryStage.Reviewing => AppTheme.AccentBright,
            MemoryStage.Learning => AppTheme.Amber,
            _ => AppTheme.Purple
        };

        return new Border
        {
            Padding = new Thickness(12, 10),
            BorderBrush = AppTheme.BorderSubtle,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                RowDefinitions = new RowDefinitions("Auto,Auto,Auto"),
                Children =
                {
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 7,
                        Children =
                        {
                            new MdiIcon(entry.IsStarred ? "star" : "star-outline", 15, entry.IsStarred ? AppTheme.Amber : AppTheme.TextMuted),
                            new TextBlock
                            {
                                Text = entry.Headword,
                                FontSize = 15,
                                FontWeight = FontWeight.Bold,
                                Foreground = AppTheme.TextPrimary,
                                TextTrimming = TextTrimming.CharacterEllipsis,
                                MaxWidth = 225
                            }
                        }
                    },
                    new TextBlock
                    {
                        [Grid.ColumnProperty] = 1,
                        Text = stars,
                        FontSize = 11.5,
                        Foreground = AppTheme.Amber
                    },
                    new TextBlock
                    {
                        [Grid.RowProperty] = 1,
                        [Grid.ColumnSpanProperty] = 2,
                        Text = entry.Translation,
                        FontSize = 12.5,
                        Foreground = AppTheme.TextSecondary,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        Margin = new Thickness(0, 4, 0, 4)
                    },
                    new StackPanel
                    {
                        [Grid.RowProperty] = 2,
                        [Grid.ColumnSpanProperty] = 2,
                        Orientation = Orientation.Horizontal,
                        Spacing = 10,
                        Children =
                        {
                            new TextBlock { Text = UiLabels.Stage(entry.Stage), FontSize = 11, FontWeight = FontWeight.Bold, Foreground = stageBrush },
                            new TextBlock { Text = $"查词 {entry.LookupCount}x", FontSize = 11, Foreground = AppTheme.TextMuted },
                            new TextBlock { Text = $"稳定度 {entry.StabilityDays:F1}d", FontSize = 11, Foreground = AppTheme.TextMuted }
                        }
                    }
                }
            }
        };
    }

    private void RenderInspector(VocabularyEntry entry)
    {
        _inspectorPanel.Children.Clear();
        var now = DateTimeOffset.UtcNow;
        var retention = entry.EstimateRetrievability(now) * 100.0;
        var intervals = SpacedRepetitionScheduler.PreviewIntervals(entry, now);

        var starBtn = StudioUi.CreateSecondaryButton(
            entry.IsStarred ? "star" : "star-outline",
            entry.IsStarred ? "取消星标" : "星标重点",
            () => _vocabularyService.ToggleStar(entry.Id));

        var reAnalyzeBtn = StudioUi.CreateSecondaryButton("sparkles", "AI 词典深度重析", () =>
            _onLookupInStudio(entry.Headword, entry.ContextSentence, entry.CaptureSource, entry.SourceTitle));

        var deleteBtn = StudioUi.CreateSecondaryButton("delete-outline", "移除", () =>
        {
            _vocabularyService.DeleteEntry(entry.Id);
            _setStatus($"已从生词本移除「{entry.Headword}」");
        });

        var headerGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"),
            ColumnSpacing = 8,
            Children =
            {
                new StackPanel
                {
                    Spacing = 5,
                    Children =
                    {
                        new TextBlock { Text = entry.Headword, FontSize = 26, FontWeight = FontWeight.Bold, Foreground = AppTheme.TextPrimary, TextWrapping = TextWrapping.Wrap },
                        new TextBlock
                        {
                            Text = string.Join("   ", new[] { entry.UkPhonetic, entry.UsPhonetic, $"[{UiLabels.ItemType(entry.ItemType)}]", $"捕获源：{UiLabels.CaptureSource(entry.CaptureSource)} {entry.SourceTitle}" }.Where(s => !string.IsNullOrWhiteSpace(s))),
                            FontFamily = AppTheme.MonoFont,
                            FontSize = 12.5,
                            Foreground = AppTheme.TextSecondary
                        }
                    }
                },
                starBtn,
                reAnalyzeBtn,
                deleteBtn
            }
        };
        Grid.SetColumn(starBtn, 1);
        Grid.SetColumn(reAnalyzeBtn, 2);
        Grid.SetColumn(deleteBtn, 3);
        _inspectorPanel.Children.Add(headerGrid);

        _inspectorPanel.Children.Add(PhonicsAudioBar.CreateWordPhonicsStrip(
            _speechService,
            _settingsStore,
            entry.Headword,
            entry.UsPhonetic,
            entry.UkPhonetic,
            entry.ContextSentence));

        _inspectorPanel.Children.Add(new TextBlock
        {
            Text = entry.Translation,
            FontSize = 17,
            FontWeight = FontWeight.SemiBold,
            Foreground = AppTheme.Emerald,
            TextWrapping = TextWrapping.Wrap
        });

        // FSRS Cognitive Telemetry & Instant Check-in Card
        var fsrsCard = new Border
        {
            Background = AppTheme.InputBg,
            BorderBrush = AppTheme.BorderStrong,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 12),
            Child = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new WrapPanel
                    {
                        Children =
                        {
                            BuildTelemetryChip("阶段", UiLabels.Stage(entry.Stage), AppTheme.AccentBright),
                            BuildTelemetryChip("稳定度 S", $"{entry.StabilityDays:F1} 天", AppTheme.Emerald),
                            BuildTelemetryChip("难度 D", $"{entry.Difficulty:F1} / 10", AppTheme.Amber),
                            BuildTelemetryChip("记忆保持率 R", $"{retention:F0}%", AppTheme.Emerald),
                            BuildTelemetryChip("复习记录", $"{entry.TotalReviews} 次 (连对 {entry.CorrectStreak})", AppTheme.Purple)
                        }
                    },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        Children =
                        {
                            new TextBlock { Text = "即时记忆反馈：", FontSize = 12, Foreground = AppTheme.TextMuted, VerticalAlignment = VerticalAlignment.Center },
                            CreateQuickRateBtn($"重来 Again ({SpacedRepetitionScheduler.FormatInterval(intervals.AgainInterval)})", AppTheme.DangerSoft, AppTheme.Danger, () => GradeEntry(entry.Id, ReviewRating.Again)),
                            CreateQuickRateBtn($"困难 Hard ({SpacedRepetitionScheduler.FormatInterval(intervals.HardInterval)})", AppTheme.AmberSoft, AppTheme.Amber, () => GradeEntry(entry.Id, ReviewRating.Hard)),
                            CreateQuickRateBtn($"良好 Good ({SpacedRepetitionScheduler.FormatInterval(intervals.GoodInterval)})", AppTheme.AccentSoft, AppTheme.AccentBright, () => GradeEntry(entry.Id, ReviewRating.Good)),
                            CreateQuickRateBtn($"熟练 Easy ({SpacedRepetitionScheduler.FormatInterval(intervals.EasyInterval)})", AppTheme.EmeraldSoft, AppTheme.Emerald, () => GradeEntry(entry.Id, ReviewRating.Easy))
                        }
                    }
                }
            }
        };
        _inspectorPanel.Children.Add(fsrsCard);

        if (!string.IsNullOrWhiteSpace(entry.ContextSentence))
        {
            _inspectorPanel.Children.Add(StudioUi.CreateInfoTile(
                $"捕获原句溯源 ({UiLabels.CaptureSource(entry.CaptureSource)})",
                $"{entry.ContextSentence}\n{entry.ContextTranslation}".Trim(),
                "format-quote-close",
                AppTheme.AccentBright));
        }

        if (!string.IsNullOrWhiteSpace(entry.EtymologyAndRoot))
            _inspectorPanel.Children.Add(StudioUi.CreateInfoTile("词源与词根词缀基因", entry.EtymologyAndRoot, "code-braces", AppTheme.Purple));

        if (!string.IsNullOrWhiteSpace(entry.MemoryMnemonic))
            _inspectorPanel.Children.Add(StudioUi.CreateInfoTile("AI 联想巧记助记法", entry.MemoryMnemonic, "lightbulb-on-outline", AppTheme.Amber));

        if (entry.Collocations.Count > 0)
        {
            _inspectorPanel.Children.Add(StudioUi.CreateInfoTile(
                "高频搭配",
                string.Join("   ·   ", entry.Collocations.Select(c => $"{c.Phrase} ({c.Meaning})")),
                "sparkles",
                AppTheme.Purple));
        }

        if (entry.Examples.Count > 0)
        {
            _inspectorPanel.Children.Add(StudioUi.CreateInfoTile(
                "双语例句库",
                string.Join("\n\n", entry.Examples.Select(ex => $"• {ex.SourceSentence}\n  {ex.TranslatedSentence}"))));
        }

        // Editable Personal Notes & Tags
        var tagsBox = new TextBox
        {
            Text = string.Join(", ", entry.Tags),
            PlaceholderText = "自定义标签（逗号分隔，如：IELTS, 论文核心, 易混词）",
            Background = AppTheme.InputBg,
            Foreground = AppTheme.TextPrimary,
            CornerRadius = new CornerRadius(8)
        };
        var noteBox = new TextBox
        {
            Text = entry.UserPersonalNote,
            PlaceholderText = "记录你的专属造句、辨析心得或上下文批注…",
            AcceptsReturn = true,
            MinHeight = 64,
            Background = AppTheme.InputBg,
            Foreground = AppTheme.TextPrimary,
            CornerRadius = new CornerRadius(8)
        };
        var saveBtn = StudioUi.CreatePrimaryButton("content-save", "保存标签与个人笔记", () =>
        {
            var newTags = (tagsBox.Text ?? "")
                .Split([',', '，', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            _vocabularyService.SaveEntry(entry with
            {
                Tags = newTags,
                UserPersonalNote = noteBox.Text?.Trim() ?? ""
            });
            _setStatus($"已更新「{entry.Headword}」的个人笔记与标签");
        });

        _inspectorPanel.Children.Add(new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = "个人笔记与标签标注", FontSize = 12.5, FontWeight = FontWeight.SemiBold, Foreground = AppTheme.TextSecondary },
                tagsBox,
                noteBox,
                saveBtn
            }
        });
    }

    private void RenderEmptyInspector()
    {
        _inspectorPanel.Children.Clear();
        _inspectorPanel.Children.Add(new TextBlock
        {
            Text = "当前筛选条件下没有词条。你可以在「查词与 AI 精翻」、全局划词弹窗（Alt+Q）或截图翻译（Alt+S）中随时收录新词与长句。",
            FontSize = 14,
            Foreground = AppTheme.TextMuted,
            Margin = new Thickness(12, 24)
        });
    }

    private void GradeEntry(string entryId, ReviewRating rating)
    {
        var updated = _vocabularyService.SubmitReview(entryId, rating);
        _setStatus($"已打卡「{updated.Headword}」({UiLabels.Rating(rating)}) → 下次复习：{updated.NextReviewAt.ToLocalTime():yyyy-MM-dd HH:mm} (稳定度 S={updated.StabilityDays:F1}d)");
    }

    private async void ShowCreateNotebookDialogAsync()
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null) return;
        var nameBox = new TextBox { PlaceholderText = "例如：ArXiv 论文高频术语 / 外刊金句本", Width = 320 };
        var descBox = new TextBox { PlaceholderText = "词本用途备注", Width = 320 };
        var dialog = new Window
        {
            Title = "新建专属生词本",
            Width = 380,
            Height = 230,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = AppTheme.CardBg
        };
        var createBtn = StudioUi.CreatePrimaryButton("check", "创建生词本", () =>
        {
            if (!string.IsNullOrWhiteSpace(nameBox.Text))
            {
                var created = _vocabularyService.CreateNotebook(nameBox.Text, descBox.Text ?? "");
                _setStatus($"已创建生词本「{created.Name}」");
                dialog.Close();
            }
        });
        dialog.Content = new StackPanel
        {
            Spacing = 10,
            Margin = new Thickness(20),
            Children =
            {
                new TextBlock { Text = "生词本名称：", Foreground = AppTheme.TextPrimary, FontWeight = FontWeight.SemiBold },
                nameBox,
                new TextBlock { Text = "简要说明：", Foreground = AppTheme.TextSecondary },
                descBox,
                createBtn
            }
        };
        await dialog.ShowDialog(owner);
    }

    private async void ExportVocabularyAsync()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;
        var filter = BuildFilter();
        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出超级生词本 (Anki TSV / Markdown / CSV / JSON)",
            SuggestedFileName = $"AiTranslate_Vocabulary_{DateTime.Now:yyyyMMdd}",
            DefaultExtension = "md",
            FileTypeChoices =
            [
                new("Markdown 精美复习笔记 (*.md)") { Patterns = ["*.md"] },
                new("Anki 闪卡导入格式 (*.tsv)") { Patterns = ["*.tsv"] },
                new("Excel / CSV 表格 (*.csv)") { Patterns = ["*.csv"] },
                new("完整结构化备份 (*.json)") { Patterns = ["*.json"] }
            ]
        });
        if (file is null) return;
        var ext = Path.GetExtension(file.Name).ToLowerInvariant();
        var content = ext switch
        {
            ".tsv" => _vocabularyService.ExportToAnkiTsv(filter),
            ".csv" => _vocabularyService.ExportToCsv(filter),
            ".json" => _vocabularyService.ExportToJson(filter),
            _ => _vocabularyService.ExportToMarkdown(filter)
        };
        await using var stream = await file.OpenWriteAsync();
        if (stream.CanSeek) stream.SetLength(0);
        await using var writer = new StreamWriter(stream, Encoding.UTF8);
        await writer.WriteAsync(content);
        _setStatus($"已导出当前生词列表至 {file.Name}");
    }

    private static Border BuildTelemetryChip(string label, string val, IBrush valBrush) => new()
    {
        Background = AppTheme.CardBg,
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(10, 4),
        Margin = new Thickness(0, 0, 8, 6),
        Child = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children =
            {
                new TextBlock { Text = $"{label}:", FontSize = 11.5, Foreground = AppTheme.TextMuted },
                new TextBlock { Text = val, FontSize = 12, FontWeight = FontWeight.Bold, Foreground = valBrush }
            }
        }
    };

    private static Button CreateQuickRateBtn(string text, IBrush bg, IBrush fg, Action onClick)
    {
        var btn = new Button
        {
            Background = bg,
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(10, 4),
            BorderThickness = new Thickness(0),
            Content = new TextBlock { Text = text, FontSize = 11.5, FontWeight = FontWeight.SemiBold, Foreground = fg }
        };
        btn.Click += (_, _) => onClick();
        return btn;
    }
}
