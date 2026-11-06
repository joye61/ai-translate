namespace AiTranslate.Core;

/// <summary>Every block a dictionary or translation result can show. The catalog decides which apply to an item type.</summary>
public enum EntryBlock
{
    Source, KeyPoints, Variants, Decisions, Structure, Grammar,
    Senses, LiteralMeaning, ContextMeaning, Inflections, Collocations, Examples,
    UsageContrasts, SynonymNuances, Mistakes, UsageNotes, Tone, Culture,
    Thesaurus, WordFamily, WordFormation, Mnemonic, Tags, Glossary, TranslationOptions, LiteralTranslation, Alignment,
    PronunciationPractice, PrimaryTranslation
}

/// <summary>
/// Core blocks are the structural basis that every result of a type is expected to carry
/// (a word's senses, a passage's source). Extended blocks are optional and appear only when the AI judges them useful.
/// </summary>
public enum EntryBlockTier { Core, Extended }

/// <param name="Insight">Analytical AI content, marked with the insight sparkle in the UI.</param>
public sealed record EntryBlockSpec(EntryBlock Block, string Title, EntryBlockTier Tier, bool Insight = false);

/// <summary>Single source of truth for which blocks each kind of result has, in reading order, and how they are titled.</summary>
public static class EntryBlockCatalog
{
    // Shared by every item type, in the translation header before the type-specific detail blocks.
    public static EntryBlockSpec PrimaryTranslation { get; } = new(EntryBlock.PrimaryTranslation, "自然译文", EntryBlockTier.Core);
    public static EntryBlockSpec LiteralTranslation { get; } = new(EntryBlock.LiteralTranslation, "直译对照", EntryBlockTier.Extended);

    private static readonly EntryBlock[] WordOrder =
    [
        EntryBlock.Senses, EntryBlock.TranslationOptions, EntryBlock.ContextMeaning, EntryBlock.Inflections, EntryBlock.WordFamily, EntryBlock.Collocations, EntryBlock.Examples,
        EntryBlock.UsageContrasts, EntryBlock.SynonymNuances, EntryBlock.Mistakes, EntryBlock.UsageNotes, EntryBlock.Thesaurus,
        EntryBlock.WordFormation, EntryBlock.Mnemonic, EntryBlock.Tags, EntryBlock.Glossary, EntryBlock.PronunciationPractice
    ];

    private static readonly EntryBlock[] PhraseOrder =
    [
        EntryBlock.Senses, EntryBlock.Variants, EntryBlock.TranslationOptions, EntryBlock.LiteralMeaning, EntryBlock.ContextMeaning, EntryBlock.Collocations,
        EntryBlock.Examples, EntryBlock.UsageContrasts, EntryBlock.SynonymNuances, EntryBlock.Mistakes, EntryBlock.UsageNotes,
        EntryBlock.Culture, EntryBlock.Inflections, EntryBlock.Thesaurus, EntryBlock.Mnemonic, EntryBlock.Tags, EntryBlock.Glossary
    ];

    private static readonly EntryBlock[] TermOrder =
    [
        EntryBlock.Senses, EntryBlock.TranslationOptions, EntryBlock.ContextMeaning, EntryBlock.Collocations, EntryBlock.Examples, EntryBlock.SynonymNuances,
        EntryBlock.Mistakes, EntryBlock.UsageNotes, EntryBlock.Inflections, EntryBlock.Thesaurus, EntryBlock.WordFamily,
        EntryBlock.WordFormation, EntryBlock.Tags, EntryBlock.Glossary
    ];

    private static readonly EntryBlock[] SentenceOrder =
    [
        EntryBlock.Source, EntryBlock.Variants, EntryBlock.TranslationOptions, EntryBlock.Decisions, EntryBlock.Structure, EntryBlock.Grammar,
        EntryBlock.Collocations, EntryBlock.Tone, EntryBlock.Culture, EntryBlock.UsageNotes, EntryBlock.Glossary
    ];

    private static readonly EntryBlock[] ParagraphOrder =
    [
        EntryBlock.Source, EntryBlock.Alignment, EntryBlock.Variants, EntryBlock.KeyPoints, EntryBlock.TranslationOptions, EntryBlock.Decisions, EntryBlock.Grammar,
        EntryBlock.Collocations, EntryBlock.Tone, EntryBlock.Culture, EntryBlock.UsageNotes, EntryBlock.Glossary
    ];

