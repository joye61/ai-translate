using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using AiTranslate.Core;

namespace AiTranslate.Ai;

public interface IAiDictionaryService
{
    Task<string?> FindSuggestionAsync(string prefix, string targetMode, CancellationToken cancellationToken);
    Task<DictionaryEntryResult> CompleteExamplesAsync(DictionaryEntryResult entry, string? contextSentence, CancellationToken cancellationToken);
    Task<DictionaryEntryResult> ReviewBlockAsync(DictionaryEntryResult entry, EntryBlock block, CancellationToken cancellationToken,
        Action<DictionaryEntryResult>? onPreview = null);
    Task<DictionaryEntryResult> CheckLexicalCoverageAsync(DictionaryEntryResult entry, string? contextSentence, CancellationToken cancellationToken);
    Task<DictionaryEntryResult> ReviewEntryAsync(DictionaryEntryResult entry, CancellationToken cancellationToken,
        Action<DictionaryEntryResult>? onPreview = null);

    /// <param name="onStreamingPreview">Receives cumulative, decoded primary translation text before the full entry is ready.</param>
    /// <param name="onEntryPreview">Receives provisional snapshots including text still arriving inside a dictionary field.</param>
    /// <param name="onUsableEntry">Receives a complete main translation and then the parsed entry while optional reviews continue.</param>
    Task<DictionaryEntryResult> LookupOrTranslateAsync(
        string sourceText,
        string targetLanguageId,
        string? contextSentence = null,
        Action<string>? onStreamingPreview = null,
        CancellationToken cancellationToken = default,
        Action<DictionaryEntryResult>? onEntryPreview = null,
        bool forceRefresh = false,
        bool cacheOnly = false,
        Action<DictionaryEntryResult>? onUsableEntry = null,
        DictionaryLookupIntent intent = DictionaryLookupIntent.Learning);
}

