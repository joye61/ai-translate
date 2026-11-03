namespace AiTranslate.Core;

/// <summary>A target-language rendering with its meaning and selection criteria in the reader's language.</summary>
public sealed record TranslationOption(string Text, string Meaning, string Usage);

/// <summary>An alternative rendering of the whole source in a distinct style (直译 / 意译 / 口语 / 正式 …).</summary>
public sealed record TranslationVariant(string Style, string Text, string Note = "");

/// <summary>One node of a sentence skeleton. Depth 0 is the main clause; deeper nodes depend on the nearest shallower one.</summary>
public sealed record ClauseNode(string Quote, string Role, int Depth, string Note = "");

/// <summary>A source sentence and the matching part of the main translation, for side-by-side paragraph reading.</summary>
public sealed record AlignedSentence(string Source, string Target);

/// <summary>Register and attitude of a passage. <see cref="Formality"/> is 1 (casual) … 5 (formal); 0 when unknown.</summary>
public sealed record ToneProfile(int Formality, IReadOnlyList<string> Tones, string Note = "")
{
    public bool IsEmpty => Formality is < 1 or > 5 && Tones.Count == 0 && string.IsNullOrWhiteSpace(Note);
}

public enum MorphemeKind { Prefix, Root, Suffix }

/// <summary>A word-formation part, e.g. in- (向内) + spect (看) + -ion (名词后缀).</summary>
public sealed record Morpheme(string Part, string Meaning, MorphemeKind Kind);

/// <summary>A row of a near-synonym comparison table. The first row is normally the looked-up item itself.</summary>
public sealed record SynonymNuance(string Word, string Nuance, string Register = "");

/// <summary>A real learner error, its correction and the reason.</summary>
public sealed record UsageMistake(string Wrong, string Right, string Why = "");
