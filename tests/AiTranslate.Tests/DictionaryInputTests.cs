using AiTranslate.Ai;
using AiTranslate.Core;
using AiTranslate.Desktop;
using AiTranslate.Desktop.Controls;
using AiTranslate.Desktop.Views;
using AiTranslate.Speech;
using AiTranslate.Vocabulary;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace AiTranslate.Tests;

[Collection("Dictionary UI")]
public sealed class DictionaryInputTests
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions());

    [Fact]
    public async Task SelectionPopupStreamsIntoDraftAndIgnoresSupersededSnapshots()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(DictionaryInputTests));
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            await session.Dispatch(() =>
            {
                var dictionary = new PendingLookup();
                using var speech = new KokoroSpeechService(directory.FullName);
                var popup = new SelectionPopupWindow(dictionary, new VocabularyService(Path.Combine(directory.FullName, "words.json")),
                    speech, new AppSettingsStore(Path.Combine(directory.FullName, "settings.json")), (_, _) => { });
                try
                {
                    var capture = new CapturedTextEvent("draft", TranslationTriggerSource.SelectionPopup, new PointD(10, 10));
                    _ = popup.ShowAndTranslateAsync(capture);
                    var first = Assert.Single(dictionary.Requests);
                    Assert.Contains("正在起草译文…", VisibleText());
                    var preview = new DictionaryEntryResult
                    {
                        SourceText = "draft", PrimaryTranslation = "草稿", Senses = [new("n.", "尚待修改的初稿", "")]
                    };
                    first.Fields(preview);
                    Dispatcher.UIThread.RunJobs();
                    Assert.Contains("尚待修改的初稿", VisibleText());
                    Assert.Contains("正在整理释义…", VisibleText());

                    _ = popup.ShowAndTranslateAsync(capture with { Text = "note" });
                    first.Fields(preview);
                    first.Completion.SetResult(preview);
                    Dispatcher.UIThread.RunJobs();
                    Assert.DoesNotContain("草稿", VisibleText());
                    Assert.Contains("正在起草译文…", VisibleText());

                    dictionary.Requests[1].Completion.SetResult(preview with
                    {
                        Origin = DictionaryResultOrigin.Cache, SourceText = "note", PrimaryTranslation = "笔记", Senses = []
                    });
                    Dispatcher.UIThread.RunJobs();
                    Assert.Contains("笔记", VisibleText());
                    Assert.DoesNotContain("正在起草译文…", VisibleText());

                    string?[] VisibleText()
                    {
                        popup.UpdateLayout();
                        return popup.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible)
                            .Select(t => t.Text).ToArray();
                    }
                }
                finally { popup.Close(); }
            }, CancellationToken.None);
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public async Task ShiftEnterAddsLineAndEnterStartsStreamingTranslation()
    {
        await RunInStudio((window, view, dictionary, _) =>
        {
            var input = view.GetVisualDescendants().OfType<TextBox>().Single(b => b.AcceptsReturn);
            var send = view.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == "发送翻译");
            Assert.False(send.IsEnabled);
            Assert.True(input.Focus());
            window.KeyTextInput("high in rank or status;");
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.Shift);
            window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.Shift);
            window.KeyTextInput("higher in rank or status than others");
            var query = input.Text!;
            Assert.Equal("high in rank or status;\nhigher in rank or status than others", query.ReplaceLineEndings("\n"));
            Assert.Empty(dictionary.Requests);

            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            var request = Assert.Single(dictionary.Requests);
            Assert.Equal(query, request.Query);
            Assert.True(string.IsNullOrEmpty(input.Text));
            Assert.Equal("停止生成", AutomationProperties.GetName(send));
            request.Preview("地位");
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("地位", Messages(view));

            window.KeyTextInput("another word");
            request.Preview("地位较高");
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("地位较高", Messages(view));
            request.Complete("地位较高");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("another word", input.Text);
            Assert.Equal("发送翻译", AutomationProperties.GetName(send));
            Assert.True(send.IsEnabled);
            send.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("another word", dictionary.Requests[1].Query);
            window.UpdateLayout();
            Assert.DoesNotContain(query, Messages(view));
            Assert.DoesNotContain("地位较高", Messages(view));
            dictionary.Requests[1].Complete("另一个词");
        });
    }

    [Fact]
    public async Task OnlyCurrentLookupCanDisplayOrSaveItsResult()
    {
        await RunInStudio((window, view, dictionary, vocabulary) =>
        {
            var input = view.GetVisualDescendants().OfType<TextBox>().Single(b => b.AcceptsReturn);
            _ = view.LookupAsync("bank", "They picnicked on the bank.", TranslationTriggerSource.VocabularyReview, "阅读摘录");
            var first = dictionary.Requests[0];
            first.Complete("河岸");
            Dispatcher.UIThread.RunJobs();
            var save = view.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == "加入生词表");
            save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var saved = vocabulary.FindByHeadword("bank");
            Assert.Equal("They picnicked on the bank.", saved?.ContextSentence);
            Assert.Equal("阅读摘录", saved?.SourceTitle);

            _ = view.LookupAsync("second", null, TranslationTriggerSource.ClipboardMonitor, "剪贴板");
            var second = dictionary.Requests[1];
            second.Preview("部分译文");
            Dispatcher.UIThread.RunJobs();
            var stop = view.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == "停止生成");
            stop.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(second.Token.IsCancellationRequested);
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "已停止生成");
            input.Text = "unsent draft";
            second.Preview("迟到的预览");
            second.Fields(new DictionaryEntryResult { SourceText = "second", PrimaryTranslation = "迟到的义项" });
            view.DisplayResult(new DictionaryEntryResult { SourceText = "third", PrimaryTranslation = "第三" },
                TranslationTriggerSource.SelectionPopup, "划词");
            second.Complete("迟到的结果");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("unsent draft", input.Text);
            Assert.Equal(new[] { "third", "第三" }, Messages(view));
        });
    }

    [Fact]
    public async Task RetryingFailedLookupKeepsSourceContextAndUnsentDraft()
    {
        await RunInStudio((window, view, dictionary, vocabulary) =>
        {
            const string context = "The news brought her happiness.";
            _ = view.LookupAsync("happiness", context, TranslationTriggerSource.SelectionPopup, "阅读");
            var first = Assert.Single(dictionary.Requests);
            var input = view.GetVisualDescendants().OfType<TextBox>().Single(b => b.AcceptsReturn);
            input.Text = "next word";
            first.Completion.SetException(new AiException("HTTP 503"));
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            view.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == "重新查询")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var retry = dictionary.Requests[1];
            Assert.Equal(("happiness", context, first.Language), (retry.Query, retry.Context, retry.Language));
            retry.Complete("幸福");
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("幸福", Messages(view));
            Assert.Equal("next word", input.Text);
        });
    }

    private static string?[] Messages(DictionaryStudioView view) =>
        view.GetVisualDescendants().OfType<SelectableTextBlock>().Where(b => b.IsEffectivelyVisible).Select(b => b.Text ?? b.Inlines?.Text).ToArray();

    [Theory]
    [InlineData(1200, 760)]
    [InlineData(788, 740)]
    public async Task TranslationAndSourceShareTheComposerReadingColumn(double workspaceWidth, double expectedWidth)
    {
        await RunInStudio((window, view, _, _) =>
        {
            window.Width = workspaceWidth;
            var entry = new DictionaryEntryResult
            {
                ItemType = VocabularyItemType.Paragraph, SourceText = "One paragraph. Another sentence.",
                PrimaryTranslation = "一段文字。另一个句子。"
            };
            view.DisplayResult(entry, TranslationTriggerSource.ManualInput, "");
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var readers = view.GetVisualDescendants().OfType<DictionaryReadingText>().ToArray();
            var translation = readers.Single(r => (r.Text ?? r.Inlines?.Text) == entry.PrimaryTranslation);
            var source = readers.Single(r => (r.Text ?? r.Inlines?.Text) == entry.SourceText);
            var composer = view.GetVisualDescendants().OfType<DictionaryComposer>().Single();
            var columnLeft = composer.TranslatePoint(default, window)!.Value.X;
            foreach (var reader in new[] { translation, source })
            {
                Assert.Equal(expectedWidth, reader.Bounds.Width);
                Assert.Equal(columnLeft, reader.TranslatePoint(default, window)!.Value.X);
            }
            Assert.Equal(expectedWidth, composer.Bounds.Width);
            Assert.True(source.TranslatePoint(default, window)!.Value.Y >
                translation.TranslatePoint(new Point(0, translation.Bounds.Height), window)!.Value.Y);
        });
    }

    [Fact]
    public async Task LinkedLookupKeepsContextAndReturnsToTheSameReadingPosition()
    {
        await RunInStudio((window, view, dictionary, vocabulary) =>
        {
            var input = view.GetVisualDescendants().OfType<TextBox>().Single(b => b.AcceptsReturn);
            input.Text = "尚未发送的草稿";
            const string sentence = "A power cut interrupted the experiment.";
            var parent = new DictionaryEntryResult
            {
                SourceText = "First, a problem. " + sentence, PrimaryTranslation = "先是出了问题。停电打断了实验。",
                ItemType = VocabularyItemType.Paragraph, DetectedSourceLanguage = "en",
                Collocations = [new("power cut", "停电")],
                UsageNotes = string.Join("\n", Enumerable.Repeat("实验记录", 30))
            };
            view.DisplayResult(parent, TranslationTriggerSource.ScreenshotOcr, "实验报告");
            window.UpdateLayout();
            var scroll = view.GetVisualDescendants().OfType<ScrollViewer>().First();
            scroll.Offset = new Vector(0, 100);
            void Click(string name) => view.GetVisualDescendants().OfType<Button>()
                .Single(b => AutomationProperties.GetName(b) == name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Click("查词 power cut");
            var first = Assert.Single(dictionary.Requests);
            Assert.Equal(("power cut", sentence, "zh-Hans"), (first.Query, first.Context, first.Language));
            first.Complete("停电");
            Dispatcher.UIThread.RunJobs();
            Click("加入生词表");
            var saved = vocabulary.FindByHeadword("power cut");
            Assert.Equal(sentence, saved?.ContextSentence);
            Assert.Equal("实验报告", saved?.SourceTitle);
            Click("返回上一级");
            window.UpdateLayout();
            Assert.Equal(100, scroll.Offset.Y);
            Assert.Equal("尚未发送的草稿", input.Text);
            Assert.Single(dictionary.Requests);

            Click("查词 power cut");
            var pending = dictionary.Requests[1];
            Click("返回上一级");
            Assert.True(pending.Token.IsCancellationRequested);
            pending.Complete("迟到的联查结果");
            Dispatcher.UIThread.RunJobs();
            Assert.Contains(parent.PrimaryTranslation, Messages(view));
            Assert.DoesNotContain("迟到的联查结果", Messages(view));
            Assert.DoesNotContain(view.GetVisualDescendants().OfType<Button>(), b => AutomationProperties.GetName(b) == "返回上一级");
        });
    }

    [Fact]
    public async Task LookingUpSelectedTranslationReversesTheOriginalLanguagePair()
    {
        await RunInStudio((window, view, dictionary, _) =>
        {
            view.DisplayResult(new DictionaryEntryResult
            {
                SourceText = "昨夜は雨だった。", PrimaryTranslation = "It rained last night.",
                TargetLanguage = "en", DetectedSourceLanguage = "ja-JP", ItemType = VocabularyItemType.Sentence
            }, TranslationTriggerSource.SelectionPopup, "日语课");
            var translation = view.GetVisualDescendants().OfType<SelectableTextBlock>().Single(b => b.Text == "It rained last night.");
            translation.SelectionStart = 3;
            translation.SelectionEnd = 9;
            translation.ContextMenu!.Items.OfType<MenuItem>().Single(m => Equals(m.Header, "查询选中文本"))
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            var child = Assert.Single(dictionary.Requests);
            Assert.Equal(("rained", "It rained last night.", "ja"), (child.Query, child.Context, child.Language));
            child.Complete("雨が降った");
        });
    }

    [Fact]
    public async Task ComposerAndCapturedLookupsRespectAutomaticAndFixedDirections()
    {
        await RunInStudio((window, view, dictionary, vocabulary) =>
        {
            var mode = view.GetVisualDescendants().OfType<ComboBox>().Single(b => AutomationProperties.GetName(b) == "翻译方向");
            var input = view.GetVisualDescendants().OfType<TextBox>().Single(b => b.AcceptsReturn);
            Assert.Equal("auto", Assert.IsType<TranslationLanguage>(mode.SelectedItem).Id);
            input.Focus();
            window.KeyTextInput("明天见");
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Assert.Equal("en", Assert.Single(dictionary.Requests).Language);

            mode.SelectedItem = TranslationLanguages.Get("ja");
            _ = view.LookupAsync("明天见", null, TranslationTriggerSource.ClipboardMonitor, "剪贴板读取");
            Assert.Equal("ja", dictionary.Requests[1].Language);
            mode.SelectedIndex = 0;
            _ = view.LookupAsync("tomorrow", null, TranslationTriggerSource.ClipboardMonitor, "剪贴板读取");
            Assert.Equal("zh-Hans", dictionary.Requests[2].Language);
            foreach (var request in dictionary.Requests) request.Complete("译文");
        });
    }

    [Fact]
    public async Task RefreshUsesTheDisplayedLanguageAndKeepsThePreviousResultOnFailure()
    {
        await RunInStudio((window, view, dictionary, vocabulary) =>
        {
            view.DisplayResult(new DictionaryEntryResult
            {
                SourceText = "port", PrimaryTranslation = "港", TargetLanguage = "ja", Origin = DictionaryResultOrigin.Cache
            }, TranslationTriggerSource.ClipboardMonitor, "剪贴板");
            Dispatcher.UIThread.RunJobs();
            var refresh = view.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b)?.StartsWith("重新用 AI 翻译") == true);
            refresh.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var request = Assert.Single(dictionary.Requests);
            Assert.True(request.ForceRefresh);
            Assert.Equal("ja", request.Language);
            request.Completion.SetException(new AiException("离线"));
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("港", Messages(view));
            Assert.Contains(view.GetVisualDescendants().OfType<Button>(), b => AutomationProperties.GetName(b)?.StartsWith("重新用 AI 翻译") == true);
        });
    }

    [Fact]
    public async Task StreamingKeepsComposerInPlaceAndRespectsReadingPosition()
    {
        await RunInStudio((window, view, dictionary, vocabulary) =>
        {
            var send = view.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == "发送翻译");
            var sendPosition = send.TranslatePoint(default, window);
            var scroll = view.GetVisualDescendants().OfType<ScrollViewer>().First();
            _ = view.LookupAsync("a passage", null, TranslationTriggerSource.ManualInput, "");
            var request = Assert.Single(dictionary.Requests);
            var translation = string.Join("\n", Enumerable.Repeat("长段落中的一行译文。", 80));
            request.Preview(translation);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.True(scroll.Extent.Height > scroll.Viewport.Height);
            Assert.Equal(0, scroll.Offset.Y);
            Assert.Equal(sendPosition, send.TranslatePoint(default, window));

            scroll.Offset = new Vector(0, 120);
            window.UpdateLayout();
            request.Preview(translation + "\n最后一行。");
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.Equal(120, scroll.Offset.Y);
            request.Complete(translation);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(120, scroll.Offset.Y);
            _ = view.LookupAsync("new", null, TranslationTriggerSource.ManualInput, "");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(0, scroll.Offset.Y);
            dictionary.Requests[1].Complete("新的");
        });
    }

    [Fact]
    public async Task DictionarySectionsArriveDuringLookupAndPassagesHaveTheirOwnLayout()
    {
        await RunInStudio((window, view, dictionary, vocabulary) =>
        {
            _ = view.LookupAsync("bank", null, TranslationTriggerSource.ManualInput, "");
            var first = Assert.Single(dictionary.Requests);
            var word = new DictionaryEntryResult
            {
                SourceText = "bank", PrimaryTranslation = "银行；河岸", DetectedSourceLanguage = "en",
                Senses = [new("n.", "银行"), new("n.", "河岸"), new("v.", "把钱存入银行")],
                UsPhonetic = "/bæŋk/", UkPhonetic = "/bæŋk/"
            };
            first.Fields(word);
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("银行", Messages(view));
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "词性与释义");
            Assert.DoesNotContain(view.GetVisualDescendants().OfType<Button>(), b => AutomationProperties.GetName(b) == "加入生词表");

            var firstDefinition = view.GetVisualDescendants().OfType<SelectableTextBlock>().Single(b => b.Text == "银行");
            var pair = new CollocationItem("bank on", "指望", Example: new("We bank on", ""));
            first.Fields(word with { Collocations = [pair] });
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("We bank on", Messages(view));
            first.Fields(word with
            {
                Senses = [.. word.Senses, new("n.", "斜坡")],
                Collocations = [pair with { Example = new("We bank on you.", "我们指望") }]
            });
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("我们指望", Messages(view));
            Assert.DoesNotContain("We bank on", Messages(view));
            Assert.Same(firstDefinition, view.GetVisualDescendants().OfType<SelectableTextBlock>().Single(b => b.Text == "银行"));

            var input = view.GetVisualDescendants().OfType<TextBox>().Single(b => b.AcceptsReturn);
            input.Focus();
            window.KeyTextInput("take off");
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Assert.True(first.Token.IsCancellationRequested);
            var phrase = word with { SourceText = "take off", PrimaryTranslation = "起飞；脱下", ItemType = VocabularyItemType.Phrase };
            dictionary.Requests[1].Completion.SetResult(phrase);
            first.Completion.SetResult(word);
            Dispatcher.UIThread.RunJobs();
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "短语释义");
            Assert.DoesNotContain("bank", Messages(view));

            var passage = word with { SourceText = "First.\n\nSecond.", PrimaryTranslation = "第一段。\n\n第二段。",
                ItemType = VocabularyItemType.Paragraph, UsageNotes = "译注内容" };
            view.DisplayResult(passage, TranslationTriggerSource.ClipboardMonitor, "剪贴板");
            Dispatcher.UIThread.RunJobs();
            Assert.Contains(passage.PrimaryTranslation, Messages(view));
            Assert.Contains(passage.SourceText, Messages(view));
            Assert.Contains("译注内容", Messages(view));
            Assert.DoesNotContain("银行", Messages(view));
            Assert.DoesNotContain(view.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "词性与释义");
        });
    }

    [Fact]
    public async Task ExamplePracticeHidesReadingUntilRevealAndReturnsWithoutALookup()
    {
        await RunInStudio((window, view, dictionary, _) =>
        {
            var entry = new DictionaryEntryResult
            {
                SourceText = "go", PrimaryTranslation = "去",
                Examples = [new("We went home.", "我们回家了。", "went")]
            };
            view.DisplayResult(entry, TranslationTriggerSource.ManualInput, "");
            window.UpdateLayout();
            void Click(string label) => view.GetVisualDescendants().OfType<Button>().Single(b => b.IsEffectivelyVisible &&
                b.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == label)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            string?[] VisibleText() => view.GetVisualDescendants().OfType<TextBlock>()
                .Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? t.Inlines?.Text).ToArray();
            Click("自测");
            window.UpdateLayout();
            Assert.Contains("We _____ home.", VisibleText());
            Assert.DoesNotContain("go", VisibleText());
            Assert.DoesNotContain("我们回家了。", VisibleText());
            Click("揭晓答案");
            window.UpdateLayout();
            Assert.Contains("went", VisibleText());
            Assert.Contains("我们回家了。", VisibleText());
            Click("返回结果");
            window.UpdateLayout();
            Assert.Contains("go", VisibleText());
            Assert.Empty(dictionary.Requests);
        });
    }

    [Fact]
    public async Task SupplementKeepsTheOriginalVisibleAndCannotReplaceANewerLookup()
    {
        await RunInStudio((window, view, dictionary, vocabulary) =>
        {
            var entry = new DictionaryEntryResult
            {
                SourceText = "test", PrimaryTranslation = "检验", Collocations = [new("run a test", "进行检测")]
            };
            view.DisplayResult(entry, TranslationTriggerSource.ManualInput, "词典");
            window.UpdateLayout();
            var fill = view.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b)?.StartsWith("补全例句") == true);
            fill.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Contains("检验", Messages(view));
            view.DisplayResult(new DictionaryEntryResult { SourceText = "set", PrimaryTranslation = "放置" },
                TranslationTriggerSource.ManualInput, "词典");
            Assert.True(dictionary.SupplementToken.IsCancellationRequested);
            dictionary.Supplement.SetResult(entry);
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("放置", Messages(view));
            Assert.DoesNotContain("检验", Messages(view));
        });
    }

    [Fact]
    public async Task SupplementShowsTheNewExampleAndSavesItWithItsPhrase()
    {
        await RunInStudio((window, view, dictionary, vocabulary) =>
        {
            var original = new DictionaryEntryResult
            {
                SourceText = "adapt", PrimaryTranslation = "适应", Collocations = [new("adapt to", "适应")]
            };
            view.DisplayResult(original, TranslationTriggerSource.SelectionPopup, "课文");
            window.UpdateLayout();
            Button ButtonNamed(string name) => view.GetVisualDescendants().OfType<Button>()
                .Single(b => AutomationProperties.GetName(b) == name);
            view.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b)?.StartsWith("补全例句") == true)
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            dictionary.Supplement.SetResult(original with
            {
                Collocations = [original.Collocations[0] with { Example = new("We adapt to change.", "我们适应变化。", "adapt to") }]
            });
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("We adapt to change.", Messages(view));
            Assert.DoesNotContain(view.GetVisualDescendants().OfType<Button>(),
                b => AutomationProperties.GetName(b)?.StartsWith("补全例句") == true);
            ButtonNamed("加入生词表").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("我们适应变化。", vocabulary.FindByHeadword("adapt")?.Collocations[0].Example?.TranslatedSentence);
        });
    }

    [Fact]
    public async Task PickerAndBookmarkStayTogetherAndToggleOnlyTheSelectedBook()
    {
        await RunInStudio((window, view, _, vocabulary) =>
        {
            var book = vocabulary.CreateNotebook("这个很长的生词本名称用来检查最小窗口下的收录操作", "");
            var result = new DictionaryEntryResult { SourceText = "bank", PrimaryTranslation = "河岸" };
            view.DisplayResult(result, TranslationTriggerSource.ManualInput, "");
            window.UpdateLayout();
            var picker = view.GetVisualDescendants().OfType<ComboBox>().Single(c => AutomationProperties.GetName(c) == "收录到");
            var button = view.GetVisualDescendants().OfType<NotebookBookmarkButton>().Single();
            void Click(string action)
            {
                Assert.Equal(action, AutomationProperties.GetName(button));
                var label = Assert.Single(button.GetVisualDescendants().OfType<TextBlock>());
                window.MouseMove(new Point(0, 0));
                Assert.Equal(action, label.Text);
                var center = new Point(button.Bounds.Width / 2, button.Bounds.Height / 2);
                window.MouseMove(button.TranslatePoint(center, window)!.Value);
                Assert.Equal(action, label.Text);
                Assert.Contains(action == "取消收录" ? "ghost" : "primary", button.Classes);
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            Click("加入生词表");
            picker.SelectedItem = book;
            window.UpdateLayout();
            var pickerRight = picker.TranslatePoint(new Point(picker.Bounds.Width, picker.Bounds.Height / 2), window)!.Value;
            var buttonLeft = button.TranslatePoint(new Point(0, button.Bounds.Height / 2), window)!.Value;
            Assert.InRange(buttonLeft.X - pickerRight.X, 0, 8);
            Assert.InRange(Math.Abs(buttonLeft.Y - pickerRight.Y), 0, 1);
            Assert.True(buttonLeft.X + button.Bounds.Width <= window.Bounds.Width);
            Click("加入生词表");
            Click("取消收录");
            Assert.False(vocabulary.IsCollected(result, book.Id));
            Assert.True(vocabulary.IsCollected(result, "default"));
            view.RefreshNotebooks("default");
            Click("取消收录");
            Assert.False(vocabulary.IsCollected(result, "default"));
        });
    }

    [Fact]
    public async Task SavedWordGroupsDefinitionsAndFormsUnderFullWidthPartOfSpeechHeadings()
    {
        await RunInStudio((window, view, _, vocabulary) =>
        {
            var result = AiDictionaryService.ParseDictionaryResponse("light", "zh-Hans", """
                {"primaryTranslation":"光；灯；轻的","itemType":"Word","detectedSourceLanguage":"en",
                 "senses":[{"partOfSpeech":"n.","definitionZh":"光线","definitionEn":"visible illumination","grammarPattern":"不可数名词；常与 natural 连用"},
                    {"partOfSpeech":"adjective","definitionZh":"轻的","grammarLabel":"可分级"},
                    {"partOfSpeech":"noun","definitionZh":"灯具","grammarLabel":"可数"}],
                 "inflections":[{"label":"复数","value":"lights"},{"partOfSpeech":"adj.","label":"比较级","value":"lighter"},
                    {"partOfSpeech":"adj.","label":"最高级","value":"lightest"},{"label":"相关词","value":"lightness"}]}
                """);
            var card = vocabulary.AddOrUpdateFromDictionary(result);
            view.DisplayResult(card.ToDictionaryResult(), TranslationTriggerSource.VocabularyReview, "");
            window.UpdateLayout();
            var section = view.GetVisualDescendants().OfType<DictionarySection>().Single(s => s.Title == "词性与释义");
            var text = section.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? t.Inlines?.Text).ToList();
            Assert.Single(text, t => t == "名词");
            Assert.Equal("3 个义项", section.Caption.Text);
            string[] expected = ["名词", "光线", "不可数", "常与 natural 连用", "灯具", "可数", "复数", "lights",
                "形容词", "可分级", "比较级", "lighter", "最高级", "lightest", "轻的"];
            Assert.Equal(expected, text.Where(t => expected.Contains(t)));
            Assert.DoesNotContain("lightness", text);
            Assert.Single(Messages(view), t => t == "lights");
            Assert.Contains("lightness", Messages(view));
            double Left(string value) => section.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == value)
                .TranslatePoint(default, section)!.Value.X;
            Assert.True(Left("光线") > Left("名词"));
            Assert.Equal(Left("光线"), Left("visible illumination"), 1);
        });
    }

    [Fact]
    public async Task ReadingDetailsKeepTheirStateThroughStreamingAndLinkedLookup()
    {
        await RunInStudio((window, view, dictionary, vocabulary) =>
        {
            window.Width = 440;
            var entry = new DictionaryEntryResult
            {
                SourceText = "bank", PrimaryTranslation = "银行；河岸", DetectedSourceLanguage = "en",
                Senses = [new("n.", "银行", "a financial institution"), new("n.", "河岸", "the side of a river")],
                Collocations = [new("river bank", "河岸"), new("bank account", "银行账户",
                    Example: new("I opened a bank account.", "我开了一个银行账户。"))]
            };
            void Click(string name)
            {
                var button = view.GetVisualDescendants().OfType<Button>().Single(b => b.IsEffectivelyVisible &&
                    AutomationProperties.GetName(b) == name);
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.UpdateLayout();
            }
            _ = view.LookupAsync("bank", null, TranslationTriggerSource.ManualInput, "");
            var request = Assert.Single(dictionary.Requests);
            request.Fields(entry);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.Contains("河岸", Messages(view));
            Assert.DoesNotContain("the side of a river", Messages(view));
            Click("展开「河岸」的详解");
            Click("收起「银行」的详解");
            Click("展开「bank account」的例句");
            request.Completion.SetResult(entry);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Click("查词 bank account");
            Click("返回上一级");
            Assert.Contains("the side of a river", Messages(view));
            Assert.DoesNotContain("a financial institution", Messages(view));
            Assert.Contains("I opened a bank account.", Messages(view));
            Click("收起「bank account」的例句");
            Assert.DoesNotContain("I opened a bank account.", Messages(view));
        });
    }

    [Fact]
    public async Task UsageConditionsStayWithTheirExpressionsAndSurviveUnrelatedStreamingChanges()
    {
        await RunInStudio((window, view, dictionary, vocabulary) =>
        {
            var sense = new PartOfSpeechSense("n.", "咖啡", Scenario: "泛指饮品", GrammarLabel: "不可数")
            {
                UsageCases = [
                    new("泛指饮品", "不可数", "some coffee", "一些咖啡"),
                    new("在咖啡店按杯点单", "可数", "two coffees", "两杯咖啡")
                ]
            };
            var entry = new DictionaryEntryResult
            {
                SourceText = "coffee", PrimaryTranslation = "咖啡", Senses = [sense],
                Inflections = [new("复数", "coffees", "n.")]
            };
            window.Width = 440;
            _ = view.LookupAsync("coffee", null, TranslationTriggerSource.ManualInput, "");
            var lookup = Assert.Single(dictionary.Requests);
            lookup.Fields(entry);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var section = view.GetVisualDescendants().OfType<DictionarySection>().Single(s => s.Title == "词性与释义");
            TextBlock Find(string value) => section.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == value);
            var second = Find("two coffees");
            Assert.Equal(Find("some coffee").TranslatePoint(default, window)!.Value.X, second.TranslatePoint(default, window)!.Value.X, 1);
            Assert.True(Find("不可数").TranslatePoint(default, window)!.Value.Y >= Find("泛指饮品").TranslatePoint(default, window)!.Value.Y - 2);
            Assert.True(Find("在咖啡店按杯点单").TranslatePoint(default, window)!.Value.Y > Find("泛指饮品").TranslatePoint(default, window)!.Value.Y);
            Assert.InRange(second.TranslatePoint(new Point(second.Bounds.Width, 0), window)!.Value.X, 0, window.Bounds.Width);
            Assert.DoesNotContain("coffees", Messages(view));
            lookup.Fields(entry with { Senses = [sense with { UsageCases = sense.UsageCases.ToArray() }], UsageNotes = "点单时可说数量。" });
            Dispatcher.UIThread.RunJobs();
            Assert.Same(second, Find("two coffees"));
        });
    }

    [Fact]
    public async Task AlternatePastFormsAndSameSpellingRelationsDoNotImplyInterchangeableForms()
    {
        await RunInStudio((window, view, _, _) =>
        {
            var hang = new DictionaryEntryResult
            {
                SourceText = "hang", PrimaryTranslation = "悬挂；绞死",
                Senses = [new("v.", "悬挂"), new("v.", "绞死")],
                Inflections = [new("第三人称单数", "hangs"), new("过去式", "hung"), new("过去式", "hanged")]
            };
            void Show(DictionaryEntryResult entry)
            {
                view.DisplayResult(entry, TranslationTriggerSource.ManualInput, "");
                window.UpdateLayout();
            }
            string?[] SenseText() => view.GetVisualDescendants().OfType<DictionarySection>().Single(s => s.Title == "词性与释义")
                .GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? t.Inlines?.Text).ToArray();
            Show(hang);
            Assert.Contains("hangs", SenseText());
            Assert.DoesNotContain("hung", SenseText());
            Assert.Contains("hanged", Messages(view));
            Show(hang with
            {
                Senses = [hang.Senses[0] with { UsageCases = [new("悬挂物品", "过去式", "hung a picture")] },
                          hang.Senses[1] with { UsageCases = [new("指执行绞刑", "过去式", "they hanged the prisoner")] }]
            });
            Assert.Contains("hung a picture", SenseText());
            Assert.Contains("they hanged the prisoner", SenseText());
            Assert.DoesNotContain("hung", Messages(view));
            Assert.DoesNotContain("hanged", Messages(view));

            Show(new DictionaryEntryResult
            {
                SourceText = "complete", PrimaryTranslation = "完整的；彻底的",
                Senses = [new("adj.", "完备的", GrammarLabel: "可分级"), new("adj.", "彻底的", GrammarLabel: "通常不分级")],
                Inflections = [new("比较级", "more complete", "adj.")]
            });
            Assert.DoesNotContain("more complete", SenseText());
            Assert.Contains("more complete", Messages(view));

            var experience = new DictionaryEntryResult
            {
                SourceText = "experience", PrimaryTranslation = "经验；经历",
                Senses = [new("n.", "工作经验", GrammarLabel: "不可数"), new("n.", "一次经历", GrammarLabel: "可数"), new("n.", "使用体验")],
                Inflections = [new("复数", "experiences", "n.")]
            };
            Show(experience);
            Assert.DoesNotContain("experiences", SenseText());
            Assert.Contains("experiences", Messages(view));
            Show(experience with
            {
                Senses = [experience.Senses[0], experience.Senses[1] with { UsageCases = [new("谈几次经历", "可数", "two experiences")] }]
            });
            Assert.Contains("two experiences", SenseText());
            Assert.DoesNotContain("experiences", Messages(view));

            Show(new DictionaryEntryResult
            {
                SourceText = "fast", PrimaryTranslation = "快",
                Senses = [new("adj.", "快速的"), new("adv.", "快速地", Example: new("They run fast.", "他们跑得很快。", "fast"))],
                WordFamily = [new("快速地", "fast", "adv.", "sameForm", "直接修饰动作", "run fast"),
                    new("牢固地", "fast", "adv.", "sameForm", "说明保持不松开", "hold fast")]
            });
            var text = SenseText();
            Assert.True(Array.IndexOf(text, "与词头同形") > Array.IndexOf(text, "副词"));
            Assert.Contains("直接修饰动作", text);
            Assert.Contains(text, t => t?.Contains("牢固地", StringComparison.Ordinal) == true);
            Assert.DoesNotContain("run fast", text);
            Assert.DoesNotContain(view.GetVisualDescendants().OfType<Button>(), b => AutomationProperties.GetName(b) == "查词 fast");
        });
    }

    private static async Task RunInStudio(Action<Window, DictionaryStudioView, PendingLookup, VocabularyService> run)
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(DictionaryInputTests));
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            await session.Dispatch(() =>
            {
                var dictionary = new PendingLookup();
                using var speech = new KokoroSpeechService(directory.FullName);
                var vocabulary = new VocabularyService(Path.Combine(directory.FullName, "words.json"));
                var view = new DictionaryStudioView(dictionary, vocabulary,
                    speech, new AppSettingsStore(Path.Combine(directory.FullName, "settings.json")),
                    _ => { });
                var window = new Window { Content = view, Width = 788, Height = 560 };
                window.Show();
                try
                {
                    run(window, view, dictionary, vocabulary);
                }
                finally
                {
                    window.Close();
                }
            }, CancellationToken.None);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private sealed class PendingLookup : IAiDictionaryService
    {
        public List<Request> Requests { get; } = [];
        public TaskCompletionSource<DictionaryEntryResult> Supplement { get; } = new();
        public Task<DictionaryEntryResult> CheckLexicalCoverageAsync(DictionaryEntryResult entry, string? context, CancellationToken cancellationToken) =>
            CompleteExamplesAsync(entry, context, cancellationToken);
        public CancellationToken SupplementToken { get; private set; }
        public Task<DictionaryEntryResult> CompleteExamplesAsync(DictionaryEntryResult entry, string? context, CancellationToken cancellationToken)
        {
            SupplementToken = cancellationToken;
            return Supplement.Task;
        }

        public Task<DictionaryEntryResult> LookupOrTranslateAsync(string sourceText, string targetLanguageId,
            string? contextSentence, Action<string>? onStreamingPreview, CancellationToken cancellationToken,
            Action<DictionaryEntryResult>? onEntryPreview = null, bool forceRefresh = false)
        {
            var request = new Request(sourceText, onStreamingPreview!, cancellationToken, onEntryPreview!, targetLanguageId, forceRefresh, contextSentence);
            Requests.Add(request);
            return request.Completion.Task;
        }
    }

    private sealed record Request(string Query, Action<string> Preview, CancellationToken Token, Action<DictionaryEntryResult> Fields,
        string Language, bool ForceRefresh, string? Context)
    {
        public TaskCompletionSource<DictionaryEntryResult> Completion { get; } = new();
        public void Complete(string translation) =>
            Completion.SetResult(new DictionaryEntryResult { SourceText = Query, PrimaryTranslation = translation });
    }
}
