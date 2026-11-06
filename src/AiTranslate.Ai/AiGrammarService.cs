using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiTranslate.Core;

namespace AiTranslate.Ai;

public interface IAiGrammarService
{
    int LibraryCount { get; }
    bool IsKnown(string title, string? language);
    IReadOnlyList<GrammarTopicSummary> Recent(int limit);

    /// <summary>Completes a typed prefix from the titles already in the library, so the answer is local too.</summary>
    Task<string?> FindSuggestionAsync(string prefix, CancellationToken cancellationToken);

    /// <summary>Resolve any question or sentence. A repeat question is answered from the local library without a model call.</summary>
    Task<GrammarLookupResult> AskAsync(string input, CancellationToken cancellationToken,
        Action<GrammarTopic>? onPreview = null, Action<string>? onStage = null);

    /// <summary>Open one named grammar point, from the library when it is there.</summary>
    Task<GrammarTopic> OpenTopicAsync(string title, string? language, CancellationToken cancellationToken,
        Action<GrammarTopic>? onPreview = null, Action<string>? onStage = null, bool forceRefresh = false);

    /// <summary>Write the advanced blocks of a topic that was saved with its core blocks only.</summary>
    Task<GrammarTopic> CompleteTopicAsync(GrammarTopic topic, CancellationToken cancellationToken,
        Action<GrammarTopic>? onPreview = null, Action<string>? onStage = null);
}

