using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AiTranslate.Core;

namespace AiTranslate.Ai;

public sealed partial class AiDictionaryService
{
    private static readonly JsonSerializerOptions BlockJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        IgnoreReadOnlyProperties = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static string[] EditableFields(EntryBlock block, DictionaryEntryResult entry) => block switch
    {
        EntryBlock.LiteralTranslation => ["literalTranslation"],
        EntryBlock.Source => ["sourceReadingNote"],
        EntryBlock.Glossary => ["glossaryUsageNote"],
        EntryBlock.PronunciationPractice => ["pronunciationTip"],
        EntryBlock.Decisions => ["translationDecisions"],
        EntryBlock.Structure => ["clauses"],
        EntryBlock.Grammar => ["grammarHighlights"],
        EntryBlock.Culture => ["culturalNote"],
        EntryBlock.Thesaurus => ["synonyms", "antonyms"],
        EntryBlock.WordFormation => ["morphemes", "etymologyAndRoot"],
        EntryBlock.Mnemonic => ["memoryMnemonic"],
        EntryBlock.Tags => ["suggestedTags"],
        EntryBlock.UsageNotes => entry.ItemType.IsPassage() ? ["grammarBreakdown", "usageNotes", "contextMeaning"] :
            new[] { "grammarBreakdown", "usageNotes", entry.SynonymNuances.Count == 0 ? "wordChoice" : null,
                entry.Mistakes.Count == 0 ? "commonMistakes" : null }.OfType<string>().ToArray(),
        _ => [JsonNamingPolicy.CamelCase.ConvertName(block.ToString())]
    };

