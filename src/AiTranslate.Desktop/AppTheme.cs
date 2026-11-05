using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace AiTranslate.Desktop;

/// <summary>
/// Warm Paper + Coral "Fresh Light" design tokens for the desktop UI.
///
/// Color system:
///   • Accent (coral)        — brand identity, primary actions, selected/focused state, the authoritative translation.
///   • Insight (violet)      — AI-generated analysis (etymology, grammar, context meaning, nuance).
///   • Success (green)       — "saved" / "mastered" / "Good" semantics.
///   • Amber                 — attention/pending semantics: memory/mnemonic helpers, "Hard" review outcome.
///   • Danger (crimson)      — errors and destructive actions ONLY.
///   • Accent hues (<see cref="Hues"/>) — muted Sky/Jade/Violet/Honey/Rose/Sage tints that code categories
///     (memory stage, item type, part of speech, section kind, notebook). Always pair Ink text with its Soft
///     background; never use a hue on large surfaces or as a second brand color.
///   • Neutral (warm gray)   — default for everything else (secondary buttons, keycaps, plain chips).
/// </summary>
public static class AppTheme
{
    public static readonly FontFamily BodyFont = new("Inter, Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI, PingFang SC, Noto Sans CJK SC, sans-serif");
    public static readonly FontFamily MonoFont = new("Cascadia Code, JetBrains Mono, Consolas, monospace");

    /// <summary>Consistent 4px-based spacing scale; prefer these over arbitrary Thickness values in new code.</summary>
    public static class Space
    {
        public const double XXS = 4, XS = 6, S = 8, M = 12, L = 16, XL = 20, XXL = 24, XXXL = 32;
    }

    // The expanded Fluent scrollbar occupies 16 DIP. Keep that space on both sides of the content.
    public static Thickness ScrollContentInset { get; } = new(Space.L, 0);

    /// <summary>Consistent corner-radius scale (8–12px for components); prefer these over arbitrary values.</summary>
    public static class Radius
    {
        public const double S = 8, M = 10, L = 12, XL = 16, Pill = 999;
    }

    /// <summary>Consistent type scale (pixels); prefer these over arbitrary FontSize values in new code.</summary>
    public static class Type
    {
        public const double Caption = 11.5, Small = 12.5, Body = 13.5, BodyLarge = 15, Title = 18, Headline = 22, Display = 30;
    }

    // Surfaces
    public static IBrush WindowBg { get; } = Frozen(0xFFFAF8F5);
    public static IBrush SidebarBg { get; } = Frozen(0xFFF3EFEA);
    public static IBrush CardBg { get; } = Frozen(0xFFFFFFFF);
    public static IBrush CardElevatedBg { get; } = Frozen(0xFFF4F0EB);
    public static IBrush InputBg { get; } = Frozen(0xFFF8F5F1);
    public static IBrush HoverBg { get; } = Frozen(0xFFF1ECE6);
    public static IBrush PressedBg { get; } = Frozen(0xFFE9E3DB);
    public static IBrush BorderSubtle { get; } = Frozen(0xFFEDE8E1);
    public static IBrush BorderStrong { get; } = Frozen(0xFFDDD5CB);

