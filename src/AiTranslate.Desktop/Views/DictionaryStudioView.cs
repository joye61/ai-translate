using AiTranslate.Ai;
using AiTranslate.Core;
using AiTranslate.Desktop.Controls;
using AiTranslate.Speech;
using AiTranslate.Vocabulary;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;

namespace AiTranslate.Desktop.Views;

/// <summary>
/// Cognitive Linguistic Studio View (查词与 AI 语境精翻工作台).
/// Replaces cramped legacy dictionary UIs with an Omni-Command Bar + 2-Column Bento Matrix + KokoroSharp Phonics Studio.
/// </summary>
public sealed class DictionaryStudioView : UserControl
{
    private readonly IAiDictionaryService _dictionaryService;
    private readonly IVocabularyService _vocabularyService;
    private readonly ISpeechSynthesisService _speechService;
    private readonly AppSettingsStore _settingsStore;
    private readonly Action<string> _setStatus;
    private readonly Func<Task> _onStartScreenshotTranslate;
    private readonly Func<Task> _onReadClipboardTranslate;

    private readonly TextBox _inputBox;
    private readonly TextBox _contextBox;
    private readonly ComboBox _targetLangCombo;
    private readonly ComboBox _targetNotebookCombo;
    private readonly StackPanel _bentoStage;
    private CancellationTokenSource? _lookupCts;

