using AiTranslate.Core;
using AiTranslate.Desktop;
using AiTranslate.Desktop.Controls;
using AiTranslate.Speech;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace AiTranslate.Tests;

[Collection("Dictionary UI")]
public sealed class DictionaryStreamingTests
{
    private static readonly DictionaryEntryResult Entry = new()
    {
        SourceText = "The report was released yesterday.", ItemType = VocabularyItemType.Sentence,
        DetectedSourceLanguage = "en", PrimaryTranslation = "这份报告昨天已经发布，现在可以查阅完整内容。",
        LiteralTranslation = "该报告于昨日被发布，现在其完整内容已经可以阅读。"
    };

    [Fact]
    public async Task BothTranslationsAdvanceBetweenNetworkChunksAndResumeAfterReattachment()
    {
        await WithPage(async (window, page) =>
        {
            page.ShowTranslation(Entry.PrimaryTranslation);
            page.ShowEntry(Entry, complete: false);
            Dispatcher.UIThread.RunJobs();
            var translations = page.GetVisualDescendants().OfType<DictionaryTranslations>().Single();
            var texts = translations.GetVisualDescendants().OfType<SelectableTextBlock>().ToArray();
            var first = texts[0].Text!;
            Assert.NotEmpty(first);
            Assert.NotEqual(Entry.PrimaryTranslation, first);
            Assert.NotEqual(Entry.LiteralTranslation, texts[1].Text);

            // No second network callback: the UI timer itself must keep revealing the buffered text.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while (texts[0].Text == first || string.IsNullOrEmpty(texts[1].Text)) await Task.Delay(10, timeout.Token);
            Assert.StartsWith(first, texts[0].Text);

            window.Content = null;
            page.ShowTranslation(Entry.PrimaryTranslation + "补充说明也应继续显示。");
            var detached = texts[0].Text;
            translations.AdvanceFrame(TimeSpan.FromMilliseconds(100));
            Assert.Equal(detached, texts[0].Text);
            window.Content = page;
            for (var i = 0; i < 60; i++) translations.AdvanceFrame(TimeSpan.FromMilliseconds(16));
            Assert.Equal(Entry.PrimaryTranslation + "补充说明也应继续显示。", texts[0].Text);
            Assert.Equal(Entry.LiteralTranslation, texts[1].Text);
            Assert.Single(page.Children.OfType<LoadingSkeleton>());
        });
    }

    [Fact]
    public async Task BurstsRenderOnlyTheLatestDetailsAndKeepUnchangedHeaderControls()
    {
        await WithPage((_, page) =>
        {
            page.ShowEntry(Entry with { UsageNotes = "最初的说明" }, complete: false);
            Dispatcher.UIThread.RunJobs();
            var language = page.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == UiLabels.Language("en"));
            var section = page.GetVisualDescendants().OfType<DictionarySection>().Single(s =>
                s.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "最初的说明"));
            var rows = Assert.IsType<StackPanel>(((StackPanel)section.Child!).Children.OfType<ContentControl>().Single().Content);
            var changes = 0;
            rows.Children.CollectionChanged += (_, _) => changes++;
            for (var i = 0; i < 20; i++) page.ShowEntry(Entry with { UsageNotes = $"说明 {i}" }, complete: false);
            Assert.Equal(0, changes);
            Dispatcher.UIThread.RunJobs();
            Assert.InRange(changes, 1, 2);
            Assert.Contains(page.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "说明 19");
            Assert.Same(language, page.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == UiLabels.Language("en")));

            page.ShowEntry(Entry with { UsageNotes = "过期的说明" }, complete: false);
            page.ShowTranslation("最终回调之前还有尚未打完的译文");
            page.ShowEntry(Entry with { PrimaryTranslation = "最终译文", UsageNotes = "最终说明" }, complete: true);
            Dispatcher.UIThread.RunJobs();
            page.ShowTranslation("迟到的预览");
            page.ShowEntry(Entry, complete: false);
            Dispatcher.UIThread.RunJobs();
            Assert.Contains(page.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "最终译文");
            Assert.Contains(page.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "最终说明");
            Assert.DoesNotContain(page.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "过期的说明");
            Assert.Empty(page.Children.OfType<LoadingSkeleton>());

            // A later completed review may still replace a completed result.
            page.ShowEntry(Entry with { PrimaryTranslation = "复核译文", Origin = DictionaryResultOrigin.Reviewed }, complete: true);
            Assert.Contains(page.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "复核译文");
            return Task.CompletedTask;
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopAndErrorFlushReceivedContentAndRejectLatePreviews(bool fail)
    {
        await WithPage((_, page) =>
        {
            page.ShowEntry(Entry with { UsageNotes = "已经收到的说明" }, complete: false);
            if (fail) page.ShowError("模拟断线");
            else page.MarkStopped();
            page.ShowTranslation("过期译文");
            page.ShowEntry(Entry with { PrimaryTranslation = "过期译文" }, complete: false);
            Dispatcher.UIThread.RunJobs();
            var translations = page.GetVisualDescendants().OfType<DictionaryTranslations>().Single();
            translations.AdvanceFrame(TimeSpan.FromMilliseconds(16));
            var texts = page.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToArray();
            Assert.Contains(Entry.PrimaryTranslation, texts);
            Assert.Contains(Entry.LiteralTranslation, texts);
            Assert.Contains("已经收到的说明", texts);
            Assert.DoesNotContain("过期译文", texts);
            Assert.Contains(fail ? "未完成" : "已停止生成", texts);
            Assert.Empty(page.Children.OfType<LoadingSkeleton>());
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task CachedTranslationAppearsImmediatelyEvenDuringOptionalSupplement()
    {
        await WithPage((_, page) =>
        {
            page.ShowEntry(Entry with { Origin = DictionaryResultOrigin.Cache }, complete: false);
            Dispatcher.UIThread.RunJobs();
            Assert.Contains(page.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == Entry.PrimaryTranslation);
            Assert.Contains(page.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == Entry.LiteralTranslation);
            return Task.CompletedTask;
        });
    }

    private static async Task WithPage(Func<Window, DictionaryResultView, Task> run)
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(DictionaryInputTests));
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            await session.Dispatch(async () =>
            {
                using var speech = new KokoroSpeechService(directory.FullName);
                var page = new DictionaryResultView(Entry.SourceText, "zh-Hans", speech,
                    new AppSettingsStore(Path.Combine(directory.FullName, "settings.json")), (_, _, _) => { });
                var window = new Window { Width = 760, Height = 600, Content = page };
                window.Show();
                try { await run(window, page); }
                finally { window.Close(); }
            }, CancellationToken.None);
        }
        finally { directory.Delete(recursive: true); }
    }
}
