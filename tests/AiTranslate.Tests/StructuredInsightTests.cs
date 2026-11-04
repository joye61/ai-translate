using AiTranslate.Ai;
using AiTranslate.Core;
using AiTranslate.Desktop;
using AiTranslate.Desktop.Controls;
using AiTranslate.Speech;
using AiTranslate.Vocabulary;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.VisualTree;
using Xunit;

namespace AiTranslate.Tests;

[Collection("Dictionary UI")]
public sealed class StructuredInsightTests
{
    [Theory]
    [InlineData("用 **过去式** 表示", "用 |过去式:Strong| 表示")]
    [InlineData("原文 `went out` 指外出", "原文 |went out:Quote| 指外出")]
    [InlineData("源自 *inspicere* 一词", "源自 |inspicere:Quote| 一词")]
    [InlineData("a*b and c*d", "a*b and c*d")]
    [InlineData("3 * 4 ** 2", "3 * 4 ** 2")]
    [InlineData("a ** b ** c", "a ** b ** c")]
    [InlineData("**未闭合", "**未闭合")]
    [InlineData("**跨\n行**", "**跨\n行**")]
    [InlineData("****", "****")]
    public void MarkupOnlyStylesTightlyPairedMarkers(string input, string expected)
    {
        var rendered = string.Concat(InlineMarkup.Parse(input).Select(s => s.Style == InlineStyle.Plain ? s.Text : $"|{s.Text}:{s.Style}|"));
        Assert.Equal(expected, rendered);
        Assert.Equal(expected.Replace(":Strong|", "").Replace(":Quote|", "").Replace("|", ""), InlineMarkup.Strip(input));
    }

    [Fact]
    public void CatalogSeparatesWordAndPassageBlocksAndKeepsCoreBlocks()
    {
        foreach (var type in Enum.GetValues<VocabularyItemType>())
        {
            var specs = EntryBlockCatalog.For(type);
            Assert.Equal(EntryBlockCatalog.LiteralTranslation, specs[0]);
            Assert.Equal(specs.Count, specs.Select(s => s.Block).Distinct().Count());
            Assert.Equal(specs.Count, specs.Select(s => s.Title).Distinct().Count());
            var blocks = specs.Select(s => s.Block).ToArray();
            if (blocks.Contains(EntryBlock.Variants))
                Assert.Equal([EntryBlock.Variants, EntryBlock.TranslationOptions],
                    blocks.Where(b => b is EntryBlock.Variants or EntryBlock.TranslationOptions));
            if (type.IsPassage())
            {
                Assert.Equal(EntryBlock.Source, specs[1].Block);
                Assert.Equal(EntryBlockTier.Core, specs.Single(s => s.Block == EntryBlock.Source).Tier);
                Assert.DoesNotContain(specs, s => s.Block is EntryBlock.Senses or EntryBlock.Inflections or EntryBlock.WordFormation or EntryBlock.Mistakes);
            }
            else
            {
                Assert.Equal(EntryBlock.Senses, specs[1].Block);
                Assert.Equal(EntryBlockTier.Core, specs[1].Tier);
                Assert.Contains(EntryBlock.TranslationOptions, blocks);
                Assert.DoesNotContain(specs, s => s.Block is EntryBlock.Source or EntryBlock.Decisions or EntryBlock.Structure or EntryBlock.Tone);
            }
        }
        var paragraph = EntryBlockCatalog.For(VocabularyItemType.Paragraph).Select(s => s.Block).ToList();
        Assert.Equal([EntryBlock.LiteralTranslation, EntryBlock.Source, EntryBlock.Alignment], paragraph.Take(3));
        Assert.Contains(EntryBlock.Structure, EntryBlockCatalog.For(VocabularyItemType.Sentence).Select(s => s.Block));
        Assert.Contains(EntryBlock.LiteralMeaning, EntryBlockCatalog.For(VocabularyItemType.Idiom).Select(s => s.Block));
        Assert.DoesNotContain(EntryBlock.LiteralMeaning, EntryBlockCatalog.For(VocabularyItemType.Word).Select(s => s.Block));
    }

