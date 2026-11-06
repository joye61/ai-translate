using AiTranslate.Core;
using Avalonia.Controls;
using Avalonia.Media;

namespace AiTranslate.Desktop;

/// <summary>
/// Semantic text roles. Pick the role that describes what the text <em>is</em>, never a raw size/color.
/// TextStyles.Get defines the typography for each role.
/// </summary>
public enum TextRole
{
    /// <summary>Headword / entry title (30 Bold, primary).</summary>
    Display,
    /// <summary>Page title in the workspace header (18 SemiBold).</summary>
    PageTitle,
    /// <summary>Section or card title (15 SemiBold).</summary>
    SectionTitle,
    /// <summary>The authoritative translation of a word or phrase (18 SemiBold, readable coral).</summary>
    Translation,
    /// <summary>Translation of a sentence or paragraph (15 Regular, primary, relaxed leading).</summary>
    PassageTranslation,
    /// <summary>A sense's Chinese definition (15 Medium, primary).</summary>
    Definition,
    /// <summary>A source-language expression: collocation, word-family member, synonym (14 SemiBold, primary).</summary>
    Term,
    /// <summary>Running body copy (13.5 Regular, primary).</summary>
    Body,
    /// <summary>Explanations, notes, scenarios (13.5 Regular, secondary).</summary>
    Secondary,
    /// <summary>A short annotation attached to a rendering or expression.</summary>
    Annotation,
    /// <summary>English gloss of a definition (13 Italic, secondary).</summary>
    Gloss,
    /// <summary>The source in a bilingual pair.</summary>
    ExampleSource,
    /// <summary>The target in a bilingual pair.</summary>
    ExampleTarget,
    /// <summary>Field label in a key–value row, e.g. 用法 / 场景 / 近义词 (12 SemiBold, muted).</summary>
    Label,
    /// <summary>Form field caption above an input (12.5 Medium, secondary).</summary>
    FieldLabel,
    /// <summary>Part-of-speech abbreviation inside a neutral tag (12 SemiBold mono, secondary).</summary>
    PartOfSpeech,
    /// <summary>Sense number or ordinal (12 SemiBold mono, muted).</summary>
    Numeral,
    /// <summary>IPA / pronunciation (12.5 mono, secondary).</summary>
    Phonetic,
    /// <summary>Counts, timestamps, origins, hints (11.5 Regular, muted).</summary>
    Meta
}

public sealed record TextStyle(double Size, FontWeight Weight, IBrush Foreground, double LineHeight = double.NaN,
    FontStyle Style = FontStyle.Normal, bool Mono = false);

public static class TextStyles
{
    internal static TextRole HeadwordRole(string text) =>
        DictionaryQuery.SuggestType(text).IsPassage() ? TextRole.PageTitle : TextRole.Display;

    public static TextStyle Get(TextRole role) => role switch
    {
        TextRole.Display => new(AppTheme.Type.Display, FontWeight.Bold, AppTheme.TextPrimary, 38),
        TextRole.PageTitle => new(AppTheme.Type.Title, FontWeight.SemiBold, AppTheme.TextPrimary),
        TextRole.SectionTitle => new(AppTheme.Type.BodyLarge, FontWeight.SemiBold, AppTheme.TextPrimary),
        TextRole.Translation => new(AppTheme.Type.Title, FontWeight.SemiBold, AppTheme.AccentBright, 28),
        TextRole.PassageTranslation => new(AppTheme.Type.BodyLarge, FontWeight.Normal, AppTheme.TextPrimary, 26),
        TextRole.Definition => new(AppTheme.Type.BodyLarge, FontWeight.Medium, AppTheme.TextPrimary, 24),
        TextRole.Term => new(14, FontWeight.SemiBold, AppTheme.TextPrimary, 22),
        TextRole.Body => new(AppTheme.Type.Body, FontWeight.Normal, AppTheme.TextPrimary, 22),
        TextRole.Secondary => new(AppTheme.Type.Body, FontWeight.Normal, AppTheme.TextSecondary, 22),
        TextRole.Annotation => new(AppTheme.Type.Small, FontWeight.Normal, AppTheme.AnnotationInk, 20),
        TextRole.Gloss => new(13, FontWeight.Normal, AppTheme.TextSecondary, 21, FontStyle.Italic),
        TextRole.ExampleSource => new(AppTheme.Type.BodyLarge, FontWeight.Medium, AppTheme.TextPrimary, 24),
        TextRole.ExampleTarget => new(AppTheme.Type.Body, FontWeight.Normal, AppTheme.PairedTranslationInk, 22),
        TextRole.Label => new(12, FontWeight.SemiBold, AppTheme.TextMuted, 21),
        TextRole.FieldLabel => new(AppTheme.Type.Small, FontWeight.Medium, AppTheme.TextSecondary),
        TextRole.PartOfSpeech => new(12, FontWeight.SemiBold, AppTheme.TextSecondary, Mono: true),
        TextRole.Numeral => new(12, FontWeight.SemiBold, AppTheme.TextMuted, 24, Mono: true),
        TextRole.Phonetic => new(AppTheme.Type.Small, FontWeight.Normal, AppTheme.TextSecondary, Mono: true),
        TextRole.Meta => new(AppTheme.Type.Caption, FontWeight.Normal, AppTheme.TextMuted),
        _ => new(AppTheme.Type.Body, FontWeight.Normal, AppTheme.TextPrimary)
    };

    public static T Apply<T>(T block, TextRole role) where T : TextBlock
    {
        var style = Get(role);
        block.FontSize = style.Size;
        block.FontWeight = style.Weight;
        block.FontStyle = style.Style;
        block.Foreground = style.Foreground;
        block.LineHeight = style.LineHeight;
        if (style.Mono) block.FontFamily = AppTheme.MonoFont;
        else block.ClearValue(TextBlock.FontFamilyProperty);
        return block;
    }

    public static TextBlock Create(string? text, TextRole role, bool wrap = true) =>
        Apply(new TextBlock { Text = text, TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap }, role);

    public static SelectableTextBlock Selectable(string? text, TextRole role) =>
        Apply(new SelectableTextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, role);
}
