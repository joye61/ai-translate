using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiTranslate.Ai;
using AiTranslate.Core;
using AiTranslate.Vocabulary;
using Xunit;

namespace AiTranslate.Tests;

internal static class PipelineReplies
{
    internal static JsonObject Data(IReadOnlyList<JsonObject> messages) =>
        JsonNode.Parse(messages[1]["content"]!.GetValue<string>())!.AsObject();

    internal static string Proposal(JsonObject request, string status, object? fields = null, object? issues = null) =>
        JsonSerializer.Serialize(new { baseHash = request["baseHash"]!.GetValue<string>(), status,
            fields = fields ?? new Dictionary<string, object>(), issues = issues ?? Array.Empty<object>() });

    // Isolate streaming, cache identity and notebook tests from the number of optional role calls.
    internal static bool TryEmptyStage(IReadOnlyList<JsonObject> messages, out AiCompletion completion)
    {
        var data = Data(messages);
        var status = data["role"]?.GetValue<string>() switch { "learning" => "not_applicable", "review" => "accepted", _ => null };
        completion = status is null ? null! : new(Proposal(data, status), []);
        return status is not null;
    }
}

public sealed class DictionaryPipelineTests
{
    [Fact]
    public async Task WholeReviewCanCiteAnExistingExampleAndCachesItsCompletedStatus()
    {
        var folder = Directory.CreateTempSubdirectory();
        try
        {
            var cache = new DictionaryEntryCache(Path.Combine(folder.FullName, "review.sqlite3"));
            var entry = new DictionaryEntryResult
            {
                SourceText = "coffee", PrimaryTranslation = "咖啡", DetectedSourceLanguage = "en", ReaderLanguage = "zh-Hans",
                Senses = [new("n.", "咖啡色") { GrammarPattern = "in coffee" }],
                Processing = new() { Intent = DictionaryLookupIntent.Learning, Extension = DictionaryStageState.Completed,
                    Review = DictionaryStageState.Failed, Message = "审校未完成" }
            };
            cache.TryAdd("fixture", entry, 1);
            var script = new Script(data => PipelineReplies.Proposal(data, "corrected",
                new { senses = new[] { new { partOfSpeech = "n.", definitionZh = "咖啡色", grammarPattern = "coffee-coloured" } } },
                new[] { new { field = "senses", sourceQuote = "in coffee", reason = "此处应使用颜色形容词。", severity = "minor" } }));
            var service = new AiDictionaryService(() => script, cache);
            var reviewed = await service.ReviewEntryAsync(cache.Read("fixture")!, default);
            Assert.Equal(DictionaryStageState.Completed, reviewed.Processing!.Review);
            Assert.Equal("coffee-coloured", Assert.Single(reviewed.Senses).GrammarPattern);
            Assert.DoesNotContain("审校未完成", reviewed.Processing.Message);
            var reopened = cache.Read("fixture")!;
            Assert.Equal(reviewed.Processing.ReviewedContentHash, reopened.Processing!.ReviewedContentHash);
            Assert.Same(reopened, await service.ReviewEntryAsync(reopened, default));
            Assert.Single(script.Roles);
        }
        finally { folder.Delete(true); }
    }

    [Theory]
    [InlineData("invented evidence")]
    [InlineData("另一栏的内容")]
    [InlineData("新写的依据")]
    public async Task ReviewRejectsUnanchoredOrWrongFieldEvidenceAndStopsAfterOneRecovery(string quote)
    {
        var entry = new DictionaryEntryResult { SourceText = "coffee", PrimaryTranslation = "咖啡", UsageNotes = "另一栏的内容" };
        var script = new Script(data => PipelineReplies.Proposal(data, "corrected",
            new { primaryTranslation = "新写的依据" },
            new[] { new { field = "primaryTranslation", sourceQuote = quote, reason = "更正", severity = "minor" } }));
        var result = await new AiDictionaryService(() => script).ReviewEntryAsync(entry, default);
        Assert.Equal(entry.PrimaryTranslation, result.PrimaryTranslation);
        Assert.Equal(DictionaryStageState.Failed, result.Processing!.Review);
        Assert.Empty(result.Processing.ReviewedContentHash);
        Assert.Contains("问题依据未引用原文或对应栏目的已有内容", result.Processing.Message);
        Assert.Equal(["review", "review"], script.Roles);
    }

