using System.Text;
using System.Text.Json.Serialization;

namespace AiTranslate.Core;

/// <summary>One form of a grammar point (affirmative, negative, question…). Pattern parts are separated by " + ".</summary>
public sealed record GrammarFormula(string Label, string Pattern, BilingualExample? Example = null, string Note = "");

/// <summary>A word or phrase that signals the grammar point (time adverbials, connectives, typical collocations).</summary>
public sealed record GrammarSignal(string Expression, string Meaning = "");

/// <summary>One situation in which the grammar point is used. Each usage can carry several graded examples.</summary>
public sealed record GrammarUsage(string Title, string Explanation)
{
    public IReadOnlyList<BilingualExample> Examples { get; init => field = value ?? []; } = [];
    public IReadOnlyList<string> Signals { get; init => field = value ?? []; } = [];
    public string Tip { get; init; } = "";
}

/// <summary>A before→after pair of a form rule, e.g. run → running.</summary>
public sealed record GrammarCase(string Form, string Result, string Note = "");

/// <summary>A formation or spelling rule with concrete cases laid out as a table.</summary>
public sealed record GrammarRule(string Title, string Explanation)
{
    public IReadOnlyList<GrammarCase> Cases { get; init => field = value ?? []; } = [];
}

/// <summary>A side-by-side comparison with the grammar point most often confused with this one.</summary>
public sealed record GrammarContrast(string Versus, string Difference)
{
    public string LeftLabel { get; init; } = "";
    public string LeftUse { get; init; } = "";
    public BilingualExample? LeftExample { get; init; }
    public string RightLabel { get; init; } = "";
    public string RightUse { get; init; } = "";
    public BilingualExample? RightExample { get; init; }
}

/// <summary>An exception, special case or limitation that breaks the general rule.</summary>
public sealed record GrammarException(string Title, string Explanation)
{
    public BilingualExample? Example { get; init; }
}

/// <summary>A self-check question. Choices are empty for fill-in-the-blank questions.</summary>
public sealed record GrammarExercise(string Question, string Answer, string Explanation)
{
    public IReadOnlyList<string> Choices { get; init => field = value ?? []; } = [];
}

/// <summary>A neighbouring grammar point. The title uses the same canonical naming as the local library.</summary>
public sealed record GrammarRelation(string Title, string Relation = "");

public enum GrammarOrigin { Ai, Library }

/// <summary>
/// A complete, reusable explanation of one grammar point. Everything here is independent of the sentence the student
/// came from, so the local library can serve it again for any later question that resolves to the same canonical title.
/// </summary>
public sealed record GrammarTopic
{
    public required string Title { get; init; }
    public string SourceTerm { get; init; } = "";
    /// <summary>Studied language code, e.g. "en".</summary>
    public string Language { get; init; } = "en";
    /// <summary>Language the explanation is written in.</summary>
    public string ReaderLanguage { get; init; } = "zh-Hans";
    public string Category { get; init; } = "";
    public string Level { get; init; } = "";
    public string Summary { get; init; } = "";
    public string Concept { get; init; } = "";
    public IReadOnlyList<string> Aliases { get; init => field = value ?? []; } = [];
    public IReadOnlyList<GrammarFormula> Formulas { get; init => field = value ?? []; } = [];
    public IReadOnlyList<GrammarUsage> Usages { get; init => field = value ?? []; } = [];
    public IReadOnlyList<GrammarSignal> Signals { get; init => field = value ?? []; } = [];
    public IReadOnlyList<GrammarRule> Rules { get; init => field = value ?? []; } = [];
    public IReadOnlyList<GrammarContrast> Contrasts { get; init => field = value ?? []; } = [];
    public IReadOnlyList<GrammarException> Exceptions { get; init => field = value ?? []; } = [];
    public IReadOnlyList<UsageMistake> Mistakes { get; init => field = value ?? []; } = [];
    public IReadOnlyList<string> ExamPoints { get; init => field = value ?? []; } = [];
    public string Mnemonic { get; init; } = "";
    public IReadOnlyList<GrammarExercise> Exercises { get; init => field = value ?? []; } = [];
    public IReadOnlyList<GrammarRelation> Related { get; init => field = value ?? []; } = [];
    /// <summary>False while only the core blocks exist. A library entry in this state can be completed later.</summary>
    public bool Extended { get; init; } = true;
    [JsonIgnore]
    public GrammarOrigin Origin { get; init; }
}

/// <summary>A grammar point that appears in a sentence the student entered, with its role in that sentence.</summary>
public sealed record GrammarSentencePoint(string Topic, string Quote, string Label, string Explanation, string Category = "");

public sealed record GrammarSentenceAnalysis(string Source)
{
    public string Language { get; init; } = "en";
    public string Translation { get; init; } = "";
    public string Structure { get; init; } = "";
    public IReadOnlyList<GrammarSentencePoint> Points { get; init => field = value ?? []; } = [];
}

/// <summary>A candidate offered when the question is broad ("时态") and the student must pick one grammar point.</summary>
public sealed record GrammarChoice(string Title, string Summary = "", string Category = "");

public enum GrammarLookupKind { Topic, Choices, Sentence, NotGrammar }

