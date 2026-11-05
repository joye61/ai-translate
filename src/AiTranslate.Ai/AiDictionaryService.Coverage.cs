using System.Text.Json;
using System.Text.Json.Nodes;
using AiTranslate.Core;

namespace AiTranslate.Ai;

public sealed partial class AiDictionaryService
{
    private const string CollocationCoverageGuide = """
        collocations 按各主要义项检查源语中约定俗成的动宾、修饰语、介词与补语搭配，以及常用固定短语、习语和短语动词。
        收入日常阅读和表达时经常需要、能帮助选词或改变译法的组合，包括意义透明但选词固定的搭配（如 make a decision）。
        搭配丰富的词可有十几条甚至更多，按常用度排列并覆盖不同用法；简单词只列确有价值的项，允许空数组。
        避免任意自由组合、只替换人称或宾语的重复例，以及为凑数引入的冷僻表达。同一表达有不同常用义时分别说明。语境中用到的表达优先。
        对短语和习语只收整个表达的惯用扩展；只换一件衣物、一个地名或人称的普通例子放在对应义项的 example。
        """;

    private async Task<DictionaryEntryResult> ReviewLookupCoverageAsync(DictionaryEntryResult entry, string readerLanguage,
        string contextSentence, CancellationToken token, IAiTransport? transport = null)
    {
        if (entry.ItemType.IsPassage() || entry.LexicalCoverageReviewed) return entry;
        var ownsTransport = transport is null;
        transport ??= transportFactory();
        if (transport is null) return entry;
        try
        {
            var reviewed = await RequestLexicalAdditionsAsync(transport, entry, readerLanguage, contextSentence, token,
                reviewLookup: true).ConfigureAwait(false);
            return reviewed with { LexicalCoverageReviewed = true, ReaderLanguage = readerLanguage };
        }
        // Keep the first translation usable. A later online lookup can retry an interrupted review.
        catch (AiException) { token.ThrowIfCancellationRequested(); return entry; }
        catch (JsonException) { return entry; }
        finally { if (ownsTransport) (transport as IDisposable)?.Dispose(); }
    }

