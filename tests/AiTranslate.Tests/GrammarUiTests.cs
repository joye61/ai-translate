using AiTranslate.Ai;
using AiTranslate.Core;
using AiTranslate.Desktop;
using AiTranslate.Desktop.Controls;
using AiTranslate.Desktop.Views;
using AiTranslate.Speech;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace AiTranslate.Tests;

[Collection("Dictionary UI")]
public sealed class GrammarUiTests
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions());

    private static IEnumerable<string> VisibleText(Visual root) => root.GetVisualDescendants().OfType<Control>()
        .Where(c => c.IsEffectivelyVisible)
        .Select(c => c switch { TextBlock t => t.Text ?? t.Inlines?.Text, _ => null }).OfType<string>().Where(t => t.Length > 0);

    private static Button Named(Visual root, string name) => root.GetVisualDescendants().OfType<Button>()
        .First(b => AutomationProperties.GetName(b) == name);

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static Window Host(Control content)
    {
        var window = new Window { Width = 900, Height = 900, Content = content };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static GrammarTopic Topic(string title = "过去进行时") => new()
    {
        Title = title, Category = "时态", Summary = "描述过去某一刻正在进行的动作。", Language = "en",
        Usages = [new("过去某一时刻", "看到具体时刻就用。") { Examples = [new("She was reading at nine.", "九点时她在看书。", "was reading")] }],
        Exercises =
        [
            new("At nine she ____ (read).", "was reading", "at nine 指过去某刻。") { Choices = ["read", "was reading", "is reading"] },
            new("I ____ (cook) when he came.", "was cooking", "when 引出短动作。")
        ],
        Related = [new("一般过去时", "易混")]
    };

    [Fact]
    public async Task ExerciseChoiceRevealsTheVerdictAndLocksTheOptions()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(GrammarUiTests));
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            await session.Dispatch(() =>
            {
                using var speech = new KokoroSpeechService(directory.FullName);
                var blocks = new GrammarBlocks(speech, new AppSettingsStore(Path.Combine(directory.FullName, "s.json")), _ => false, _ => { }, (_, _) => { })
                    { Interactive = true };
                var window = Host(blocks.Exercises(Topic().Exercises)!);

                Assert.DoesNotContain("回答正确", VisibleText(window));
                Click(Named(window, "选项 read"));
                Dispatcher.UIThread.RunJobs();
                Assert.Contains("再想想", VisibleText(window));
                Assert.Contains("at nine 指过去某刻。", VisibleText(window));
                Assert.All(new[] { "选项 read", "选项 was reading", "选项 is reading" }, name => Assert.False(Named(window, name).IsHitTestVisible));

                var fill = Named(window, "查看答案");
                Click(fill);
                Dispatcher.UIThread.RunJobs();
                Assert.Contains("参考答案", VisibleText(window));
                Assert.Contains("was cooking", VisibleText(window));
                window.Close();
                return 0;
            }, CancellationToken.None);
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public async Task ExercisesStayStaticWhileTheArticleIsStillStreaming()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(GrammarUiTests));
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            await session.Dispatch(() =>
            {
                using var speech = new KokoroSpeechService(directory.FullName);
                var blocks = new GrammarBlocks(speech, new AppSettingsStore(Path.Combine(directory.FullName, "s.json")), _ => false, _ => { }, (_, _) => { });
                var window = Host(blocks.Exercises(Topic().Exercises)!);
                Assert.Empty(window.GetVisualDescendants().OfType<Button>());
                window.Close();
                return 0;
            }, CancellationToken.None);
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public async Task TranslationGrammarRowsOpenTheStudioUnderTheirCanonicalTopic()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(GrammarUiTests));
        await session.Dispatch(() =>
        {
            var opened = new List<(string Topic, string Language)>();
            GrammarLinks.Open = (topic, language) => opened.Add((topic, language));
            GrammarLinks.IsKnown = (topic, _) => topic == "过去进行时";
            try
            {
                GrammarHighlight[] highlights =
                [
                    new("was reading", "过去进行", "当时正在进行。", "过去进行时"),
                    new("when I called her", "时间状语从句", "旧结果没有 topic。")
                ];
                var window = Host(DictionaryInsightBlocks.Grammar(highlights, "en", linked: true)!);

                Click(Named(window, "精解「过去进行时」"));
                Click(Named(window, "精解「时间状语从句」"));

                Assert.Equal([("过去进行时", "en"), ("时间状语从句", "en")], opened);
                // Only the grammar point the library already holds is marked.
                Assert.Single(VisibleText(window), "已收录");
                window.Close();

                var plain = Host(DictionaryInsightBlocks.Grammar(highlights, "en", linked: false)!);
                Assert.DoesNotContain(plain.GetVisualDescendants().OfType<Button>(), b => AutomationProperties.GetName(b)?.StartsWith("精解") == true);
                plain.Close();
            }
            finally
            {
                GrammarLinks.Open = null;
                GrammarLinks.IsKnown = null;
            }
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task StudioWalksFromSentencePointsToTheTopicAndBack()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(GrammarUiTests));
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            await session.Dispatch(async () =>
            {
                using var speech = new KokoroSpeechService(directory.FullName);
                var service = new StubGrammar();
                var status = new List<string>();
                var studio = new GrammarStudioView(service, speech, new AppSettingsStore(Path.Combine(directory.FullName, "s.json")), status.Add, (_, _) => { });
                var window = Host(studio);

                await studio.AskAsync("She was reading when I called her.");
                Dispatcher.UIThread.RunJobs();
                Assert.Contains("句中语法", VisibleText(window));
                Assert.Contains("过去进行时", VisibleText(window));
                Assert.Equal(["She was reading when I called her."], service.Asked);

                Click(Named(window, "精解「过去进行时」"));
                await Task.Delay(50);
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(["过去进行时"], service.Opened);
                Assert.Contains("描述过去某一刻正在进行的动作。", VisibleText(window));
                Assert.Contains("小练习", VisibleText(window));

                Click(Named(window, "返回上一级"));
                Dispatcher.UIThread.RunJobs();
                Assert.Contains("句中语法", VisibleText(window));
                Assert.Single(service.Opened);
                window.Close();
            }, CancellationToken.None);
        }
        finally { directory.Delete(recursive: true); }
    }

    private sealed class StubGrammar : IAiGrammarService
    {
        public List<string> Asked { get; } = [];
        public List<string> Opened { get; } = [];
        public int LibraryCount => 0;
        public bool IsKnown(string title, string? language) => false;
        public IReadOnlyList<GrammarTopicSummary> Recent(int limit) => [];
        public Task<string?> FindSuggestionAsync(string prefix, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public Task<GrammarLookupResult> AskAsync(string input, CancellationToken cancellationToken, Action<GrammarTopic>? onPreview = null, Action<string>? onStage = null)
        {
            Asked.Add(input);
            return Task.FromResult(new GrammarLookupResult(GrammarLookupKind.Sentence, input)
            {
                Sentence = new GrammarSentenceAnalysis(input)
                {
                    Translation = "我打电话时她在看书。", Structure = "when 引导时间状语从句。",
                    Points = [new("过去进行时", "was reading", "was reading", "当时正在进行。", "时态")]
                }
            });
        }

        public Task<GrammarTopic> OpenTopicAsync(string title, string? language, CancellationToken cancellationToken, Action<GrammarTopic>? onPreview = null,
            Action<string>? onStage = null, bool forceRefresh = false)
        {
            Opened.Add(title);
            return Task.FromResult(Topic(title));
        }

        public Task<GrammarTopic> CompleteTopicAsync(GrammarTopic topic, CancellationToken cancellationToken, Action<GrammarTopic>? onPreview = null,
            Action<string>? onStage = null) => Task.FromResult(topic);
    }
}
