using System.Globalization;
using AiTranslate.Core;
using AiTranslate.Desktop;
using AiTranslate.Desktop.Controls;
using AiTranslate.Speech;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using Xunit;

namespace AiTranslate.Tests;

[Collection("Dictionary UI")]
public sealed class SelectionPopupStreamingTests
{
    [Theory]
    [InlineData("一段分批送达的译文，需要连续显示而不是整段突然出现。")]
    [InlineData("👩‍💻e\u0301🙂测试文字")]
    public void BurstsAdvanceThroughWholeTextElementsAndCatchUp(string target)
    {
        var shown = "";
        var frames = new List<string>();
        var boundaries = StringInfo.ParseCombiningCharacters(target).Append(target.Length).ToArray();
        for (var i = 0; i < 40 && shown != target; i++)
        {
            var next = StreamingText.Advance(shown, target, TimeSpan.FromMilliseconds(16));
            Assert.StartsWith(shown, next);
            Assert.Contains(next.Length, boundaries);
            frames.Add(next);
            shown = next;
        }
        Assert.Equal(target, shown);
        Assert.True(frames.Count > 1);
        Assert.Equal("已修正", StreamingText.Advance(shown, "已修正", TimeSpan.FromMilliseconds(16)));
        Assert.Equal("", StreamingText.Advance(shown, "", TimeSpan.FromMilliseconds(16)));
    }

    [Fact]
    public async Task PopupSmoothsBurstsReusesSenseRowsAndStopsLoadingAtTerminalStates()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(DictionaryInputTests));
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            await session.Dispatch(() =>
            {
                using var speech = new KokoroSpeechService(directory.FullName);
                var settings = new AppSettingsStore(Path.Combine(directory.FullName, "settings.json"));
                var body = new SelectionPopupBody(speech, settings, (_, _) => { });
                var window = new Window { Width = 420, Content = body };
                window.Show();
                try
                {
                    Assert.Single(body.Children.OfType<LoadingSkeleton>());
                    var entry = new DictionaryEntryResult
                    {
                        SourceText = "draft", PrimaryTranslation = "一份还需要修改和完善的草稿", LiteralTranslation = "初步起草的文本",
                        Senses = [new("n.", "尚待修改和完善的初稿")], MemoryMnemonic = "先起草再修改，逐渐完善。"
                    };
                    body.ShowEntry(entry, complete: false);
                    var primary = body.GetVisualDescendants().OfType<DictionaryTranslations>().Single()
                        .GetVisualDescendants().OfType<SelectableTextBlock>().First();
                    var first = primary.Text!;
                    Assert.NotEmpty(first);
                    Assert.NotEqual(entry.PrimaryTranslation, first);
                    body.AdvanceFrame(TimeSpan.FromMilliseconds(16));
                    Assert.StartsWith(first, primary.Text);
                    Assert.True(primary.Text!.Length > first.Length);

                    for (var i = 0; i < 40; i++) body.AdvanceFrame(TimeSpan.FromMilliseconds(16));
                    Assert.Equal(entry.PrimaryTranslation, primary.Text);
                    Assert.Empty(body.Children.OfType<LoadingSkeleton>()); // Background enrichment must not leave empty placeholders.
                    var definition = body.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == entry.Senses[0].DefinitionZh);
                    var extended = entry with { Senses = [new("n.", "尚待修改和完善的初稿，也指初步方案")] };
                    body.ShowEntry(extended, complete: false);
                    for (var i = 0; i < 40; i++) body.AdvanceFrame(TimeSpan.FromMilliseconds(16));
                    Assert.Same(definition, body.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == extended.Senses[0].DefinitionZh));

                    // Detaching pauses the obsolete body; reattaching resumes from its latest snapshot.
                    window.Content = new Border();
                    body.ShowEntry(entry with { PrimaryTranslation = entry.PrimaryTranslation + "，随后继续补充更多细节" }, complete: false);
                    var before = primary.Text;
                    body.AdvanceFrame(TimeSpan.FromMilliseconds(100));
                    Assert.Equal(before, primary.Text);
                    window.Content = body;
                    body.AdvanceFrame(TimeSpan.FromMilliseconds(16));
                    Assert.True(primary.Text!.Length > before!.Length);

                    body.ShowEntry(entry with { PrimaryTranslation = "完整的最终译文" }, complete: true);
                    Assert.Empty(body.Children.OfType<LoadingSkeleton>());
                    body.ShowEntry(extended, complete: false);
                    body.AdvanceFrame(TimeSpan.FromMilliseconds(16));
                    Assert.Contains(body.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "完整的最终译文");

                    var failed = new SelectionPopupBody(speech, settings, (_, _) => { });
                    window.Content = failed;
                    failed.ShowEntry(entry, complete: false);
                    failed.ShowError("测试失败");
                    failed.AdvanceFrame(TimeSpan.FromMilliseconds(16));
                    Assert.Empty(failed.Children.OfType<LoadingSkeleton>());
                    Assert.False(failed.Children.OfType<DictionaryTranslations>().Single().IsVisible);

                    var cached = new SelectionPopupBody(speech, settings, (_, _) => { });
                    window.Content = cached;
                    cached.ShowEntry(entry with { Origin = DictionaryResultOrigin.Cache }, complete: true);
                    Assert.Empty(cached.Children.OfType<LoadingSkeleton>());
                    Assert.Contains(cached.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == entry.PrimaryTranslation);

                }
                finally { window.Close(); }
            }, CancellationToken.None);
        }
        finally { directory.Delete(recursive: true); }
    }
}