    [Fact]
    public void LiteralCounterpartIsOptionalForEveryItemType()
    {
        foreach (var type in Enum.GetValues<VocabularyItemType>())
        {
            DictionaryEntryResult Parse(string field) => AiDictionaryService.ParseDictionaryResponse("sample", "zh-Hans",
                $$"""{"primaryTranslation":"今天就到这儿吧。","itemType":"{{type}}"{{field}}}""");
            Assert.Equal("", Parse("").LiteralTranslation);
            Assert.Equal("", Parse(""","literalTranslation":" 今天就到这儿吧！ " """).LiteralTranslation);
            var entry = Parse(""","literalTranslation":"我们把这称作一天吧。","variants":[{"style":"直译","text":"我们把这称作一天吧"}]""");
            Assert.Equal("我们把这称作一天吧。", entry.LiteralTranslation);
            Assert.Empty(entry.Variants);
        }
    }

    [Fact]
    public void WordKeepsLexicalInsightsAndDropsPassageOnlyBlocks()
    {
        var entry = AiDictionaryService.ParseDictionaryResponse("inspection", "zh-Hans", """
            {"primaryTranslation":"检查；视察","itemType":"Word","detectedSourceLanguage":"en",
             "domains":["质量管理","质量管理","工程"],
             "morphemes":[{"part":"in-","meaning":"向内","kind":"prefix"},{"part":"spect","meaning":"看"},{"part":"-ion","meaning":"名词后缀"}],
             "synonymNuances":[{"word":"inspection","nuance":"按标准**逐项**查看"},{"word":"examination","nuance":"深入细致的检查","register":"中性"},{"word":"Inspection","nuance":"重复"}],
             "mistakes":[{"wrong":"make an inspection to the site","right":"make an inspection of the site","why":"inspection 后接 **of**"},{"wrong":"same","right":"same"}],
             "variants":[{"style":"正式","text":"查验"}],
             "clauses":[{"quote":"inspection","role":"主干","depth":0},{"quote":"inspection","role":"主干","depth":1}],
             "tone":{"formality":4},"keyPoints":["不应出现"],"literalMeaning":"不应出现"}
            """);
        Assert.Equal(["质量管理", "工程"], entry.Domains);
        Assert.Equal([MorphemeKind.Prefix, MorphemeKind.Root, MorphemeKind.Suffix], entry.Morphemes.Select(m => m.Kind));
        Assert.Equal(["inspection", "examination"], entry.SynonymNuances.Select(n => n.Word));
        Assert.Equal("make an inspection of the site", Assert.Single(entry.Mistakes).Right);
        Assert.Empty(entry.Variants);
        Assert.Empty(entry.Clauses);
        Assert.Null(entry.Tone);
        Assert.Empty(entry.KeyPoints);
        Assert.Equal("", entry.LiteralMeaning);
    }

    [Fact]
    public void PassageInsightsMustBeAnchoredInTheSource()
    {
        const string sentence = "The report, which was released yesterday, shows that sales fell.";
        var entry = AiDictionaryService.ParseDictionaryResponse(sentence, "zh-Hans", """
            {"primaryTranslation":"昨天发布的报告显示销量下降了。","itemType":"Sentence",
             "variants":[{"style":"直译","text":"昨天发布的报告显示，销量下降了"},{"style":"简洁","text":"昨日报告：销量下滑。"},{"style":"简洁","text":"重复风格"}],
             "clauses":[{"quote":"The report shows","role":"主干","depth":0},
                        {"quote":"The report","role":"主干","depth":0},
                        {"quote":"which was released yesterday","role":"定语从句","depth":2,"note":"修饰 `report`"},
                        {"quote":"that sales fell","role":"宾语从句","depth":1}],
             "tone":{"formality":9,"tones":["客观","陈述","中性","多余"]},
             "morphemes":[{"part":"re-","meaning":"x"},{"part":"port","meaning":"y"}]}
            """);
        Assert.Equal(["简洁"], entry.Variants.Select(v => v.Style));
        Assert.Equal(["The report", "which was released yesterday", "that sales fell"], entry.Clauses.Select(c => c.Quote));
        Assert.Equal([0, 1, 1], entry.Clauses.Select(c => c.Depth));
        Assert.Equal(0, entry.Tone!.Formality);
        Assert.Equal(3, entry.Tone.Tones.Count);
        Assert.Empty(entry.Morphemes);

        const string paragraph = "Prices rose. Demand fell.\n\nAnalysts were surprised.";
        var passage = AiDictionaryService.ParseDictionaryResponse(paragraph, "zh-Hans", """
            {"primaryTranslation":"价格上涨。需求下降。\n\n分析师感到意外。","itemType":"Paragraph",
             "keyPoints":["价格与需求背离","分析师意外","第三条","第四条","第五条"],
             "alignment":[{"source":"Prices rose.","target":"价格上涨。"},{"source":"Invented line.","target":"编造"},
                          {"source":"Demand fell.","target":"需求下降。"},{"source":"Prices rose.","target":"回头重复"},
                          {"source":"Analysts were surprised.","target":"分析师感到意外。"}]}
            """);
        Assert.Equal(4, passage.KeyPoints.Count);
        Assert.Equal(["Prices rose.", "Demand fell.", "Analysts were surprised."], passage.Alignment.Select(a => a.Source));
    }