public sealed record GrammarLookupResult(GrammarLookupKind Kind, string Query)
{
    public GrammarTopic? Topic { get; init; }
    public GrammarSentenceAnalysis? Sentence { get; init; }
    public IReadOnlyList<GrammarChoice> Choices { get; init => field = value ?? []; } = [];
    public string Language { get; init; } = "en";
    public string Message { get; init; } = "";
}

public sealed record GrammarTopicSummary(string Id, string Title, string Language, string Category, string Summary,
    DateTimeOffset UpdatedAt, int Hits, bool Extended = true);

/// <summary>
/// Shared order and titles of the grammar article. Parsing, streaming preview and the reading view all read this list,
/// so a new block is added in one place.
/// </summary>
public enum GrammarBlock { Summary, Concept, Formulas, Usages, Signals, Rules, Contrasts, Exceptions, Mistakes, ExamPoints, Mnemonic, Exercises, Related }

public sealed record GrammarBlockSpec(GrammarBlock Block, string Title, EntryBlockTier Tier, bool Insight = false);

public static class GrammarBlockCatalog
{
    public static IReadOnlyList<GrammarBlockSpec> All { get; } =
    [
        new(GrammarBlock.Summary, "一句话讲清", EntryBlockTier.Core),
        new(GrammarBlock.Concept, "核心概念", EntryBlockTier.Core),
        new(GrammarBlock.Formulas, "构成公式", EntryBlockTier.Core),
        new(GrammarBlock.Usages, "用法详解", EntryBlockTier.Core),
        new(GrammarBlock.Signals, "标志词与信号", EntryBlockTier.Core),
        new(GrammarBlock.Rules, "形式变化规则", EntryBlockTier.Core),
        new(GrammarBlock.Contrasts, "易混对比", EntryBlockTier.Extended, Insight: true),
        new(GrammarBlock.Exceptions, "特殊情况与例外", EntryBlockTier.Extended, Insight: true),
        new(GrammarBlock.Mistakes, "常见错误", EntryBlockTier.Extended),
        new(GrammarBlock.ExamPoints, "考点与解题技巧", EntryBlockTier.Extended, Insight: true),
        new(GrammarBlock.Mnemonic, "记忆口诀", EntryBlockTier.Extended, Insight: true),
        new(GrammarBlock.Exercises, "小练习", EntryBlockTier.Extended),
        new(GrammarBlock.Related, "相关语法", EntryBlockTier.Extended)
    ];

    public static GrammarBlockSpec For(GrammarBlock block) => All.Single(s => s.Block == block);
}

/// <summary>Normalization shared by the library index and the planner, so spelling variants resolve to one grammar point.</summary>
public static class GrammarText
{
    private static readonly string[] NoiseSuffixes =
    [
        "的用法与例句", "的用法和例句", "的用法", "的讲解", "的详解", "的精解", "的知识点", "用法", "详解", "精解", "讲解", "知识点", "语法点", "语法"
    ];

    private static string Letters(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormKC))
            if (char.IsLetterOrDigit(c)) builder.Append(char.ToLowerInvariant(c));
        return builder.ToString();
    }

    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var normalized = Letters(text);
        for (var changed = true; changed;)
        {
            changed = false;
            foreach (var suffix in NoiseSuffixes)
            {
                var plain = Letters(suffix);
                if (normalized.Length - plain.Length >= 2 && normalized.EndsWith(plain, StringComparison.Ordinal))
                {
                    normalized = normalized[..^plain.Length];
                    changed = true;
                }
            }
        }
        // "过去进行时态" and "过去进行时" name the same thing.
        return normalized.Length > 2 && normalized.EndsWith("时态", StringComparison.Ordinal) ? normalized[..^1] : normalized;
    }

    /// <summary>Studied-language code from a detected source language such as "en-US" or "zh-Hant".</summary>
    public static string LanguageCode(string? detected)
    {
        var code = (detected ?? "").Trim().Replace('_', '-').ToLowerInvariant();
        if (code.StartsWith("zh", StringComparison.Ordinal)) return "zh";
        var primary = code.Split('-')[0];
        return primary is "" or "auto" or "und" ? "en" : primary;
    }
}

/// <summary>
/// Local grammar library. Lookups are exact on the canonical title or a learned alias, so the model only has to name
/// the grammar point consistently for a repeat question to cost nothing.
/// </summary>
public interface IGrammarLibrary
{
    int Count { get; }
    /// <summary>Exact match on a canonical title or alias. <paramref name="language"/> narrows the studied language.</summary>
    GrammarTopic? Find(string reader, string? language, string query);
    bool Contains(string reader, string? language, string query);
    /// <summary>Canonical title of the entry a name or alias resolves to, or null. Does not count as a use.</summary>
    string? ResolveTitle(string reader, string? language, string query);
    /// <summary>Entries whose title or alias contains, or is contained in, the query. Best match first.</summary>
    IReadOnlyList<GrammarTopicSummary> Search(string reader, string query, int limit);
    IReadOnlyList<GrammarTopicSummary> Recent(string reader, int limit);
    /// <summary>Canonical titles, most used first, for keeping model-chosen names consistent.</summary>
    IReadOnlyList<string> Titles(string reader, int limit);
    void Save(GrammarTopic topic, IEnumerable<string> aliases);
    void AddAliases(string reader, string language, string title, IEnumerable<string> aliases);
    void Remove(string reader, string language, string title);
    event EventHandler? Changed;
}
