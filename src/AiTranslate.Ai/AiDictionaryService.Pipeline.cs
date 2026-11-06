using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AiTranslate.Core;

namespace AiTranslate.Ai;

public sealed partial class AiDictionaryService
{
    public async Task<DictionaryEntryResult> ReviewEntryAsync(DictionaryEntryResult entry, CancellationToken cancellationToken,
        Action<DictionaryEntryResult>? onPreview = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var started = DateTime.UtcNow.Ticks;
        var reader = entry.ReaderLanguage ?? DictionaryTranslationDirection.ReaderLanguage(readerLanguageId?.Invoke()).Id;
        var (stored, missingKey) = await RestoreForReviewAsync(entry, reader, cancellationToken).ConfigureAwait(false);
        stored = stored with { ReaderLanguage = stored.ReaderLanguage ?? reader };
        var state = stored.Processing ?? new() { Core = DictionaryStageState.Completed, Intent = DictionaryLookupIntent.Learning };
        var resumeExtension = state.CanResumeExtension;
        if (!resumeExtension && state.PolicyVersion == DictionaryProcessing.CurrentPolicyVersion &&
            state.Review is DictionaryStageState.Completed or DictionaryStageState.Unresolved && state.ReviewedContentHash == ContentHash(stored))
            return entry;
        var transport = transportFactory() ?? throw new AiException("请先配置 AI 引擎，再核对词条。");
        try
        {
            var run = new DictionaryRun(transport);
            stored = stored with { Processing = state with { Message = "", PolicyVersion = DictionaryProcessing.CurrentPolicyVersion } };
            // Completed reviews retain their corrections for display, not as open problems.
            IReadOnlyList<DictionaryQualityIssue> issues = state.Review == DictionaryStageState.Completed ? [] : state.Issues;
            var reviewed = resumeExtension
                ? await FinishStagesAsync(run, stored, issues, onPreview, cancellationToken).ConfigureAwait(false)
                : await RunQualityReviewAsync(run, stored, issues, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            reviewed = WithReviewSync(stored, reviewed with { Origin = DictionaryResultOrigin.Reviewed,
                Processing = reviewed.Processing! with { Usage = run.Usage.ToArray() } });
            if (missingKey is null)
                return await PersistSupplementAsync(stored, reviewed, started, cancellationToken, DictionaryResultOrigin.Reviewed).ConfigureAwait(false);
            if (!await Task.Run(() => cache!.TryAdd(missingKey, reviewed, started), cancellationToken).ConfigureAwait(false)) throw StaleEntry();
            return SyncCollectedEntries(reviewed with { CacheStamp = new(missingKey, started) }, started);
        }
        finally { (transport as IDisposable)?.Dispose(); }
    }

    private static DictionaryEntryResult WithReviewSync(DictionaryEntryResult stored, DictionaryEntryResult reviewed)
    {
        var before = ContentWire(stored);
        var after = ContentWire(reviewed);
        bool Changed(string name) => !JsonNode.DeepEquals(before[name], after[name]);
        var changedBlocks = EntryBlockCatalog.For(stored.ItemType)
            .Where(spec => EditableFields(spec.Block, stored).Any(Changed)).Select(spec => spec.Block);
        var priorSync = stored.Processing?.ReviewSync;
        var headFields = new[] { "primaryTranslation", "domains", "pronunciation", "ukPhonetic", "usPhonetic" };
        var sync = new DictionaryReviewSync
        {
            KnownTranslations = (priorSync?.KnownTranslations ?? []).Append(stored.PrimaryTranslation).Distinct().ToArray(),
            ChangedHeadFields = (priorSync?.ChangedHeadFields ?? []).Union(headFields.Where(Changed)).ToArray()
        };
        return reviewed with { ReviewedBlocks = stored.ReviewedBlocks.Union(changedBlocks).ToArray(),
            Processing = reviewed.Processing! with { ReviewSync = sync } };
    }

    private sealed class DictionaryRun(IAiTransport transport)
    {
        private readonly AiRequestBudget _budget = new();
        public List<DictionaryStageUsage> Usage { get; } = [];
        public bool TryReserveRetry() => _budget.TryReserveRetry();

        public async Task<AiCompletion> RequestAsync(string role, JsonObject[] messages, Action<string> preview,
            int outputLimit, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var started = Stopwatch.GetTimestamp();
            var before = _budget.Requests;
            AiCompletion? reply = null;
            try
            {
                reply = await transport.CompleteJsonAsync(messages, preview, token, new(_budget, outputLimit)).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (reply.Calls.Count != 0) throw new AiException("翻译请求返回了不适用的工具调用。");
                return reply;
            }
            finally
            {
                Usage.Add(new(role, _budget.Requests - before, reply?.InputTokens ?? 0, reply?.OutputTokens ?? 0,
                    (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds));
            }
        }
    }

    private static readonly HashSet<string> CoreFields = new(StringComparer.Ordinal)
    {
        "primaryTranslation", "literalTranslation", "senses", "inflections", "collocations",
        "contextMeaning", "domains", "pronunciation", "ukPhonetic", "usPhonetic"
    };

    // Some providers return extra learning sections even for a quick lookup. Keep only the
    // core reading fields in both its streaming preview and its durable quick-cache record.
    private static DictionaryEntryResult ForIntent(DictionaryEntryResult entry, DictionaryLookupIntent intent) =>
        intent == DictionaryLookupIntent.Learning ? entry : new DictionaryEntryResult
        {
            SourceText = entry.SourceText, TargetLanguage = entry.TargetLanguage, DetectedSourceLanguage = entry.DetectedSourceLanguage,
            ItemType = entry.ItemType, PrimaryTranslation = entry.PrimaryTranslation, LiteralTranslation = entry.LiteralTranslation,
            ContextMeaning = entry.ContextMeaning, Senses = entry.Senses, TranslationOptions = entry.TranslationOptions,
            Pronunciation = entry.Pronunciation, UkPhonetic = entry.UkPhonetic, UsPhonetic = entry.UsPhonetic,
            Inflections = entry.Inflections, Collocations = entry.Collocations, Domains = entry.Domains, Examples = entry.Examples
        };

    private static JsonObject ContentWire(DictionaryEntryResult entry)
    {
        var wire = JsonSerializer.SerializeToNode(entry, BlockJson)!.AsObject();
        wire.Remove("processing");
        wire.Remove("reviewedBlocks");
        wire.Remove("lexicalCoverageReviewed");
        return wire;
    }

    // Hash the accepted snapshot and lookup context, not cache timestamps, progress or provider telemetry.
    private static string ContentHash(DictionaryEntryResult entry) => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new { entry.LookupContext, content = ContentWire(entry) })));

    private static HashSet<string> StageFields(DictionaryEntryResult entry, bool review)
    {
        var fields = EntryBlockCatalog.For(entry.ItemType).SelectMany(spec => EditableFields(spec.Block, entry))
            .ToHashSet(StringComparer.Ordinal);
        if (review)
        {
            fields.Add("primaryTranslation");
            if (!entry.ItemType.IsPassage()) fields.UnionWith(["domains", "pronunciation", "ukPhonetic", "usPhonetic"]);
        }
        else fields.ExceptWith(CoreFields);
        return fields;
    }

    private static JsonObject[] StageMessages(string role, string prompt, DictionaryEntryResult entry, string hash,
        HashSet<string> allowed, IReadOnlyList<DictionaryQualityIssue> issues)
    {
        var content = ContentWire(entry);
        var data = new
        {
            role, baseHash = hash, source_text = entry.SourceText, context_sentence = entry.LookupContext,
            source_language = entry.DetectedSourceLanguage, reader_language = entry.ReaderLanguage, target_language = entry.TargetLanguage,
            glossary = entry.GlossaryReferences.Select(t => new { source = t.Phrase, translation = t.Meaning }),
            english_reference = entry.Processing?.Intent == DictionaryLookupIntent.Quick ? null : CoverageReference(entry), intent = entry.Processing?.Intent.ToString(),
            missing = entry.PrimaryRenderings().Where(t => !entry.TranslationOptions.Any(o => o.Text == t && o.Usage.Length > 0)),
            allowed_fields = allowed.Order(StringComparer.Ordinal),
            entry = content,
            issues = issues.Where(issue => IssueIsAnchored(issue, entry.SourceText, content))
        };
        return [new() { ["role"] = "system", ["content"] = prompt + "\n只输出约定的 JSON 对象。" },
            new() { ["role"] = "user", ["content"] = JsonSerializer.Serialize(data, JsonSerializerOptions.Web) }];
    }

    private static DictionaryQualityIssue[] ReadCoreIssues(string raw, string source)
    {
        try
        {
            using var doc = JsonDocument.Parse(AiTranslationService.StripMarkdownCodeFence(raw));
            return doc.RootElement.TryGetProperty("qualityIssues", out var list)
                ? ReadIssues(list, source) : [];
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or AiException) { return []; }
    }

    private static DictionaryQualityIssue[] ReadIssues(JsonElement list, string source, JsonObject? reviewedContent = null)
    {
        if (list.ValueKind != JsonValueKind.Array) throw InvalidStage();
        var issues = new List<DictionaryQualityIssue>();
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) throw InvalidStage();
            var field = GetString(item, "field").Trim();
            var quote = GetString(item, "sourceQuote").Trim();
            var reason = GetString(item, "reason").Trim();
            var severity = GetString(item, "severity");
            if (field.Length == 0 || quote.Length == 0 || reason.Length == 0 ||
                severity is not ("minor" or "major" or "critical"))
                throw InvalidStage();
            // An editor can cite an error in an existing example or explanation. Anchor it
            // to that specific field in the input snapshot, never to newly proposed content.
            if (!IssueIsAnchored(new(field, quote, reason, severity), source, reviewedContent))
                throw InvalidStage("问题依据未引用原文或对应栏目的已有内容。");
            issues.Add(new(field, quote, reason, severity));
        }
        return issues.ToArray();
    }

    private static bool IssueIsAnchored(DictionaryQualityIssue issue, string source, JsonObject? content) =>
        !string.IsNullOrWhiteSpace(issue.SourceQuote) &&
        (source.Contains(issue.SourceQuote, StringComparison.Ordinal) || ContainsQuote(content?[issue.Field], issue.SourceQuote));

    private static bool ContainsQuote(JsonNode? node, string quote) => node switch
    {
        JsonValue value when value.TryGetValue<string>(out var text) => text.Contains(quote, StringComparison.Ordinal),
        JsonObject obj => obj.Any(pair => ContainsQuote(pair.Value, quote)),
        JsonArray array => array.Any(value => ContainsQuote(value, quote)),
        _ => false
    };

    private static bool HasSemanticRisk(DictionaryEntryResult entry, IReadOnlyList<DictionaryQualityIssue> issues)
    {
        if (issues.Count > 0 || entry.ItemType is VocabularyItemType.Paragraph or VocabularyItemType.TechnicalTerm ||
            entry.SourceText.Length > 120) return true;
        if (!entry.ItemType.IsPassage() && !DictionaryQuery.SuggestType(entry.SourceText).IsPassage()) return false;
        return RiskCue().IsMatch(entry.SourceText) || entry.SourceText.Any(char.IsDigit);
    }

    [GeneratedRegex(@"(?:\b(?:not|unless|except|only|without|neither|if|never|nicht|kein|nur|außer|sauf|seulement|jamais|sin|nunca|salvo|excepto|apenas|exceto|não|senza|tranne|лишь|кроме|если|только|никогда|kecuali|hanya|tanpa|nếu|trừ|không|إلا|ليس|ليست|لن|إذا|بدون)\b|并非|並非|除非|只有|不得|否则|否則|未必|不一定|ではない|なければ|に限り|지 않|경우에만|아니라|เว้นแต่|ก็ต่อเมื่อ)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RiskCue();

    [GeneratedRegex(@"\{\{[\w.]+\}\}|\$?\{[\w.]+\}|%(?:\d+\$)?[sdif]")]
    private static partial Regex Placeholder();

    private static IEnumerable<DictionaryQualityIssue> MissingPlaceholders(DictionaryEntryResult entry)
    {
        var expected = Placeholder().Matches(entry.SourceText).ToLookup(match => match.Value);
        var actual = Placeholder().Matches(entry.PrimaryTranslation).ToLookup(match => match.Value);
        foreach (var group in expected)
            if (actual[group.Key].Count() != group.Count())
                yield return new("primaryTranslation", group.Key, $"译文中的占位符 {group.Key} 与原文数量不符。");
        foreach (var group in actual)
            if (!expected.Contains(group.Key))
                yield return new("primaryTranslation", entry.SourceText, $"译文多出了占位符 {group.Key}。");
    }

    private static async Task<DictionaryEntryResult> FinishStagesAsync(DictionaryRun run, DictionaryEntryResult entry,
        IReadOnlyList<DictionaryQualityIssue> issues, Action<DictionaryEntryResult>? preview, CancellationToken token)
    {
        var state = entry.Processing!;
        issues = issues.Concat(MissingPlaceholders(entry)).Distinct().ToArray();
        var risk = HasSemanticRisk(entry, issues);
        var expand = state.Intent != DictionaryLookupIntent.Quick &&
            (!entry.ItemType.IsPassage() || risk);
        if (expand)
        {
            entry = entry with { Processing = state with { Extension = DictionaryStageState.Running } };
            preview?.Invoke(entry);
            try
            {
                var hash = ContentHash(entry);
                var allowed = StageFields(entry, review: false);
                var messages = StageMessages("learning", LearningGenerationPrompt, entry, hash, allowed, issues);
                var frozen = entry;
                var stream = new DictionaryFieldPreview(null, preview is null ? null : root =>
                {
                    if (token.IsCancellationRequested || GetString(root, "baseHash") != hash ||
                        !root.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Object) return;
                    try
                    {
                        var wire = ContentWire(frozen);
                        foreach (var field in fields.EnumerateObject())
                            if (allowed.Contains(field.Name)) wire[field.Name] = JsonNode.Parse(field.Value.GetRawText());
                        var frame = ReadEntry(frozen.SourceText, frozen.TargetLanguage, JsonSerializer.SerializeToElement(wire), preview: true);
                        preview(frame with { ReaderLanguage = frozen.ReaderLanguage, LookupContext = frozen.LookupContext,
                            GlossaryReferences = frozen.GlossaryReferences, Processing = frozen.Processing });
                    }
                    catch (Exception ex) when (IsStageError(ex)) { /* An incomplete field is not a commit. */ }
                });
                var reply = await run.RequestAsync("learning", messages, stream.Append, 6144, token).ConfigureAwait(false);
                var proposal = ReadProposal(reply.Text, hash, entry.SourceText, ContentWire(entry));
                if (proposal.LexicalAdditions is not null || proposal.ConfirmedFields.Length != 0 ||
                    proposal.Status is not ("completed" or "not_applicable" or "insufficient_evidence") ||
                    (proposal.Status != "completed" && proposal.Fields.Count != 0))
                    throw InvalidStage();
                entry = MergeLearningFields(entry, allowed, proposal.Fields);
                issues = issues.Concat(proposal.Issues).Distinct().ToArray();
                state = state with { Extension = proposal.Status switch
                {
                    "completed" => DictionaryStageState.Completed,
                    "not_applicable" => DictionaryStageState.NotApplicable,
                    _ => DictionaryStageState.InsufficientEvidence
                } };
            }
            catch (Exception ex) when (IsStageError(ex))
            {
                token.ThrowIfCancellationRequested();
                state = state with { Extension = DictionaryStageState.Failed,
                    Message = ex is AiException ? ex.Message : "学习解析数据不完整，可重新补全。" };
            }
            entry = entry with { Processing = state };
        }
        if (expand || risk)
        {
            preview?.Invoke(entry with { Processing = state with { Review = DictionaryStageState.Running } });
            entry = await RunQualityReviewAsync(run, entry, issues, token).ConfigureAwait(false);
        }
        return entry with { Processing = entry.Processing! with { Usage = run.Usage.ToArray() } };
    }

    private sealed record StageProposal(string Status, JsonObject Fields, DictionaryQualityIssue[] Issues,
        JsonObject? LexicalAdditions, string[] ConfirmedFields);

    private static StageProposal ReadProposal(string raw, string expectedHash, string source, JsonObject? reviewedContent = null)
    {
        var root = JsonNode.Parse(AiTranslationService.StripMarkdownCodeFence(raw)) as JsonObject;
        if (root is null || root["fields"] is not JsonObject fields || root["issues"] is not JsonArray list)
            throw InvalidStage("AI 回复缺少 fields 对象或 issues 数组。");
        if (root["baseHash"]?.GetValue<string>() != expectedHash)
            throw InvalidStage("AI 回复的词条版本不匹配，baseHash 必须原样回传。");
        var status = root["status"]?.GetValue<string>() ?? throw InvalidStage();
        // Some compatible models echo the response-format marker. It carries no edits.
        if (root["type"] is JsonValue marker && marker.TryGetValue<string>(out var type) && type == "json_object")
            root.Remove("type");
        if (root.Any(p => p.Key is not ("baseHash" or "status" or "fields" or "issues" or "confirmedFields" or "lexicalAdditions")))
            throw InvalidStage("AI 回复含有约定外的顶层字段，仅允许 baseHash、status、fields、issues、confirmedFields 和 lexicalAdditions。");
        return new(status, fields, ReadIssues(JsonSerializer.SerializeToElement(list), source, reviewedContent),
            root["lexicalAdditions"]?.AsObject(), root["confirmedFields"]?.Deserialize<string[]>() ?? []);
    }

    private static async Task<DictionaryEntryResult> RunQualityReviewAsync(DictionaryRun run, DictionaryEntryResult entry,
        IReadOnlyList<DictionaryQualityIssue> issues, CancellationToken token)
    {
        var state = entry.Processing ?? new() { Core = DictionaryStageState.Completed };
        try
        {
            var hash = ContentHash(entry);
            var allowed = StageFields(entry, review: true);
            var messages = StageMessages("review", QualityReviewPrompt, entry, hash, allowed, issues);
            while (true)
            {
                var completion = await run.RequestAsync("review", messages, _ => { }, 6144, token).ConfigureAwait(false);
                try { return ApplyQualityReview(entry, completion.Text, hash, allowed); }
                catch (Exception ex) when (ex is StageValidationException or JsonException or InvalidOperationException or FormatException)
                {
                    token.ThrowIfCancellationRequested();
                    if (!run.TryReserveRetry()) throw;
                    messages = [.. messages,
                        new() { ["role"] = "assistant", ["content"] = completion.Text },
                        new() { ["role"] = "user", ["content"] = JsonSerializer.Serialize(new
                        {
                            validation_error = ReviewFailureReason(ex),
                            instruction = "上次回复未通过校验，尚未应用任何修改。基于同一个输入词条重新返回完整审校 JSON；原样回传 baseHash，问题引文仅来自原文或对应字段的已有内容，遵守字段与状态约定。"
                        }) }];
                }
            }
        }
        catch (Exception ex) when (IsStageError(ex))
        {
            token.ThrowIfCancellationRequested();
            return entry with { Processing = state with { Review = DictionaryStageState.Failed,
                ReviewedContentHash = "", Issues = issues,
                Message = $"整条核对未完成：{ReviewFailureReason(ex).TrimEnd('。', ' ')}。已保留原内容，可重试。" } };
        }
    }

    private static DictionaryEntryResult ApplyQualityReview(DictionaryEntryResult entry, string response, string hash, HashSet<string> allowed)
    {
        var state = entry.Processing ?? new() { Core = DictionaryStageState.Completed };
        var reply = ReadProposal(response, hash, entry.SourceText, ContentWire(entry));
        if (reply.Status != "corrected" && (reply.LexicalAdditions is not null || reply.ConfirmedFields.Length != 0))
            throw InvalidStage();
        if (reply.LexicalAdditions is { } additions)
        {
            if (entry.ItemType.IsPassage() || state.Intent == DictionaryLookupIntent.Quick ||
                additions["senses"] is not JsonArray || additions["collocations"] is not JsonArray) throw InvalidStage();
            var covered = MergeLexicalAdditions(entry, JsonSerializer.SerializeToElement(additions), entry.LookupContext.Length == 0,
                requireTranslationQuote: true, matchDefinition: entry.ReaderLanguage == entry.TargetLanguage && entry.TargetLanguage.StartsWith("zh", StringComparison.Ordinal));
            var before = ContentWire(entry);
            foreach (var (key, value) in ContentWire(covered))
                if (!JsonNode.DeepEquals(before[key], value) && !reply.Fields.ContainsKey(key)) reply.Fields[key] = value?.DeepClone();
        }
        switch (reply.Status)
        {
            case "accepted" when reply.Fields.Count == 0 && reply.Issues.Length == 0:
                break;
            case "corrected" when reply.Fields.Count > 0 && reply.Issues.Length > 0:
                if (!reply.Issues.Any(i => reply.Fields.ContainsKey(i.Field))) throw InvalidStage();
                var candidate = MergeReviewedFields(entry, allowed, reply.Fields, out var invalidated, reply.ConfirmedFields);
                if (MissingPlaceholders(candidate).Any()) throw InvalidStage();
                entry = candidate;
                if (invalidated)
                    state = state with { Extension = DictionaryStageState.Invalidated, Message = "主译或义项已修正，已移除失效的关联内容。" };
                break;
            case "unresolved" when reply.Issues.Length > 0 && reply.Fields.Count == 0:
                return entry with { Processing = state with { Review = DictionaryStageState.Unresolved,
                    ReviewedContentHash = hash, Issues = reply.Issues, Message = reply.Issues[0].Reason } };
            default: throw InvalidStage();
        }
        var missing = MissingPlaceholders(entry).ToArray();
        if (missing.Length > 0)
            return entry with { Processing = state with { Review = DictionaryStageState.Unresolved,
                ReviewedContentHash = ContentHash(entry), Issues = missing, Message = missing[0].Reason } };
        var acceptedHash = ContentHash(entry);
        return entry with { LexicalCoverageReviewed = !entry.ItemType.IsPassage(),
            Processing = state with { Review = DictionaryStageState.Completed, ReviewedContentHash = acceptedHash,
                Issues = reply.Issues, SectionChecks = state.SectionChecks.Where(check => check.ContentHash == acceptedHash).ToArray() } };
    }

    private static string ReviewFailureReason(Exception ex) => ex is AiException ? ex.Message : "AI 回复格式或字段不完整";
    private sealed class StageValidationException(string message) : AiException(message);
    private static bool IsStageError(Exception ex) => ex is AiException or JsonException or InvalidOperationException or FormatException;
    private static AiException InvalidStage(string message = "回复的版本、证据或字段未通过校验。") => new StageValidationException(message);

    private static DictionaryEntryResult MergeLearningFields(DictionaryEntryResult entry, HashSet<string> allowed, JsonObject fields)
    {
        if (fields.Count == 0) return entry;
        var wire = ContentWire(entry);
        var proposed = wire.DeepClone().AsObject();
        var keys = fields.Select(f => f.Key).Where(allowed.Contains).ToArray();
        foreach (var key in keys) proposed[key] = fields[key]?.DeepClone();
        var parsed = ReadEntry(entry.SourceText, entry.TargetLanguage, JsonSerializer.SerializeToElement(proposed), reviewOf: entry);
        if (keys.Contains("translationOptions") && !entry.ItemType.IsPassage())
        {
            var options = entry.TranslationOptions.ToDictionary(o => o.Text, StringComparer.Ordinal);
            var candidates = entry.PrimaryRenderings().Concat(options.Keys).Distinct(StringComparer.Ordinal).ToArray();
            foreach (var option in parsed.TranslationOptions)
                if (candidates.Contains(option.Text, StringComparer.Ordinal) && option.Usage.Length > 0 &&
                    (!options.TryGetValue(option.Text, out var old) || old.Usage.Length == 0))
                    options[option.Text] = option;
            parsed = parsed with { TranslationOptions = candidates.Where(options.ContainsKey).Select(k => options[k]).ToArray() };
        }
        if (keys.Contains("examples"))
            parsed = parsed with { Examples = parsed.Examples.Where(e => !string.IsNullOrWhiteSpace(e.TranslatedSentence)).ToArray() };

        // Commit only the optional content the full reader can validate against the frozen entry.
        // One unanchored quote or duplicate row must not roll back unrelated rows or sections.
        var normalized = ContentWire(parsed);
        var usable = keys.Where(key => HasFieldContent(normalized[key])).ToArray();
        if (usable.Length == 0) throw new AiException("学习解析中没有通过校验的内容，可重新补全。");
        foreach (var key in usable) wire[key] = normalized[key]?.DeepClone();
        return RestoreMergedEntry(entry, wire);
    }

    // Review corrections can change the primary translation and its evidence together. Apply them atomically.
    private static DictionaryEntryResult MergeReviewedFields(DictionaryEntryResult entry, HashSet<string> allowed,
        JsonObject fields, out bool invalidated, string[]? confirmed = null)
    {
        invalidated = false;
        if (fields.Count == 0) return entry;
        if (fields.Any(p => !allowed.Contains(p.Key)) || confirmed?.Any(f => !allowed.Contains(f)) == true) throw InvalidStage();
        var wire = ContentWire(entry);
        var empty = ContentWire(new DictionaryEntryResult { SourceText = entry.SourceText, PrimaryTranslation = "" });
        var primaryChanged = fields.TryGetPropertyValue("primaryTranslation", out var primary) &&
            !JsonNode.DeepEquals(primary, wire["primaryTranslation"]);
        var sensesChanged = fields.TryGetPropertyValue("senses", out var senses) && !JsonNode.DeepEquals(senses, wire["senses"]);
        if (primaryChanged || sensesChanged)
        {
            // Do not carry unexamined explanations across a semantic change. The reviewer can explicitly
            // resupply still-correct fields in the same proposal. Immutable source/identity and sound forms survive.
            foreach (var key in allowed)
            {
                if (fields.ContainsKey(key) || confirmed?.Contains(key) == true ||
                    key is "primaryTranslation" or "ukPhonetic" or "usPhonetic" or "pronunciation") continue;
                if (!HasFieldContent(wire[key])) continue;
                wire[key] = empty[key]?.DeepClone();
                invalidated = true;
            }
        }
        foreach (var (key, value) in fields) wire[key] = value?.DeepClone();
        var parsed = ReadEntry(entry.SourceText, entry.TargetLanguage, JsonSerializer.SerializeToElement(wire), reviewOf: entry);
        var normalized = ContentWire(parsed);
        if (confirmed?.Any(key => !RetainsContent(wire[key], normalized[key])) == true) throw InvalidStage();
        foreach (var (key, value) in fields)
        {
            if (!RetainsContent(value, normalized[key])) throw InvalidStage();
            if (key == "examples" && parsed.Examples.Any(e => string.IsNullOrWhiteSpace(e.TranslatedSentence))) throw InvalidStage();
            if (key == "collocations" && parsed.Collocations.Any(c => string.IsNullOrWhiteSpace(c.Meaning) ||
                entry.ItemType.IsPassage() && DictionaryTextMatch.IndexOf(entry.SourceText, c.Phrase) < 0)) throw InvalidStage();
            wire[key] = normalized[key]?.DeepClone();
        }
        return RestoreMergedEntry(entry, wire);
    }

    private static DictionaryEntryResult RestoreMergedEntry(DictionaryEntryResult entry, JsonObject wire)
    {
        var merged = wire.Deserialize<DictionaryEntryResult>(BlockJson) ?? throw InvalidStage();
        return merged with { CacheStamp = entry.CacheStamp, LookupContext = entry.LookupContext, Origin = entry.Origin,
            Processing = entry.Processing, LexicalCoverageReviewed = entry.LexicalCoverageReviewed, ReviewedBlocks = entry.ReviewedBlocks };
    }

    private static bool HasFieldContent(JsonNode? node) => node switch
    {
        null => false,
        JsonArray array => array.Count > 0,
        JsonValue scalar when scalar.TryGetValue<string>(out var text) => !string.IsNullOrWhiteSpace(text),
        _ => true
    };
}