    [Fact]
    public async Task MalformedReviewRecoversAgainstTheOriginalSnapshotWithoutCommittingItsDraft()
    {
        var calls = 0;
        var script = new Script(data => ++calls == 1
            ? PipelineReplies.Proposal(data, "corrected", new { primaryTranslation = "不应保留的修改" })
            : PipelineReplies.Proposal(data, "accepted"));
        var entry = new DictionaryEntryResult { SourceText = "coffee", PrimaryTranslation = "咖啡" };
        var result = await new AiDictionaryService(() => script).ReviewEntryAsync(entry, default);
        Assert.Equal("咖啡", result.PrimaryTranslation);
        Assert.Equal(DictionaryStageState.Completed, result.Processing!.Review);
        Assert.Equal(2, result.Processing.Usage.Sum(u => u.Requests));
    }

    [Fact]
    public async Task ReviewIgnoresTheCompatibleProvidersJsonFormatMarker()
    {
        var script = new Script(data =>
        {
            var reply = JsonNode.Parse(PipelineReplies.Proposal(data, "accepted"))!;
            reply["type"] = "json_object";
            return reply.ToJsonString();
        });
        var result = await new AiDictionaryService(() => script).ReviewEntryAsync(
            new DictionaryEntryResult { SourceText = "coffee", PrimaryTranslation = "咖啡" }, default);
        Assert.Equal(DictionaryStageState.Completed, result.Processing!.Review);
        Assert.Single(script.Roles);
    }

    [Fact]
    public async Task ReviewConnectionFailureKeepsTheReasonAndDoesNotRetryAsAFormatError()
    {
        var script = new Script(_ => throw new AiException("无法连接到 AI 服务端点"));
        var result = await new AiDictionaryService(() => script).ReviewEntryAsync(
            new DictionaryEntryResult { SourceText = "coffee", PrimaryTranslation = "咖啡" }, default);
        Assert.Equal(DictionaryStageState.Failed, result.Processing!.Review);
        Assert.Contains("无法连接到 AI 服务端点", result.Processing.Message);
        Assert.Single(script.Roles);
    }

    [Theory]
    [InlineData(DictionaryStageState.Completed, 0)]
    [InlineData(DictionaryStageState.Failed, 1)]
    public async Task ResumingLearningUsesCurrentEvidenceAndDoesNotReopenResolvedCorrections(DictionaryStageState previousReview, int openIssues)
    {
        var entry = new DictionaryEntryResult { SourceText = "coffee", PrimaryTranslation = "咖啡",
            Senses = [new("n.", "咖啡") { GrammarLabel = "不可数" }],
            Processing = new() { Intent = DictionaryLookupIntent.Learning, Extension = DictionaryStageState.Invalidated,
                Review = previousReview, Issues = [new("senses", "不再存在的旧问题", "已修正"), new("senses", "coffee", "上轮意见")] } };
        var script = new Script(data =>
        {
            var issues = data["issues"]!.AsArray();
            Assert.DoesNotContain(issues, i => (string?)i?["sourceQuote"] == "不再存在的旧问题");
            if ((string?)data["role"] == "learning")
            {
                Assert.Equal(openIssues, issues.Count);
                return PipelineReplies.Proposal(data, "insufficient_evidence", issues: new[]
                    { new { field = "senses", sourceQuote = "不可数", reason = "核对可数性适用范围", severity = "minor" } });
            }
            Assert.Equal(openIssues + 1, issues.Count);
            Assert.Contains(issues, i => (string?)i?["sourceQuote"] == "不可数");
            return PipelineReplies.Proposal(data, "accepted");
        });
        var result = await new AiDictionaryService(() => script).ReviewEntryAsync(entry, default);
        Assert.Equal(DictionaryStageState.Completed, result.Processing!.Review);
        Assert.Equal(DictionaryStageState.InsufficientEvidence, result.Processing.Extension);
        Assert.Equal(["learning", "review"], script.Roles);
    }