    public DictionaryStudioView(
        IAiDictionaryService dictionaryService,
        IVocabularyService vocabularyService,
        ISpeechSynthesisService speechService,
        AppSettingsStore settingsStore,
        Action<string> setStatus,
        Func<Task> onStartScreenshotTranslate,
        Func<Task> onReadClipboardTranslate)
    {
        _dictionaryService = dictionaryService;
        _vocabularyService = vocabularyService;
        _speechService = speechService;
        _settingsStore = settingsStore;
        _setStatus = setStatus;
        _onStartScreenshotTranslate = onStartScreenshotTranslate;
        _onReadClipboardTranslate = onReadClipboardTranslate;

        _inputBox = new TextBox
        {
            PlaceholderText = "输入单词、短语、习语、专业术语或长难句（按 Enter 或 Ctrl+Enter 启动 AI 深度语义解析）…",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 68,
            MaxHeight = 130,
            FontSize = 15.5,
            Background = AppTheme.InputBg,
            Foreground = AppTheme.TextPrimary,
            BorderBrush = AppTheme.BorderStrong,
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(16, 12)
        };
        _inputBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && (e.KeyModifiers.HasFlag(KeyModifiers.Control) || !(_inputBox.Text ?? "").Contains('\n')))
            {
                e.Handled = true;
                _ = LookupAsync(_inputBox.Text ?? "", _contextBox?.Text, TranslationTriggerSource.ManualInput, "工作台搜索");
            }
        };

        _contextBox = new TextBox
        {
            PlaceholderText = "语境锚点（可选）：粘贴你在论文/源码/外刊中遇到该词的原句，AI 将精准剖析当前语境义并随生词卡片永久溯源保存",
            FontSize = 12.5,
            Background = AppTheme.InputBg,
            Foreground = AppTheme.TextSecondary,
            BorderBrush = AppTheme.BorderSubtle,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 8)
        };

        _targetLangCombo = new ComboBox
        {
            Width = 150,
            CornerRadius = new CornerRadius(8),
            ItemsSource = TranslationLanguages.All,
            SelectedItem = TranslationLanguages.Get(_settingsStore.Current.DefaultTargetLanguage),
            ItemTemplate = new FuncDataTemplate<TranslationLanguage>((l, _) => new TextBlock { Text = l?.DisplayName })
        };

        _targetNotebookCombo = new ComboBox
        {
            Width = 195,
            CornerRadius = new CornerRadius(8),
            ItemTemplate = new FuncDataTemplate<VocabularyNotebook>((nb, _) => new TextBlock { Text = nb?.Name })
        };

        var sampleChips = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = "一键体验：", FontSize = 12, Foreground = AppTheme.TextMuted, VerticalAlignment = VerticalAlignment.Center }
            }
        };
        foreach (var word in new[] { "serendipity", "ubiquitous", "ephemeral" })
        {
            var w = word;
            var chip = new Button
            {
                Content = w,
                FontSize = 12,
                Padding = new Thickness(10, 4),
                CornerRadius = new CornerRadius(999),
                Background = AppTheme.CardElevatedBg,
                Foreground = AppTheme.AccentBright,
                BorderBrush = AppTheme.BorderSubtle,
                BorderThickness = new Thickness(1)
            };
            chip.Click += (_, _) =>
            {
                _inputBox.Text = w;
                _ = LookupAsync(w, null, TranslationTriggerSource.ManualInput, "精选词条");
            };
            sampleChips.Children.Add(chip);
        }

        var actionRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto,Auto,Auto"),
            ColumnSpacing = 10,
            Children =
            {
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    Children =
                    {
                        new MdiIcon("translate", 15, AppTheme.AccentBright),
                        _targetLangCombo
                    }
                },
                new StackPanel
                {
                    [Grid.ColumnProperty] = 1,
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    Children =
                    {
                        new MdiIcon("book-open-page-variant", 15, AppTheme.Emerald),
                        _targetNotebookCombo
                    }
                },
                sampleChips,
                StudioUi.CreateSecondaryButton("clipboard-text-outline", "剪贴板精翻", () => _ = _onReadClipboardTranslate()),
                StudioUi.CreateSecondaryButton("crop-free", "截图原位翻译", () => _ = _onStartScreenshotTranslate()),
                StudioUi.CreatePrimaryButton("sparkles", "AI 深度解析", () =>
                    _ = LookupAsync(_inputBox.Text ?? "", _contextBox.Text, TranslationTriggerSource.ManualInput, "手动查询"))
            }
        };
        Grid.SetColumn(sampleChips, 2);
        Grid.SetColumn(actionRow.Children[3], 3);
        Grid.SetColumn(actionRow.Children[4], 4);
        Grid.SetColumn(actionRow.Children[5], 5);

        var commandDeck = StudioUi.CreateBentoCard(
            new StackPanel { Spacing = 10, Children = { _inputBox, _contextBox, actionRow } },
            padding: new Thickness(18, 16),
            borderBrush: AppTheme.BorderStrong);
        commandDeck.Margin = new Thickness(24, 20, 24, 12);

        _bentoStage = new StackPanel
        {
            Spacing = 16,
            Margin = new Thickness(24, 4, 24, 24)
        };

        var rootGrid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            Children =
            {
                commandDeck,
                new ScrollViewer { [Grid.RowProperty] = 1, Content = _bentoStage }
            }
        };

        Content = rootGrid;
        RefreshNotebooks();
    }

    public void RefreshNotebooks()
    {
        var notebooks = _vocabularyService.GetNotebooks();
        var prevId = (_targetNotebookCombo.SelectedItem as VocabularyNotebook)?.Id ?? _settingsStore.Current.DefaultNotebookId;
        _targetNotebookCombo.ItemsSource = notebooks;
        _targetNotebookCombo.SelectedItem = notebooks.FirstOrDefault(n => n.Id == prevId) ?? notebooks.FirstOrDefault();
    }

    public async Task LookupAsync(string query, string? contextSentence, TranslationTriggerSource source, string sourceTitle)
    {
        if (string.IsNullOrWhiteSpace(query)) return;
        _inputBox.Text = query.Trim();
        if (!string.IsNullOrWhiteSpace(contextSentence))
            _contextBox.Text = contextSentence.Trim();

        _lookupCts?.Cancel();
        _lookupCts?.Dispose();
        _lookupCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = _lookupCts.Token;

        var targetLangId = (_targetLangCombo.SelectedItem as TranslationLanguage)?.Id
            ?? _settingsStore.Current.DefaultTargetLanguage;

        _setStatus($"正在调用 AI 认知词典引擎深度解析「{Truncate(query.Trim(), 28)}」…");

        try
        {
            var result = await _dictionaryService.LookupOrTranslateAsync(
                query.Trim(),
                targetLangId,
                contextSentence,
                cancellationToken: token);

            if (token.IsCancellationRequested) return;
            DisplayResult(result, source, sourceTitle);
            _setStatus($"已完成「{Truncate(result.SourceText, 24)}」的 AI 认知词典解析");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _setStatus($"查询异常：{ex.Message}");
        }
    }

    public void DisplayResult(DictionaryEntryResult result, TranslationTriggerSource source, string sourceTitle)
    {
        _inputBox.Text = result.SourceText;
        _bentoStage.Children.Clear();

        var existing = _vocabularyService.FindByHeadword(result.SourceText);

        // 1. HERO SPOTLIGHT BANNER
        var saveBtn = StudioUi.CreatePrimaryButton(
            existing is not null ? "check-all" : "notebook-plus",
            existing is not null ? $"已入生词本 (累计查词 {existing.LookupCount} 次 · 点击更新卡片)" : "一键收录至超级单词本",
            () =>
            {
                var nbId = (_targetNotebookCombo.SelectedItem as VocabularyNotebook)?.Id ?? "default";
                var saved = _vocabularyService.AddOrUpdateFromDictionary(
                    result,
                    notebookId: nbId,
                    contextSentence: _contextBox.Text,
                    source: source,
                    sourceTitle: sourceTitle);
                DisplayResult(result, source, sourceTitle);
                _setStatus($"已将「{saved.Headword}」收录进单词本，并激活 FSRS 记忆追踪！");
            });
        if (existing is not null)
            saveBtn.Background = AppTheme.Emerald;

        var copyBtn = StudioUi.CreateSecondaryButton("content-copy", "复制精翻结果", async () =>
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.Clipboard is not null)
            {
                await topLevel.Clipboard.SetTextAsync($"{result.SourceText}\n{result.PrimaryTranslation}");
                _setStatus("已复制词条与核心译文到剪贴板");
            }
        });

        var badgesRow = new WrapPanel { Margin = new Thickness(0, 2, 0, 2) };
        badgesRow.Children.Add(new Border
        {
            Margin = new Thickness(0, 0, 10, 4),
            Child = StudioUi.CreateBadge(result.ItemType.ToString(), AppTheme.AccentSoft, AppTheme.AccentBright)
        });
        foreach (var tag in result.SuggestedTags)
        {
            badgesRow.Children.Add(new Border
            {
                Margin = new Thickness(0, 0, 8, 4),
                Child = StudioUi.CreateBadge($"#{tag}", AppTheme.PurpleSoft, AppTheme.Purple)
            });
        }

        // Interactive KokoroSharp Phonics & Multi-Accent Speech Strip
        var phonicsStrip = PhonicsAudioBar.CreateWordPhonicsStrip(
            _speechService,
            _settingsStore,
            result.SourceText,
            result.UsPhonetic,
            result.UkPhonetic,
            result.Examples.FirstOrDefault()?.SourceSentence);

        if (_settingsStore.Current.AutoPronounceOnLookup)
        {
            _ = _speechService.SpeakAsync(
                result.SourceText,
                SpeechAccent.AmericanEnglish,
                result.UsPhonetic,
                _settingsStore.Current.SpeechSpeed,
                _settingsStore.Current.PreferredUsVoiceId);
        }

        var heroGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 16,
            Children =
            {
                new StackPanel
                {
                    Spacing = 10,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = result.SourceText,
                            FontSize = 30,
                            FontWeight = FontWeight.Bold,
                            Foreground = AppTheme.TextPrimary,
                            TextWrapping = TextWrapping.Wrap
                        },
                        phonicsStrip,
                        badgesRow,
                        new TextBlock
                        {
                            Text = result.PrimaryTranslation,
                            FontSize = 18.5,
                            FontWeight = FontWeight.SemiBold,
                            Foreground = AppTheme.Emerald,
                            TextWrapping = TextWrapping.Wrap
                        }
                    }
                },
                new StackPanel
                {
                    [Grid.ColumnProperty] = 1,
                    Orientation = Orientation.Horizontal,
                    Spacing = 10,
                    VerticalAlignment = VerticalAlignment.Top,
                    Children = { copyBtn, saveBtn }
                }
            }
        };
        _bentoStage.Children.Add(StudioUi.CreateHeroCard(heroGrid));

        // 2. ASYMMETRIC 2-COLUMN BENTO MATRIX (Left: Senses + Examples | Right: Etymology + Mnemonic + Collocations)
        var leftColumn = new StackPanel { Spacing = 16 };
        var rightColumn = new StackPanel { Spacing = 16 };

        // Left Card A: Multi-POS Senses & Inflections
        if (result.Senses.Count > 0 || result.Inflections.Count > 0)
        {
            var sensesStack = new StackPanel { Spacing = 10 };
            sensesStack.Children.Add(StudioUi.CreateSectionHeader("book-open-variant", "多维词性释义与词形矩阵", "Senses & Morphology", AppTheme.AccentBright));
            foreach (var sense in result.Senses)
            {
                sensesStack.Children.Add(new Border
                {
                    Background = AppTheme.InputBg,
                    BorderBrush = AppTheme.BorderSubtle,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(14, 10),
                    Child = new StackPanel
                    {
                        Spacing = 4,
                        Children =
                        {
                            new StackPanel
                            {
                                Orientation = Orientation.Horizontal,
                                Spacing = 10,
                                Children =
                                {
                                    StudioUi.CreateBadge(sense.PartOfSpeech, AppTheme.AmberSoft, AppTheme.Amber),
                                    new TextBlock
                                    {
                                        Text = sense.DefinitionZh,
                                        FontSize = 14.5,
                                        FontWeight = FontWeight.SemiBold,
                                        Foreground = AppTheme.TextPrimary,
                                        VerticalAlignment = VerticalAlignment.Center,
                                        TextWrapping = TextWrapping.Wrap
                                    }
                                }
                            },
                            new TextBlock
                            {
                                Text = sense.DefinitionEn,
                                FontSize = 12.5,
                                Foreground = AppTheme.TextSecondary,
                                IsVisible = !string.IsNullOrWhiteSpace(sense.DefinitionEn),
                                TextWrapping = TextWrapping.Wrap
                            }
                        }
                    }
                });
            }

            if (result.Inflections.Count > 0)
            {
                var infWrap = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
                foreach (var inf in result.Inflections)
                {
                    infWrap.Children.Add(new Border
                    {
                        Background = AppTheme.CardElevatedBg,
                        CornerRadius = new CornerRadius(8),
                        Padding = new Thickness(12, 5),
                        Margin = new Thickness(0, 0, 8, 6),
                        Child = new TextBlock
                        {
                            Text = $"{inf.Label}  →  {inf.Value}",
                            FontSize = 12.5,
                            FontFamily = AppTheme.MonoFont,
                            Foreground = AppTheme.TextPrimary
                        }
                    });
                }
                sensesStack.Children.Add(infWrap);
            }
            leftColumn.Children.Add(StudioUi.CreateBentoCard(sensesStack));
        }

        // Left Card B: Authentic Bilingual Examples
        if (result.Examples.Count > 0)
        {
            var exStack = new StackPanel { Spacing = 10 };
            exStack.Children.Add(StudioUi.CreateSectionHeader("format-quote-close", "真实语料库双语例句", "Authentic Context Examples", AppTheme.Emerald));
            foreach (var ex in result.Examples)
            {
                var exSentence = ex.SourceSentence;
                exStack.Children.Add(new Border
                {
                    Background = AppTheme.InputBg,
                    BorderBrush = AppTheme.BorderSubtle,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(14, 11),
                    Child = new Grid
                    {
                        ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                        ColumnSpacing = 10,
                        Children =
                        {
                            new StackPanel
                            {
                                Spacing = 5,
                                Children =
                                {
                                    new TextBlock
                                    {
                                        Text = ex.SourceSentence,
                                        FontSize = 14,
                                        FontWeight = FontWeight.Medium,
                                        Foreground = AppTheme.TextPrimary,
                                        TextWrapping = TextWrapping.Wrap
                                    },
                                    new TextBlock
                                    {
                                        Text = ex.TranslatedSentence,
                                        FontSize = 13,
                                        Foreground = AppTheme.TextSecondary,
                                        TextWrapping = TextWrapping.Wrap
                                    }
                                }
                            },
                            new Border
                            {
                                [Grid.ColumnProperty] = 1,
                                VerticalAlignment = VerticalAlignment.Top,
                                Child = PhonicsAudioBar.CreateSentenceReadButton(_speechService, _settingsStore, exSentence, "跟读")
                            }
                        }
                    }
                });
            }
            leftColumn.Children.Add(StudioUi.CreateBentoCard(exStack));
        }

        // Right Card A: AI Cognitive Memory & Etymology Lab
        if (!string.IsNullOrWhiteSpace(result.EtymologyAndRoot) ||
            !string.IsNullOrWhiteSpace(result.MemoryMnemonic) ||
            !string.IsNullOrWhiteSpace(result.GrammarBreakdown) ||
            !string.IsNullOrWhiteSpace(result.ContextMeaning))
        {
            var cogStack = new StackPanel { Spacing = 10 };
            cogStack.Children.Add(StudioUi.CreateSectionHeader("lightbulb-on-outline", "AI 词源拆解与认知助记", "Etymology, Mnemonics & Syntax", AppTheme.Amber));

            if (!string.IsNullOrWhiteSpace(result.ContextMeaning))
                cogStack.Children.Add(StudioUi.CreateInfoTile("当前语境精准义", result.ContextMeaning, "sparkles", AppTheme.AccentBright));
            if (!string.IsNullOrWhiteSpace(result.EtymologyAndRoot))
                cogStack.Children.Add(StudioUi.CreateInfoTile("词源与词根词缀基因 (Etymology & Roots)", result.EtymologyAndRoot, "code-braces", AppTheme.Emerald));
            if (!string.IsNullOrWhiteSpace(result.MemoryMnemonic))
                cogStack.Children.Add(StudioUi.CreateInfoTile("AI 高效联想巧记法 (Memory Mnemonic)", result.MemoryMnemonic, "lightbulb-on-outline", AppTheme.Amber));
            if (!string.IsNullOrWhiteSpace(result.GrammarBreakdown))
                cogStack.Children.Add(StudioUi.CreateInfoTile("句法与语法结构剖析", result.GrammarBreakdown, "school", AppTheme.Purple));
            if (!string.IsNullOrWhiteSpace(result.UsageNotes))
                cogStack.Children.Add(StudioUi.CreateInfoTile("母语者语域与辨析提示", result.UsageNotes));

            rightColumn.Children.Add(StudioUi.CreateBentoCard(cogStack));
        }

        // Right Card B: Collocations & Semantic Network
        if (result.Collocations.Count > 0 || result.Synonyms.Count > 0 || result.Antonyms.Count > 0)
        {
            var netStack = new StackPanel { Spacing = 10 };
            netStack.Children.Add(StudioUi.CreateSectionHeader("sparkles", "高频搭配与近反义词网络", "Collocations & Semantic Graph", AppTheme.Purple));

            if (result.Collocations.Count > 0)
            {
                var wrap = new WrapPanel();
                foreach (var c in result.Collocations)
                {
                    wrap.Children.Add(new Border
                    {
                        Background = AppTheme.InputBg,
                        BorderBrush = AppTheme.BorderSubtle,
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(10),
                        Padding = new Thickness(12, 8),
                        Margin = new Thickness(0, 0, 8, 8),
                        Child = new StackPanel
                        {
                            Spacing = 2,
                            Children =
                            {
                                new TextBlock { Text = c.Phrase, FontWeight = FontWeight.Bold, Foreground = AppTheme.AccentBright, FontSize = 13.5 },
                                new TextBlock { Text = c.Meaning, Foreground = AppTheme.TextSecondary, FontSize = 12 }
                            }
                        }
                    });
                }
                netStack.Children.Add(wrap);
            }

            if (result.Synonyms.Count > 0)
                netStack.Children.Add(StudioUi.CreateInfoTile("同义替换扩展 (Synonyms)", string.Join("   ·   ", result.Synonyms), "check", AppTheme.Emerald));
            if (result.Antonyms.Count > 0)
                netStack.Children.Add(StudioUi.CreateInfoTile("反义对比词汇 (Antonyms)", string.Join("   ·   ", result.Antonyms), "swap-horizontal", AppTheme.Danger));

            rightColumn.Children.Add(StudioUi.CreateBentoCard(netStack));
        }

        var bentoColumns = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("1.1*,0.9*"),
            ColumnSpacing = 16,
            Children = { leftColumn, rightColumn }
        };
        Grid.SetColumn(rightColumn, 1);
        _bentoStage.Children.Add(bentoColumns);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
