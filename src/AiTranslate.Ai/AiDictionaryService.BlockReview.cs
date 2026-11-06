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
        EntryBlock.PronunciationPractice => ["pronunciationTip", "pronunciationDrills"],
        EntryBlock.Decisions => ["translationDecisions"],
        EntryBlock.Structure => ["clauses"],
        EntryBlock.Grammar => ["grammarHighlights"],
        EntryBlock.Culture => ["culturalNote", "culturalNotes"],
        EntryBlock.Tone => ["tone", "toneEvidence"],
        EntryBlock.Thesaurus => ["synonyms", "antonyms"],
        EntryBlock.WordFormation => ["morphemes", "etymologyAndRoot", "etymologyNotes"],
        EntryBlock.Mnemonic => ["memoryMnemonic", "mnemonics"],
        EntryBlock.Tags => ["suggestedTags"],
        EntryBlock.UsageNotes => entry.ItemType.IsPassage() ? ["grammarBreakdown", "usageNotes", "usageGuidance", "contextMeaning"] :
            new[] { "grammarBreakdown", "usageNotes", "usageGuidance", entry.SynonymNuances.Count == 0 ? "wordChoice" : null,
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
        var restored = await RestoreForReviewAsync(entry, reader, cancellationToken).ConfigureAwait(false);
        entry = restored.Entry;
        reader = entry.ReaderLanguage ?? reader;
        var missingKey = restored.MissingKey;
        var contentHash = ContentHash(entry);
        if (entry.Processing is { PolicyVersion: DictionaryProcessing.CurrentPolicyVersion } receipt &&
            receipt.SectionChecks.Any(s => s.Block == block && s.ContentHash == contentHash))
            return original;
        var fields = EditableFields(block, entry);
        // A stray edit to the head translation from another block invalidates the reply.
        var otherSectionFields = EntryBlockCatalog.For(entry.ItemType).Where(s => s.Block != block && s.Block != EntryBlock.PrimaryTranslation)
            .SelectMany(s => EditableFields(s.Block, entry)).ToHashSet(StringComparer.Ordinal);
        var wire = JsonSerializer.SerializeToNode(entry, BlockJson)!.AsObject();
        var background = wire.DeepClone().AsObject();
        var current = new JsonObject();
        foreach (var field in fields)
        {
            current[field] = wire[field]?.DeepClone();
            background.Remove(field);
        }
        background.Remove("processing");
        background.Remove("reviewedBlocks");
        background.Remove("lexicalCoverageReviewed");
        var messages = BlockReviewMessages(entry, spec, reader, fields, current, background);
        var transport = transportFactory() ?? throw new AiException("请先在「应用设置」中配置 AI 引擎。");
        DictionaryEntryResult? reviewed;
        var budget = new AiRequestBudget(maximumRequests: 2);
        try
        {
            while (true)
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
                var response = (await transport.CompleteJsonAsync(messages, stream.Append, cancellationToken,
                    new(budget, 8192)).ConfigureAwait(false)).Text;
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (string.IsNullOrWhiteSpace(response)) throw InvalidEdits("回复为空。请返回完整的复核 JSON。");
                    reviewed = ApplyReview(entry, otherSectionFields, wire.DeepClone().AsObject(), current, response);
                    break;
                }
                catch (BlockReviewValidationException ex)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!budget.TryReserveRetry())
                        throw new AiException("本次复核未完成，请稍后重试。", ex);
                    // Both attempts start from the same snapshot. A rejected response or a streamed
                    // preview must never become the base of the correction or reach persistence.
                    onPreview?.Invoke(entry);
                    messages = [.. messages,
                        new() { ["role"] = "assistant", ["content"] = response },
                        new() { ["role"] = "user", ["content"] = JsonSerializer.Serialize(new
                        {
                            validation_error = ex.Message, field = ex.Field, accepted_content = ex.AcceptedContent,
                            instruction = "上次回复未通过当前栏目规则，尚未保存任何修改。请基于最初的 current_section 重新给出完整复核 JSON，不要接着上次的索引编辑。修正不合规则的字段或移除本次无效的新增建议，保留有效改进；不要添加其他栏目的内容。若确实没有可靠改进，返回 unchanged。"
                        }) }];
                }
            }
        }
        finally { (transport as IDisposable)?.Dispose(); }
        cancellationToken.ThrowIfCancellationRequested();
        // Detached results have nowhere to store a receipt. On persisted/managed entries
        // also remember a successful no-op, so clicking the same block again is free.
        if (reviewed is null && entry.Processing is null && cache is null) return original;
        var changed = reviewed is not null;
        var changedBlocks = entry.ReviewedBlocks.ToHashSet();
        if (changed) changedBlocks.Add(block);
        reviewed ??= entry;
        var state = entry.Processing ?? new DictionaryProcessing { Core = DictionaryStageState.Completed };
        if (changed && state.Review is DictionaryStageState.Completed or DictionaryStageState.Unresolved)
            state = state with { Review = DictionaryStageState.Invalidated, ReviewedContentHash = "", Issues = [], Message = "" };
        if (changed && block == EntryBlock.Senses && entry.Processing is not null &&
            !SameSenseInventory(entry.Senses, reviewed.Senses))
        {
            var patch = new JsonObject { ["senses"] = ContentWire(reviewed)["senses"]?.DeepClone() };
            reviewed = MergeReviewedFields(entry, StageFields(entry, true), patch, out var cleared);
            if (cleared)
            {
                state = state with { Extension = DictionaryStageState.Invalidated };
                var before = ContentWire(entry);
                var after = ContentWire(reviewed);
                foreach (var specChanged in EntryBlockCatalog.For(entry.ItemType))
                    if (EditableFields(specChanged.Block, entry).Any(key => !JsonNode.DeepEquals(before[key], after[key])))
                        changedBlocks.Add(specChanged.Block);
            }
        }
        var checkedHash = ContentHash(reviewed with { ReaderLanguage = reader, LookupContext = entry.LookupContext });
        var checks = state.SectionChecks.Where(s => s.Block != block && s.ContentHash == checkedHash)
            .Append(new DictionarySectionCheck(block, checkedHash, changed)).ToArray();
        var updated = reviewed with
        {
            Processing = state with { SectionChecks = checks },
            ReviewedBlocks = changedBlocks.ToArray(), ReaderLanguage = reader,
            LookupContext = entry.LookupContext, Origin = DictionaryResultOrigin.Reviewed, CacheStamp = entry.CacheStamp
        };
        if (changed && block == EntryBlock.PrimaryTranslation)
        {
            updated = WithReviewSync(entry, updated);
            if (entry.Alignment.Count > updated.Alignment.Count || entry.TranslationDecisions.Count > updated.TranslationDecisions.Count ||
                entry.TranslationOptions.Count > updated.TranslationOptions.Count)
                updated = updated with { Processing = updated.Processing! with { Extension = DictionaryStageState.Invalidated } };
        }
        if (missingKey is null)
            return await PersistSupplementAsync(entry, updated, started, cancellationToken, DictionaryResultOrigin.Reviewed).ConfigureAwait(false);
        if (!await Task.Run(() => cache!.TryAdd(missingKey, updated, started), cancellationToken).ConfigureAwait(false)) throw StaleEntry();
        updated = updated with { CacheStamp = new(missingKey, started) };
        return SyncCollectedEntries(updated, started);
    }

    private async Task<(DictionaryEntryResult Entry, string? MissingKey)> RestoreForReviewAsync(DictionaryEntryResult entry,
        string reader, CancellationToken cancellationToken)
    {
        string? missingKey = null;
        if (cache is not null)
        {
            // Saved cards can predate cache stamps or contain a lossy projection of the full lookup.
            var (stored, key) = await Task.Run(() =>
            {
                if (entry.CacheStamp is { } stamp && cache.Read(stamp.Key) is { } exact) return (exact, stamp.Key);
                var terms = lookupTerms?.Invoke(entry.SourceText, entry.TargetLanguage) ?? entry.GlossaryReferences;
                var keys = LookupKeys(entry.SourceText, entry.TargetLanguage, reader, entry.LookupContext, terms);
                if (entry.Processing?.Intent == DictionaryLookupIntent.Quick)
                    return (cache.Read("quick:" + keys.Current), "quick:" + keys.Current);
                return (cache.Read(keys.Current) ?? (keys.Legacy is null ? null : cache.Read(keys.Legacy)), keys.Current);
            }, cancellationToken).ConfigureAwait(false);
            if (stored is not null)
            {
                if (stored.SourceText != entry.SourceText || stored.PrimaryTranslation != entry.PrimaryTranslation ||
                    stored.TargetLanguage != entry.TargetLanguage || stored.ItemType != entry.ItemType ||
                    entry.CacheStamp is { } seen && seen != stored.CacheStamp) throw StaleEntry();
                entry = stored with { LookupContext = entry.LookupContext };
            }
            else missingKey = key;
        }
        return (entry, missingKey);
    }

    private static bool SameSenseInventory(IReadOnlyList<PartOfSpeechSense> a, IReadOnlyList<PartOfSpeechSense> b) =>
        a.Select(s => (s.PartOfSpeech, s.DefinitionZh)).Order().SequenceEqual(b.Select(s => (s.PartOfSpeech, s.DefinitionZh)).Order());

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
            var parsed = ReadEntry(entry.SourceText, entry.TargetLanguage, JsonSerializer.SerializeToElement(wire), reviewOf: entry);
            if (changed.Contains("examples") && parsed.Examples.Except(entry.Examples).Any(e => string.IsNullOrWhiteSpace(e.TranslatedSentence)) ||
                changed.Contains("collocations") && parsed.Collocations.Except(entry.Collocations).Any(c => string.IsNullOrWhiteSpace(c.Meaning) ||
                    entry.ItemType.IsPassage() && DictionaryTextMatch.IndexOf(entry.SourceText, c.Phrase) < 0) ||
                changed.Contains("alignment") && parsed.Alignment.Except(entry.Alignment).Any(a =>
                    DictionaryTextMatch.IndexOf(entry.PrimaryTranslation, a.Target, comparison: StringComparison.Ordinal) < 0))
                throw InvalidEdits();
            var validated = JsonSerializer.SerializeToNode(parsed, BlockJson)!.AsObject();
            if (changed.Contains("primaryTranslation"))
            {
                if (MissingPlaceholders(parsed).Any()) throw InvalidEdits("自然译文中的占位符必须与原文一致。");
                // The parser rechecks target quotes and duplicate renderings. Keep legacy fields
                // outside the entry type's catalog, which were never part of this review.
                var dependents = new[] { EntryBlock.Alignment, EntryBlock.Decisions, EntryBlock.Variants, EntryBlock.LiteralTranslation };
                foreach (var dependent in dependents.Where(b => EntryBlockCatalog.Applies(entry.ItemType, b)))
                {
                    var key = EditableFields(dependent, entry)[0];
                    wire[key] = validated[key]?.DeepClone();
                }
                if (!entry.ItemType.IsPassage() && entry.TranslationOptions.Count > 0)
                {
                    var removed = entry.PrimaryRenderings().Except(parsed.PrimaryRenderings()).ToHashSet(StringComparer.Ordinal);
                    wire["translationOptions"] = JsonSerializer.SerializeToNode(entry.TranslationOptions.Where(o => !removed.Contains(o.Text)), BlockJson);
                }
            }
            foreach (var field in changed)
            {
                if (!RetainsContent(wire[field], validated[field])) throw InvalidEdits(field switch
                {
                    "synonymNuances" => "辨析表需有至少两个不同的词语及对应的区别说明。",
                    "morphemes" => "词素拆分需有至少两项，每项都要有词素、类型和含义。",
                    "wordFamily" => "关联词必须满足当前 wordFamily 规则：英语 value 只能是单个单词；label、partOfSpeech、relation、usage、expression 必填，expression 和双语 example 须包含该词形。短语搭配不能作为关联词追加。",
                    _ => $"{field} 中有内容未满足当前栏目的字段或引用规则。请对照 accepted_content 修正被过滤或归一化的内容。"
                }, field, validated[field]);
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

    private sealed class BlockReviewValidationException(string reason, string? field, JsonNode? acceptedContent) : AiException(reason)
    {
        public string? Field { get; } = field;
        public JsonNode? AcceptedContent { get; } = acceptedContent?.DeepClone();
    }

    private static AiException InvalidEdits(string reason = "模型返回的修改格式不正确。", string? field = null,
        JsonNode? acceptedContent = null) => new BlockReviewValidationException(reason, field, acceptedContent);
    private static AiException StaleEntry() => new("词条与本地缓存版本不同，请打开最新查词结果后再复核。");
}
