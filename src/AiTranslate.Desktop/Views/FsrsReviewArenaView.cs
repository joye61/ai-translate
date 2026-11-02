using AiTranslate.Core;
using AiTranslate.Desktop.Controls;
using AiTranslate.Speech;
using AiTranslate.Vocabulary;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace AiTranslate.Desktop.Views;

/// <summary>
/// Immersive FSRS Spaced Repetition Arena (FSRS 科学记忆训练场).
/// Supports Flashcard Flip, Context Cloze Deletion, Spelling Dictation, and Listening Dictation (听音拼写)
/// with real-time FSRS interval previews and KokoroSharp neural speech.
/// </summary>
public sealed class FsrsReviewArenaView : UserControl
{
    private readonly IVocabularyService _vocabularyService;
    private readonly ISpeechSynthesisService _speechService;
    private readonly AppSettingsStore _settingsStore;
    private readonly Action<string> _setStatus;

    private readonly ComboBox _modeCombo;
    private readonly ComboBox _deckCombo;
    private readonly TextBlock _progressText;
    private readonly StackPanel _cardStage;

    private IReadOnlyList<VocabularyEntry> _queue = [];
    private int _currentIndex;
    private bool _answerRevealed;

    public FsrsReviewArenaView(
        IVocabularyService vocabularyService,
        ISpeechSynthesisService speechService,
        AppSettingsStore settingsStore,
        Action<string> setStatus)
    {
        _vocabularyService = vocabularyService;
        _speechService = speechService;
        _settingsStore = settingsStore;
        _setStatus = setStatus;

        _modeCombo = new ComboBox
        {
            Width = 240,
            CornerRadius = new CornerRadius(9),
            ItemsSource = new[]
            {
                "闪卡主动回忆 (Flashcard)",
                "原句语境完形填空 (Cloze Context)",
                "看义拼写默写检验 (Spelling Quiz)",
                "盲听发音默写挑战 (Listening Dictation)"
            },
            SelectedIndex = 0
        };
        _modeCombo.SelectionChanged += (_, _) => RenderCard();

        _deckCombo = new ComboBox { Width = 200, CornerRadius = new CornerRadius(9) };
        _deckCombo.SelectionChanged += (_, _) => StartSession();

        _progressText = new TextBlock
        {
            FontSize = 13.5,
            FontWeight = FontWeight.SemiBold,
            Foreground = AppTheme.AccentBright,
            VerticalAlignment = VerticalAlignment.Center
        };

        var topBar = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,*,Auto"),
            ColumnSpacing = 12,
            Margin = new Thickness(28, 20, 28, 12),
            Children =
            {
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children =
                    {
                        new MdiIcon("brain", 20, AppTheme.AccentBright),
                        new TextBlock { Text = "训练模式:", VerticalAlignment = VerticalAlignment.Center, Foreground = AppTheme.TextSecondary },
                        _modeCombo
                    }
                },
                new StackPanel
                {
                    [Grid.ColumnProperty] = 1,
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children =
                    {
                        new TextBlock { Text = "训练词本:", VerticalAlignment = VerticalAlignment.Center, Foreground = AppTheme.TextSecondary },
                        _deckCombo
                    }
                },
                StudioUi.CreateSecondaryButton("refresh", "重载复习队列", StartSession),
                _progressText
            }
        };
        Grid.SetColumn(topBar.Children[2], 2);
        Grid.SetColumn(_progressText, 4);

        _cardStage = new StackPanel
        {
            MaxWidth = 820,
            Spacing = 16,
            Margin = new Thickness(28, 12, 28, 28),
            VerticalAlignment = VerticalAlignment.Center
        };

        Content = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            Children =
            {
                topBar,
                new ScrollViewer { [Grid.RowProperty] = 1, Content = _cardStage }
            }
        };
    }

    public void RefreshDecksAndStart()
    {
        var notebooks = _vocabularyService.GetNotebooks();
        var allOption = new VocabularyNotebook { Id = "all", Name = "全部生词本 (All Decks)" };
        var list = new[] { allOption }.Concat(notebooks).ToArray();
        var prevId = (_deckCombo.SelectedItem as VocabularyNotebook)?.Id ?? "all";
        _deckCombo.ItemsSource = list;
        _deckCombo.SelectedItem = list.FirstOrDefault(n => n.Id == prevId) ?? allOption;
        _deckCombo.ItemTemplate = new FuncDataTemplate<VocabularyNotebook>((n, _) => new TextBlock { Text = n?.Name });
        StartSession();
    }

    public void StartSession()
    {
        var deckId = (_deckCombo.SelectedItem as VocabularyNotebook)?.Id ?? "all";
        var due = _vocabularyService.GetDueQueue(deckId, maxCount: 50);
        if (due.Count == 0)
        {
            due = _vocabularyService.QueryEntries(new VocabularyQueryFilter
            {
                NotebookId = deckId,
                SortOrder = VocabularySortOrder.NextReviewDue
            });
        }
        _queue = due;
        _currentIndex = 0;
        _answerRevealed = false;
        RenderCard();
    }

    private void RenderCard()
    {
        _cardStage.Children.Clear();
        if (_queue.Count == 0 || _currentIndex >= _queue.Count)
        {
            _progressText.Text = "本轮记忆训练已完成 🎉";
            _cardStage.Children.Add(StudioUi.CreateHeroCard(new StackPanel
            {
                Spacing = 14,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(24, 34),
                Children =
                {
                    new MdiIcon("check-all", 52, AppTheme.Emerald),
                    new TextBlock
                    {
                        Text = "所有待复习卡片均已完成 FSRS 间隔调度！",
                        FontSize = 20,
                        FontWeight = FontWeight.Bold,
                        Foreground = AppTheme.TextPrimary,
                        HorizontalAlignment = HorizontalAlignment.Center
                    },
                    StudioUi.CreatePrimaryButton("refresh", "开启新一轮强化训练", StartSession)
                }
            }));
            return;
        }

        var entry = _queue[_currentIndex];
        _progressText.Text = $"训练进度：{_currentIndex + 1} / {_queue.Count}   ·   阶段：{UiLabels.Stage(entry.Stage)} (稳定度 S={entry.StabilityDays:F1}d)";

        var modeIndex = Math.Clamp(_modeCombo.SelectedIndex, 0, 3);
        var mode = modeIndex >= 2 ? ReviewQuizMode.SpellingDictation : (ReviewQuizMode)modeIndex;
        var cardStack = new StackPanel { Spacing = 16, Margin = new Thickness(10) };

        if (modeIndex == 0) // Flashcard
        {
            cardStack.Children.Add(new TextBlock
            {
                Text = entry.Headword,
                FontSize = 34,
                FontWeight = FontWeight.Bold,
                Foreground = AppTheme.TextPrimary,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            });
            var audioBar = PhonicsAudioBar.CreateWordPhonicsStrip(
                _speechService,
                _settingsStore,
                entry.Headword,
                entry.UsPhonetic,
                entry.UkPhonetic,
                entry.ContextSentence);
            audioBar.HorizontalAlignment = HorizontalAlignment.Center;
            cardStack.Children.Add(audioBar);
            if (!string.IsNullOrWhiteSpace(entry.ContextSentence))
            {
                cardStack.Children.Add(StudioUi.CreateInfoTile("捕获语境原句提示", entry.ContextSentence, "format-quote-close", AppTheme.AccentBright));
            }
        }
        else if (modeIndex == 1) // ClozeContext
        {
            var sentence = !string.IsNullOrWhiteSpace(entry.ContextSentence)
                ? entry.ContextSentence
                : entry.Examples.FirstOrDefault()?.SourceSentence ?? $"Context usage of [{entry.Headword}].";
            cardStack.Children.Add(new TextBlock { Text = "原句语境完形填空：请根据语境与释义回忆空缺词条", FontSize = 13, Foreground = AppTheme.Amber });
            cardStack.Children.Add(new TextBlock
            {
                Text = MaskHeadword(sentence, entry.Headword),
                FontSize = 22,
                FontWeight = FontWeight.SemiBold,
                Foreground = AppTheme.TextPrimary,
                TextWrapping = TextWrapping.Wrap
            });
            cardStack.Children.Add(new TextBlock
            {
                Text = $"核心词义：{entry.Translation}",
                FontSize = 15,
                Foreground = AppTheme.Emerald,
                TextWrapping = TextWrapping.Wrap
            });
        }
        else // Spelling Quiz or Listening Dictation (盲听发音默写)
        {
            var isListeningMode = modeIndex == 3;
            if (isListeningMode)
            {
                _ = _speechService.SpeakAsync(
                    entry.Headword,
                    SpeechAccent.AmericanEnglish,
                    entry.UsPhonetic,
                    _settingsStore.Current.SpeechSpeed,
                    _settingsStore.Current.PreferredUsVoiceId);
            }
            cardStack.Children.Add(new TextBlock
            {
                Text = isListeningMode
                    ? "盲听发音默写挑战：点击下方发音按钮听辨读音，输入你听到的英文词条并按 Enter 验证"
                    : "看义拼写默写：请输入对应英文词条并按 Enter 验证",
                FontSize = 13,
                Foreground = AppTheme.AccentBright
            });
            cardStack.Children.Add(PhonicsAudioBar.CreateWordPhonicsStrip(
                _speechService,
                _settingsStore,
                entry.Headword,
                entry.UsPhonetic,
                entry.UkPhonetic,
                entry.ContextSentence));
            if (!isListeningMode || _answerRevealed)
            {
                cardStack.Children.Add(new TextBlock
                {
                    Text = entry.Translation,
                    FontSize = 22,
                    FontWeight = FontWeight.Bold,
                    Foreground = AppTheme.Emerald,
                    TextWrapping = TextWrapping.Wrap
                });
            }
            var spellInput = new TextBox
            {
                PlaceholderText = "在此输入拼写并按 Enter 键检阅…",
                FontSize = 18,
                FontFamily = AppTheme.MonoFont,
                Background = AppTheme.InputBg,
                Foreground = AppTheme.TextPrimary,
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14, 10)
            };
            spellInput.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    var typed = (spellInput.Text ?? "").Trim();
                    var ok = string.Equals(typed, entry.Headword, StringComparison.OrdinalIgnoreCase);
                    _setStatus(ok ? $"✓ 拼写完全正确：{entry.Headword}" : $"✗ 拼写偏差，正确拼写为：{entry.Headword}");
                    _answerRevealed = true;
                    RenderCard();
                }
            };
            cardStack.Children.Add(spellInput);
        }

        if (!_answerRevealed)
        {
            var revealBtn = StudioUi.CreatePrimaryButton("eye-outline", "翻转揭晓完整释义与 AI 助记", () =>
            {
                _answerRevealed = true;
                RenderCard();
            });
            revealBtn.HorizontalAlignment = HorizontalAlignment.Center;
            cardStack.Children.Add(revealBtn);
        }
        else
        {
            cardStack.Children.Add(new Border { Height = 1, Background = AppTheme.BorderSubtle });
            cardStack.Children.Add(new TextBlock
            {
                Text = $"{entry.Headword}   —   {entry.Translation}",
                FontSize = 19,
                FontWeight = FontWeight.Bold,
                Foreground = AppTheme.Emerald,
                TextWrapping = TextWrapping.Wrap
            });
            if (!string.IsNullOrWhiteSpace(entry.EtymologyAndRoot))
                cardStack.Children.Add(StudioUi.CreateInfoTile("词源与词根拆解", entry.EtymologyAndRoot, "code-braces", AppTheme.Purple));
            if (!string.IsNullOrWhiteSpace(entry.MemoryMnemonic))
                cardStack.Children.Add(StudioUi.CreateInfoTile("AI 联想助记", entry.MemoryMnemonic, "lightbulb-on-outline", AppTheme.Amber));

            var intervals = SpacedRepetitionScheduler.PreviewIntervals(entry, DateTimeOffset.UtcNow);
            var gradeGrid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,*,*,*"),
                ColumnSpacing = 12,
                Margin = new Thickness(0, 8, 0, 0)
            };

            void Submit(ReviewRating r)
            {
                var updated = _vocabularyService.SubmitReview(entry.Id, r, mode);
                _setStatus($"已记录「{updated.Headword}」反馈 ({UiLabels.Rating(r)}) · 下次复习间隔已更新");
                _currentIndex++;
                _answerRevealed = false;
                RenderCard();
            }

            var g1 = CreateGradeButton("1 · 忘记重来 (Again)", SpacedRepetitionScheduler.FormatInterval(intervals.AgainInterval), AppTheme.DangerSoft, AppTheme.Danger, () => Submit(ReviewRating.Again));
            var g2 = CreateGradeButton("2 · 印象模糊 (Hard)", SpacedRepetitionScheduler.FormatInterval(intervals.HardInterval), AppTheme.AmberSoft, AppTheme.Amber, () => Submit(ReviewRating.Hard));
            var g3 = CreateGradeButton("3 · 认识良好 (Good)", SpacedRepetitionScheduler.FormatInterval(intervals.GoodInterval), AppTheme.AccentSoft, AppTheme.AccentBright, () => Submit(ReviewRating.Good));
            var g4 = CreateGradeButton("4 · 秒答熟练 (Easy)", SpacedRepetitionScheduler.FormatInterval(intervals.EasyInterval), AppTheme.EmeraldSoft, AppTheme.Emerald, () => Submit(ReviewRating.Easy));
            Grid.SetColumn(g1, 0);
            Grid.SetColumn(g2, 1);
            Grid.SetColumn(g3, 2);
            Grid.SetColumn(g4, 3);
            gradeGrid.Children.Add(g1);
            gradeGrid.Children.Add(g2);
            gradeGrid.Children.Add(g3);
            gradeGrid.Children.Add(g4);
            cardStack.Children.Add(gradeGrid);
        }

        _cardStage.Children.Add(StudioUi.CreateHeroCard(cardStack));
    }

    private static string MaskHeadword(string sentence, string headword)
    {
        var idx = sentence.IndexOf(headword, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return $"{sentence}  [ ________ ]";
        return sentence[..idx] + " [ ________ ] " + sentence[(idx + headword.Length)..];
    }

    private static Button CreateGradeButton(string title, string interval, IBrush bg, IBrush fg, Action onClick)
    {
        var btn = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Background = bg,
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12, 14),
            BorderThickness = new Thickness(0),
            Content = new StackPanel
            {
                Spacing = 4,
                HorizontalAlignment = HorizontalAlignment.Center,
                Children =
                {
                    new TextBlock { Text = title, FontSize = 13.5, FontWeight = FontWeight.Bold, Foreground = fg, HorizontalAlignment = HorizontalAlignment.Center },
                    new TextBlock { Text = $"下次：{interval}", FontSize = 11.5, Foreground = AppTheme.TextSecondary, HorizontalAlignment = HorizontalAlignment.Center }
                }
            }
        };
        btn.Click += (_, _) => onClick();
        return btn;
    }
}
