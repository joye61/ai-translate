using System.Text.Json;
using System.Text.Json.Nodes;
using AiTranslate.Ai;
using AiTranslate.Core;
using AiTranslate.Vocabulary;
using Xunit;

namespace AiTranslate.Tests;

public sealed class WordFormationReviewTests
{
    private static DictionaryEntryResult Word(string source) => new()
    {
        SourceText = source, PrimaryTranslation = "译义", DetectedSourceLanguage = "en", TargetLanguage = "zh-Hans",
        ReaderLanguage = "zh-Hans", EtymologyAndRoot = "已有词源。"
    };

    [Fact]
    public async Task PlentyCanAddItsFirstDecompositionAndPersistTheEnginesPerPartNotes()
    {
        // This is the shape returned by the live engine for plenty: a complete array, with
        // text instead of part and a note that is separate from the short meaning on each tile.
        const string reply = """
            {"status":"improved","edits":[{"path":"/morphemes","value":[
              {"text":"plen-","kind":"root","meaning":"满；充满","note":"拉丁语 plenus（满的）的词根"},
              {"text":"-ty","kind":"suffix","meaning":"名词后缀","note":"表示状态或性质"}]},
              {"path":"/etymologyAndRoot","value":"来自古法语 plenté。"}]}
            """;
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var cache = new DictionaryEntryCache(Path.Combine(directory.FullName, "lookup.db"));
            var cardPath = Path.Combine(directory.FullName, "cards.json");
            var cards = new VocabularyService(cardPath);
            var word = Word("plenty");
            var card = cards.AddOrUpdateFromDictionary(word, sourceTitle: "课本");
            var service = new AiDictionaryService(() => new Reply(reply), cache,
                refreshCollectedEntries: cards.RefreshCollectedEntries, readerLanguageId: () => "zh-Hans");
            var frames = new List<DictionaryEntryResult>();
            var reviewed = await service.ReviewBlockAsync(word, EntryBlock.WordFormation, default, frames.Add);
            Assert.Equal("来自古法语 plenté。", reviewed.EtymologyAndRoot);
            Assert.Equal([new Morpheme("plen-", "满；充满", MorphemeKind.Root, "拉丁语 plenus（满的）的词根"),
                new Morpheme("-ty", "名词后缀", MorphemeKind.Suffix, "表示状态或性质")], reviewed.Morphemes);
            Assert.Contains(frames, draft => draft.Morphemes.Count == 2);
            var offline = new AiDictionaryService(() => throw new Exception("unexpected lookup"), cache, readerLanguageId: () => "zh-Hans");
            Assert.Equal(reviewed.Morphemes, (await offline.LookupOrTranslateAsync("plenty", "zh-Hans")).Morphemes);
            var reopened = new VocabularyService(cardPath).QueryEntries(new() { SearchKeyword = "plenty" }).Single(c => c.Id == card.Id);
            Assert.Equal(reviewed.Morphemes, reopened.ToDictionaryResult().Morphemes);
            Assert.Equal("课本", reopened.SourceTitle);
        }
        finally { directory.Delete(true); }
    }

    [Theory]
    [InlineData("unkindness", "un-,kind,-ness")]
    [InlineData("unhelpfulness", "un-,help,-ful,-ness")]
    [InlineData("renationalization", "re-,nation,-al,-iz(e),-ation")]
    public async Task AllThreeToFivePartsSurviveAFullArrayReview(string headword, string components)
    {
        var forms = components.Split(',');
        var rows = forms.Select(part => new { part, meaning = "含义", kind = part.EndsWith('-') ? "prefix" : part.StartsWith('-') ? "suffix" : "root" });
        var reply = JsonSerializer.Serialize(new { status = "improved", edits = new[] { new { path = "/morphemes", value = rows } } });
        var original = Word(headword) with { Morphemes = [new(forms[0], "含义", MorphemeKind.Prefix), new(forms[1], "含义", MorphemeKind.Root)] };
        var result = await new AiDictionaryService(() => new Reply(reply)).ReviewBlockAsync(original, EntryBlock.WordFormation, default);
        Assert.Equal(forms, result.Morphemes.Select(m => m.Part));
        Assert.Equal(original.Morphemes, result.Morphemes.Take(2));
    }

    [Fact]
    public async Task OldRowsWithoutNotesRemainReadableAndEtymologyCanStandAlone()
    {
        var legacy = JsonSerializer.Deserialize<Morpheme>("""{"Part":"pro-","Meaning":"向前","Kind":0}""");
        Assert.Equal(new("pro-", "向前", MorphemeKind.Prefix), legacy);
        var result = await new AiDictionaryService(() => new Reply(
            """{"status":"improved","edits":[{"path":"/etymologyAndRoot","value":"有据可查的历史词源。"}]}"""))
            .ReviewBlockAsync(Word("plenty"), EntryBlock.WordFormation, default);
        Assert.Empty(result.Morphemes);
        Assert.Equal("有据可查的历史词源。", result.EtymologyAndRoot);
    }

    [Fact]
    public async Task ALongerImportedDecompositionCanBeRevisedWithoutTruncation()
    {
        var parts = Enumerable.Range(1, 6).Select(i => new Morpheme("片段" + i, "已有说明", MorphemeKind.Root)).ToArray();
        var result = await new AiDictionaryService(() => new Reply(
            """{"status":"improved","edits":[{"path":"/morphemes/5/note","value":"补充说明"}]}"""))
            .ReviewBlockAsync(Word("legacy") with { Morphemes = parts }, EntryBlock.WordFormation, default);
        Assert.Equal(parts.Take(5), result.Morphemes.Take(5));
        Assert.Equal(parts[5] with { Note = "补充说明" }, result.Morphemes[5]);
    }

    [Fact]
    public async Task ConflictingPartSpellingsRejectTheWholeReview()
    {
        const string reply = """
            {"status":"improved","edits":[{"path":"/etymologyAndRoot","value":"修订。"},{"path":"/morphemes","value":[
            {"part":"plen-","text":"pla-","meaning":"满","kind":"root"},{"part":"-ty","meaning":"性质","kind":"suffix"}]}]}
            """;
        await Assert.ThrowsAsync<AiException>(() => new AiDictionaryService(() => new Reply(reply))
            .ReviewBlockAsync(Word("plenty"), EntryBlock.WordFormation, default));
    }

    private sealed class Reply(string reply) : IAiTransport
    {
        public Task<AiCompletion> CompleteAsync(IReadOnlyList<JsonObject> messages, JsonArray tools, Action<string> onText, CancellationToken token)
        {
            foreach (var chunk in reply.Chunk(24)) onText(new string(chunk));
            return Task.FromResult(new AiCompletion(reply, []));
        }
    }
}
