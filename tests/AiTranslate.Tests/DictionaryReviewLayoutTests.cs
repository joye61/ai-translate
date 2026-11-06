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
using Avalonia.VisualTree;
using Xunit;

namespace AiTranslate.Tests;

[Collection("Dictionary UI")]
public sealed class DictionaryReviewLayoutTests
{
    [Fact]
    public async Task AlignedSentencesReflowAsPairsAndKeepSelectionAndLookupDirection()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(DictionaryInputTests));
        await session.Dispatch(() =>
        {
            var pairs = new[] { new AlignedSentence("Although prices rose, demand held steady.", "尽管物价上涨，需求依然平稳。"),
                new AlignedSentence("Shops stayed open.", "商店照常营业。") };
            var queries = new List<(string Query, string Context)>();
            var block = DictionaryInsightBlocks.Alignment(pairs, s => TextStyles.Selectable(s, TextRole.ExampleSource),
                (q, context) => queries.Add((q, context)))!;
            var window = new Window { Width = 800, Height = 500, Content = block };
            window.Show();
            try
            {
                var texts = block.GetVisualDescendants().OfType<SelectableTextBlock>().ToArray();
                var original = texts.Single(t => t.Text == pairs[0].Source);
                var translation = texts.Single(t => t.Text == pairs[0].Target);
                var next = texts.Single(t => t.Text == pairs[1].Source);
                translation.SelectAll();
                foreach (var width in new[] { 760, 354, 760 })
                {
                    block.Width = width;
                    window.UpdateLayout();
                    var start = original.TranslatePoint(default, block)!.Value;
                    var target = translation.TranslatePoint(default, block)!.Value;
                    if (width == 354)
                    {
                        Assert.Equal(start.X, target.X, 1);
                        Assert.True(target.Y >= start.Y + original.Bounds.Height);
                    }
                    else
                    {
                        Assert.True(target.X >= start.X + original.Bounds.Width);
                        Assert.Equal(start.Y, target.Y, 1);
                    }
                    Assert.True(next.TranslatePoint(default, block)!.Value.Y >= target.Y + translation.Bounds.Height);
                    Assert.Equal(pairs[0].Target, translation.SelectedText);
                }
                translation.RaiseEvent(new ContextRequestedEventArgs());
                Assert.Equal((pairs[0].Target, pairs[0].Target), Assert.Single(queries));
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task LongVariantLabelsLeaveTheFullLineForTheTranslationAndItsCopyActionWorks()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(DictionaryInputTests));
        await session.Dispatch(() =>
        {
            var variant = new TranslationVariant("供研究报告与面向公众的新闻摘要使用的审慎书面表达", "尽管物价上涨，需求依然平稳。", "用于陈述调查结果。");
            string? copied = null;
            var block = DictionaryInsightBlocks.Variants([variant], true, s => copied = s, null)!;
            var window = new Window { Width = 354, Height = 420, Content = block };
            window.Show();
            try
            {
                window.UpdateLayout();
                var style = block.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == variant.Style);
                var text = block.GetVisualDescendants().OfType<SelectableTextBlock>().Single(t => t.Text == variant.Text);
                var copy = block.GetVisualDescendants().OfType<Button>().Single();
                var labelEnd = style.TranslatePoint(new Point(style.Bounds.Width, style.Bounds.Height), block)!.Value;
                Assert.True(labelEnd.X <= copy.TranslatePoint(default, block)!.Value.X);
                Assert.True(labelEnd.Y <= text.TranslatePoint(default, block)!.Value.Y);
                Assert.True(text.Bounds.Width > block.Bounds.Width * .85);
                copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(variant.Text, copied);
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task MorphemesKeepTheirOperatorsWhenWrappedAndNotesStayReadable(int count)
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(DictionaryInputTests));
        await session.Dispatch(() =>
        {
            const string note = "用于说明词形变化的来源。这段背景说明放在词素下方，读者可以顺着同一行读完。";
            Morpheme[] parts = [new("dis-", "否定", MorphemeKind.Prefix),
                new("establish", "设立", MorphemeKind.Root, note),
                new("-ment", "行为或结果", MorphemeKind.Suffix),
                new("-arian", "从事某事的人", MorphemeKind.Suffix),
                new("-ism", "思想体系", MorphemeKind.Suffix)];
            var headword = count switch { 3 => "disestablishment", 4 => "disestablishmentarian", _ => "disestablishmentarianism" };
            var block = DictionaryInsightBlocks.WordFormation(parts[..count], "源于 establish 的派生形式。", headword)!;
            var window = new Window { Width = 720, Height = 600, Content = block };
            window.Show();
            try
            {
                foreach (var width in new[] { 720, 354 })
                {
                    block.Width = width;
                    window.UpdateLayout();
                    var equation = Assert.Single(block.GetVisualDescendants().OfType<WrapPanel>());
                    var texts = equation.GetVisualDescendants().OfType<TextBlock>().ToArray();
                    var operators = texts.Where(t => t.Text is "+" or "=").ToArray();
                    Assert.Equal(count, operators.Length);
                    var operands = parts.Skip(1).Take(count - 1).Select(p => p.Part).Append(headword);
                    foreach (var (op, operand) in operators.Zip(operands))
                    {
                        var form = texts.Single(t => t.Text == operand);
                        var from = op.TranslatePoint(new Point(0, op.Bounds.Height / 2), equation)!.Value;
                        var top = form.TranslatePoint(new Point(0, 0), equation)!.Value;
                        var right = form.TranslatePoint(new Point(form.Bounds.Width, 0), equation)!.Value.X;
                        Assert.True(from.X < top.X);
                        Assert.InRange(from.Y, top.Y - 8, top.Y + form.Bounds.Height + 8);
                        Assert.InRange(right, 0, width + 1);
                    }
                    var explanation = Assert.Single(block.GetVisualDescendants().OfType<SelectableTextBlock>(),
                        t => (t.Text ?? t.Inlines?.Text) == note);
                    Assert.True(explanation.TranslatePoint(default, equation)!.Value.Y >= equation.Bounds.Height);
                    Assert.InRange(explanation.TranslatePoint(new Point(explanation.Bounds.Width, 0), block)!.Value.X, 0, width + 1);
                }
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task TranslationDecisionConditionIsVisibleWithoutInventingAnAlternative()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(DictionaryInputTests));
        await session.Dispatch(() =>
        {
            var decision = new TranslationDecision("gets too hot", "温度过高", "说明触发条件", Condition: "适用于自动温控系统。");
            var block = DictionaryInsightBlocks.Decisions([decision], lookup: null)!;
            var window = new Window { Content = block, Width = 440, Height = 260 };
            window.Show();
            try
            {
                var labels = block.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? t.Inlines?.Text).ToArray();
                Assert.Contains("适用条件", labels);
                Assert.Contains(decision.Condition, labels);
                Assert.DoesNotContain("备选译法", labels);
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ReviewedCollocationNotesAndRelatedExamplesStayWithTheirRows()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(DictionaryInputTests));
        var folder = Directory.CreateTempSubdirectory();
        try
        {
            await session.Dispatch(() =>
            {
                using var speech = new KokoroSpeechService(folder.FullName);
                var details = new DictionaryDetails(speech, new AppSettingsStore(Path.Combine(folder.FullName, "settings.json")), (_, _, _) => { });
                var collocation = new CollocationItem("hold fast", "紧握") { UsageNote = "也可表示坚持某种立场。" };
                var related = new RelatedForm("牢固", "fastness", "n.", "derived", "描述牢固状态", "the fastness of the knot")
                    { Example = new("I tested the fastness of the knot.", "我检查了绳结的牢固程度。", "fastness") };
                var same = new RelatedForm("快速地", "fast", "adv.", "sameForm", "直接修饰动作", "run fast")
                    { Example = new("The dog runs fast.", "这只狗跑得快。", "fast") };
                var entry = new DictionaryEntryResult { SourceText = "fast", PrimaryTranslation = "快速的", DetectedSourceLanguage = "en",
                    Senses = [new("adj.", "快速的"), new("adv.", "快速地")], Collocations = [collocation], WordFamily = [same, related],
                    Examples = [related.Example, same.Example] };
                details.ShowEntry(entry, complete: true);
                var window = new Window { Content = new ScrollViewer { Content = details }, Width = 520, Height = 600 };
                window.Show();
                try
                {
                    window.UpdateLayout();
                    var sections = details.GetVisualDescendants().OfType<DictionarySection>();
                    string?[] Texts(string title) => sections.Single(s => s.Title == title).GetVisualDescendants()
                        .OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? t.Inlines?.Text).ToArray();
                    Assert.Contains(collocation.Meaning, Texts("常用搭配"));
                    Assert.Contains("用法", Texts("常用搭配"));
                    Assert.Contains(collocation.UsageNote, Texts("常用搭配"));
                    Assert.Contains(related.Example.TranslatedSentence, Texts("关联词与用法"));
                    Assert.Contains(same.Example.TranslatedSentence, Texts("词性与释义"));
                    Assert.DoesNotContain(same.Example.TranslatedSentence, Texts("关联词与用法"));
                    Assert.DoesNotContain(sections, s => s.Title == "双语例句" && s.IsEffectivelyVisible);
                }
                finally { window.Close(); }
            }, CancellationToken.None);
        }
        finally { folder.Delete(true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommittedReviewRemainsVisibleWhenNotebookWriteFails(bool inNotebook)
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(DictionaryInputTests));
        var folder = Directory.CreateTempSubdirectory();
        try
        {
            await session.Dispatch(async () =>
            {
                using var speech = new KokoroSpeechService(folder.FullName);
                var cache = new DictionaryEntryCache(Path.Combine(folder.FullName, "lookup.db"));
                var store = new VocabularyService(Path.Combine(folder.FullName, "cards.json"));
                var settings = new AppSettingsStore(Path.Combine(folder.FullName, "settings.json"));
                Assert.True(cache.TryAdd("fixture", new DictionaryEntryResult { SourceText = "coffee", PrimaryTranslation = "咖啡",
                    Senses = [new("n.", "旧释义")] }, 1));
                var original = cache.Read("fixture")!;
                var book = store.CreateNotebook("复核验收");
                store.AddOrUpdateFromDictionary(original, book.Id);
                var service = new AiDictionaryService(() => new ReviewedNotebookSyncTests.ReviewReply(), cache,
                    refreshCollectedEntries: (_, _) => throw new UnauthorizedAccessException("词本只读"));
                string? status = null;
                var window = new Window { Width = 850, Height = 640 };
                static Button Find(Control parent, string name) => parent.GetVisualDescendants().OfType<Button>().Single(b =>
                    b.IsEffectivelyVisible && AutomationProperties.GetName(b) == name);
                static void Click(Button b) => b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                try
                {
                    Window target = window;
                    if (inNotebook)
                    {
                        var vault = new VocabularyVaultView(store, speech, settings, (_, _, _, _, _) => { },
                            s => status = s, service);
                        window.Content = vault;
                        window.Show();
                        vault.RefreshAll();
                        window.UpdateLayout();
                        Click(Find(vault, "复核验收，1 条"));
                        window.UpdateLayout();
                        Click(Find(vault, "查看词条 coffee"));
                        target = Assert.Single(window.OwnedWindows.OfType<Window>());
                    }
                    else
                    {
                        var studio = new DictionaryStudioView(service, store, speech, settings, s => status = s);
                        window.Content = studio;
                        window.Show();
                        studio.DisplayResult(original, TranslationTriggerSource.ManualInput, "");
                    }
                    target.UpdateLayout();
                    var review = Find(target, "智能复核 · 词性与释义");
                    Click(review);
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                    while (!review.IsEnabled) await Task.Delay(10, timeout.Token);
                    target.UpdateLayout();
                    var text = target.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible)
                        .Select(t => t.Text ?? t.Inlines?.Text).ToList();
                    Assert.Contains(ReviewedNotebookSyncTests.ReviewReply.Definition, text);
                    Assert.DoesNotContain("旧释义", text);
                    text.Add(status);
                    Assert.Contains(text, s => s?.Contains("已保存到本地缓存，生词本将在下次查词时重试同步") == true);
                    Assert.DoesNotContain(text, s => s?.Contains("已保留原内容") == true);
                }
                finally { window.Close(); }
                return true;
            }, CancellationToken.None);
        }
        finally { folder.Delete(true); }
    }
}
