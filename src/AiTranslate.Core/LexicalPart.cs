namespace AiTranslate.Core;

/// <summary>Canonical display group for a dictionary part of speech, including qualifiers in older abbreviations.</summary>
public readonly record struct LexicalPart(string Name, string Abbreviation = "", string Grammar = "")
{
    public static LexicalPart From(string? text) => text?.Trim().TrimEnd('.').ToLowerInvariant() switch
    {
        "n" or "noun" or "名词" => new("名词", "n."),
        "n. [c]" or "可数名词" => new("名词", "n.", "可数"),
        "n. [u]" or "不可数名词" => new("名词", "n.", "不可数"),
        "v" or "verb" or "动词" => new("动词", "v."),
        "vt" or "transitive verb" or "及物动词" => new("动词", "v.", "及物"),
        "vi" or "intransitive verb" or "不及物动词" => new("动词", "v.", "不及物"),
        "adj" or "a" or "adjective" or "形容词" => new("形容词", "adj."),
        "adv" or "ad" or "adverb" or "副词" => new("副词", "adv."),
        "pron" or "pronoun" or "代词" => new("代词", "pron."),
        "prep" or "preposition" or "介词" => new("介词", "prep."),
        "conj" or "conjunction" or "连词" => new("连词", "conj."),
        "int" or "interj" or "interjection" or "感叹词" => new("感叹词", "interj."),
        "num" or "numeral" or "数词" => new("数词", "num."),
        "art" or "article" or "冠词" => new("冠词", "art."),
        "aux" or "auxiliary verb" or "助动词" => new("助动词", "aux."),
        "det" or "determiner" or "限定词" => new("限定词", "det."),
        "phr" or "phrase" or "短语" => new("短语", "phr."),
        "abbr" or "abbreviation" or "缩写" => new("缩写", "abbr."),
        "idiom" or "习语" => new("习语", "idiom"),
        _ => new(text?.Trim() ?? "")
    };
}
