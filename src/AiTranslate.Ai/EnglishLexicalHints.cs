using System.IO.Compression;
using System.Text.Json;
using AiTranslate.Core;

namespace AiTranslate.Ai;

// Derived from Princeton WordNet 3.0, https://wordnetcode.princeton.edu/3.0/WNdb-3.0.tar.gz.
// The index supplies attested POS and usage clues, not a frequency ranking.
internal sealed record EnglishLexicalHints(Dictionary<string, string> Parts, string[] PhrasalVerbs)
{
    private static readonly Lazy<Dictionary<string, EnglishLexicalHints>> Index = new(ReadIndex);

    public static EnglishLexicalHints? For(DictionaryEntryResult entry) =>
        entry.ItemType is VocabularyItemType.Word or VocabularyItemType.Phrase or VocabularyItemType.Idiom &&
        DictionaryTranslationDirection.FromSourceCode(entry.DetectedSourceLanguage)?.Id == "en"
            ? Find(entry.SourceText) : null;

    public static EnglishLexicalHints? Find(string spelling) =>
        spelling.Length > 0 && spelling.All(c => char.IsAsciiLetterLower(c) || c == ' ') ? Index.Value.GetValueOrDefault(spelling) : null;

    private static Dictionary<string, EnglishLexicalHints> ReadIndex()
    {
        using var resource = typeof(EnglishLexicalHints).Assembly.GetManifestResourceStream("AiTranslate.Ai.Lexicon.english-lexical-hints.json.gz")!;
        using var stream = new GZipStream(resource, CompressionMode.Decompress);
        return JsonSerializer.Deserialize<Dictionary<string, EnglishLexicalHints>>(stream)!;
    }
}