    private sealed class Script(Func<JsonObject, string> respond) : IAiTransport
    {
        public List<string> Roles { get; } = [];
        public Task<AiCompletion> CompleteAsync(IReadOnlyList<JsonObject> messages, JsonArray tools, Action<string> onText, CancellationToken token)
        {
            var data = PipelineReplies.Data(messages);
            Roles.Add(data["role"]?.GetValue<string>() ?? "translation");
            var reply = respond(data);
            onText(reply);
            return Task.FromResult(new AiCompletion(reply, [], 12, 5));
        }
    }

    [Fact]
    public async Task QuickAndLearningUseDistinctCacheScopesAndOnlyTheFinalSnapshotIsReviewed()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var cache = new DictionaryEntryCache(Path.Combine(dir.FullName, "cache.sqlite3"));
            var serviceReply = new Script(data =>
            {
                Assert.Equal("ja", (string?)data["reader_language"]);
                Assert.Equal("en", (string?)data["target_language"]);
                Assert.Equal("比较", (string?)data["source_text"]);
                Assert.Equal("比较两种结果", (string?)data["context_sentence"]);
                Assert.Single(data["glossary"]!.AsArray());
                switch ((string?)data["role"])
                {
                    case "learning":
                        Assert.DoesNotContain("primaryTranslation", data["allowed_fields"]!.AsArray().Select(n => (string?)n));
                        return PipelineReplies.Proposal(data, "completed", new { usageGuidance = new[] { new { title = "使い分け", explanation = "比較する対象を示します。" } } });
                    case "review":
                        Assert.Single(data["entry"]!["usageGuidance"]!.AsArray());
                        return PipelineReplies.Proposal(data, "accepted");
                    default:
                        return """{"primaryTranslation":"compare","itemType":"Word","detectedSourceLanguage":"zh","senses":[{"partOfSpeech":"v.","definitionZh":"比べる"}]}""";
                }
            });
            var service = new AiDictionaryService(() => serviceReply, cache, (_, _) => [new("比较", "compare")], readerLanguageId: () => "ja");
            var quick = await service.LookupOrTranslateAsync("比较", "en", "比较两种结果", intent: DictionaryLookupIntent.Quick);
            Assert.Equal(["translation"], serviceReply.Roles);
            Assert.Equal(DictionaryStageState.NotRequested, quick.Processing!.Review);
            var offline = new AiDictionaryService(() => null, cache, (_, _) => [new("比较", "compare")], readerLanguageId: () => "ja");
            var available = await offline.LookupOrTranslateAsync("比较", "en", "比较两种结果");
            Assert.Equal(quick.CacheStamp, available.CacheStamp);
            var frames = new List<DictionaryEntryResult>();
            var full = await service.LookupOrTranslateAsync("比较", "en", "比较两种结果", onEntryPreview: frames.Add);
            Assert.Equal(["translation", "translation", "learning", "review"], serviceReply.Roles);
            Assert.NotEqual(quick.CacheStamp!.Key, full.CacheStamp!.Key);
            Assert.Contains(frames, f => f.Processing?.Extension == DictionaryStageState.Running);
            Assert.Contains(frames, f => f.Processing?.Review == DictionaryStageState.Running);
            Assert.Equal(DictionaryStageState.Completed, full.Processing!.Review);
            Assert.Equal(3, full.Processing.Usage.Sum(u => u.Requests));
            Assert.Equal(36, full.Processing.Usage.Sum(u => u.InputTokens));
            var reopened = await service.LookupOrTranslateAsync("比较", "en", "比较两种结果", intent: DictionaryLookupIntent.Quick);
            Assert.Equal(full.CacheStamp, reopened.CacheStamp);
            Assert.Same(reopened, await service.ReviewEntryAsync(reopened, default));
            Assert.Equal(4, serviceReply.Roles.Count);
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public async Task CoreCorrectionCommitsTogetherWithItsNewAnchorsAndRemovesStaleExplanations()
    {
        const string source = "Not all files were deleted.";
        var transport = new Script(data => (string?)data["role"] switch
        {
            "review" => PipelineReplies.Proposal(data, "corrected", new { primaryTranslation = "并非所有文件都被删除。",
                translationDecisions = new[] { new { sourceQuote = "Not all", targetQuote = "并非所有", reason = "部分否定。" } } },
                new[] { new { field = "primaryTranslation", sourceQuote = "Not all", reason = "原译把部分否定写成全部否定。", severity = "major" } }),
            _ => """{"primaryTranslation":"所有文件都没有被删除。","itemType":"Sentence","detectedSourceLanguage":"en","literalTranslation":"没有文件被删。","usageNotes":"全部否定。"}"""
        });
        var result = await new AiDictionaryService(() => transport).LookupOrTranslateAsync(source, "zh-Hans", intent: DictionaryLookupIntent.Quick);
        Assert.Equal(["translation", "review"], transport.Roles);
        Assert.Equal("并非所有文件都被删除。", result.PrimaryTranslation);
        Assert.Equal("并非所有", Assert.Single(result.TranslationDecisions).TargetQuote);
        Assert.True(string.IsNullOrEmpty(result.UsageNotes));
        Assert.Empty(result.LiteralTranslation);
        Assert.Equal(DictionaryStageState.Invalidated, result.Processing!.Extension);
        Assert.Equal(DictionaryStageState.Completed, result.Processing.Review);
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("unanchored")]
    [InlineData("identity")]
    public async Task InvalidAmendmentCannotPartiallyReplaceTheTranslation(string fault)
    {
        var transport = new Script(data =>
        {
            if (data["role"] is null) return """{"primaryTranslation":"并非所有人都知道。","itemType":"Sentence","detectedSourceLanguage":"en"}""";
            var fields = new JsonObject { ["primaryTranslation"] = "有人不知道。" };
            if (fault == "identity") fields["sourceText"] = "other source";
            if (fault == "unanchored") fields["translationDecisions"] = JsonNode.Parse("""[{"sourceQuote":"Not all","targetQuote":"不存在","reason":"说明"}]""");
            var proposal = JsonNode.Parse(PipelineReplies.Proposal(data, "corrected", fields,
                new[] { new { field = "primaryTranslation", sourceQuote = "Not all", reason = "核对范围。", severity = "major" } }))!;
            if (fault == "stale") proposal["baseHash"] = "different version";
            return proposal.ToJsonString();
        });
        var result = await new AiDictionaryService(() => transport)
            .LookupOrTranslateAsync("Not all people know.", "zh-Hans", intent: DictionaryLookupIntent.Quick);
        Assert.Equal("并非所有人都知道。", result.PrimaryTranslation);
        Assert.Equal(DictionaryStageState.Failed, result.Processing!.Review);
        Assert.Empty(result.Processing.ReviewedContentHash);
    }

