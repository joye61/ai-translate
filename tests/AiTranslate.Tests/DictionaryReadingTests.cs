using AiTranslate.Core;
using AiTranslate.Ai;
using AiTranslate.Desktop;
using AiTranslate.Desktop.Controls;
using AiTranslate.Speech;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Xunit;

namespace AiTranslate.Tests;

[Collection("Dictionary UI")]
public sealed class DictionaryReadingTests
{
    [Fact]
    public async Task NotebookRendersThroughTheDictionaryBlocksAndFiltersStaleTranslationAnchors()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(DictionaryInputTests));
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            await session.Dispatch(() =>
            {
                using var speech = new KokoroSpeechService(directory.FullName);
                var settings = new AppSettingsStore(Path.Combine(directory.FullName, "settings.json"));
                var sample = new BilingualExample("I stopped working.", "我停止了工作。", "stopped working");
                var word = new VocabularyEntry
                {
                    Id = "past", Headword = "stopped", Translation = "停下了", SourceLanguage = "en",
                    Senses = [new("v.", "停止", "to cease", Example: sample)], Examples = [sample, new("It stopped.", "")],
                    Collocations = [new("stop short", "突然停下", "强调动作中断")],
                    GrammarOrUsageNote = "后接动名词", Synonyms = ["cease"], Antonyms = ["continue"],
                    WordFamily = [new("名词", "stoppage")], UsageContrasts = [new("stop doing", "停止原来的事", "stop to do", "停下来做另一件事")]
                };
                var texts = Render(word);
                Assert.Single(texts, t => t.Contains(sample.SourceSentence, StringComparison.Ordinal));
                Assert.Contains(texts, t => t.Contains("to cease", StringComparison.Ordinal));
                Assert.Contains(texts, t => t.Contains("强调动作中断", StringComparison.Ordinal));
                Assert.Contains("后接动名词", texts);
                Assert.Contains(texts, t => t.Contains("continue", StringComparison.Ordinal));
                Assert.Contains("It stopped.", texts);
                Assert.Contains(texts, t => t.Contains("stoppage", StringComparison.Ordinal));
                Assert.Contains(texts, t => t.Contains("stop to do", StringComparison.Ordinal));

                var sentence = new VocabularyEntry
                {
                    Id = "s", Headword = "It stopped.", Translation = "它停下了。", ItemType = VocabularyItemType.Sentence,
                    TranslationDecisions = [new("stopped", "停下了", "保留结束的意味。"), new("stopped", "停下过", "已失效的旧解释")]
                };
                texts = Render(sentence);
                Assert.Contains(texts, t => t.Contains("保留结束的意味", StringComparison.Ordinal));
                Assert.DoesNotContain(texts, t => t.Contains("已失效", StringComparison.Ordinal));

                string[] Render(VocabularyEntry entry)
                {
                    var details = new DictionaryDetails(speech, settings, (_, _, _) => { });
                    details.ShowEntry(entry.ToDictionaryResult(), complete: true);
                    var window = new Window { Content = new ScrollViewer { Content = details } };
                    window.Show();
                    try
                    {
                        window.UpdateLayout();
                        return details.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible)
                            .Select(t => t.Text ?? t.Inlines?.Text ?? "").ToArray();
                    }
                    finally { window.Close(); }
                }
            }, CancellationToken.None);
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task RelatedWordShowsTheReasonForItsLinkAndOpensWithItsOwnUsageContext()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(DictionaryInputTests));
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            await session.Dispatch(() =>
            {
                Assert.True(BuiltInShowcaseLexicon.TryGet("hard", out var entry));
                var related = Assert.Single(entry.WordFamily);
                using var speech = new KokoroSpeechService(directory.FullName);
                var settings = new AppSettingsStore(Path.Combine(directory.FullName, "settings.json"));
                var opened = new List<(string Text, string Context)>();
                var details = new DictionaryDetails(speech, settings, (text, context, _) => opened.Add((text, context)));
                var window = new Window { Width = 420, Height = 560, Content = new ScrollViewer { Content = details } };
                window.Show();
                try
                {
                    details.ShowEntry(entry, complete: true);
                    window.UpdateLayout();
                    var block = details.GetVisualDescendants().OfType<DictionarySection>().Single(s => s.Title == "关联词与用法");
                    var labels = block.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? t.Inlines?.Text);
                    Assert.Contains("副词", labels);
                    Assert.Contains("易混词", labels);
                    Assert.Contains("几乎不", labels);
                    Assert.Contains(related.Expression, labels);
                    var button = Assert.Single(block.GetVisualDescendants().OfType<Button>());
                    button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.Equal(("hardly", related.Expression), Assert.Single(opened));
                }
                finally { window.Close(); }
            }, CancellationToken.None);
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task LocalSelectionProtectionPreservesTheSelectionAndOnlyFiltersInjectedCopy()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(DictionaryInputTests));
        await session.Dispatch(async () =>
        {
            var text = TextStyles.Selectable("literature review", TextRole.Body);
            text.Background = Avalonia.Media.Brushes.Transparent;
            var editor = new TextBox { Text = "editable words" };
            var panel = new StackPanel { Background = AppTheme.WindowBg, Children = { text, editor } };
            var window = new Window { Width = 340, Height = 200, Content = panel };
            var gestures = new List<Window?>();
            var injected = true;
            using var privacy = new WindowsSelectionPrivacy(gestures.Add, () => injected);
            window.Show();
            try
            {
                window.UpdateLayout();
                window.MouseDown(new Point(5, 160), MouseButton.Left);
                window.MouseUp(new Point(5, 160), MouseButton.Left);
                Assert.Empty(gestures);
                var start = text.TextLayout.HitTestTextRange(0, 1).First().Center;
                var end = text.TextLayout.HitTestTextRange(8, 1).First().Center;
                window.MouseDown(start, MouseButton.Left);
                window.MouseMove(end, RawInputModifiers.LeftMouseButton);
                window.MouseUp(end, MouseButton.Left);
                Assert.Equal([window, null], gestures);
                Assert.False(string.IsNullOrWhiteSpace(text.SelectedText));
                var clipboard = window.Clipboard!;
                foreach (var control in new Control[] { text, editor })
                {
                    control.Focus();
                    if (control == editor) editor.SelectAll();
                    else text.SelectAll();
                    await clipboard.SetTextAsync("previous copy");
                    injected = true;
                    PressCopy();
                    Assert.Equal("previous copy", await clipboard.TryGetTextAsync());
                    injected = false;
                    PressCopy();
                    Assert.Equal(control == editor ? editor.Text : text.Text, await clipboard.TryGetTextAsync());
                }

                void PressCopy()
                {
                    window.KeyPressQwerty(PhysicalKey.C, RawInputModifiers.Control);
                    window.KeyReleaseQwerty(PhysicalKey.C, RawInputModifiers.Control);
                }
            }
            finally { window.Close(); }
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ClickingAwayClearsTextSelectionWithoutInterruptingReadingOrEditing()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(DictionaryInputTests));
        await session.Dispatch(() =>
        {
            var first = TextStyles.Selectable("one two three", TextRole.Body);
            var queries = new List<string>();
            var second = new DictionaryReadingText("four five six", (text, _) => queries.Add(text), ["six"]);
            first.Background = second.Background = Avalonia.Media.Brushes.Transparent;
            var editor = new TextBox { Text = "editable text" };
            var panel = new StackPanel
            {
                Background = AppTheme.WindowBg, Children = { first, second, editor }
            };
            var window = new Window { Content = panel, Width = 420, Height = 220 };
            window.Show();
            try
            {
                window.UpdateLayout();
                Point At(SelectableTextBlock block, int index) => block.TranslatePoint(
                    block.TextLayout.HitTestTextRange(index, 1).First().Center, window)!.Value;
                void Click(Point point)
                {
                    window.MouseDown(point, MouseButton.Left);
                    window.MouseUp(point, MouseButton.Left);
                }
                var blank = new Point(160, 180);
                window.MouseDown(At(first, 0), MouseButton.Left);
                window.MouseMove(At(first, 6), RawInputModifiers.LeftMouseButton);
                window.MouseUp(At(first, 6), MouseButton.Left);
                Assert.False(string.IsNullOrWhiteSpace(first.SelectedText));
                Click(blank);
                Assert.True(string.IsNullOrEmpty(first.SelectedText));

                first.Focus();
                first.SelectAll();
                Click(At(second, 11));
                Assert.True(string.IsNullOrEmpty(first.SelectedText));
                Assert.Equal(["six"], queries);

                second.SelectAll();
                var point = At(second, 3);
                window.MouseDown(point, MouseButton.Right);
                window.MouseUp(point, MouseButton.Right);
                var menu = second.ContextMenu!;
                Assert.True(menu.IsOpen);
                Assert.Equal("four five six", second.SelectedText);
                var copy = (MenuItem)menu.Items[1]!;
                Assert.True(copy.IsEnabled);
                var find = (MenuItem)menu.Items[0]!;
                var menuRoot = TopLevel.GetTopLevel(find)!;
                menuRoot.UpdateLayout();
                var menuPoint = find.TranslatePoint(new Point(find.Bounds.Width / 2, find.Bounds.Height / 2), menuRoot)!.Value;
                menuRoot.MouseDown(menuPoint, MouseButton.Left);
                menuRoot.MouseUp(menuPoint, MouseButton.Left);
                Assert.Equal(["six", "four five six"], queries);

                editor.Focus();
                editor.SelectAll();
                Click(blank);
                Assert.Equal("", editor.SelectedText);
                Assert.Equal("editable text", editor.Text);
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task WrappedPassageLinksOnlyItsTermsAndLeavesOrdinaryTextSelectable()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(DictionaryInputTests));
        await session.Dispatch(() =>
        {
            const string target = "请先检查运行时设置，再验证命令是否正确。\n如果运行时出错，请检查日志。";
            var entry = new DictionaryEntryResult
            {
                SourceText = "Check the runtime settings.", PrimaryTranslation = target, ItemType = VocabularyItemType.Paragraph,
                Collocations = [new("runtime", "运行时"), new("run", "运行"), new("invalid", target)]
            };
            var queries = new List<(string, string)>();
            var reader = DictionaryReadingText.Primary(entry, (term, context) => queries.Add((term, context)));
            var window = new Window { Content = reader, Width = 160, Height = 320 };
            window.Show();
            try
            {
                window.UpdateLayout();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                var links = reader.Inlines!.OfType<Run>().Where(r => r.TextDecorations?.Count > 0).ToArray();
                Assert.Equal(["运行时", "运行时"], links.Select(r => r.Text));
                void ClickAt(int index)
                {
                    var local = reader.TextLayout.HitTestTextRange(index, 1).First().Center;
                    var point = reader.TranslatePoint(local, window)!.Value;
                    window.MouseDown(point, MouseButton.Left);
                    window.MouseUp(point, MouseButton.Left);
                }
                ClickAt(0);
                Assert.Empty(queries);
                ClickAt(target.LastIndexOf("运行时", StringComparison.Ordinal) + 1);
                Assert.Equal(("运行时", "如果运行时出错，请检查日志。"), Assert.Single(queries));
                reader.SelectAll();
                Assert.Equal(target, reader.SelectedText);

                var plain = DictionaryReadingText.Primary(entry with { Collocations = [] }, (_, _) => { });
                Assert.False(plain.HasLinkedTerms);
                Assert.True(plain.TextDecorations is null or { Count: 0 });
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task GrammarAndTermCanOverlapWithoutBreakingSelectionOrClickLookup()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(DictionaryInputTests));
        await session.Dispatch(() =>
        {
            const string source = "They will bank on you. Stay close.";
            var queries = new List<(string, string)>();
            var reader = new DictionaryReadingText(source, (term, context) => queries.Add((term, context)),
                ["bank on", "ill"], [new("will bank on", "情态表达", "will 后接动词原形。")]);
            var window = new Window { Content = reader, Width = 500, Height = 90 };
            window.Show();
            try
            {
                window.UpdateLayout();
                Assert.Equal(source, reader.Inlines!.Text);
                var mark = Assert.Single(reader.Inlines!.OfType<Run>(), r => r.Text == "bank on");
                Assert.NotNull(mark.Background);
                Assert.NotNull(mark.TextDecorations);
                var point = reader.TextLayout.HitTestTextRange(source.IndexOf("bank", StringComparison.Ordinal), 1).First().Center;
                window.MouseDown(point, MouseButton.Left);
                window.MouseUp(point, MouseButton.Left);
                Assert.Equal(("bank on", "They will bank on you."), Assert.Single(queries));

                reader.SelectAll();
                Assert.Equal(source, reader.SelectedText);
                reader.SelectionStart = source.IndexOf("close", StringComparison.Ordinal);
                reader.SelectionEnd = reader.SelectionStart + 5;
                var search = (MenuItem)reader.ContextMenu!.Items[0]!;
                search.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Assert.Equal(("close", "Stay close."), queries.Last());
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }
}