    public async Task<DictionaryEntryResult> ReviewBlockAsync(DictionaryEntryResult entry, EntryBlock block,
        CancellationToken cancellationToken, Action<DictionaryEntryResult>? onPreview = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var spec = EntryBlockCatalog.For(entry.ItemType).SingleOrDefault(s => s.Block == block)
            ?? throw new AiException("此栏目不适用于当前词条。");
        var started = DateTime.UtcNow.Ticks;
        var reader = entry.ReaderLanguage ?? DictionaryTranslationDirection.ReaderLanguage(readerLanguageId?.Invoke()).Id;
        var original = entry;
        string? missingKey = null;
        if (cache is not null)
        {
            // Saved cards can predate cache stamps or contain a lossy projection of the full lookup.
            var (stored, key) = await Task.Run(() =>
            {
                if (entry.CacheStamp is { } stamp && cache.Read(stamp.Key) is { } exact) return (exact, stamp.Key);
                var terms = lookupTerms?.Invoke(entry.SourceText, entry.TargetLanguage) ?? entry.GlossaryReferences;
                var keys = LookupKeys(entry.SourceText, entry.TargetLanguage, reader, entry.LookupContext, terms);
                return (cache.Read(keys.Current) ?? (keys.Legacy is null ? null : cache.Read(keys.Legacy)), keys.Current);
            }, cancellationToken).ConfigureAwait(false);
            if (stored is not null)
            {
                if (stored.SourceText != entry.SourceText || stored.PrimaryTranslation != entry.PrimaryTranslation ||
                    stored.TargetLanguage != entry.TargetLanguage || stored.ItemType != entry.ItemType ||
                    entry.CacheStamp is { } seen && seen != stored.CacheStamp) throw StaleEntry();
                entry = stored with { LookupContext = entry.LookupContext };
                reader = entry.ReaderLanguage ?? reader;
            }
            else missingKey = key;
        }

        var fields = EditableFields(block, entry);
        var otherSectionFields = EntryBlockCatalog.For(entry.ItemType).Where(s => s.Block != block)
            .SelectMany(s => EditableFields(s.Block, entry)).ToHashSet(StringComparer.Ordinal);
        var wire = JsonSerializer.SerializeToNode(entry, BlockJson)!.AsObject();
        var background = wire.DeepClone().AsObject();
        var current = new JsonObject();
        foreach (var field in fields)
        {
            current[field] = wire[field]?.DeepClone();
            background.Remove(field);
        }
        background.Remove("reviewedBlocks");
        background.Remove("lexicalCoverageReviewed");
        var messages = BlockReviewMessages(entry, spec, reader, fields, current, background);
        var transport = transportFactory() ?? throw new AiException("请先在「引擎与快捷键」中配置 AI 引擎。");
        string response;
        try
        {
            var stream = new DictionaryFieldPreview(null, onPreview is null ? null : root =>
            {
                if (cancellationToken.IsCancellationRequested ||
                    (!root.TryGetProperty("edits", out _) && !root.TryGetProperty("fields", out _))) return;
                // A prefix can end in the middle of a path, a string or a new row. Render only
                // readable edits, always against the original and never against the previous frame.
                DictionaryEntryResult? draft;
                try { draft = ApplyReview(entry, otherSectionFields, wire.DeepClone().AsObject(), current, root.GetRawText()); }
                catch (AiException) { return; }
                if (draft is not null) onPreview(draft);
            });
            response = (await transport.CompleteJsonAsync(messages, stream.Append, cancellationToken).ConfigureAwait(false)).Text;
        }
        finally { (transport as IDisposable)?.Dispose(); }
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(response)) throw new AiException("引擎没有返回复核内容，请重新复核。");
        var reviewed = ApplyReview(entry, otherSectionFields, wire, current, response);
        if (reviewed is null) return original;
        var updated = reviewed with
        {
            ReviewedBlocks = entry.ReviewedBlocks.Append(block).Distinct().ToArray(), ReaderLanguage = reader,
            LookupContext = entry.LookupContext, Origin = DictionaryResultOrigin.Reviewed, CacheStamp = entry.CacheStamp
        };
        if (missingKey is null)
            return await PersistSupplementAsync(entry, updated, started, cancellationToken, DictionaryResultOrigin.Reviewed).ConfigureAwait(false);
        if (!await Task.Run(() => cache!.TryAdd(missingKey, updated, started), cancellationToken).ConfigureAwait(false)) throw StaleEntry();
        updated = updated with { CacheStamp = new(missingKey, started) };
        return SyncCollectedEntries(updated, started);
    }

    private static DictionaryEntryResult? ApplyReview(DictionaryEntryResult entry, HashSet<string> otherSectionFields, JsonObject wire,
        JsonObject current, string response)
    {
        try
        {
            var reply = JsonNode.Parse(AiTranslationService.StripMarkdownCodeFence(response)) as JsonObject;
            if (reply?["status"]?.GetValue<string>() == "unchanged") return null;
            if (reply?["status"]?.GetValue<string>() != "improved") throw InvalidEdits();
            var edits = ReviewEdits(reply, current);
            var changed = new HashSet<string>(StringComparer.Ordinal);
            var addedVariants = new List<JsonNode>();
            foreach (var edit in edits)
            {
                var path = EditPath(edit?["path"]);
                // Providers sometimes append advice about other sections. Discard those operations
                // as units. Never move data across the boundary or modify the lookup identity.
                if (edit?["op"]?.GetValue<string>() == "move" &&
                    !EditPath(edit["from"]).SkipLast(1).SequenceEqual(path.SkipLast(1))) throw InvalidEdits();
                if (!current.ContainsKey(path[0]))
                {
                    var field = path is ["entry_context", var name, ..] ? name : path[0];
                    if (otherSectionFields.Contains(field)) continue;
                    throw InvalidEdits("模型试图修改本栏以外的数据。");
                }
                var normalized = path[0] == "morphemes" ? NormalizeMorphemeEdit(path, edit!) : edit!;
                if (path[0] is "synonyms" or "antonyms") NormalizeThesaurusEdit(path, normalized);
                if (!NormalizeLexicalReviewEdit(path, normalized, wire)) continue;
                var variants = path is ["variants", _] ? wire["variants"] as JsonArray : null;
                var countBefore = variants?.Count ?? 0;
                ApplyBlockEdit(wire, path, normalized);
                if (variants is not null && variants.Count > countBefore)
                {
                    var at = path[1] == "-" ? variants.Count - 1 : int.Parse(path[1]);
                    addedVariants.Add(variants[at]!);
                }
                changed.Add(path[0]);
            }
            if (changed.Count == 0) throw InvalidEdits("回复中没有适用于当前栏目的修改。");
            // Keep provider indexes stable throughout the batch. Only after the last edit may we
            // omit new rows that repeat an existing rendering, including rows moved in this batch.
            foreach (var variant in addedVariants)
                if (variant.Parent is JsonArray list && RepeatsTranslation(entry, variant)) list.Remove(variant);

            // Reuse the lookup parser for type-specific rules, quote anchors, row limits and deduplication.
            // Only copy the edited fields back. Older data in unrelated fields must survive byte-for-byte.
            var parsed = ReadEntry(entry.SourceText, entry.TargetLanguage, JsonSerializer.SerializeToElement(wire), reviewOf: entry);
            if (changed.Contains("examples") && parsed.Examples.Except(entry.Examples).Any(e => string.IsNullOrWhiteSpace(e.TranslatedSentence)) ||
                changed.Contains("collocations") && parsed.Collocations.Except(entry.Collocations).Any(c => string.IsNullOrWhiteSpace(c.Meaning) ||
                    entry.ItemType.IsPassage() && DictionaryTextMatch.IndexOf(entry.SourceText, c.Phrase) < 0) ||
                changed.Contains("alignment") && parsed.Alignment.Except(entry.Alignment).Any(a =>
                    DictionaryTextMatch.IndexOf(entry.PrimaryTranslation, a.Target, comparison: StringComparison.Ordinal) < 0))
                throw InvalidEdits();
            var validated = JsonSerializer.SerializeToNode(parsed, BlockJson)!.AsObject();
            foreach (var field in changed)
            {
                if (!RetainsContent(wire[field], validated[field])) throw InvalidEdits(field switch
                {
                    "synonymNuances" => "辨析表需有不同的词语及对应的区别说明，通常二至五项。",
                    "morphemes" => "词素拆分需有二至五项，每项都要有词素、类型和含义。",
                    _ => "模型返回的栏目内容不完整或与原词条不符。"
                });
                wire[field] = validated[field]?.DeepClone();
            }
            return changed.All(f => JsonNode.DeepEquals(current[f], wire[f])) ? null : wire.Deserialize<DictionaryEntryResult>(BlockJson);
        }
        catch (JsonException)
        {
            throw InvalidEdits("引擎返回的结构化数据不完整或无法解析。");
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or ArgumentException)
        {
            throw InvalidEdits();
        }
    }

    private static AiException InvalidEdits(string reason = "模型返回的修改格式不正确。") => new(reason);
    private static AiException StaleEntry() => new("词条与本地缓存版本不同，请打开最新查词结果后再复核。");
}