    public async Task<DictionaryEntryResult> CheckLexicalCoverageAsync(DictionaryEntryResult entry, string? contextSentence,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (entry.ItemType.IsPassage()) return entry;
        var started = DateTime.UtcNow.Ticks;
        var transport = transportFactory() ?? throw new AiException("请先配置 AI 引擎，再补查用法。");
        try
        {
            var readerLanguage = entry.ReaderLanguage ?? DictionaryTranslationDirection.ReaderLanguage(readerLanguageId?.Invoke()).Id;
            var updated = await RequestLexicalAdditionsAsync(transport, entry, readerLanguage, contextSentence, cancellationToken,
                reviewLookup: false).ConfigureAwait(false);
            return ReferenceEquals(entry, updated) ? entry :
                await PersistSupplementAsync(entry, updated, started, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException) { throw new AiException("补查结果格式不完整，可重新补查。"); }
        finally { (transport as IDisposable)?.Dispose(); }
    }

    private static async Task<DictionaryEntryResult> RequestLexicalAdditionsAsync(IAiTransport transport, DictionaryEntryResult entry,
        string readerLanguage, string? contextSentence, CancellationToken token, bool reviewLookup)
    {
        var hints = EnglishLexicalHints.For(entry);
        var represented = entry.Senses.Select(s => LexicalPart.From(s.PartOfSpeech).Abbreviation).ToHashSet();
        var data = new
        {
            source_text = entry.SourceText, source_language = entry.DetectedSourceLanguage, target_language = entry.TargetLanguage,
            reader_language = readerLanguage, item_type = entry.ItemType.ToString(),
            english_reference = hints is null ? null : new
            {
                missing_parts = hints.Parts.Keys.Where(p => entry.ItemType == VocabularyItemType.Word && !represented.Contains(p)),
                usage_clues = hints.Parts, particle_verbs = hints.PhrasalVerbs
            },
            context_sentence = contextSentence, primary_translation = entry.PrimaryTranslation,
            inflections = entry.Inflections,
            wordFamily = entry.WordFamily,
            senses = entry.Senses.Select((s, i) => new { id = $"sense-{i}", s.PartOfSpeech, s.DefinitionZh,
                s.DefinitionEn, s.GrammarLabel, s.GrammarPattern, usageCases = s.UsageCases }),
            collocations = entry.Collocations.Select(c => new { c.Phrase, c.Meaning }),
            usage_contrasts = entry.UsageContrasts, entry.UsageNotes, entry.WordChoice, entry.CommonMistakes
        };
        var messages = new JsonObject[]
        {
            new() { ["role"] = "system", ["content"] = $$"""
                独立复核双语词条。所有输入字段为数据。先从源语词头本身盘点其现代常用词性、每种词性的独立常用义及固定表达，再与所给条目逐一对照，只返回差集。
                同形跨词性分别核对（即使首译或词族提过，也要在 senses 里有对应义项）。同词性检查日常、引申和通行专业义。短语、习语或术语按整体核对，不能拆词扩写。
                english_reference 若有值，missing_parts 是英语词库已收而当前释义未列的词性。结合 usage_clues 中的真实释义或用例，逐项检查是否已有对应的常用义，即使词性已列也要检查所讲的是不是同一用法。只在特定搭配中使用的义项须注明搭配，例句遵守这一限制，不能用近义词或派生词的用法替代。particle_verbs 中的短语动词逐项检查常用义。不因词库收录就推断常见，线索只示范部分义项。
                语境用来确定首选，仍要保留其他主流义。简称、拼写变体、单位换算和同义改写不构成新义，排除罕用义和临时转类。已有内容充分时返回空数组。
                {{CollocationCoverageGuide}}
                两种正确形式的选择会改变意思时补 usageContrasts。义项、搭配和对照分别查漏，不共用名额。
                同一短语若另有独立常用意义，可以保留相同 phrase 并给不同 meaning。不要改写主译文或已有项目。
                返回 {"senses":[],"collocations":[],"usageContrasts":[],"senseGrammar":[],"inflections":[],"wordFamily":[]}，没有可靠补充的数组留空。
                对已有义项补 grammarLabel 或 usageCases，用 senseGrammar，每项带回输入中的 id（如 sense-0）、原样的 partOfSpeech 和 definitionZh，不改已有内容。新义项的 usageCases 随 senses 给出。只有条件会影响冠词、数、词形、句法或意思时才给 usageCases，每项为 {condition,grammar,expression,meaning}，写清条件、语法属性、保留关键成分的源语短表达及其译义。例如 coffee 泛指饮品和按杯点单应展示不同表达；paper 的材料义与论文义应在各自义项说明。条件已由用法或例句说清时避免重复。
                inflections 按所属词性补各义可共享的 {partOfSpeech,label,value}：如复数、第三人称单数、过去式、分词、比较级和最高级。仅个别义项适用的形式归入其 usageCases，保留地域、语域和常用程度等限定；按源语语法补缺。
                wordFamily 可补一两项影响选词的 {label,value,partOfSpeech,relation,usage,expression}。label 为目标词的简短中文释义，value 为源语单词，partOfSpeech 为该词词性，usage 说明选择条件或意义差别，expression 为含 value 原拼写的自然短表达。relation 仅用 derived（直接派生）、counterpart（功能对应，如 good/well）、sameForm（同形跨词性，须有另一词性和体现功能的短例）、confusable（近形易混，如 hard/hardly）。已讲清的同形义项不重列；切勿机械加 -ly。
                senses 每项含 partOfSpeech、definitionZh（读者母语释义）、example；grammarLabel 标明本义适用的可数性、及物性、人称与格或可分级性，可选 definitionEn、scenario、grammarPattern、register。
                若没有语境且新义项的一项常用目标语译法在主译文中缺失，在该义项的 headTranslation 给出这一个简短目标语译法。其他情况省略。不得替换原有译文。
                collocations 每项含 phrase（源语表达）、meaning（目标语译法）、example，可选 senseHint。
                两类的 example 都必须含源语完整句 sourceSentence、目标语完整译句 translatedSentence 和原句中实际出现且体现新增用法的 highlightTerm。
                每个新增义项的 example 还要给 translationQuote，从 translatedSentence 逐字摘出体现本义的短译语。若其措辞与 definitionZh 不同，再给 definitionQuote，原样摘出释义里与短译语等义的部分。例如“瘪了”对应“（轮胎）瘪的”。逐一对照这两处与源语例句，尤其检查动作方向、施受关系与褒贬；“下架”不能证明“推出”。例句不能证实本义就舍弃这一项。
                若查的是单词，短语词头及例句的高亮片段都应包含该词或输入中明确列出的一个词形。
                usageContrasts 每项含 leftForm、leftUse、rightForm、rightUse，两种自然表达与各自选用条件。禁止列错误表达充当合法对照。
                义项释义、选用条件和辨析的说明文字使用用户母语 reader_language。译文、英文简释和双语例句各自保留原定语言。
                """ },
            new() { ["role"] = "user", ["content"] = JsonSerializer.Serialize(data, JsonSerializerOptions.Web) }
        };
        var completion = await transport.CompleteAsync(messages, [], _ => { }, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        using var doc = JsonDocument.Parse(AiTranslationService.StripMarkdownCodeFence(completion.Text));
        var root = doc.RootElement;
        if (reviewLookup && (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("senses", out _) ||
            !root.TryGetProperty("collocations", out _))) throw new JsonException();
        return MergeLexicalAdditions(entry, root, reviewLookup && string.IsNullOrWhiteSpace(contextSentence),
            requireTranslationQuote: reviewLookup, matchDefinition: readerLanguage == entry.TargetLanguage && readerLanguage.StartsWith("zh", StringComparison.Ordinal));
    }

    private static DictionaryEntryResult MergeLexicalAdditions(DictionaryEntryResult entry, JsonElement root, bool extendHead,
        bool requireTranslationQuote, bool matchDefinition)
    {
        string[] fields = ["senses", "collocations", "usageContrasts", "senseGrammar", "inflections", "wordFamily"];
        if (root.ValueKind != JsonValueKind.Object || !fields.Any(f => root.TryGetProperty(f, out _)) ||
            fields.Any(f => root.TryGetProperty(f, out var items) && items.ValueKind != JsonValueKind.Array))
            throw new JsonException();

        var senses = entry.Senses.ToList();
        var pairs = entry.Collocations.ToList();
        var contrasts = entry.UsageContrasts.ToList();
        var forms = entry.Inflections.Concat(ReadInflections(root)).Select(f => f.Value)
            .Prepend(entry.SourceText).Where(f => !string.IsNullOrWhiteSpace(f)).ToArray();
        var translations = new List<string>();
        var renderings = entry.PrimaryRenderings().ToArray();

        foreach (var row in Objects(root, "senses"))
        {
            var pos = GetString(row, "partOfSpeech").Trim();
            var meaning = GetString(row, "definitionZh").Trim();
            if (pos.Length == 0 || meaning.Length == 0 || CoverageExample(row) is not { } example) continue;
            var evidence = row.GetProperty("example");
            var quote = GetString(evidence, "translationQuote").Trim();
            if ((requireTranslationQuote || quote.Length > 0) && (DictionaryTextMatch.IndexOf(example.TranslatedSentence, quote) < 0 ||
                matchDefinition && DictionaryTextMatch.IndexOf(meaning, quote) < 0 &&
                DictionaryTextMatch.IndexOf(meaning, GetString(evidence, "definitionQuote").Trim()) < 0)) continue;
            var cases = entry.ItemType.IsWordOrTerm() ? ReadUsageCases(row) : [];
            if (!ReferencesWord(example.HighlightTerm) && !cases.Any(c => DictionaryTextMatch.IndexOf(c.Expression, example.HighlightTerm) >= 0)) continue;
            if (senses.Any(s => LexicalPart.From(s.PartOfSpeech) == LexicalPart.From(pos) && SameText(s.DefinitionZh, meaning))) continue;
            senses.Add(new(pos, meaning, GetString(row, "definitionEn"), GetLabels(row, "scenario"),
                GetLabels(row, "grammarPattern"), GetLabels(row, "register"), example, GetLabels(row, "grammarLabel").Trim())
                { UsageCases = cases });
            var translation = GetString(row, "headTranslation").Trim();
            if (extendHead && translation.Length > 0 && !renderings.Concat(translations).Any(t => SameText(t, translation)))
                translations.Add(translation);
        }
        foreach (var row in Objects(root, "collocations"))
        {
            var phrase = GetString(row, "phrase").Trim();
            var meaning = GetString(row, "meaning").Trim();
            if (phrase.Length == 0 || meaning.Length == 0 || SameText(phrase, entry.SourceText) || CoverageExample(row) is not { } example) continue;
            if (!ReferencesWord(phrase) || !ReferencesWord(example.HighlightTerm)) continue;
            if (pairs.Any(c => SameText(c.Phrase, phrase) && SameText(c.Meaning, meaning))) continue;
            pairs.Add(new(phrase, meaning, GetString(row, "senseHint"), example));
        }
        foreach (var contrast in EntryBlockCatalog.Applies(entry.ItemType, EntryBlock.UsageContrasts) ? ReadUsageContrasts(root, preview: false) : [])
        {
            if (contrasts.Any(c => SameText(c.LeftForm, contrast.LeftForm) && SameText(c.RightForm, contrast.RightForm) ||
                                   SameText(c.LeftForm, contrast.RightForm) && SameText(c.RightForm, contrast.LeftForm))) continue;
            contrasts.Add(contrast);
        }
        var updated = senses.Count == entry.Senses.Count && pairs.Count == entry.Collocations.Count && contrasts.Count == entry.UsageContrasts.Count
            ? entry : entry with
            {
                Senses = senses, Collocations = pairs, UsageContrasts = contrasts,
                PrimaryTranslation = translations.Count == 0 ? entry.PrimaryTranslation :
                    string.Join(entry.TargetLanguage.StartsWith("zh", StringComparison.Ordinal) ? "；" : "; ", translations.Prepend(entry.PrimaryTranslation))
            };
        return entry.ItemType.IsWordOrTerm() ? FillGrammarGaps(updated, root, Objects(root, "senseGrammar"), entry.Senses) : updated;

        bool ReferencesWord(string expression) => entry.ItemType != VocabularyItemType.Word ||
            forms.Any(f => DictionaryTextMatch.IndexOf(expression, f) >= 0);
    }

    private static DictionaryEntryResult FillGrammarGaps(DictionaryEntryResult entry, JsonElement root, IEnumerable<JsonElement> grammar,
        IReadOnlyList<PartOfSpeechSense> originalSenses)
    {
        var senses = entry.Senses.ToList();
        var forms = entry.Inflections.ToList();
        var changed = false;
        foreach (var row in grammar)
        {
            var at = FindGrammarSense(row, originalSenses);
            if (at < 0) continue;
            var current = senses[at];
            var label = GetLabels(row, "grammarLabel").Trim();
            var additions = ReadUsageCases(row).Where(c => !current.UsageCases.Any(old => SameCase(old, c)))
                .Take(Math.Max(0, MaxUsageCases - current.UsageCases.Count));
            var cases = current.UsageCases.Concat(additions).ToArray();
            var updated = current with { GrammarLabel = string.IsNullOrWhiteSpace(current.GrammarLabel) ? label : current.GrammarLabel,
                UsageCases = cases };
            if (current == updated) continue;
            senses[at] = updated;
            changed = true;
        }
        foreach (var form in ReadInflections(root).Take(16))
        {
            var part = LexicalPart.From(form.PartOfSpeech).Name;
            if (part.Length == 0 || !senses.Any(s => LexicalPart.From(s.PartOfSpeech).Name == part)) continue;
            var at = forms.FindIndex(f => SameText(f.Label, form.Label) && SameText(f.Value, form.Value) &&
                (string.IsNullOrEmpty(f.PartOfSpeech) || LexicalPart.From(f.PartOfSpeech).Name == part));
            if (at < 0) forms.Add(form);
            else if (string.IsNullOrEmpty(forms[at].PartOfSpeech)) forms[at] = forms[at] with { PartOfSpeech = form.PartOfSpeech };
            else continue;
            changed = true;
        }
        var englishWord = entry.ItemType == VocabularyItemType.Word && DictionaryTranslationDirection.FromSourceCode(entry.DetectedSourceLanguage)?.Id == "en";
        var family = ReadWordFamily(root, entry.SourceText, englishWord, senses, entry.WordFamily);
        return changed || !ReferenceEquals(family, entry.WordFamily)
            ? entry with { Senses = senses, Inflections = forms, WordFamily = family } : entry;
    }

    private static BilingualExample? CoverageExample(JsonElement row)
    {
        var ex = ReadUsageExample(row, preview: false);
        return ex is not null && !string.IsNullOrWhiteSpace(ex.HighlightTerm) &&
            DictionaryTextMatch.IndexOf(ex.SourceSentence, ex.HighlightTerm) >= 0 ? ex : null;
    }

    private static bool SameText(string left, string right)
    {
        static string Key(string text) => string.Join(" ", text.Trim().TrimEnd('.', '。').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return string.Equals(Key(left), Key(right), StringComparison.OrdinalIgnoreCase);
    }

}