/// <summary>
/// Grammar studio engine. Order of work, cheapest first: exact library hit (no model), a small planning call that
/// normalizes the question to a canonical title (then a library hit again), and only then the article itself.
/// </summary>
public sealed class AiGrammarService(Func<IAiTransport?> transportFactory, IGrammarLibrary? library = null,
    Func<string?>? readerLanguageId = null) : IAiGrammarService
{
    public const int MaxInputLength = 2000;
    private const int KnownTitleLimit = 40;

    // CJK stays literal on the wire; escaped \uXXXX sequences would cost several times the tokens.
    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private sealed record PlannedTopic(string Title, string Reuse, string SourceTerm, string Category, string Summary,
        IReadOnlyList<string> Aliases);
    private sealed record GrammarPlan(string Kind, string Language, string Message, IReadOnlyList<PlannedTopic> Topics,
        GrammarSentenceAnalysis? Sentence);

    public int LibraryCount => library?.Count ?? 0;

    public bool IsKnown(string title, string? language) =>
        library is not null && !string.IsNullOrWhiteSpace(title) && library.Contains(Reader().Id, language, title);

    public IReadOnlyList<GrammarTopicSummary> Recent(int limit) => library?.Recent(Reader().Id, limit) ?? [];

    public Task<string?> FindSuggestionAsync(string prefix, CancellationToken cancellationToken)
    {
        if (library is null || string.IsNullOrWhiteSpace(prefix)) return Task.FromResult<string?>(null);
        var reader = Reader().Id;
        return Task.Run(() => library.Titles(reader, 500)
            .Where(title => title.Length > prefix.Length && title.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(title => title.Length).FirstOrDefault(), cancellationToken);
    }

    private TranslationLanguage Reader() => DictionaryTranslationDirection.ReaderLanguage(readerLanguageId?.Invoke());

    private static string LanguageName(string code) => code == "zh" ? "中文"
        : TranslationLanguages.All.FirstOrDefault(l => l.Id == code)?.ChineseName ?? code;

    private static Task<T> Local<T>(Func<T> work, CancellationToken token) => Task.Run(work, token);

    public async Task<GrammarLookupResult> AskAsync(string input, CancellationToken token,
        Action<GrammarTopic>? onPreview = null, Action<string>? onStage = null)
    {
        var query = DictionaryQuery.CleanInput(input).ReplaceLineEndings("\n").Trim();
        if (query.Length == 0) throw new ArgumentException("请输入要讲解的语法点，或粘贴一个想分析的句子。", nameof(input));
        if (query.Length > MaxInputLength) throw new AiException($"输入太长了，请缩短到 {MaxInputLength} 字以内。");
        token.ThrowIfCancellationRequested();
        var reader = Reader();
        onStage?.Invoke("正在查询本地语法库…");

        if (library is not null && query.Length <= 80)
        {
            var known = await Local(() => library.Find(reader.Id, null, query), token).ConfigureAwait(false);
            if (known is not null) return new(GrammarLookupKind.Topic, query) { Topic = known, Language = known.Language };
        }

        var candidates = library is null ? [] : await Local(() => library.Search(reader.Id, query, 6), token).ConfigureAwait(false);
        var transport = transportFactory();
        if (transport is null)
        {
            if (candidates.Count > 0)
                return new(GrammarLookupKind.Choices, query)
                {
                    Choices = candidates.Select(c => new GrammarChoice(c.Title, c.Summary, c.Category)).ToArray(),
                    Message = "还没有配置 AI 引擎，先从本地语法库里找到这些相关的语法点。"
                };
            throw new AiException("请在「应用设置」中配置 AI 引擎，以讲解这个语法点。");
        }

        try
        {
            var budget = new AiRequestBudget();
            onStage?.Invoke("正在识别语法点…");
            var plan = await PlanAsync(transport, budget, query, reader, candidates, token).ConfigureAwait(false);
            var language = GrammarText.LanguageCode(plan.Language);
            switch (plan.Kind)
            {
                case "sentence" when plan.Sentence is { } sentence:
                    return await ResolveSentenceAsync(query, sentence with { Language = language }, reader, token).ConfigureAwait(false);
                case "topic" when plan.Topics.Count == 1:
                    return await ResolveTopicAsync(transport, budget, query, plan.Topics[0], language, reader, onPreview, onStage, token)
                        .ConfigureAwait(false);
                case "topic" when plan.Topics.Count > 1:
                    var choices = new List<GrammarChoice>();
                    foreach (var planned in plan.Topics)
                    {
                        var title = library is null ? null : await Local(() => ResolveTitle(reader.Id, language, planned), token).ConfigureAwait(false);
                        choices.Add(new(title ?? planned.Title, planned.Summary, planned.Category));
                    }
                    return new(GrammarLookupKind.Choices, query) { Choices = choices, Language = language, Message = plan.Message };
                default:
                    return new(GrammarLookupKind.NotGrammar, query)
                    {
                        Message = plan.Message.Length > 0 ? plan.Message
                            : "没有从输入里识别出语法点。可以输入语法点名称（如“过去进行时”），或粘贴一个想分析的句子。"
                    };
            }
        }
        finally { (transport as IDisposable)?.Dispose(); }
    }

    private string? ResolveTitle(string reader, string language, PlannedTopic planned)
    {
        foreach (var name in new[] { planned.Reuse, planned.Title, planned.SourceTerm }.Concat(planned.Aliases))
            if (!string.IsNullOrWhiteSpace(name) && library!.ResolveTitle(reader, language, name) is { } title) return title;
        return null;
    }

    private async Task<GrammarLookupResult> ResolveSentenceAsync(string query, GrammarSentenceAnalysis sentence, TranslationLanguage reader,
        CancellationToken token)
    {
        // The same grammar point must carry the same name everywhere, or the library link would miss.
        var points = new List<GrammarSentencePoint>();
        foreach (var point in sentence.Points)
        {
            var canonical = library is null ? null
                : await Local(() => library.ResolveTitle(reader.Id, sentence.Language, point.Topic), token).ConfigureAwait(false);
            points.Add(canonical is null ? point : point with { Topic = canonical });
        }
        if (points.Count == 0)
            return new(GrammarLookupKind.NotGrammar, query) { Message = "这段文字里没有找到值得单独讲解的语法点。" };
        return new(GrammarLookupKind.Sentence, query) { Sentence = sentence with { Source = query, Points = points }, Language = sentence.Language };
    }

    private async Task<GrammarLookupResult> ResolveTopicAsync(IAiTransport transport, AiRequestBudget budget, string query,
        PlannedTopic planned, string language, TranslationLanguage reader, Action<GrammarTopic>? onPreview, Action<string>? onStage,
        CancellationToken token)
    {
        if (library is not null)
        {
            var existing = await Local(() =>
            {
                foreach (var name in new[] { planned.Reuse, planned.Title, planned.SourceTerm }.Concat(planned.Aliases))
                    if (!string.IsNullOrWhiteSpace(name) && library.Find(reader.Id, language, name) is { } hit) return hit;
                return null;
            }, token).ConfigureAwait(false);
            if (existing is not null)
            {
                // Remember how this student phrased the question, so asking it again needs no model at all.
                await Local(() =>
                {
                    library.AddAliases(reader.Id, existing.Language, existing.Title, LearnedAliases(query, planned));
                    return 0;
                }, token).ConfigureAwait(false);
                return new(GrammarLookupKind.Topic, query) { Topic = existing, Language = existing.Language };
            }
        }
        var topic = await GenerateAsync(transport, budget, planned.Title, planned, language, reader, LearnedAliases(query, planned),
            onPreview, onStage, token).ConfigureAwait(false);
        return new(GrammarLookupKind.Topic, query) { Topic = topic, Language = language };
    }

    private static IEnumerable<string> LearnedAliases(string query, PlannedTopic planned) =>
        query.Length <= 30 && !query.Contains('\n') ? planned.Aliases.Append(query) : planned.Aliases;

    public async Task<GrammarTopic> OpenTopicAsync(string title, string? language, CancellationToken token,
        Action<GrammarTopic>? onPreview = null, Action<string>? onStage = null, bool forceRefresh = false)
    {
        title = DictionaryQuery.CleanInput(title).Trim();
        if (title.Length == 0) throw new ArgumentException("缺少语法点名称。", nameof(title));
        token.ThrowIfCancellationRequested();
        var reader = Reader();
        onStage?.Invoke("正在查询本地语法库…");
        string? existingTitle = null;
        if (library is not null)
        {
            if (!forceRefresh && await Local(() => library.Find(reader.Id, language, title), token).ConfigureAwait(false) is { } hit) return hit;
            existingTitle = await Local(() => library.ResolveTitle(reader.Id, language, title), token).ConfigureAwait(false);
        }
        var transport = transportFactory() ?? throw new AiException("请在「应用设置」中配置 AI 引擎，以讲解这个语法点。");
        try
        {
            var budget = new AiRequestBudget();
            PlannedTopic? planned = null;
            var code = language ?? "en";
            // A name the library does not know may still be a spelling of something it does ("过去进行式").
            var candidates = library is null || forceRefresh ? [] : await Local(() => library.Search(reader.Id, title, 4), token).ConfigureAwait(false);
            if (candidates.Count > 0)
            {
                onStage?.Invoke("正在核对是否已有同一语法点…");
                var plan = await PlanAsync(transport, budget, title, reader, candidates, token).ConfigureAwait(false);
                if (plan.Kind == "topic" && plan.Topics.Count == 1)
                {
                    planned = plan.Topics[0];
                    code = GrammarText.LanguageCode(plan.Language);
                    var found = await Local(() =>
                    {
                        foreach (var name in new[] { planned.Reuse, planned.Title, planned.SourceTerm }.Concat(planned.Aliases))
                            if (!string.IsNullOrWhiteSpace(name) && library!.Find(reader.Id, code, name) is { } match) return match;
                        return null;
                    }, token).ConfigureAwait(false);
                    if (found is not null)
                    {
                        await Local(() =>
                        {
                            library!.AddAliases(reader.Id, found.Language, found.Title, planned.Aliases.Append(title));
                            return 0;
                        }, token).ConfigureAwait(false);
                        return found;
                    }
                }
            }
            return await GenerateAsync(transport, budget, existingTitle ?? planned?.Title ?? title, planned, code, reader,
                planned is null ? [title] : planned.Aliases.Append(title), onPreview, onStage, token).ConfigureAwait(false);
        }
        finally { (transport as IDisposable)?.Dispose(); }
    }

    public async Task<GrammarTopic> CompleteTopicAsync(GrammarTopic topic, CancellationToken token,
        Action<GrammarTopic>? onPreview = null, Action<string>? onStage = null)
    {
        if (topic.Extended) return topic;
        var transport = transportFactory() ?? throw new AiException("请在「应用设置」中配置 AI 引擎，以补全这个语法点。");
        try
        {
            var reader = Reader();
            var extended = await ExtendAsync(transport, new AiRequestBudget(2), topic, reader, onPreview, onStage, token).ConfigureAwait(false);
            await SaveAsync(extended, [], token).ConfigureAwait(false);
            return extended;
        }
        finally { (transport as IDisposable)?.Dispose(); }
    }

    private async Task<GrammarPlan> PlanAsync(IAiTransport transport, AiRequestBudget budget, string query, TranslationLanguage reader,
        IReadOnlyList<GrammarTopicSummary> candidates, CancellationToken token)
    {
        var titles = library is null ? [] : await Local(() => library.Titles(reader.Id, KnownTitleLimit), token).ConfigureAwait(false);
        var messages = new JsonObject[]
        {
            new() { ["role"] = "system", ["content"] = GrammarPrompts.Plan(reader) },
            new()
            {
                ["role"] = "user",
                ["content"] = JsonSerializer.Serialize(new
                {
                    input = query, reader_language = reader.Id,
                    library_candidates = candidates.Select(c => new { c.Title, c.Language, c.Category }),
                    library_titles = titles
                }, Wire)
            }
        };
        var reply = await RequestAsync(transport, messages, _ => { }, 3072, budget, token).ConfigureAwait(false);
        try
        {
            using var doc = JsonDocument.Parse(AiTranslationService.StripMarkdownCodeFence(reply.Text));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new JsonException();
            var kind = GrammarJson.Str(root, "kind").ToLowerInvariant();
            var language = GrammarJson.Str(root, "language");
            var topics = new List<PlannedTopic>();
            foreach (var item in GrammarJson.Objects(root, "topics"))
            {
                var title = GrammarJson.Str(item, "title");
                if (title.Length == 0 || topics.Any(t => GrammarText.Normalize(t.Title) == GrammarText.Normalize(title))) continue;
                topics.Add(new(title, GrammarJson.Str(item, "reuse"), GrammarJson.Str(item, "sourceTerm"), GrammarJson.Str(item, "category"),
                    GrammarJson.Str(item, "summary"), GrammarJson.Strings(item, "aliases")));
                if (topics.Count == 6) break;
            }
            GrammarSentenceAnalysis? sentence = null;
            if (kind == "sentence" && root.TryGetProperty("sentence", out var wire) && wire.ValueKind == JsonValueKind.Object)
                sentence = new GrammarSentenceAnalysis(query)
                {
                    Translation = GrammarJson.Str(wire, "translation"), Structure = GrammarJson.Str(wire, "structure"),
                    Points = GrammarJson.ReadPoints(query, wire, "points")
                };
            return new(kind, language, GrammarJson.Str(root, "message"), topics, sentence);
        }
        catch (JsonException) { throw new AiException("语法识别结果不完整，请重新输入。"); }
    }

    private async Task<GrammarTopic> GenerateAsync(IAiTransport transport, AiRequestBudget budget, string title, PlannedTopic? planned,
        string language, TranslationLanguage reader, IEnumerable<string> learnedAliases, Action<GrammarTopic>? onPreview,
        Action<string>? onStage, CancellationToken token)
    {
        var seed = new GrammarTopic
        {
            Title = title, Language = language, ReaderLanguage = reader.Id, SourceTerm = planned?.SourceTerm ?? "",
            Category = planned?.Category ?? "", Aliases = planned?.Aliases ?? [], Extended = false
        };
        onStage?.Invoke("正在撰写讲解…");
        var titles = library is null ? [] : await Local(() => library.Titles(reader.Id, KnownTitleLimit), token).ConfigureAwait(false);
        var messages = new JsonObject[]
        {
            new() { ["role"] = "system", ["content"] = GrammarPrompts.Core(reader, LanguageName(language)) },
            new()
            {
                ["role"] = "user",
                ["content"] = JsonSerializer.Serialize(new { title, language, reader_language = reader.Id, library_titles = titles },
                    Wire)
            }
        };
        var stream = new DictionaryFieldPreview(null, onPreview is null ? null : root => onPreview(GrammarJson.ReadCore(root, seed, preview: true)));
        var reply = await RequestAsync(transport, messages, stream.Append, 7000, budget, token).ConfigureAwait(false);
        GrammarTopic core;
        try
        {
            using var doc = JsonDocument.Parse(AiTranslationService.StripMarkdownCodeFence(reply.Text));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
            core = GrammarJson.ReadCore(doc.RootElement, seed, preview: false);
        }
        catch (JsonException) { throw new AiException("语法讲解数据不完整，请重新查询。"); }
        if (core.Summary.Length == 0 || core.Usages.Count == 0 && core.Formulas.Count == 0)
            throw new AiException("语法讲解数据不完整，请重新查询。");
        core = core with { Title = title, Origin = GrammarOrigin.Ai };
        var aliases = learnedAliases.ToArray();

        try
        {
            core = await ExtendAsync(transport, budget, core, reader, onPreview, onStage, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The core article is already worth keeping; the advanced blocks can be completed later.
            await SaveAsync(core, aliases, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) when (ex is AiException or JsonException)
        {
            onStage?.Invoke("进阶内容未能生成，已保存核心讲解，可稍后补全。");
        }
        await SaveAsync(core, aliases, CancellationToken.None).ConfigureAwait(false);
        return core;
    }

    private async Task<GrammarTopic> ExtendAsync(IAiTransport transport, AiRequestBudget budget, GrammarTopic topic,
        TranslationLanguage reader, Action<GrammarTopic>? onPreview, Action<string>? onStage, CancellationToken token)
    {
        onStage?.Invoke("正在补充易混对比与练习…");
        var titles = library is null ? [] : await Local(() => library.Titles(reader.Id, KnownTitleLimit), token).ConfigureAwait(false);
        var core = new
        {
            topic.Title, topic.Summary, topic.Concept,
            formulas = topic.Formulas.Select(f => new { f.Label, f.Pattern }),
            usages = topic.Usages.Select(u => new { u.Title, examples = u.Examples.Select(e => e.SourceSentence) })
        };
        var messages = new JsonObject[]
        {
            new() { ["role"] = "system", ["content"] = GrammarPrompts.Extension(reader, LanguageName(topic.Language)) },
            new()
            {
                ["role"] = "user",
                ["content"] = JsonSerializer.Serialize(new { title = topic.Title, language = topic.Language, reader_language = reader.Id, core, library_titles = titles },
                    Wire)
            }
        };
        var stream = new DictionaryFieldPreview(null, onPreview is null ? null : root => onPreview(GrammarJson.ReadExtension(root, topic, preview: true)));
        var reply = await RequestAsync(transport, messages, stream.Append, 6000, budget, token).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(AiTranslationService.StripMarkdownCodeFence(reply.Text));
        if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
        var extended = GrammarJson.ReadExtension(doc.RootElement, topic, preview: false);
        if (extended.Contrasts.Count == 0 && extended.Mistakes.Count == 0 && extended.Exercises.Count == 0)
            throw new AiException("进阶内容不完整。");
        return extended with { Extended = true };
    }

    private async Task SaveAsync(GrammarTopic topic, IEnumerable<string> aliases, CancellationToken token)
    {
        if (library is null) return;
        try { await Local(() => { library.Save(topic, aliases); return 0; }, token).ConfigureAwait(false); }
        // The article is still usable when the library cannot be written; the next question simply asks the model again.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Trace.TraceWarning("Grammar library write failed: {0}", ex.Message);
        }
    }

    private static async Task<AiCompletion> RequestAsync(IAiTransport transport, JsonObject[] messages, Action<string> preview,
        int outputLimit, AiRequestBudget budget, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var reply = await transport.CompleteJsonAsync(messages, preview, token, new(budget, outputLimit)).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (reply.Calls.Count != 0) throw new AiException("语法请求返回了不适用的工具调用。");
        return reply;
    }
}