    public static IReadOnlyList<EntryBlockSpec> For(VocabularyItemType type) => (type switch
    {
        VocabularyItemType.Word => WordOrder,
        VocabularyItemType.Phrase or VocabularyItemType.Idiom => PhraseOrder,
        VocabularyItemType.TechnicalTerm => TermOrder,
        VocabularyItemType.Sentence => SentenceOrder,
        _ => ParagraphOrder
    }).Select(block => Spec(block, type)).Prepend(LiteralTranslation).Prepend(PrimaryTranslation).ToArray();

    public static bool Applies(VocabularyItemType type, EntryBlock block) => For(type).Any(s => s.Block == block);

    private static EntryBlockSpec Spec(EntryBlock block, VocabularyItemType type)
    {
        var paragraph = type == VocabularyItemType.Paragraph;
        var passage = type.IsPassage();
        return block switch
        {
            EntryBlock.Source => new(block, "完整原文", EntryBlockTier.Core),
            EntryBlock.PronunciationPractice => new(block, "发音练习", EntryBlockTier.Extended),
            EntryBlock.Alignment => new(block, "逐句对照", EntryBlockTier.Extended),
            EntryBlock.Senses => new(block, type switch
            {
                VocabularyItemType.Phrase => "短语释义",
                VocabularyItemType.Idiom => "习语释义",
                VocabularyItemType.TechnicalTerm => "术语释义",
                _ => "词性与释义"
            }, EntryBlockTier.Core),
            EntryBlock.Inflections => new(block, type == VocabularyItemType.Word ? "词形变化" : "常见变体", EntryBlockTier.Core),
            EntryBlock.Examples => new(block, "双语例句", EntryBlockTier.Core),
            EntryBlock.Collocations => new(block, paragraph ? "文中术语与表达" : passage ? "重点表达" : "常用搭配", EntryBlockTier.Core),
            EntryBlock.KeyPoints => new(block, "要点速览", EntryBlockTier.Extended, Insight: true),
            EntryBlock.TranslationOptions => new(block, "译法辨析", EntryBlockTier.Extended, Insight: true),
            EntryBlock.Variants => new(block, "多种译法", EntryBlockTier.Extended, Insight: true),
            EntryBlock.Decisions => new(block, "译法与依据", EntryBlockTier.Extended, Insight: true),
            EntryBlock.Structure => new(block, "句子结构", EntryBlockTier.Extended, Insight: true),
            EntryBlock.Grammar => new(block, "关键语法", EntryBlockTier.Extended, Insight: true),
            EntryBlock.LiteralMeaning => new(block, "字面与引申", EntryBlockTier.Extended, Insight: true),
            EntryBlock.ContextMeaning => new(block, "语境义", EntryBlockTier.Extended, Insight: true),
            EntryBlock.UsageContrasts => new(block, "用法对照", EntryBlockTier.Extended),
            EntryBlock.SynonymNuances => new(block, "近义词辨析", EntryBlockTier.Extended, Insight: true),
            EntryBlock.Mistakes => new(block, "常见错误", EntryBlockTier.Extended),
            EntryBlock.UsageNotes => new(block, paragraph ? "译注" : passage ? "句式与用法" : "用法与辨析", EntryBlockTier.Extended),
            EntryBlock.Tone => new(block, "语气与语域", EntryBlockTier.Extended, Insight: true),
            EntryBlock.Culture => new(block, "文化与典故", EntryBlockTier.Extended, Insight: true),
            EntryBlock.Thesaurus => new(block, "近义与反义", EntryBlockTier.Extended),
            EntryBlock.WordFamily => new(block, "关联词与用法", EntryBlockTier.Extended),
            EntryBlock.WordFormation => new(block, "构词与词源", EntryBlockTier.Extended, Insight: true),
            EntryBlock.Mnemonic => new(block, "记忆助记", EntryBlockTier.Extended, Insight: true),
            EntryBlock.Tags => new(block, "建议标签", EntryBlockTier.Extended),
            EntryBlock.Glossary => new(block, "生词本术语参考", EntryBlockTier.Extended),
            _ => new(block, block.ToString(), EntryBlockTier.Extended)
        };
    }
}
