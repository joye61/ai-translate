using System.Text.Json;
using System.Text.Json.Nodes;
using AiTranslate.Core;

namespace AiTranslate.Ai;

public interface IAiDictionaryService
{
    Task<DictionaryEntryResult> LookupOrTranslateAsync(
        string sourceText,
        string targetLanguageId,
        string? contextSentence = null,
        Action<string>? onStreamingPreview = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Full AI-powered dictionary & deep linguistic analyzer.
/// Handles single words, idioms/phrases, technical terms, and full sentences/paragraphs.
/// </summary>
public sealed class AiDictionaryService(Func<IAiTransport?> transportFactory) : IAiDictionaryService
{
    public async Task<DictionaryEntryResult> LookupOrTranslateAsync(
        string sourceText,
        string targetLanguageId,
        string? contextSentence = null,
        Action<string>? onStreamingPreview = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceText);
        var trimmed = sourceText.Trim();
        var targetLanguage = TranslationLanguages.Get(targetLanguageId);

        var transport = transportFactory();
        if (transport is null)
            return BuildOfflineFallbackEntry(trimmed, targetLanguage, contextSentence);

        try
        {
            var isWordOrShortPhrase = CountWords(trimmed) <= 5 && !trimmed.Contains('\n');
            var messages = new JsonObject[]
            {
                new()
                {
                    ["role"] = "system",
                    ["content"] = $$"""
                    你是新一代专业 AI 双语词典与语言学认知解析引擎。请针对用户输入的文本（可能是一个单词、短语、习语、专业术语，或者完整的句子/段落），生成详尽、准确的词典级解析与翻译结果，目标语言为 {{targetLanguage.ChineseName}} ({{targetLanguage.Id}})。
                    只返回一个合法的 JSON 对象（不要使用 Markdown 代码围栏，不要输出任何额外说明），结构严格如下：
                    {
                      "primaryTranslation": "核心译文或最常用释义汇总",
                      "detectedSourceLanguage": "源语言代码如 en / zh-Hans / ja",
                      "itemType": "Word | Phrase | Sentence | Idiom | TechnicalTerm",
                      "ukPhonetic": "英式音标（若为英文词汇/短语，如 /səˈrendɪpəti/，否则空字符串）",
                      "usPhonetic": "美式音标（若为英文词汇/短语，如 /ˌserənˈdɪpəti/，否则空字符串）",
                      "contextMeaning": "若提供了上下文句子或本身是长句，解释其在当前语境下的精准含义",
                      "etymologyAndRoot": "词源与词根词缀拆解（例如 prefix + root + suffix 及演变逻辑；若为长句则填写核心词汇拆解）",
                      "memoryMnemonic": "高效记忆助记法（联想记忆、谐音、画面记忆或近形词辨析，帮助背诵）",
                      "grammarBreakdown": "语法结构拆解或用法要点（长句分析主谓宾从句结构；词汇说明及物/不及物、常见句型）",
                      "usageNotes": "语域、常见易错点、同义词微妙辨析",
                      "senses": [
                        { "partOfSpeech": "n. / v. / adj. / adv. / phrase", "definitionZh": "中文释义", "definitionEn": "简明英英释义" }
                      ],
                      "inflections": [
                        { "label": "复数 / 过去式 / 过去分词 / 现在分词 / 比较级 / 派生词", "value": "变形词" }
                      ],
                      "collocations": [
                        { "phrase": "高频搭配短语", "meaning": "搭配释义" }
                      ],
                      "synonyms": ["同义词1", "同义词2"],
                      "antonyms": ["反义词1", "反义词2"],
                      "examples": [
                        { "sourceSentence": "地道外文例句", "translatedSentence": "精准译文", "highlightTerm": "例句中命中的核心词" }
                      ],
                      "suggestedTags": ["CET6", "IELTS", "学术", "高频核心"]
                    }
                    {{(isWordOrShortPhrase
                        ? "当前输入是单词或短语：请务必提供完整音标、全部常用词性释义、变形、词根词缀、巧记助记法、3~4个高频搭配和2~3个地道双语例句。"
                        : "当前输入是句子或段落：请提供精准自然的全文翻译作为 primaryTranslation，在 grammarBreakdown 中拆解句子结构与长难句语法，在 senses 中提取句中 2~4 个重点生词/短语及其释义，在 examples 中提供相似句型示例。")}}
                    """
                },
                new()
                {
                    ["role"] = "user",
                    ["content"] = new JsonObject
                    {
                        ["source_text"] = trimmed,
                        ["context_sentence"] = contextSentence ?? "",
                        ["target_language"] = targetLanguage.Id
                    }.ToJsonString()
                }
            };

            var completion = await transport.CompleteAsync(messages, [], delta =>
            {
                onStreamingPreview?.Invoke(delta);
            }, cancellationToken).ConfigureAwait(false);

            return ParseDictionaryResponse(trimmed, targetLanguage.Id, completion.Text);
        }
        finally
        {
            (transport as IDisposable)?.Dispose();
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

            var primary = GetString(root, "primaryTranslation");
            if (string.IsNullOrWhiteSpace(primary)) primary = sourceText;

            var itemTypeStr = GetString(root, "itemType");
            if (!Enum.TryParse<VocabularyItemType>(itemTypeStr, ignoreCase: true, out var itemType))
            {
                itemType = CountWords(sourceText) <= 2 ? VocabularyItemType.Word
                    : CountWords(sourceText) <= 6 ? VocabularyItemType.Phrase
                    : VocabularyItemType.Sentence;
            }

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
                        senses.Add(new(string.IsNullOrWhiteSpace(pos) ? "释义" : pos, zh, en));
                }
            }

            var inflections = new List<WordInflection>();
            if (root.TryGetProperty("inflections", out var infEl) && infEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in infEl.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var label = GetString(item, "label");
                    var val = GetString(item, "value");
                    if (!string.IsNullOrWhiteSpace(label) && !string.IsNullOrWhiteSpace(val))
                        inflections.Add(new(label, val));
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
                        collocations.Add(new(phrase, meaning));
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
                        examples.Add(new(src, tr, hl));
                }
            }