    [Fact]
    public void InsightsPersistInTheNotebookAndProjectBackForRendering()
    {
        var result = new DictionaryEntryResult
        {
            SourceText = "break the ice", PrimaryTranslation = "打破僵局", ItemType = VocabularyItemType.Idiom,
            LiteralMeaning = "破开冰层", LiteralTranslation = "打破冰面", CulturalNote = "源自破冰船为船队开路。", Variants = [new("口语", "活跃气氛")],
            TranslationOptions = [new("打破僵局", "break a deadlock", "Emphasizes the impasse.")],
            SynonymNuances = [new("break the ice", "开场缓和"), new("warm up", "预热")],
            Mistakes = [new("break an ice", "break the ice", "固定搭配用 the")], Tone = new(2, ["轻松"])
        };
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(directory.FullName, "words.json");
            new VocabularyService(path).AddOrUpdateFromDictionary(result);
            var reopened = new VocabularyService(path);
            var saved = reopened.FindByHeadword("break the ice")!;
            var projected = saved.ToDictionaryResult();
            Assert.Equal(result.LiteralTranslation, projected.LiteralTranslation);
            Assert.Contains(result.LiteralTranslation, reopened.ExportToMarkdown());
            Assert.Contains(result.LiteralTranslation, reopened.ExportToCsv());
            Assert.Contains(result.LiteralTranslation, reopened.ExportToAnkiTsv());
            Assert.Equal(result.LiteralMeaning, projected.LiteralMeaning);
            Assert.Equal(result.CulturalNote, projected.CulturalNote);
            Assert.Equal(result.Variants, projected.Variants);
            Assert.Equal(result.TranslationOptions, projected.TranslationOptions);
            Assert.Equal(result.SynonymNuances, projected.SynonymNuances);
            Assert.Equal(result.Mistakes, projected.Mistakes);
            Assert.Equal(["轻松"], projected.Tone!.Tones);

            var stale = saved with { Clauses = [new("break", "主干", 0), new("elsewhere", "插入语", 1)] };
            Assert.Equal(["break"], stale.ToDictionaryResult().Clauses.Select(c => c.Quote));
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task StructuredBlocksRenderAsRichDocumentsAndSurviveStreamingUpdates()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(DictionaryInputTests));
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            await session.Dispatch(() =>
            {
                using var speech = new KokoroSpeechService(directory.FullName);
                var settings = new AppSettingsStore(Path.Combine(directory.FullName, "settings.json"));
                var looked = new List<string>();
                var details = new DictionaryDetails(speech, settings, (q, _, _) => looked.Add(q), _ => { });
                var window = new Window { Content = new ScrollViewer { Content = details }, Width = 720, Height = 600 };
                window.Show();
                try
                {
                    var word = new DictionaryEntryResult
                    {
                        SourceText = "inspection", PrimaryTranslation = "检查；视察", DetectedSourceLanguage = "en",
                        Senses = [new("n.", "检查")],
                        TranslationOptions = [new("检查", "检验状况", "核对标准时使用"), new("视察", "实地察看", "强调现场考察")],
                        Morphemes = [new("in-", "向内", MorphemeKind.Prefix), new("spect", "看", MorphemeKind.Root)],
                        EtymologyAndRoot = "源自拉丁语 **inspicere**",
                        SynonymNuances = [new("inspection", "按标准查看"), new("examination", "深入检查", "中性")],
                        Synonyms = ["examination", "check"],
                        Mistakes = [new("inspection to", "inspection of", "介词用 `of`")],
                        MemoryMnemonic = "in 向里 + spect 看"
                    };
                    details.ShowEntry(word with { Senses = [] }, complete: false);
                    window.UpdateLayout();
                    Assert.DoesNotContain(Texts(), t => t.Text == "词性与释义");
                    Assert.Contains(Texts(), t => t.Text == "译法辨析");
                    var tile = Texts().Single(t => t.Text == "spect");
                    details.ShowEntry(word with { Collocations = [new("carry out an inspection", "进行检查")] }, complete: false);
                    window.UpdateLayout();
                    Assert.Same(tile, Texts().Single(t => t.Text == "spect"));
                    Assert.Equal(["词性与释义", "译法辨析"], Texts().Select(t => t.Text).Where(t => t is "词性与释义" or "译法辨析"));

                    details.ShowEntry(word, complete: true);
                    window.UpdateLayout();
                    var texts = Texts().Select(t => t.Text ?? t.Inlines?.Text ?? "").ToArray();
                    Assert.Equal(["词性与释义", "译法辨析"], texts.Where(t => t is "词性与释义" or "译法辨析"));
                    Assert.Contains("构词与词源", texts);
                    Assert.Contains("近义词辨析", texts);
                    Assert.Contains("常见错误", texts);
                    Assert.Contains("记忆助记", texts);
                    Assert.Contains("侧重与区别", texts);
                    Assert.DoesNotContain(texts, t => t.Contains("**", StringComparison.Ordinal) || t.Contains('`'));
                    var emphasis = Texts().Single(t => t.Inlines?.Text == "源自拉丁语 inspicere");
                    Assert.Same(AppTheme.Insight, Assert.Single(emphasis.Inlines!.OfType<Run>(), r => r.Text == "inspicere").Foreground);
                    var wrong = Texts().Single(t => t.Text == "inspection to");
                    Assert.NotEmpty(wrong.TextDecorations!);
                    // Words compared in the table are not repeated as chips; the rest still are.
                    Assert.Contains("check", texts);
                    Assert.Single(texts, t => t == "examination");
                    details.GetVisualDescendants().OfType<Button>().Single(b => Avalonia.Automation.AutomationProperties.GetName(b) == "查词 examination")
                        .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                    Assert.Equal(["examination"], looked);

                    var paragraph = new DictionaryEntryResult
                    {
                        SourceText = "Prices rose.\n\nDemand fell. Shops closed.", PrimaryTranslation = "价格上涨。\n\n需求下降。商店关门。", ItemType = VocabularyItemType.Paragraph,
                        KeyPoints = ["价格与需求背离"],
                        Alignment = [new("Prices rose.", "价格上涨。"), new("Demand fell.", "需求下降。")],
                        Tone = new(3, ["客观"], "新闻语体")
                    };
                    details.ShowEntry(paragraph with { Alignment = [] }, complete: false);
                    window.UpdateLayout();
                    var original = Assert.Single(Texts(), t => t.Text == paragraph.SourceText);
                    Assert.DoesNotContain(Texts(), t => t.Text == "逐句对照");
                    details.ShowEntry(paragraph, complete: false);
                    window.UpdateLayout();
                    Assert.Same(original, Assert.Single(Texts(), t => t.Text == paragraph.SourceText));
                    details.ShowEntry(paragraph, complete: true);
                    window.UpdateLayout();
                    texts = Texts().Select(t => t.Text ?? t.Inlines?.Text ?? "").ToArray();
                    Assert.Single(texts, t => t == paragraph.SourceText);
                    Assert.Equal(["完整原文", paragraph.SourceText, "逐句对照", "要点速览"],
                        texts.Where(t => t == paragraph.SourceText || t is "完整原文" or "逐句对照" or "要点速览"));
                    Assert.Contains("Demand fell.", texts);
                    Assert.Contains("需求下降。", texts);
                    Assert.Contains("语气与语域", texts);
                    Assert.DoesNotContain("构词与词源", texts);
                }
                finally { window.Close(); }

                TextBlock[] Texts() => details.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).ToArray();
            }, CancellationToken.None);
        }
        finally { directory.Delete(true); }
    }
}
