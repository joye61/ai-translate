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
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
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
    public async Task SelectionPopupStreamsWithSkeletonAndIgnoresSupersededSnapshots()
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
                var settingsPath = Path.Combine(directory.FullName, "settings.json");
                File.WriteAllText(settingsPath, """{"autoAddLookedUpWordsToNotebook":true,"dictionaryReaderLanguage":"de"}""");
                var popup = new SelectionPopupWindow(dictionary, vocabulary, speech, new AppSettingsStore(settingsPath), (_, _, _, _) => { });
                var editor = new TextBox { Text = "a selected word" };
                var sourceWindow = new Window { Content = editor };
                sourceWindow.Show();
                editor.Focus();
                editor.SelectionStart = 2;
                editor.SelectionEnd = 10;
                try
                {
                    var workArea = popup.Screens.Primary!.WorkingArea;
                    var capture = new CapturedTextEvent(" ✨“draft,” ", TranslationTriggerSource.SelectionPopup,
                        new PointD(workArea.Right - 12, workArea.Bottom - 12), "阅读器");
                    _ = popup.ShowAndTranslateAsync(capture with { Text = "，✨" });
                    Assert.Empty(dictionary.Requests);
                    Assert.False(popup.IsVisible);
                    _ = popup.ShowAndTranslateAsync(capture);
                    var first = Assert.Single(dictionary.Requests);
                    Assert.Equal("draft", first.Query);
                    Assert.Contains("draft", VisibleText());
                    _ = popup.ShowAndTranslateAsync(capture with { Text = "，✨" });
                    Assert.False(first.Token.IsCancellationRequested);
                    Assert.False(popup.ShowActivated);
                    Assert.True(editor.IsFocused);
                    Assert.Equal("selected", editor.SelectedText);
                    Assert.Single(popup.GetVisualDescendants().OfType<LoadingSkeleton>());
                    Dispatcher.UIThread.RunJobs();
                    popup.UpdateLayout();
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    Dispatcher.UIThread.RunJobs();
                    AssertInsideWorkingArea();
                    var header = popup.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "SelectionPopupHeader");
                    foreach (var point in new[]
                    {
                        new Point(header.Bounds.Width / 2, 4), // top padding
                        new Point(4, header.Bounds.Height / 2), // side padding
                        new Point(header.Bounds.Width / 2, header.Bounds.Height - 4) // below buttons
                    })
                    {
                        var actual = popup.InputHitTest(header.TranslatePoint(point, popup)!.Value);
                        Assert.Same(header, actual);
                    }
                    if (OperatingSystem.IsWindows())
                    {
                        var start = header.TranslatePoint(new Point(header.Bounds.Width / 2, 6), popup)!.Value;
                        var before = popup.Position;
                        popup.MouseDown(start, MouseButton.Left);
                        popup.MouseMove(start - new Point(40, 0), RawInputModifiers.LeftMouseButton);
                        popup.MouseUp(start, MouseButton.Left);
                        Assert.True(popup.Position.X < before.X);
                        AssertInsideWorkingArea();
                    }
                    var preview = new DictionaryEntryResult
                    {
                        SourceText = "draft", DetectedSourceLanguage = "en", PrimaryTranslation = "草稿", LiteralTranslation = "起草的文本",
                        Senses = [new("n.", "尚待修改的初稿", "")]
                    };
                    first.Fields(new DictionaryEntryResult { SourceText = "draft", PrimaryTranslation = "草" });
                    Dispatcher.UIThread.RunJobs();
                    Assert.Contains("草", VisibleText());
                    var bookmark = popup.GetVisualDescendants().OfType<NotebookBookmarkButton>().Single();
                    Assert.False(bookmark.IsEnabled);
                    first.Usable(preview with { Senses = [], LiteralTranslation = "" });
                    first.Fields(preview);
                    Dispatcher.UIThread.RunJobs();
                    FinishFrames();
                    Assert.Equal(["草稿", "尚待修改的初稿", "自然拼读"],
                        VisibleText().Where(t => t is "草稿" or "尚待修改的初稿" or "自然拼读"));
                    Assert.Contains("直译对照", VisibleText());
                    Assert.Contains(preview.LiteralTranslation, VisibleText());
                    Assert.DoesNotContain("正在整理释义…", VisibleText());
                    Assert.False(first.Completion.Task.IsCompleted);
                    Assert.Empty(popup.GetVisualDescendants().OfType<LoadingSkeleton>());
                    Assert.True(bookmark.IsEnabled);
                    Assert.Null(vocabulary.FindByHeadword("draft"));
                    bookmark.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.Contains("取消收录", VisibleText());
                    var saved = vocabulary.FindByHeadword("draft");
                    Assert.NotNull(saved);
                    Assert.Equal("草稿", saved.Translation);
                    Assert.Equal("阅读器", saved.SourceTitle);
                    var reviewed = vocabulary.SubmitReview(saved.Id, ReviewRating.Good);

                    first.Fields(preview with { Senses = [new("n.", "草稿"), new("noun", "征兵"), new("n.", "气流"), new("v.", "起草")] });
                    Dispatcher.UIThread.RunJobs();
                    FinishFrames();
                    Assert.Contains("草稿；征兵；气流", VisibleText());
                    Assert.Contains("起草", VisibleText());
                    AssertInsideWorkingArea();

                    _ = popup.ShowAndTranslateAsync(capture with { Text = "note" });
                    Assert.False(first.Token.IsCancellationRequested);
                    first.Usable(preview);
                    first.Fields(preview);
                    first.Completion.SetResult(preview with
                    {
                        SuggestedTags = ["写作"], Examples = [new("Read the draft.", "读一下草稿。")]
                    });
                    Dispatcher.UIThread.RunJobs();
                    Assert.DoesNotContain("草稿", VisibleText());
                    Assert.Contains("note", VisibleText());
                    Assert.False(bookmark.IsEnabled);
                    var reopened = new VocabularyService(Path.Combine(directory.FullName, "words.json"));
                    var enriched = reopened.FindByHeadword("draft");
                    Assert.NotNull(enriched);
                    Assert.Equal(saved.Id, enriched.Id);
                    Assert.Equal("写作", Assert.Single(enriched.Tags));
                    Assert.Equal("Read the draft.", enriched.ContextSentence);
                    Assert.Equal("阅读器", enriched.SourceTitle);
                    Assert.Equal(preview.Senses, enriched.Senses);
                    Assert.Equal(1, enriched.LookupCount);
                    Assert.Equal(reviewed.NextReviewAt, enriched.NextReviewAt);
                    Assert.Equal(reviewed.LastReviewedAt, enriched.LastReviewedAt);

                    var note = preview with { SourceText = "note", PrimaryTranslation = "笔记", LiteralTranslation = "", Senses = [] };
                    dictionary.Requests[1].Usable(note);
                    Dispatcher.UIThread.RunJobs();
                    Assert.True(bookmark.IsEnabled);
                    bookmark.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    bookmark.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    dictionary.Requests[1].Completion.SetResult(note with { Origin = DictionaryResultOrigin.Cache });
                    Dispatcher.UIThread.RunJobs();
                    Assert.Contains("笔记", VisibleText());
                    Assert.Empty(popup.GetVisualDescendants().OfType<LoadingSkeleton>());
                    Assert.DoesNotContain("直译对照", VisibleText());
                    Assert.Null(vocabulary.FindByHeadword("note"));
                    bookmark.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.Equal(TranslationTriggerSource.SelectionPopup, vocabulary.FindByHeadword("note")?.CaptureSource);

                    void FinishFrames()
                    {
                        var body = popup.GetVisualDescendants().OfType<SelectionPopupBody>().Single();
                        for (var i = 0; i < 60; i++) body.AdvanceFrame(TimeSpan.FromMilliseconds(16));
                    }

                    void AssertInsideWorkingArea() => Assert.True(workArea.Contains(new PixelRect(
                        popup.Position, PixelSize.FromSize(popup.ClientSize, popup.RenderScaling))));

                    string?[] VisibleText()
                    {
                        popup.UpdateLayout();
                        return popup.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible)
                            .Select(t => t.Text).ToArray();
                    }
                }
                finally { popup.Close(); sourceWindow.Close(); }
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
            window.KeyTextInput("，✨");
            Dispatcher.UIThread.RunJobs();
            Assert.False(send.IsEnabled);
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Assert.Empty(dictionary.Requests);
            input.Clear();
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
            view.GetVisualDescendants().OfType<DictionaryTranslations>().Single().AdvanceFrame(TimeSpan.FromMilliseconds(100));
            Assert.Contains("地位", Messages(view));

            window.KeyTextInput("“another word，”");
            request.Preview("地位较高");
            Dispatcher.UIThread.RunJobs();
            view.GetVisualDescendants().OfType<DictionaryTranslations>().Single().AdvanceFrame(TimeSpan.FromMilliseconds(100));
            Assert.Contains("地位较高", Messages(view));
            request.Complete("地位较高");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("“another word，”", input.Text);
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

    [Theory]
    [InlineData(788, 1.5)]
    [InlineData(1200, 1.0)]
    public async Task WrappedDraftClearsTheScrollbarAndKeepsItsReadingWidth(double workspaceWidth, double scale)
    {
        await RunInStudio((window, view, _, _) =>
        {
            window.Width = workspaceWidth;
            window.SetRenderScaling(scale);
            var composer = view.GetVisualDescendants().OfType<DictionaryComposer>().Single();
            var input = composer.GetVisualDescendants().OfType<TextBox>().Single(b => b.AcceptsReturn);
            var text = input.GetVisualDescendants().OfType<TextPresenter>().Single();
            var bar = input.GetVisualDescendants().OfType<ScrollBar>().Single(b => b.Name == "PART_VerticalScrollBar");
            input.Focus();
            input.Text = "A short draft.";
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.False(bar.IsVisible);
            var shortWidth = text.Bounds.Width;

            input.Text = string.Join(" ", Enumerable.Repeat("A longer sentence wraps at the right edge of the composer.", 100));
            input.CaretIndex = input.Text.Length;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.True(bar.IsVisible);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            var thumb = bar.GetVisualDescendants().OfType<Thumb>().Single();
            var point = thumb.TranslatePoint(new Point(thumb.Bounds.Width / 2, thumb.Bounds.Height / 2), window)!.Value;
            bar.ShowDelay = TimeSpan.Zero;
            window.MouseMove(point);
            Dispatcher.UIThread.RunJobs();
            Assert.True(bar.IsExpanded, $"Pointer at {point} hit {window.InputHitTest(point)}");
            var left = text.TranslatePoint(default, composer)!.Value.X;
            var right = left + text.Bounds.Width;
            var track = bar.TranslatePoint(default, composer)!.Value.X;
            Assert.Equal(shortWidth, text.Bounds.Width);
            Assert.Equal(left, composer.Bounds.Width - right, 3);
            Assert.True(right <= track + 0.001);
            Assert.InRange(composer.Bounds.Width - track - bar.Bounds.Width, 0, 2);
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
            window.UpdateLayout();
            var at = translation.TranslatePoint(translation.TextLayout.HitTestTextRange(5, 1).First().Center, window)!.Value;
            window.MouseDown(at, MouseButton.Right);
            window.MouseUp(at, MouseButton.Right);
            var child = Assert.Single(dictionary.Requests);
            Assert.Equal(("rained", "It rained last night.", "ja"), (child.Query, child.Context, child.Language));
            child.Complete("雨が降った");
        });
    }

    [Fact]
    public async Task RightClickQueriesTheSelectionInAnOrdinarySenseDefinition()
    {
        await RunInStudio((window, view, dictionary, _) =>
        {
            const string definition = "条件相对简单的舱位等级";
            view.DisplayResult(new DictionaryEntryResult
            {
                SourceText = "economy class", PrimaryTranslation = "经济舱",
                DetectedSourceLanguage = "en", Senses = [new("n.", definition)]
            }, TranslationTriggerSource.ManualInput, "");
            window.UpdateLayout();
            var sense = view.GetVisualDescendants().OfType<SelectableTextBlock>().Single(b => b.Text == definition);
            sense.BringIntoView();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Point At(int i) => sense.TranslatePoint(sense.TextLayout.HitTestTextRange(i, 1).First().Center, window)!.Value;
            var begin = At(2);
            var end = At(6);
            Assert.Same(sense, window.InputHitTest(begin));
            window.MouseDown(begin, MouseButton.Left);
            window.MouseMove(end, RawInputModifiers.LeftMouseButton);
            window.MouseUp(end, MouseButton.Left);
            var selected = sense.SelectedText;
            Assert.Contains("简单", selected);
            var point = At(4);
            window.MouseDown(point, MouseButton.Right);
            window.MouseUp(point, MouseButton.Right);
            var child = Assert.Single(dictionary.Requests);
            Assert.Equal((selected, definition), (child.Query, child.Context));
            Assert.False(sense.ContextFlyout?.IsOpen == true);
            child.Complete("simple");
        });
    }

    [Theory]
    [InlineData("对比", "zh-CN", "zh-Hans")]
    [InlineData("比べる", "ja-JP", "ja")]
    public async Task EachTranslationOpensItsOwnReverseLookupAndCanReturn(string query, string source, string reverse)
    {
        await RunInStudio((window, view, dictionary, _) =>
        {
            var entry = new DictionaryEntryResult
            {
                SourceText = query, PrimaryTranslation = "compare; compare with; contrast",
                TargetLanguage = "en", DetectedSourceLanguage = source,
                TranslationOptions = [new("compare", "比较", "考察异同"), new("compare with", "与……比较", "明确比较对象"),
                    new("contrast", "对照", "突出差异")]
            };
            view.DisplayResult(entry, TranslationTriggerSource.ManualInput, "");
            foreach (var option in entry.TranslationOptions)
            {
                window.UpdateLayout();
                var link = view.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == $"查词 {option.Text}");
                link.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                VerifyLookup(option.Text);

                var heading = view.GetVisualDescendants().OfType<DictionaryReadingText>().Single(b => b.Inlines?.Text == entry.PrimaryTranslation);
                var index = entry.PrimaryTranslation.IndexOf(option.Text, StringComparison.Ordinal);
                var point = heading.TranslatePoint(heading.TextLayout.HitTestTextRange(index, 1).First().Center, window)!.Value;
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                var previousCount = dictionary.Requests.Count;
                window.MouseDown(point, MouseButton.Left);
                window.MouseUp(point, MouseButton.Left);
                Assert.True(dictionary.Requests.Count > previousCount, $"Click on {option.Text} at {point} hit {window.InputHitTest(point)}; selection '{heading.SelectedText}'");
                VerifyLookup(option.Text);
            }
            Assert.Equal(6, dictionary.Requests.Count);

            void VerifyLookup(string expected)
            {
                var request = dictionary.Requests[^1];
                Assert.Equal((expected, reverse, ""), (request.Query, request.Language, request.Context));
                request.Complete("译义");
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                view.GetVisualDescendants().OfType<Button>().Single(b => b.GetVisualDescendants().OfType<TextBlock>()
                    .Any(t => t.Text == "返回上一级")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.UpdateLayout();
            }
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
            var translations = view.GetVisualDescendants().OfType<DictionaryTranslations>().Single();
            for (var frame = 0; frame < 60; frame++) translations.AdvanceFrame(TimeSpan.FromMilliseconds(16));
            window.UpdateLayout();
            Assert.True(scroll.Extent.Height > scroll.Viewport.Height);
            Assert.Equal(0, scroll.Offset.Y);
            Assert.Equal(sendPosition, send.TranslatePoint(default, window));

            scroll.Offset = new Vector(0, 120);
            window.UpdateLayout();
            request.Preview(translation + "\n最后一行。");
            Dispatcher.UIThread.RunJobs();
            for (var frame = 0; frame < 16; frame++)
            {
                translations.AdvanceFrame(TimeSpan.FromMilliseconds(16));
                window.UpdateLayout();
                Assert.Equal(120, scroll.Offset.Y);
                Assert.Equal(sendPosition, send.TranslatePoint(default, window));
            }
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

            var senses = view.GetVisualDescendants().OfType<DictionarySection>().Single(s => s.Title == "词性与释义");
            var firstDefinition = senses.GetVisualDescendants().OfType<SelectableTextBlock>().Single(b => b.Text == "银行");
            var pair = new CollocationItem("bank on", "指望", Example: new("We bank on", ""));
            first.Fields(word with { Collocations = [pair] });
            Dispatcher.UIThread.RunJobs();
            Assert.DoesNotContain("We bank on", Messages(view));
            view.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == "展开「bank on」的例句")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Contains("We bank on", Messages(view));
            first.Fields(word with
            {
                Senses = [.. word.Senses, new("n.", "斜坡")],
                Collocations = [pair with { Example = new("We bank on you.", "我们指望") }]
            });
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("我们指望", Messages(view));
            Assert.DoesNotContain("We bank on", Messages(view));
            Assert.Same(firstDefinition, senses.GetVisualDescendants().OfType<SelectableTextBlock>().Single(b => b.Text == "银行"));

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
    public async Task ExpandedCollocationShowsBothSentencesWithOnlyReadAloud()
    {
        await RunInStudio((window, view, dictionary, _) =>
        {
            var entry = new DictionaryEntryResult
            {
                SourceText = "good", PrimaryTranslation = "好的", DetectedSourceLanguage = "en",
                Collocations = [new("be good at", "擅长", "表示能力",
                    new("He is good at solving problems under pressure.", "他擅长在压力下解决问题。", "is good at"))]
            };
            view.DisplayResult(entry, TranslationTriggerSource.ManualInput, "");
            window.UpdateLayout();
            Assert.DoesNotContain("他擅长在压力下解决问题。", Messages(view));
            view.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == "展开「be good at」的例句")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.UpdateLayout();
            var source = view.GetVisualDescendants().OfType<DictionaryReadingText>()
                .Single(t => t.Inlines?.Text == entry.Collocations[0].Example!.SourceSentence);
            var example = source.GetVisualAncestors().OfType<Grid>().First();
            Assert.True(example.IsEffectivelyVisible);
            Assert.Contains(example.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "他擅长在压力下解决问题。");
            var read = Assert.Single(example.GetVisualDescendants().OfType<Button>());
            Assert.Contains(read.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "跟读");
            Assert.Empty(dictionary.Requests);
        });
    }

    [Fact]
    public async Task ReviewButtonsBelongToFinishedSectionsAndKeepTheReadingInPlace()
    {
        await RunInStudio((window, view, dictionary, _) =>
        {
            var entry = new DictionaryEntryResult { SourceText = "coffee", PrimaryTranslation = "咖啡",
                DetectedSourceLanguage = "en", Senses = [new("n.", "咖啡")], UsageNotes = "按杯点单时可数。",
                LiteralTranslation = "咖啡饮品" };
            view.DisplayResult(entry, TranslationTriggerSource.ManualInput, "");
            window.UpdateLayout();
            var sections = view.GetVisualDescendants().OfType<DictionarySection>().Where(s => s.IsEffectivelyVisible).ToArray();
            Assert.True(sections.Length >= 3);
            foreach (var section in sections)
                Assert.Equal($"智能复核 · {section.Title}", AutomationProperties.GetName(Assert.IsType<Button>(section.ActionSlot.Content)));
            Assert.Contains(view.GetVisualDescendants().OfType<Button>(), b => AutomationProperties.GetName(b) == "智能复核 · 直译对照");
            var target = sections.Single(s => s.Title == "词性与释义");
            var button = (Button)target.ActionSlot.Content!;
            var input = view.GetVisualDescendants().OfType<SuggestingTextBox>().Single();
            var flow = view.GetVisualDescendants().OfType<FlowingBorderEffect>().Single();
            input.Text = "下一个词";
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(EntryBlock.Senses, dictionary.ReviewedBlock);
            Assert.True(input.IsEffectivelyEnabled);
            Assert.True(flow.IsVisible);
            Assert.All(sections, s => Assert.False(((Button)s.ActionSlot.Content!).IsEnabled));
            dictionary.ReviewPreview!(entry with { Senses = [new("n.", "预览中的释义")], LiteralTranslation = "其他栏目的草稿" });
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.Contains("预览中的释义", Messages(view));
            Assert.Contains("咖啡饮品", Messages(view));
            Assert.DoesNotContain("其他栏目的草稿", Messages(view));
            dictionary.Supplement.SetResult(entry);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.DoesNotContain("预览中的释义", Messages(view));
            Assert.True(input.IsEffectivelyEnabled);
            Assert.Equal("下一个词", input.Text);
            Assert.False(flow.IsVisible);
            Assert.True(button.IsEnabled);
            Assert.Contains(button.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "无需调整");
            Assert.Same(button, target.ActionSlot.Content);
        });
    }

    [Theory]
    [InlineData(DictionaryStageState.Completed, "核对整条")]
    [InlineData(DictionaryStageState.Failed, "补全并核对")]
    public async Task WholeEntryReviewShowsActivityAndCanBeStopped(DictionaryStageState extension, string label)
    {
        await RunInStudio((window, view, dictionary, _) =>
        {
            var entry = new DictionaryEntryResult
            {
                SourceText = "economy", PrimaryTranslation = "经济",
                Processing = new() { Intent = DictionaryLookupIntent.Learning, Extension = extension, Review = DictionaryStageState.Failed }
            };
            view.DisplayResult(entry, TranslationTriggerSource.ManualInput, "");
            window.UpdateLayout();
            Button ButtonNamed(string name) => view.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == name);
            ButtonNamed(label).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var input = view.GetVisualDescendants().OfType<SuggestingTextBox>().Single();
            var flow = view.GetVisualDescendants().OfType<FlowingBorderEffect>().Single();
            Assert.True(input.IsEffectivelyEnabled);
            Assert.True(flow.IsVisible);
            Assert.Contains("核对", input.PlaceholderText);
            ButtonNamed("停止复核").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(dictionary.SupplementToken.IsCancellationRequested);
            Assert.False(flow.IsVisible);
            dictionary.Supplement.SetResult(entry with { PrimaryTranslation = "迟到的修改" });
            Dispatcher.UIThread.RunJobs();
            Assert.DoesNotContain("迟到的修改", Messages(view));
        });
    }

    [Fact]
    public async Task SendingADraftDuringBlockReviewSupersedesTheReview()
    {
        await RunInStudio((window, view, dictionary, _) =>
        {
            var entry = new DictionaryEntryResult { SourceText = "economy", PrimaryTranslation = "经济", Senses = [new("n.", "经济")] };
            view.DisplayResult(entry, TranslationTriggerSource.ManualInput, "");
            window.UpdateLayout();
            var review = view.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == "智能复核 · 词性与释义");
            review.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var input = view.GetVisualDescendants().OfType<SuggestingTextBox>().Single();
            input.Focus();
            input.Text = "cabin";
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Assert.True(dictionary.SupplementToken.IsCancellationRequested);
            var next = Assert.Single(dictionary.Requests);
            Assert.Equal("cabin", next.Query);
            dictionary.ReviewPreview!(entry with { Senses = [new("n.", "迟到的预览")] });
            dictionary.Supplement.SetResult(entry);
            Dispatcher.UIThread.RunJobs();
            var flow = view.GetVisualDescendants().OfType<FlowingBorderEffect>().Single();
            Assert.True(flow.IsVisible);
            next.Complete("客舱");
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("客舱", Messages(view));
            Assert.False(flow.IsVisible);
        });
    }

    [Theory]
    [InlineData(808, true)]
    [InlineData(620, false)]
    public async Task PairedTranslationsKeepEqualTypeAndSeparatePageTools(double width, bool sideBySide)
    {
        await RunInStudio((window, view, _, _) =>
        {
            window.Width = width;
            var entry = new DictionaryEntryResult
            {
                SourceText = "a series of pleasant thoughts about something you would prefer to be doing",
                ItemType = VocabularyItemType.Phrase, PrimaryTranslation = "对自己更愿意去做的事所产生的一连串愉快想象",
                LiteralTranslation = "关于某件你更愿意做的事情的一系列愉快想法"
            };
            view.DisplayResult(entry, TranslationTriggerSource.ManualInput, "");
            window.UpdateLayout();
            var group = view.GetVisualDescendants().OfType<DictionaryTranslations>().Single();
            var natural = group.GetVisualDescendants().OfType<DictionaryReadingText>().Single();
            var literal = group.GetVisualDescendants().OfType<SelectableTextBlock>().Single(t => t.Text == entry.LiteralTranslation);
            Assert.False(natural.HasLinkedTerms);
            Assert.Equal((natural.FontSize, natural.FontWeight, natural.Foreground, natural.LineHeight),
                (literal.FontSize, literal.FontWeight, literal.Foreground, literal.LineHeight));
            var first = natural.TranslatePoint(default, window)!.Value;
            var second = literal.TranslatePoint(default, window)!.Value;
            if (sideBySide)
            {
                Assert.Equal(first.Y, second.Y, 2);
                Assert.True(first.X + natural.Bounds.Width <= second.X);
            }
            else
            {
                Assert.Equal(first.X, second.X, 2);
                Assert.True(first.Y + natural.Bounds.Height < second.Y);
            }
            var hint = view.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "选中正文后右键查词");
            Assert.True(hint.TranslatePoint(default, group)!.Value.Y > group.Bounds.Height);
        });
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task TranslationReviewStreamsOnlyItsReadingAndDropsLateFrames(bool natural, bool fail)
    {
        await RunInStudio((window, view, dictionary, _) =>
        {
            var entry = new DictionaryEntryResult { SourceText = "kick the bucket", PrimaryTranslation = "去世",
                LiteralTranslation = "踢桶", Senses = [new("v.", "去世")] };
            view.DisplayResult(entry, TranslationTriggerSource.ManualInput, "");
            window.UpdateLayout();
            var button = view.GetVisualDescendants().OfType<Button>()
                .Single(b => AutomationProperties.GetName(b) == (natural ? "智能复核 · 自然译文" : "智能复核 · 直译对照"));
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(natural ? EntryBlock.PrimaryTranslation : EntryBlock.LiteralTranslation, dictionary.ReviewedBlock);
            var draft = entry with { PrimaryTranslation = "离开人", LiteralTranslation = "踢那个" };
            dictionary.ReviewPreview!(draft);
            Dispatcher.UIThread.RunJobs();
            Assert.Contains(natural ? "离开人" : "踢那个", Messages(view));
            Assert.DoesNotContain(natural ? "踢那个" : "离开人", Messages(view));
            if (fail) dictionary.Supplement.SetException(new AiException("连接中断"));
            else dictionary.Supplement.SetResult(natural ? entry with { PrimaryTranslation = "离开人世" } : entry with { LiteralTranslation = "踢那个桶" });
            Dispatcher.UIThread.RunJobs();
            dictionary.ReviewPreview!(draft);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.Contains(natural ? fail ? "去世" : "离开人世" : fail ? "踢桶" : "踢那个桶", Messages(view));
            Assert.Contains(natural ? entry.LiteralTranslation : entry.PrimaryTranslation, Messages(view));
            Assert.DoesNotContain(natural ? "离开人" : "踢那个", Messages(view));
            Assert.True(view.GetVisualDescendants().OfType<SuggestingTextBox>().Single().IsEffectivelyEnabled);
            Assert.Equal(fail, view.GetVisualDescendants().OfType<DictionaryTranslations>().Single()
                .GetVisualDescendants().OfType<TextBlock>().Any(t => t.IsEffectivelyVisible && t.Text?.Contains("连接中断") == true));
            if (fail)
            {
                Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "连接中断。已保留原内容。");
                Assert.Contains(button.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "重新复核");
            }
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
            Assert.DoesNotContain("We adapt to change.", Messages(view));
            ButtonNamed("展开「adapt to」的例句").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
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
            Assert.DoesNotContain("visible illumination", Messages(view));
            view.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == "展开「光线」的详解")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
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
            Assert.DoesNotContain("a financial institution", Messages(view));
            Assert.DoesNotContain("the side of a river", Messages(view));
            Click("展开「河岸」的详解");
            Click("展开「银行」的详解");
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
    public async Task ReadingTablesFitLongLabelsAndReflowHintsWhenTheWindowNarrows()
    {
        await RunInStudio((window, view, _, _) =>
        {
            const string condition = "指某人或某语言的词汇总量";
            const string phrase = "build/expand your vocabulary";
            var entry = new DictionaryEntryResult
            {
                SourceText = "vocabulary", PrimaryTranslation = "词汇",
                Senses = [new("n.", "词汇") { UsageCases = [
                    new(condition, "不可数名词", "a large vocabulary", "词汇量大"),
                    new("指专门的词语表或小词表", "可数名词", "a vocabulary of technical terms", "一份术语表")] }],
                Collocations = [new(phrase, "扩大词汇量", "词汇总量"),
                    new("technical vocabulary", "专业词汇", "某领域用语", new("Learn technical vocabulary.", "学习专业词汇。"))]
            };
            view.DisplayResult(entry, TranslationTriggerSource.VocabularyReview, "");
            void Resize(double width) { window.Width = width; window.UpdateLayout(); }
            TextBlock Find(string text) => view.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == text);
            Point Position(TextBlock text) => text.TranslatePoint(default, window)!.Value;
            Resize(960);
            var meaning = Find("扩大词汇量");
            var hint = Find("词汇总量");
            var term = Find(phrase);
            Assert.Single(Find(condition).TextLayout.TextLines);
            Assert.Single(term.TextLayout.TextLines);
            Assert.Equal(Position(meaning).Y, Position(hint).Y, 1);
            Assert.True(Position(meaning).X < Position(hint).X);
            Assert.Equal(Position(meaning).X, Position(Find("专业词汇")).X, 1);
            Resize(355);
            Assert.True(Position(hint).Y > Position(meaning).Y);
            Assert.Equal(Position(meaning).X, Position(hint).X, 1);
            Assert.InRange(hint.TranslatePoint(new Point(hint.Bounds.Width, 0), window)!.Value.X, 0, window.Width);
            Resize(960);
            Assert.Same(hint, Find("词汇总量"));
            Assert.Equal(Position(meaning).Y, Position(hint).Y, 1);
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
            Assert.DoesNotContain("two coffees", Messages(view));
            view.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == "展开「咖啡」的详解")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
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

    [Theory]
    [InlineData(788, 1.0)]
    [InlineData(440, 1.5)]
    public async Task OnlyVisibleSuggestionsAreAcceptedAndTabDoesNotSubmit(double width, double scale)
    {
        await RunInStudioAsync(async (window, view, dictionary, _) =>
        {
            window.Width = width;
            window.SetRenderScaling(scale);
            var input = view.GetVisualDescendants().OfType<SuggestingTextBox>().Single();
            var presenter = input.GetVisualDescendants().OfType<TextPresenter>().Single();
            var first = new TaskCompletionSource<string?>();
            var started = new TaskCompletionSource();
            dictionary.Suggest = (prefix, _) =>
            {
                if (prefix != "g") return Task.FromResult(prefix == "go" ? "good" : null);
                started.TrySetResult();
                return first.Task;
            };
            input.Focus();
            window.KeyTextInput("g");
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            window.KeyTextInput("o");
            var preview = input.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Inlines?.Count == 2);
            async Task WaitForPreview()
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while (!preview.IsVisible) await Task.Delay(10, timeout.Token);
                window.UpdateLayout();
            }
            void Key(PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
            {
                window.KeyPressQwerty(key, modifiers);
                window.KeyReleaseQwerty(key, modifiers);
            }
            await WaitForPreview();
            first.SetResult("garden");
            await Task.Yield();
            Assert.Equal("go\u200Bod", preview.Inlines!.Text);
            Assert.Equal("go", input.Text);
            var ghostStart = preview.TextLayout.HitTestTextPosition(3).TopLeft;
            var caret = presenter.TextLayout.HitTestTextPosition(2).TopLeft;
            Assert.Equal(presenter.TranslatePoint(caret, input), preview.TranslatePoint(ghostStart, input));
            window.KeyTextInput("t");
            Assert.False(preview.IsVisible);
            Assert.Equal("got", input.Text);
            Key(PhysicalKey.Backspace);
            await WaitForPreview();
            presenter.PreeditText = "拼";
            Assert.False(preview.IsVisible);
            presenter.PreeditText = null;
            await WaitForPreview();
            Key(PhysicalKey.ArrowLeft);
            Assert.False(preview.IsVisible);
            Key(PhysicalKey.End);
            await WaitForPreview();
            window.KeyTextInput("o");
            Assert.True(preview.IsVisible);
            Key(PhysicalKey.Tab);
            Assert.True(input.IsFocused);
            Assert.Equal("good", input.Text);
            Assert.Equal(4, input.CaretIndex);
            Assert.Empty(dictionary.Requests);
            Key(PhysicalKey.Enter);
            Assert.Equal("good", Assert.Single(dictionary.Requests).Query);
            Assert.True(dictionary.CacheOnly);
            window.KeyTextInput("unknown");
            Key(PhysicalKey.Tab);
            Assert.False(input.IsFocused);
            Assert.Equal("unknown", input.Text);
        });
    }

    [Theory]
    [InlineData(788, "good")]
    [InlineData(440, "gem")]
    public async Task BackspacingKeepsTheMatchingSuggestionVisibleWhileReranking(int width, string winner)
    {
        await RunInStudioAsync(async (window, view, dictionary, _) =>
        {
            window.Width = width;
            var input = view.GetVisualDescendants().OfType<SuggestingTextBox>().Single();
            var preview = input.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Inlines?.Count == 2);
            var lookup = new TaskCompletionSource<string?>();
            var started = new TaskCompletionSource();
            dictionary.Suggest = (prefix, _) =>
            {
                if (prefix is "goo" or "go") return Task.FromResult<string?>("good");
                started.TrySetResult();
                return lookup.Task;
            };
            input.Focus();
            window.KeyTextInput("goo");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!preview.IsVisible) await Task.Delay(10, timeout.Token);
            var previewWasHidden = false;
            preview.PropertyChanged += (_, e) =>
            {
                if (e.Property == Visual.IsVisibleProperty && !preview.IsVisible) previewWasHidden = true;
            };
            void Press(PhysicalKey key)
            {
                window.KeyPressQwerty(key, RawInputModifiers.None);
                window.KeyReleaseQwerty(key, RawInputModifiers.None);
            }
            Press(PhysicalKey.Backspace);
            Assert.True(preview.IsVisible);
            Assert.Equal("go\u200Bod", preview.Inlines!.Text);
            Press(PhysicalKey.Backspace);
            Assert.True(preview.IsVisible);
            Assert.Equal("g\u200Bood", preview.Inlines.Text);
            await started.Task.WaitAsync(timeout.Token);
            Assert.True(preview.IsVisible);
            lookup.SetResult(winner);
            await Task.Yield();
            Assert.False(previewWasHidden);
            Assert.Equal("g\u200B" + winner[1..], preview.Inlines.Text);
            Press(PhysicalKey.Tab);
            Assert.Equal(winner, input.Text);
            Press(PhysicalKey.Backspace);
            Assert.True(preview.IsVisible);
            Assert.Equal(winner[..^1] + "\u200B" + winner[^1], preview.Inlines.Text);
            Assert.Empty(dictionary.Requests);
        });
    }

    private static Task RunInStudio(Action<Window, DictionaryStudioView, PendingLookup, VocabularyService> run) =>
        RunInStudioAsync((w, v, d, s) => { run(w, v, d, s); return Task.CompletedTask; });

    [Fact]
    public async Task WrappingTheSuggestionKeepsTheTypedWordInPlace()
    {
        await RunInStudioAsync(async (window, view, dictionary, _) =>
        {
            window.Width = 440;
            var input = view.GetVisualDescendants().OfType<SuggestingTextBox>().Single();
            var presenter = input.GetVisualDescendants().OfType<TextPresenter>().Single();
            dictionary.Suggest = (prefix, _) => Task.FromResult<string?>(prefix + "odness");
            input.Focus();
            // Place a partial word where the prefix fits but the whole word must wrap.
            var prefix = "go";
            for (var i = 0; i < 100; i++)
            {
                input.Text = prefix;
                input.CaretIndex = prefix.Length;
                window.UpdateLayout();
                if (presenter.Bounds.Width - presenter.TextLayout.HitTestTextPosition(prefix.Length).X < 35) break;
                prefix = "a " + prefix;
            }
            var preview = input.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Inlines?.Count == 2);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!preview.IsVisible) await Task.Delay(10, timeout.Token);
            window.UpdateLayout();
            Assert.True(preview.TextLayout.TextLines.Count > presenter.TextLayout.TextLines.Count);
            var last = prefix.Length - 1;
            Assert.Equal(presenter.TranslatePoint(presenter.TextLayout.HitTestTextPosition(last).TopLeft, input),
                preview.TranslatePoint(preview.TextLayout.HitTestTextPosition(last).TopLeft, input));
        });
    }

    private static async Task RunInStudioAsync(Func<Window, DictionaryStudioView, PendingLookup, VocabularyService, Task> run)
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(DictionaryInputTests));
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            await session.Dispatch(async () =>
            {
                System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo("zh-CN");
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
                    await run(window, view, dictionary, vocabulary);
                }
                finally
                {
                    window.Close();
                }
                return true;
            }, CancellationToken.None);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private sealed class PendingLookup : IAiDictionaryService
    {
        public Task<DictionaryEntryResult> ReviewEntryAsync(DictionaryEntryResult entry, CancellationToken token,
            Action<DictionaryEntryResult>? onPreview = null) => CompleteExamplesAsync(entry, entry.LookupContext, token);
        public Func<string, CancellationToken, Task<string?>> Suggest { get; set; } = (_, _) => Task.FromResult<string?>(null);
        public Task<string?> FindSuggestionAsync(string prefix, string targetMode, CancellationToken cancellationToken) => Suggest(prefix, cancellationToken);
        public bool CacheOnly { get; private set; }
        public List<Request> Requests { get; } = [];
        public TaskCompletionSource<DictionaryEntryResult> Supplement { get; } = new();
        public EntryBlock? ReviewedBlock { get; private set; }
        public Action<DictionaryEntryResult>? ReviewPreview { get; private set; }
        public Task<DictionaryEntryResult> ReviewBlockAsync(DictionaryEntryResult entry, EntryBlock block, CancellationToken cancellationToken,
            Action<DictionaryEntryResult>? onPreview = null)
        {
            ReviewPreview = onPreview;
            ReviewedBlock = block;
            return CompleteExamplesAsync(entry, entry.LookupContext, cancellationToken);
        }
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
            Action<DictionaryEntryResult>? onEntryPreview = null, bool forceRefresh = false, bool cacheOnly = false,
            Action<DictionaryEntryResult>? onUsableEntry = null, DictionaryLookupIntent intent = DictionaryLookupIntent.Learning)
        {
            CacheOnly = cacheOnly;
            var request = new Request(sourceText, onStreamingPreview!, cancellationToken, onEntryPreview!, targetLanguageId, forceRefresh, contextSentence)
                { Usable = onUsableEntry! };
            Requests.Add(request);
            return request.Completion.Task;
        }
    }

    private sealed record Request(string Query, Action<string> Preview, CancellationToken Token, Action<DictionaryEntryResult> Fields,
        string Language, bool ForceRefresh, string? Context)
    {
        public Action<DictionaryEntryResult> Usable { get; init; } = null!;
        public TaskCompletionSource<DictionaryEntryResult> Completion { get; } = new();
        public void Complete(string translation) =>
            Completion.SetResult(new DictionaryEntryResult { SourceText = Query, PrimaryTranslation = translation });
    }
}