            return new DictionaryEntryResult
            {
                SourceText = sourceText,
                PrimaryTranslation = primary,
                DetectedSourceLanguage = GetString(root, "detectedSourceLanguage", "en"),
                TargetLanguage = targetLanguageId,
                ItemType = itemType,
                UkPhonetic = GetString(root, "ukPhonetic"),
                UsPhonetic = GetString(root, "usPhonetic"),
                ContextMeaning = GetString(root, "contextMeaning"),
                EtymologyAndRoot = GetString(root, "etymologyAndRoot"),
                MemoryMnemonic = GetString(root, "memoryMnemonic"),
                GrammarBreakdown = GetString(root, "grammarBreakdown"),
                UsageNotes = GetString(root, "usageNotes"),
                Senses = senses,
                Inflections = inflections,
                Collocations = collocations,
                Synonyms = GetStringArray(root, "synonyms"),
                Antonyms = GetStringArray(root, "antonyms"),
                Examples = examples,
                SuggestedTags = GetStringArray(root, "suggestedTags")
            };
        }
        catch (JsonException)
        {
            return new DictionaryEntryResult
            {
                SourceText = sourceText,
                // Use the fence-stripped text, not the raw response: otherwise a ```json fence around
                // malformed JSON leaks into the user-facing fallback "translation".
                PrimaryTranslation = jsonText,
                TargetLanguage = targetLanguageId,
                ItemType = CountWords(sourceText) <= 3 ? VocabularyItemType.Word : VocabularyItemType.Sentence
            };
        }
    }

    private static DictionaryEntryResult BuildOfflineFallbackEntry(string sourceText, TranslationLanguage targetLanguage, string? contextSentence)
    {
        if (BuiltInShowcaseLexicon.TryGet(sourceText, out var builtin))
            return builtin;

        var wordCount = CountWords(sourceText);
        var isSingleWord = wordCount == 1 && sourceText.All(c => char.IsLetter(c) || c is '-' or '\'');
        return new DictionaryEntryResult
        {
            SourceText = sourceText,
            PrimaryTranslation = $"[请配置 AI API Key 解锁实时 AI 精翻] {sourceText}",
            DetectedSourceLanguage = "auto",
            TargetLanguage = targetLanguage.Id,
            ItemType = isSingleWord ? VocabularyItemType.Word : wordCount <= 5 ? VocabularyItemType.Phrase : VocabularyItemType.Sentence,
            UkPhonetic = isSingleWord ? $"/{sourceText.ToLowerInvariant()}/" : "",
            UsPhonetic = isSingleWord ? $"/{sourceText.ToLowerInvariant()}/" : "",
            ContextMeaning = string.IsNullOrWhiteSpace(contextSentence)
                ? "在左侧「引擎与热键配置」中填入 DeepSeek / OpenAI / Ollama 端点后，即可获得实时 AI 语境精翻、词根拆解、助记与双语例句。"
                : $"捕获自上下文：{contextSentence}",
            EtymologyAndRoot = "配置 AI 供应商后自动生成词源、词根词缀（Prefix / Root / Suffix）深度拆解。",
            MemoryMnemonic = "配置 AI 供应商后自动生成联想助记、谐音助记与近形词辨析。",
            GrammarBreakdown = "配置 AI 供应商后自动分析长难句语法树与句法成分。",
            Senses =
            [
                new(isSingleWord ? "n./v." : "译文", $"待 AI 翻译：{sourceText}（目标：{targetLanguage.ChineseName}）", "Configure AI provider in Settings for live translation.")
            ],
            Examples =
            [
                new(
                    string.IsNullOrWhiteSpace(contextSentence) ? $"Example usage of \"{sourceText}\" in authentic context." : contextSentence,
                    "配置 AI Key 后自动生成地道双语例句与原句翻译。",
                    sourceText)
            ],
            SuggestedTags = ["待同步AI"]
        };
    }

    private static int CountWords(string text) =>
        text.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Length;

    private static string GetString(JsonElement el, string prop, string fallback = "") =>
        el.TryGetProperty(prop, out var val) && val.ValueKind == JsonValueKind.String
            ? val.GetString() ?? fallback
            : fallback;

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