/// <summary>
/// Full AI-powered dictionary & deep linguistic analyzer.
/// Handles single words, idioms/phrases, technical terms, and full sentences/paragraphs.
/// </summary>
public sealed partial class AiDictionaryService(Func<IAiTransport?> transportFactory, IDictionaryEntryCache? cache = null,
    Func<string, string, IReadOnlyList<CollocationItem>>? lookupTerms = null,
    Action<DictionaryEntryResult, long>? refreshCollectedEntries = null, Func<string?>? readerLanguageId = null) : IAiDictionaryService
{
    // The workbench and capture popup share this service. A second lookup waits for the first SQLite commit.
    private readonly ConcurrentDictionary<string, PendingLookup> _pending = new();

    public Task<string?> FindSuggestionAsync(string prefix, string targetMode, CancellationToken cancellationToken)
    {
        if (cache is null || string.IsNullOrWhiteSpace(prefix)) return Task.FromResult<string?>(null);
        var reader = DictionaryTranslationDirection.ReaderLanguage(readerLanguageId?.Invoke());
        return Task.Run(() =>
        {
            string? targetFilter = targetMode == DictionaryTranslationDirection.AutoId ? null : TranslationLanguages.Get(targetMode).Id;
            foreach (var (source, key) in cache.FindCompletions(prefix, targetFilter, reader.Id, cancellationToken))
            {
                var target = DictionaryTranslationDirection.ResolveTarget(source, targetMode, reader).Id;
                var terms = lookupTerms?.Invoke(source, target) ?? [];
                var keys = LookupKeys(source, target, reader.Id, "", terms);
                if (key == keys.Current || key == keys.Legacy || key == "quick:" + keys.Current) return source;
            }
            return null;
        }, cancellationToken);
    }

    // Preserve case (US/us), word boundaries, context and glossary changes for both lookup and completion.
    private static (string Current, string? Legacy) LookupKeys(string source, string target, string reader,
        string contextSentence, IReadOnlyList<CollocationItem> terms)
    {
        static string Hash(object fields) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(fields)));
        return (Hash(new { schema = 4, source, target, reader, contextSentence, terms }),
            reader.StartsWith("zh-", StringComparison.Ordinal) ? Hash(new { schema = 3, source, target, contextSentence, terms }) : null);
    }

    private sealed class PendingLookup(ConcurrentDictionary<string, PendingLookup> pending, string key) : IDisposable
    {
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Dispose()
        {
            pending.TryRemove(key, out _);
            Finished.SetResult();
        }
    }

    private async Task<PendingLookup> WaitForLookupAsync(string key, CancellationToken token)
    {
        var lookup = new PendingLookup(_pending, key);
        while (true)
        {
            var active = _pending.GetOrAdd(key, lookup);
            if (active == lookup) return lookup;
            await active.Finished.Task.WaitAsync(token).ConfigureAwait(false);
        }
    }

    public async Task<DictionaryEntryResult> LookupOrTranslateAsync(
        string sourceText,
        string targetLanguageId,
        string? contextSentence = null,
        Action<string>? onStreamingPreview = null,
        CancellationToken cancellationToken = default,
        Action<DictionaryEntryResult>? onEntryPreview = null,
        bool forceRefresh = false,
        bool cacheOnly = false,
        Action<DictionaryEntryResult>? onUsableEntry = null,
        DictionaryLookupIntent intent = DictionaryLookupIntent.Learning)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceText);
        var trimmed = DictionaryQuery.CleanInput(sourceText).ReplaceLineEndings("\n");
        if (trimmed.Length == 0) throw new ArgumentException("输入中没有可翻译的内容。", nameof(sourceText));
        cancellationToken.ThrowIfCancellationRequested();
        var readerLanguage = DictionaryTranslationDirection.ReaderLanguage(readerLanguageId?.Invoke());
        var targetLanguage = DictionaryTranslationDirection.ResolveTarget(trimmed, targetLanguageId, readerLanguage);
        var started = DateTime.UtcNow.Ticks;
        var terms = lookupTerms is null ? [] : await Task.Run(() => lookupTerms(trimmed, targetLanguage.Id), cancellationToken).ConfigureAwait(false);
        contextSentence = contextSentence?.Trim().ReplaceLineEndings("\n") ?? "";
        var (cacheKey, legacyKey) = cache is null ? default : LookupKeys(trimmed, targetLanguage.Id, readerLanguage.Id, contextSentence, terms);
        var fullKey = cacheKey;
        if (cacheKey is not null && intent == DictionaryLookupIntent.Quick) cacheKey = "quick:" + cacheKey;
        using var pending = fullKey is not null && !forceRefresh && !cacheOnly
            ? await WaitForLookupAsync(fullKey, cancellationToken).ConfigureAwait(false) : null;
        if (!forceRefresh && cacheKey is not null)
        {
            var hit = await Task.Run(() =>
            {
                var full = cache!.Read(fullKey!) ?? (legacyKey is null ? null : cache.Read(legacyKey));
                var quick = cacheKey != fullKey || cacheOnly && full is null ? cache.Read("quick:" + fullKey) : null;
                var newest = full is null || quick?.CacheStamp?.Revision > full.CacheStamp?.Revision ? quick : full;
                return newest is null ? null : RestoreCached(newest);
            }, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (hit is not null)
            {
                onUsableEntry?.Invoke(hit);
                onEntryPreview?.Invoke(hit);
                // Migrate incomplete pre-pipeline records with the existing narrow repairs. Preserve
                // reviewed legacy fields and keep their whole-entry review status unknown.
                if (!cacheOnly && intent != DictionaryLookupIntent.Quick && hit.Processing is null && hit.ReviewedBlocks.Count == 0)
                {
                    var updated = await ReviewLookupCoverageAsync(hit, readerLanguage.Id, contextSentence, cancellationToken).ConfigureAwait(false);
                    updated = await CompleteTranslationOptionsAsync(updated, readerLanguage.Id, contextSentence, cancellationToken).ConfigureAwait(false);
                    if (!ReferenceEquals(updated, hit))
                        return await SaveResultAsync(updated with { Origin = DictionaryResultOrigin.Supplemented }, hit.CacheStamp).ConfigureAwait(false);
                }
                return hit;
            }
        }

        if (cacheOnly) throw new AiException("这条本地建议已失效，请重新查询。");
        var transport = transportFactory();
        if (transport is null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!forceRefresh && fullKey is not null && intent == DictionaryLookupIntent.Learning)
            {
                var quick = await Task.Run(() => cache!.Read("quick:" + fullKey), cancellationToken).ConfigureAwait(false);
                if (quick is not null)
                {
                    var available = RestoreCached(quick);
                    onUsableEntry?.Invoke(available);
                    onEntryPreview?.Invoke(available);
                    return available;
                }
            }
            if (!forceRefresh && contextSentence.Length == 0 && BuiltInShowcaseLexicon.TryGet(trimmed, out var builtin) && builtin.TargetLanguage == targetLanguage.Id)
                return await SaveResultAsync(builtin with { SourceText = trimmed, Origin = DictionaryResultOrigin.BuiltIn, GlossaryReferences = terms }).ConfigureAwait(false);
            throw new AiException("请在「引擎与快捷键」中配置 AI 引擎，以查询这条内容。");
        }

        try
        {
            var messages = new JsonObject[]
            {
                new()
                {
                    ["role"] = "system",
                    ["content"] = intent == DictionaryLookupIntent.Quick ? QuickGenerationPrompt(targetLanguage, readerLanguage) : CoreGenerationPrompt(targetLanguage, readerLanguage)
                },
                new()
                {
                    ["role"] = "user",
                    ["content"] = new JsonObject
                    {
                        ["source_text"] = trimmed,
                        ["reader_language"] = readerLanguage.Id,
                        ["context_sentence"] = contextSentence,
                        ["english_lookup_hints"] = intent == DictionaryLookupIntent.Quick ? null : JsonSerializer.SerializeToNode(EnglishLexicalHints.Find(trimmed)),
                        ["glossary"] = JsonSerializer.SerializeToNode(terms.Select(t => new { source = t.Phrase, translation = t.Meaning })),
                        ["target_language"] = targetLanguage.Id
                    }.ToJsonString()
                }
            };

            var preview = new DictionaryFieldPreview(onStreamingPreview, onEntryPreview is null ? null : root =>
                onEntryPreview(ForIntent(ReadEntry(trimmed, targetLanguage.Id, root, preview: true), intent)), onUsableEntry is null ? null :
                primary => onUsableEntry(new DictionaryEntryResult
                {
                    SourceText = trimmed, PrimaryTranslation = primary, TargetLanguage = targetLanguage.Id,
                    ItemType = DictionaryQuery.SuggestType(trimmed), ReaderLanguage = readerLanguage.Id,
                    GlossaryReferences = terms, LookupContext = contextSentence
                }));
            var run = new DictionaryRun(transport);
            var completion = await run.RequestAsync("translation", messages, preview.Append,
                intent == DictionaryLookupIntent.Quick && trimmed.Length < 800 ? 2048 : 8192, cancellationToken).ConfigureAwait(false);
            var result = ForIntent(ParseDictionaryResponse(trimmed, targetLanguage.Id, completion.Text), intent) with
            {
                GlossaryReferences = terms, ReaderLanguage = readerLanguage.Id, LookupContext = contextSentence,
                Processing = new() { Core = DictionaryStageState.Completed, Intent = intent, Usage = run.Usage.ToArray() }
            };
            onUsableEntry?.Invoke(result);
            result = await FinishStagesAsync(run, result, ReadCoreIssues(completion.Text, trimmed),
                onEntryPreview, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return await SaveResultAsync(result).ConfigureAwait(false);
        }
        finally
        {
            (transport as IDisposable)?.Dispose();
        }

        DictionaryEntryResult RestoreCached(DictionaryEntryResult hit)
        {
            var result = hit with { Origin = DictionaryResultOrigin.Cache, LookupContext = contextSentence };
            return result.CacheStamp is { } stamp ? SyncCollectedEntries(result, stamp.Revision) : result;
        }

        Task<DictionaryEntryResult> SaveResultAsync(DictionaryEntryResult result, DictionaryCacheStamp? previous = null)
        {
            result = result with { LookupContext = contextSentence };
            return Task.Run(() =>
            {
                var revision = started;
                if (cacheKey is not null && cache is not null)
                {
                    if (forceRefresh && result.Processing is { } stages &&
                        (stages.Extension == DictionaryStageState.Failed || stages.Review == DictionaryStageState.Failed) &&
                        cache.Read(cacheKey) is { Processing.Review: DictionaryStageState.Completed } lastGood)
                        throw new AiException("刷新时未能完成解析或审校，已保留上次核对的版本。");
                    bool saved;
                    if (previous is not null && previous.Key == cacheKey)
                    {
                        revision = Math.Max(revision, previous.Revision + 1);
                        saved = cache.TryReplace(previous, result, revision);
                    }
                    else saved = cache.Write(cacheKey, result, revision);
                    if (!saved)
                        return cache.Read(cacheKey) is { } latest ? RestoreCached(latest) : result;
                    result = result with { CacheStamp = new(cacheKey, revision) };
                }
                return result.Origin is DictionaryResultOrigin.Ai or DictionaryResultOrigin.Supplemented
                    ? SyncCollectedEntries(result, revision) : result;
            }, cancellationToken);
        }
    }

    public static DictionaryEntryResult ParseDictionaryResponse(string sourceText, string targetLanguageId, string rawResponse)
    {
        var jsonText = AiTranslationService.StripMarkdownCodeFence(rawResponse);
        try
        {
            using var doc = JsonDocument.Parse(jsonText);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new JsonException("Dictionary response root is not a JSON object.");

            return ReadEntry(sourceText, targetLanguageId, root);
        }
        catch (JsonException)
        {
            // A plain-text translation is still useful. Never display broken wire JSON as a definition.
            if (string.IsNullOrWhiteSpace(jsonText) || jsonText.TrimStart()[0] is '{' or '[')
                throw new AiException("词条数据不完整，请重新查询。");
            return new DictionaryEntryResult
            {
                SourceText = sourceText, PrimaryTranslation = jsonText, TargetLanguage = targetLanguageId,
                ItemType = DictionaryQuery.SuggestType(sourceText)
            };
        }
    }

    private static DictionaryEntryResult ReadEntry(string sourceText, string targetLanguageId, JsonElement root, bool preview = false,
        DictionaryEntryResult? reviewOf = null)
    {
        var primary = GetString(root, "primaryTranslation");
        if (!preview && string.IsNullOrWhiteSpace(primary)) throw new AiException("引擎未返回译文，请重试。");

        var itemTypeStr = GetString(root, "itemType");
        if (!Enum.TryParse<VocabularyItemType>(itemTypeStr, ignoreCase: true, out var itemType) || !Enum.IsDefined(itemType))
            itemType = DictionaryQuery.SuggestType(sourceText);
        else if (itemType == VocabularyItemType.Sentence && sourceText.ReplaceLineEndings("\n").Contains("\n\n"))
            itemType = VocabularyItemType.Paragraph;

        var senses = new List<PartOfSpeechSense>();
        if (root.TryGetProperty("senses", out var sensesEl) && sensesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in sensesEl.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var pos = GetString(item, "partOfSpeech");
                var zh = GetString(item, "definitionZh");
                var en = GetString(item, "definitionEn");
                if (!string.IsNullOrWhiteSpace(zh))
                    senses.Add(new(string.IsNullOrWhiteSpace(pos) ? "释义" : pos, zh, en,
                        GetLabels(item, "scenario"), GetLabels(item, "grammarPattern"), GetLabels(item, "register"),
                        ReadUsageExample(item, preview), GetLabels(item, "grammarLabel").Trim())
                        { UsageCases = !itemType.IsPassage()
                            ? ReadUsageCases(item) : [] });
            }
        }

        var collocations = new List<CollocationItem>();
        if (root.TryGetProperty("collocations", out var colEl) && colEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in colEl.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var phrase = GetString(item, "phrase");
                var meaning = GetString(item, "meaning");
                if (!string.IsNullOrWhiteSpace(phrase))
                    collocations.Add(new(phrase, meaning, GetString(item, "senseHint"), ReadUsageExample(item, preview))
                    {
                        UsageNote = GetString(item, "usageNote").Trim() is { Length: > 0 } note ? note : null
                    });
            }
        }

        var examples = new List<BilingualExample>();
        if (root.TryGetProperty("examples", out var exEl) && exEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in exEl.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var src = GetString(item, "sourceSentence");
                var tr = GetString(item, "translatedSentence");
                var hl = GetString(item, "highlightTerm");
                if (!string.IsNullOrWhiteSpace(src))
                    examples.Add(new(src, tr, hl, GetString(item, "senseHint")));
            }
        }

        var sourceLanguage = GetString(root, "detectedSourceLanguage", "auto");
        var englishWord = itemType == VocabularyItemType.Word && DictionaryTranslationDirection.FromSourceCode(sourceLanguage)?.Id == "en";
        bool Has(EntryBlock block) => EntryBlockCatalog.Applies(itemType, block);
        var literal = ReadLiteralTranslation(root, primary, itemType, preview);
        return new DictionaryEntryResult
        {
            SourceText = sourceText,
            PrimaryTranslation = primary,
            SourceReadingNote = GetString(root, "sourceReadingNote"),
            GlossaryUsageNote = GetString(root, "glossaryUsageNote"),
            PronunciationTip = GetString(root, "pronunciationTip"),
            PronunciationDrills = Has(EntryBlock.PronunciationPractice) ? ReadPronunciationDrills(root, preview) : [],
            UsageGuidance = Has(EntryBlock.UsageNotes) ? ReadUsageGuidance(root, preview) : [],
            EtymologyNotes = Has(EntryBlock.WordFormation) ? ReadExplanations(root, "etymologyNotes", preview) : [],
            Mnemonics = Has(EntryBlock.Mnemonic) ? ReadMnemonics(root, preview) : [],
            CulturalNotes = Has(EntryBlock.Culture) ? ReadExplanations(root, "culturalNotes", preview) : [],
            ToneEvidence = Has(EntryBlock.Tone) ? ReadToneEvidence(sourceText, root, preview) : [],
            LiteralTranslation = literal,
            DetectedSourceLanguage = sourceLanguage,
            TargetLanguage = targetLanguageId,
            ItemType = itemType,
            UkPhonetic = GetString(root, "ukPhonetic"),
            UsPhonetic = GetString(root, "usPhonetic"),
            ContextMeaning = GetString(root, "contextMeaning"),
            EtymologyAndRoot = GetString(root, "etymologyAndRoot"),
            MemoryMnemonic = GetString(root, "memoryMnemonic"),
            GrammarBreakdown = GetString(root, "grammarBreakdown"),
            GrammarHighlights = itemType.IsPassage() ? ReadGrammarHighlights(sourceText, root) : [],
            TranslationDecisions = itemType.IsPassage() ? ReadTranslationDecisions(sourceText, primary, root, reviewOf is not null) : [],
            UsageNotes = GetString(root, "usageNotes"),
            CommonMistakes = GetString(root, "commonMistakes"),
            WordChoice = GetString(root, "wordChoice"),
            UsageContrasts = itemType.IsPassage() ? [] : ReadUsageContrasts(root, preview),
            Pronunciation = GetString(root, "pronunciation"),
            Senses = senses,
            Inflections = ReadInflections(root).ToArray(),
            WordFamily = Has(EntryBlock.WordFamily) ? ReadWordFamily(root, sourceText, englishWord, senses) : [],
            Collocations = collocations,
            Synonyms = GetStringArray(root, "synonyms"),
            Antonyms = GetStringArray(root, "antonyms"),
            Examples = examples,
            SuggestedTags = GetStringArray(root, "suggestedTags"),
            TranslationOptions = Has(EntryBlock.TranslationOptions) ? ReadTranslationOptions(root) : [],
            Variants = Has(EntryBlock.Variants) ? ReadVariants(root, primary, literal, preview) : [],
            KeyPoints = Has(EntryBlock.KeyPoints) ? GetStringArray(root, "keyPoints").Distinct().ToArray() : [],
            Alignment = Has(EntryBlock.Alignment) ? ReadAlignment(sourceText, primary, root) : [],
            Clauses = Has(EntryBlock.Structure) ? ReadClauses(sourceText, root) : [],
            Tone = Has(EntryBlock.Tone) ? ReadTone(root) : null,
            LiteralMeaning = Has(EntryBlock.LiteralMeaning) && (literal.Length == 0 || !string.IsNullOrEmpty(reviewOf?.LiteralMeaning))
                ? GetString(root, "literalMeaning").Trim() : "",
            CulturalNote = Has(EntryBlock.Culture) ? GetString(root, "culturalNote").Trim() : "",
            Domains = itemType.IsPassage() ? [] : GetStringArray(root, "domains").Distinct().ToArray(),
            Morphemes = Has(EntryBlock.WordFormation) ? ReadMorphemes(root) : [],
            SynonymNuances = Has(EntryBlock.SynonymNuances) ? ReadSynonymNuances(root) : [],
            Mistakes = Has(EntryBlock.Mistakes) ? ReadMistakes(root, preview) : []
        };
    }

    private static IReadOnlyList<TranslationDecision> ReadTranslationDecisions(string source, string target, JsonElement root,
        bool reviewing = false)
    {
        if (!root.TryGetProperty("translationDecisions", out var items) || items.ValueKind != JsonValueKind.Array) return [];
        var decisions = new List<TranslationDecision>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var decision = new TranslationDecision(GetString(item, "sourceQuote").Trim(), GetString(item, "targetQuote").Trim(),
                GetString(item, "reason").Trim());
            if (!decision.IsAnchoredIn(source, target) || !seen.Add(decision.SourceQuote)) continue;
            var alternative = GetString(item, "alternative").Trim();
            var condition = GetString(item, "condition").Trim();
            if (alternative.Length > 0 && condition.Length > 0 && !alternative.Equals(decision.TargetQuote, StringComparison.OrdinalIgnoreCase))
                decision = decision with { Alternative = alternative, Condition = condition };
            else if (reviewing && alternative.Length == 0 && condition.Length > 0)
                decision = decision with { Condition = condition };
            decisions.Add(decision);
        }
        return decisions;
    }

    private static IReadOnlyList<GrammarHighlight> ReadGrammarHighlights(string source, JsonElement root)
    {
        if (!root.TryGetProperty("grammarHighlights", out var items) || items.ValueKind != JsonValueKind.Array)
            return [];
        var anchors = new List<(int Start, int End, GrammarHighlight Highlight)>();
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var quote = GetString(item, "quote").Trim();
            var label = GetString(item, "label").Trim();
            var explanation = GetString(item, "explanation").Trim();
            if (quote.Length == 0 || label.Length == 0 || explanation.Length == 0) continue;
            var start = DictionaryTextMatch.IndexOf(source, quote, comparison: StringComparison.Ordinal);
            if (start < 0 || DictionaryTextMatch.IndexOf(source, quote, start + 1, StringComparison.Ordinal) >= 0) continue;
            var end = start + quote.Length;
            if (anchors.Any(a => start < a.End && end > a.Start)) continue;
            anchors.Add((start, end, new(quote, label, explanation)));
        }
        return anchors.OrderBy(a => a.Start).Select(a => a.Highlight).ToArray();
    }

    private static string GetString(JsonElement el, string prop, string fallback = "") =>
        el.TryGetProperty(prop, out var val) && val.ValueKind == JsonValueKind.String
            ? val.GetString() ?? fallback
            : fallback;

    private static BilingualExample? ReadUsageExample(JsonElement item, bool preview, string field = "example")
    {
        if (!item.TryGetProperty(field, out var example) || example.ValueKind != JsonValueKind.Object) return null;
        var source = GetString(example, "sourceSentence");
        var translation = GetString(example, "translatedSentence");
        if (string.IsNullOrWhiteSpace(source) || (!preview && string.IsNullOrWhiteSpace(translation))) return null;
        return new(source, translation, GetString(example, "highlightTerm"), GetString(example, "senseHint"));
    }

    private static IEnumerable<WordInflection> ReadInflections(JsonElement root)
    {
        if (!root.TryGetProperty("inflections", out var items) || items.ValueKind != JsonValueKind.Array) yield break;
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var label = GetString(item, "label").Trim();
            var value = GetString(item, "value").Trim();
            if (label.Length > 0 && value.Length > 0)
                yield return new(label, value, GetString(item, "partOfSpeech").Trim());
        }
    }

    private static IReadOnlyList<UsageContrast> ReadUsageContrasts(JsonElement root, bool preview)
    {
        if (!root.TryGetProperty("usageContrasts", out var items) || items.ValueKind != JsonValueKind.Array) return [];
        var contrasts = new List<UsageContrast>();
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var left = GetString(item, "leftForm").Trim();
            var right = GetString(item, "rightForm").Trim();
            var leftUse = GetString(item, "leftUse").Trim();
            var rightUse = GetString(item, "rightUse").Trim();
            if (left.Length == 0 || left == right) continue;
            if (!preview && (right.Length == 0 || leftUse.Length == 0 || rightUse.Length == 0 || leftUse == rightUse)) continue;
            if (contrasts.Any(c => (c.LeftForm == left && c.RightForm == right) || (c.LeftForm == right && c.RightForm == left))) continue;
            contrasts.Add(new(left, leftUse, right, rightUse)
            {
                LeftExample = ReadUsageExample(item, preview, "leftExample"),
                RightExample = ReadUsageExample(item, preview, "rightExample")
            });
        }
        return contrasts;
    }

    private static string GetLabels(JsonElement el, string prop) => el.TryGetProperty(prop, out var value) && value.ValueKind == JsonValueKind.Array
        ? string.Join(" · ", GetStringArray(el, prop))
        : GetString(el, prop);

    private static IReadOnlyList<string> GetStringArray(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return [];
        return arr.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(x.GetString()))
            .Select(x => x.GetString()!.Trim())
            .ToArray();
    }
}