    [Fact]
    public async Task ModelAcceptanceCannotOverrideAMissingPlaceholder()
    {
        var transport = new Script(data => data["role"] is null ? """{"itemType":"Sentence","primaryTranslation":"你好！"}""" :
            PipelineReplies.Proposal(data, "accepted"));
        var result = await new AiDictionaryService(() => transport).LookupOrTranslateAsync("Hello, {name}!", "zh-Hans",
            intent: DictionaryLookupIntent.Quick);
        Assert.Equal(["translation", "review"], transport.Roles);
        Assert.Equal(DictionaryStageState.Unresolved, result.Processing!.Review);
        Assert.Equal("{name}", Assert.Single(result.Processing.Issues).SourceQuote);
    }

    [Fact]
    public async Task HttpRetriesAcrossRolesShareOneRecoveryAndFourPhysicalRequests()
    {
        var roles = new List<string>();
        using var client = new HttpClient(new ScriptedHttp(async request =>
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!;
            var data = JsonNode.Parse((string)body["messages"]![1]!["content"]!)!.AsObject();
            var role = (string?)data["role"] ?? "translation";
            roles.Add(role);
            if (roles.Count == 1 || role == "learning") return new(HttpStatusCode.ServiceUnavailable)
                { Headers = { RetryAfter = new(TimeSpan.Zero) } };
            var text = role == "review" ? PipelineReplies.Proposal(data, "accepted") : """{"primaryTranslation":"测试","itemType":"Word"}""";
            var chunk = JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content = text }, finish_reason = "stop" } } });
            return new(HttpStatusCode.OK) { Content = new StringContent("data: " + chunk + "\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream") };
        }));
        var service = new AiDictionaryService(() => new AiTransport(new("compatible", "local", "http://localhost:8080/v1"), client));
        var result = await service.LookupOrTranslateAsync("test", "zh-Hans");
        Assert.Equal(["translation", "translation", "learning", "review"], roles);
        Assert.Equal(4, result.Processing!.Usage.Sum(u => u.Requests));
        Assert.Equal(DictionaryStageState.Failed, result.Processing.Extension);
        Assert.Equal(DictionaryStageState.Completed, result.Processing.Review);
        Assert.Equal("测试", result.PrimaryTranslation);
    }

    [Fact]
    public async Task ExtensionFailureResumesFromTheSavedCoreAndReviewsTheNewSnapshotOnce()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var extensionCalls = 0;
            var script = new Script(data => (string?)data["role"] switch
            {
                "learning" when ++extensionCalls == 1 => throw new AiException("暂时不可用"),
                "learning" => PipelineReplies.Proposal(data, "completed", new { usageGuidance = new[] { new { title = "搭配", explanation = "可接地点。" } } }),
                "review" => PipelineReplies.Proposal(data, "accepted"),
                _ => """{"primaryTranslation":"抵达","itemType":"Word","detectedSourceLanguage":"en"}"""
            });
            var cache = new DictionaryEntryCache(Path.Combine(dir.FullName, "cache.db"));
            var service = new AiDictionaryService(() => script, cache);
            var partial = await service.LookupOrTranslateAsync("arrive", "zh-Hans");
            Assert.True(partial.Processing!.CanResumeExtension);
            var cached = await service.LookupOrTranslateAsync("arrive", "zh-Hans");
            Assert.Equal(3, script.Roles.Count);
            var complete = await service.ReviewEntryAsync(cached, default);
            Assert.Equal(["translation", "learning", "review", "learning", "review"], script.Roles);
            Assert.Single(complete.UsageGuidance);
            Assert.Equal(DictionaryStageState.Completed, complete.Processing!.Review);
            Assert.False(complete.Processing.CanResumeExtension);
            Assert.Same(complete, await service.ReviewEntryAsync(complete, default));
        }
        finally { dir.Delete(true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PassageKeepsValidLearningRowsThroughReviewAndCache(bool resumeCachedEntry)
    {
        const string source = "How weak is the Chinese military really? There are two schools of thought. Who is right here?";
        const string translation = "中国军队到底有多弱？有两派观点。谁说得对？";
        var badHighlight = new { quote = "Who is really right here?", label = "疑问句", explanation = "追问判断。" };
        var goodHighlight = new { quote = "Who is right here?", label = "疑问句", explanation = "用提问收尾。" };
        var failFirst = resumeCachedEntry;
        var script = new Script(data =>
        {
            switch ((string?)data["role"])
            {
                case "learning" when failFirst:
                    failFirst = false;
                    return PipelineReplies.Proposal(data, "completed", new { grammarHighlights = new[] { badHighlight } });
                case "learning":
                    return PipelineReplies.Proposal(data, "completed", new
                    {
                        primaryTranslation = "扩展阶段不能改主译文。",
                        grammarHighlights = new[] { badHighlight, goodHighlight },
                        keyPoints = new[] { "文中提出了两派观点。" },
                        alignment = new[] { new { source = "There are two schools of thought.", target = "有两派观点。" },
                            new { source = "Who is right here?", target = "谁说得对？" } },
                        toneEvidence = new[] { new { quote = "not in the source", aspect = "语气", explanation = "引文有误。" } },
                        tone = new { formality = "invalid" }
                    });
                case "review":
                    return PipelineReplies.Proposal(data, "accepted");
                default:
                    return JsonSerializer.Serialize(new { primaryTranslation = translation, detectedSourceLanguage = "en", itemType = "Paragraph" });
            }
        });
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var cache = new DictionaryEntryCache(Path.Combine(directory.FullName, "passage.db"));
            var service = new AiDictionaryService(() => script, cache);
            var frames = new List<DictionaryEntryResult>();
            var result = await service.LookupOrTranslateAsync(source, "zh-Hans", onEntryPreview: frames.Add);
            if (resumeCachedEntry)
            {
                Assert.True(result.Processing!.CanResumeExtension);
                Assert.Empty(result.GrammarHighlights);
                var cached = await service.LookupOrTranslateAsync(source, "zh-Hans");
                result = await service.ReviewEntryAsync(cached, default, frames.Add);
            }
            Assert.Single(script.Roles, r => r == "translation");
            Assert.Contains(frames, f => f.Processing?.Extension == DictionaryStageState.Running && f.Alignment.Count == 2);
            Assert.Contains(frames, f => f.Processing?.Review == DictionaryStageState.Running && f.Alignment.Count == 2);
            var offline = new AiDictionaryService(() => null, cache);
            foreach (var entry in new[] { result, await offline.LookupOrTranslateAsync(source, "zh-Hans") })
            {
                Assert.Equal(translation, entry.PrimaryTranslation);
                Assert.Equal(2, entry.Alignment.Count);
                Assert.Equal(goodHighlight.quote, Assert.Single(entry.GrammarHighlights).Quote);
                Assert.Single(entry.KeyPoints);
                Assert.Empty(entry.ToneEvidence);
                Assert.Equal(DictionaryStageState.Completed, entry.Processing!.Extension);
                Assert.Equal(DictionaryStageState.Completed, entry.Processing.Review);
                Assert.False(entry.Processing.CanResumeExtension);
            }
        }
        finally { directory.Delete(true); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ManualWholeReviewSyncsOnlyItsChangesAndReopensOffline(bool correct)
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var cache = new DictionaryEntryCache(Path.Combine(dir.FullName, "lookup.db"));
            var path = Path.Combine(dir.FullName, "cards.json");
            var cards = new VocabularyService(path);
            var transport = new Script(data => (string?)data["role"] switch
            {
                "review" when correct => PipelineReplies.Proposal(data, "corrected", new { primaryTranslation = "请关门。" },
                    new[] { new { field = "primaryTranslation", sourceQuote = "Close", reason = "动词表示关闭。", severity = "major" } }),
                "review" => PipelineReplies.Proposal(data, "accepted"),
                _ => """{"itemType":"Sentence","detectedSourceLanguage":"en","primaryTranslation":"请靠近门。","literalTranslation":"靠近那扇门。"}"""
            });
            var service = new AiDictionaryService(() => transport, cache, refreshCollectedEntries: cards.RefreshCollectedEntries);
            var quick = await service.LookupOrTranslateAsync("Close the door.", "zh-Hans", "He pointed to the door.", intent: DictionaryLookupIntent.Quick);
            var card = cards.AddOrUpdateFromDictionary(quick, contextSentence: quick.LookupContext, sourceTitle: "会话课");
            var personal = new BilingualExample("Please use the side door.", "请走侧门。");
            card = cards.SaveEntry(card with { Examples = [personal], UserPersonalNote = "课堂笔记" });
            var progress = cards.SubmitReview(card.Id, ReviewRating.Good);
            var reviewed = await service.ReviewEntryAsync(quick, default);
            Assert.Equal(["translation", "review"], transport.Roles);
            Assert.Equal(DictionaryStageState.Completed, reviewed.Processing!.Review);
            var saved = new VocabularyService(path).QueryEntries(new() { NotebookId = card.NotebookId }).Single(e => e.Id == card.Id);
            Assert.Equal(reviewed.PrimaryTranslation, saved.Translation);
            Assert.Equal(reviewed.LiteralTranslation, saved.LiteralTranslation);
            Assert.Equal(personal, Assert.Single(saved.Examples));
            Assert.Equal("会话课", saved.SourceTitle);
            Assert.Equal("课堂笔记", saved.UserPersonalNote);
            Assert.Equal(progress.NextReviewAt, saved.NextReviewAt);
            Assert.Equal(quick.LookupContext, saved.DictionaryContext);
            var offline = new AiDictionaryService(() => throw new InvalidOperationException("意外调用模型"), cache);
            var reopened = await offline.LookupOrTranslateAsync(quick.SourceText, quick.TargetLanguage, quick.LookupContext, intent: DictionaryLookupIntent.Quick);
            Assert.Same(reopened, await offline.ReviewEntryAsync(reopened, default));
        }
        finally { dir.Delete(true); }
    }

    [Theory]
    [InlineData("لن يتم الحذف إلا إذا وافقت.", "只有你同意才会删除。")]
    [InlineData("並非所有檔案都有備份。", "Not all files have backups.")]
    [InlineData("승인된 경우에만 삭제됩니다.", "仅在批准后删除。")]
    public async Task SemanticRiskTriggersOneReviewForQuickMultilingualInput(string source, string translation)
    {
        var transport = new Script(data => data["role"] is null
            ? JsonSerializer.Serialize(new { primaryTranslation = translation, itemType = "Sentence" })
            : PipelineReplies.Proposal(data, "accepted"));
        var target = source.StartsWith("並非", StringComparison.Ordinal) ? "en" : "zh-Hans";
        var result = await new AiDictionaryService(() => transport).LookupOrTranslateAsync(source, target, intent: DictionaryLookupIntent.Quick);
        Assert.Equal(["translation", "review"], transport.Roles);
        Assert.Equal(DictionaryStageState.Completed, result.Processing!.Review);
    }

    [Fact]
    public async Task UnchangedBlockReceiptSurvivesAWholeEntryCheckAndAnOfflineRestart()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "local.db");
            var calls = 0;
            var transport = new Script(data =>
            {
                calls++;
                if (data["section"] is not null) return """{"status":"unchanged"}""";
                if ((string?)data["role"] == "review") return PipelineReplies.Proposal(data, "accepted");
                return """{"itemType":"Word","primaryTranslation":"书","senses":[{"partOfSpeech":"n.","definitionZh":"书籍"}]}""";
            });
            var service = new AiDictionaryService(() => transport, new DictionaryEntryCache(path));
            var entry = await service.LookupOrTranslateAsync("book", "zh-Hans", "She opened a book.", intent: DictionaryLookupIntent.Quick);
            var checkedBlock = await service.ReviewBlockAsync(entry, EntryBlock.Senses, default);
            Assert.False(Assert.Single(checkedBlock.Processing!.SectionChecks).Changed);
            var checkedEntry = await service.ReviewEntryAsync(checkedBlock, default);
            Assert.Same(checkedEntry, await service.ReviewBlockAsync(checkedEntry, EntryBlock.Senses, default));
            Assert.Equal(3, calls);
            var offline = new AiDictionaryService(() => throw new InvalidOperationException("意外调用模型"), new DictionaryEntryCache(path));
            var reopened = await offline.LookupOrTranslateAsync(entry.SourceText, entry.TargetLanguage, entry.LookupContext, intent: DictionaryLookupIntent.Quick);
            Assert.Same(reopened, await offline.ReviewBlockAsync(reopened, EntryBlock.Senses, default));
        }
        finally { dir.Delete(true); }
    }

    private sealed class ScriptedHttp(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => respond(request);
    }
}