    public static IBrush HeroGradient { get; } = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
        GradientStops =
        [
            new GradientStop(Color.FromUInt32(0xFFFFFFFF), 0.0),
            new GradientStop(Color.FromUInt32(0xFFFFFBF8), 1.0)
        ]
    }.ToImmutable();

    public static IBrush BrandGradient { get; } = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops =
        [
            new GradientStop(Color.FromUInt32(0xFFFF8663), 0.0),
            new GradientStop(Color.FromUInt32(0xFFFF6F4B), 1.0)
        ]
    }.ToImmutable();

    /// <summary>Soft fade from transparent to <see cref="WindowBg"/>, for content scrolling under a docked composer.</summary>
    public static IBrush WindowFadeBottom { get; } = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops =
        [
            new GradientStop(Color.FromUInt32(0x00FAF8F5), 0.0),
            new GradientStop(Color.FromUInt32(0xFFFAF8F5), 1.0)
        ]
    }.ToImmutable();

    // Accent (coral). `AccentBright` is the accent used for text/icons on light surfaces (AA-readable on white and on AccentSoft).
    public static ISolidColorBrush Accent { get; } = new ImmutableSolidColorBrush(0xFFFF7A59);
    public static IBrush AccentHover { get; } = Frozen(0xFFF2643F);
    public static IBrush AccentPressed { get; } = Frozen(0xFFE4552D);
    public static IBrush AccentSoft { get; } = Frozen(0x1FFF7A59);
    public static ISolidColorBrush AccentBright { get; } = new ImmutableSolidColorBrush(0xFFD6482A);
    public static ISolidColorBrush FlowTrack { get; } = new ImmutableSolidColorBrush(0xFFFFD9CC);

    /// <summary>Barely-there coral wash for a highlighted row (e.g. the looked-up word in a comparison table).</summary>
    public static IBrush AccentFaint { get; } = Frozen(0x0FFF7A59);

    // Rich text: thin offset underlines for clickable expressions, and the inset behind inline `code`.
    public static IBrush LinkUnderline { get; } = Frozen(0xFFC9BDAF);
    public static IBrush AccentUnderline { get; } = Frozen(0x8CFF7A59);
    public static IBrush CodeBg { get; } = Frozen(0xFFEFE9E1);

    internal static LinearGradientBrush CreateFlowHighlight() => new()
    {
        GradientStops =
        [
            new GradientStop(FlowTrack.Color, 0),
            new GradientStop(Accent.Color, .52),
            new GradientStop(AccentBright.Color, 1)
        ]
    };

    // Semantic colors (meaningful states)
    public static IBrush Emerald { get; } = Frozen(0xFF1B9A68);
    public static IBrush EmeraldHover { get; } = Frozen(0xFF168457);
    public static IBrush EmeraldSoft { get; } = Frozen(0x1F1B9A68);
    public static IBrush Amber { get; } = Frozen(0xFFB06E0A);
    public static IBrush AmberSoft { get; } = Frozen(0x24E8A317);
    public static IBrush Danger { get; } = Frozen(0xFFC0283A);
    public static IBrush DangerSoft { get; } = Frozen(0x1AC0283A);

    /// <summary>
    /// Muted accent hues tuned for the warm paper surfaces. Each hue offers an AA-readable <see cref="Hue.Ink"/>
    /// for text/icons, a translucent <see cref="Hue.Soft"/> tint for pills and icon tiles, and a <see cref="Hue.Solid"/>
    /// fill for dots, bars and small marks.
    /// </summary>
    public static class Hues
    {
        public static Hue Coral { get; } = new(AccentBright, AccentSoft, Accent);
        public static Hue Sky { get; } = Hue.Create(0xFF2F68AD, 0xFF4F8AD0);
        public static Hue Jade { get; } = Hue.Create(0xFF167565, 0xFF2A9D88);
        public static Hue Violet { get; } = Hue.Create(0xFF6B4BB0, 0xFF8C6BD3);
        public static Hue Honey { get; } = new(Amber, AmberSoft, Frozen(0xFFE8A317));
        public static Hue Rose { get; } = Hue.Create(0xFFB23462, 0xFFDB5C87);
        public static Hue Sage { get; } = new(Frozen(0xFF167F56), EmeraldSoft, Emerald);
        public static Hue Neutral { get; } = new(TextSecondary, CardElevatedBg, TextMuted);

        /// <summary>Rotation used to give user-created items (notebooks) a stable color.</summary>
        public static IReadOnlyList<Hue> Rotation { get; } = [Coral, Sky, Jade, Violet, Honey, Rose, Sage];
    }

    /// <summary>The "AI-generated insight" marker (etymology, grammar, context meaning, nuance).</summary>
    public static IBrush Insight => Hues.Violet.Ink;
    public static IBrush InsightSoft => Hues.Violet.Soft;

    // Standard macOS window controls.
    public static IBrush MacClose { get; } = Frozen(0xFFFF5F57);
    public static IBrush MacMinimize { get; } = Frozen(0xFFFEBC2E);
    public static IBrush MacZoom { get; } = Frozen(0xFF28C840);

    // Text
    public static IBrush TextPrimary { get; } = Frozen(0xFF26211C);
    public static IBrush TextSecondary { get; } = Frozen(0xFF5E564D);
    public static IBrush TextMuted { get; } = Frozen(0xFF73695F);
    public static IBrush AnnotationInk { get; } = Frozen(0xFF4B6077);
    public static IBrush PairedTranslationInk { get; } = Frozen(0xFF305F59);

    /// <summary>Hairline-soft elevation for cards: a tight contact shadow plus a wide, faint ambient one.</summary>
    public static BoxShadows CardShadow { get; } = BoxShadows.Parse("0 1 2 0 #0A28211A, 0 8 24 -8 #1228211A");

    /// <summary>Floating surfaces (popups, toolbars).</summary>
    public static BoxShadows PopupShadow { get; } = BoxShadows.Parse("0 2 6 0 #1428211A, 0 16 40 -8 #2428211A");

    public static IBrush Frozen(uint argb) => new SolidColorBrush(Color.FromUInt32(argb)).ToImmutable();
}

/// <param name="Ink">Text and icon color on light surfaces.</param>
/// <param name="Soft">Translucent tint for pills, icon tiles and callouts.</param>
/// <param name="Solid">Saturated fill for dots, bars and small marks.</param>
public sealed record Hue(IBrush Ink, IBrush Soft, IBrush Solid)
{
    public static Hue Create(uint ink, uint solid) =>
        new(AppTheme.Frozen(ink), AppTheme.Frozen(0x1F000000 | (solid & 0x00FFFFFF)), AppTheme.Frozen(solid));
}
