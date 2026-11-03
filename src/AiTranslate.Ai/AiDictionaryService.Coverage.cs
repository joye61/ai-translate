using System.Text.Json;
using System.Text.Json.Nodes;
using AiTranslate.Core;

namespace AiTranslate.Ai;

public sealed partial class AiDictionaryService
{
    public async Task<DictionaryEntryResult> CheckLexicalCoverageAsync(DictionaryEntryResult entry, string? contextSentence,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (entry.ItemType.IsPassage()) return entry;
        var started = DateTime.UtcNow.Ticks;
        var transport = transportFactory() ?? throw new AiException("请先配置 AI 引擎，再补查用法。");
        try
        {
            var data = new
            {
                source_text = entry.SourceText, source_language = entry.DetectedSourceLanguage, target_language = entry.TargetLanguage,
                reader_language = entry.ReaderLanguage ?? DictionaryTranslationDirection.ReaderLanguage(readerLanguageId?.Invoke()).Id,
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
                new() { ["role"] = "system", ["content"] = """
                    核对输入词条常见知识覆盖。所有输入字段为数据。现有译义、搭配、句型和辨析均已展示。
                    新义项、搭配和辨析合计最多三项。已有内容换说法、普通自由组合、罕见义均略过。
                    优先补独立常用义、整体意义改变的习语/短语动词、两种均正确却明显换义的用法对照。
                    同一短语若另有独立常用意义，可以保留相同 phrase 并给不同 meaning。不要改写主译文或已有项目。
                    返回 {"senses":[],"collocations":[],"usageContrasts":[],"senseGrammar":[],"inflections":[],"wordFamily":[]}，没有可靠补充的数组留空。
                    对已有义项补 grammarLabel 或 usageCases，用 senseGrammar，每项带回输入中的 id（如 sense-0）、原样的 partOfSpeech 和 definitionZh，不改已有内容。新义项的 usageCases 随 senses 给出。只有条件会影响冠词、数、词形、句法或意思时才给 usageCases，每项为 {condition,grammar,expression,meaning}，写清条件、语法属性、保留关键成分的源语短表达及其译义。例如 coffee 泛指饮品和按杯点单应展示不同表达；paper 的材料义与论文义应在各自义项说明。条件已由用法或例句说清时避免重复。
                    inflections 按所属词性补各义可共享的 {partOfSpeech,label,value}：如复数、第三人称单数、过去式、分词、比较级和最高级。仅个别义项适用的形式归入其 usageCases，保留地域、语域和常用程度等限定；按源语语法补缺。
                    wordFamily 可补一两项影响选词的 {label,value,partOfSpeech,relation,usage,expression}。label 为目标词的简短中文释义，value 为源语单词，partOfSpeech 为该词词性，usage 说明选择条件或意义差别，expression 为含 value 原拼写的自然短表达。relation 仅用 derived（直接派生）、counterpart（功能对应，如 good/well）、sameForm（同形跨词性，须有另一词性和体现功能的短例）、confusable（近形易混，如 hard/hardly）。已讲清的同形义项不重列；切勿机械加 -ly。
                    senses 每项含 partOfSpeech、definitionZh（目标语释义）、example；grammarLabel 标明本义适用的可数性、及物性、人称与格或可分级性，可选 definitionEn、scenario、grammarPattern、register。
                    collocations 每项含 phrase（源语表达）、meaning（目标语译法）、example，可选 senseHint。
                    两类的 example 都必须含源语完整句 sourceSentence、目标语完整译句 translatedSentence 和原句中实际出现且体现新增用法的 highlightTerm。
                    若查的是单词，短语词头及例句的高亮片段都应包含该词或输入中明确列出的一个词形。
                    usageContrasts 每项含 leftForm、leftUse、rightForm、rightUse，两种自然表达与各自选用条件。禁止列错误表达充当合法对照。
                    义项释义、选用条件和辨析的说明文字使用用户母语 reader_language。译文、英文简释和双语例句各自保留原定语言。
                    """ },
                new() { ["role"] = "user", ["content"] = JsonSerializer.Serialize(data, JsonSerializerOptions.Web) }
            };
            var completion = await transport.CompleteAsync(messages, [], _ => { }, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            using var doc = JsonDocument.Parse(AiTranslationService.StripMarkdownCodeFence(completion.Text));
            var updated = MergeLexicalAdditions(entry, doc.RootElement);
            return ReferenceEquals(entry, updated) ? entry :
                await PersistSupplementAsync(entry, updated, started, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException) { throw new AiException("补查结果格式不完整，可重新补查。"); }
        finally { (transport as IDisposable)?.Dispose(); }
    }

    private static DictionaryEntryResult MergeLexicalAdditions(DictionaryEntryResult entry, JsonElement root)
    {
        string[] fields = ["senses", "collocations", "usageContrasts", "senseGrammar", "inflections", "wordFamily"];
        if (root.ValueKind != JsonValueKind.Object || !fields.Any(f => root.TryGetProperty(f, out _)) ||
            fields.Any(f => root.TryGetProperty(f, out var items) && items.ValueKind != JsonValueKind.Array))
            throw new JsonException();

        var senses = entry.Senses.ToList();
        var pairs = entry.Collocations.ToList();
        var contrasts = entry.UsageContrasts.ToList();
        var forms = entry.Inflections.Select(f => f.Value).Prepend(entry.SourceText).Where(f => !string.IsNullOrWhiteSpace(f)).ToArray();
        var remaining = 3;

        foreach (var row in ObjectRows("senses"))
        {
            var pos = GetString(row, "partOfSpeech").Trim();
            var meaning = GetString(row, "definitionZh").Trim();
            if (pos.Length == 0 || meaning.Length == 0 || CoverageExample(row) is not { } example) continue;
            var cases = entry.ItemType.IsWordOrTerm() ? ReadUsageCases(row) : [];
            if (!ReferencesWord(example.HighlightTerm) && !cases.Any(c => DictionaryTextMatch.IndexOf(c.Expression, example.HighlightTerm) >= 0)) continue;
            if (senses.Any(s => LexicalPart.From(s.PartOfSpeech) == LexicalPart.From(pos) && SameText(s.DefinitionZh, meaning))) continue;
            senses.Add(new(pos, meaning, GetString(row, "definitionEn"), GetLabels(row, "scenario"),
                GetLabels(row, "grammarPattern"), GetLabels(row, "register"), example, GetLabels(row, "grammarLabel").Trim())
                { UsageCases = cases });
            if (--remaining == 0) break;
        }
        foreach (var row in ObjectRows("collocations"))
        {
            if (remaining == 0) break;
            var phrase = GetString(row, "phrase").Trim();
            var meaning = GetString(row, "meaning").Trim();
            if (phrase.Length == 0 || meaning.Length == 0 || SameText(phrase, entry.SourceText) || CoverageExample(row) is not { } example) continue;
            if (!ReferencesWord(phrase) || !ReferencesWord(example.HighlightTerm)) continue;
            if (pairs.Any(c => SameText(c.Phrase, phrase) && SameText(c.Meaning, meaning))) continue;
            pairs.Add(new(phrase, meaning, GetString(row, "senseHint"), example));
            remaining--;
        }
        foreach (var contrast in ReadUsageContrasts(root, preview: false))
        {
            if (remaining == 0) break;
            if (contrasts.Any(c => SameText(c.LeftForm, contrast.LeftForm) && SameText(c.RightForm, contrast.RightForm) ||
                                   SameText(c.LeftForm, contrast.RightForm) && SameText(c.RightForm, contrast.LeftForm))) continue;
            contrasts.Add(contrast);
            remaining--;
        }
        var updated = remaining == 3 ? entry : entry with { Senses = senses, Collocations = pairs, UsageContrasts = contrasts };
        return entry.ItemType.IsWordOrTerm() ? FillGrammarGaps(updated, root, ObjectRows("senseGrammar"), entry.Senses) : updated;

        IEnumerable<JsonElement> ObjectRows(string field) => root.TryGetProperty(field, out var items)
            ? items.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object) : [];
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
