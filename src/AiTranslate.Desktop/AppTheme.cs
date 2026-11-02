using Avalonia;
using Avalonia.Media;

namespace AiTranslate.Desktop;

/// <summary>
/// Obsidian + Electric Indigo + Emerald Cognitive Studio design tokens.
///
/// Color discipline (keep the palette calm — do not pick a "random" accent per section):
///   • Accent (indigo/blue)  — brand identity, primary actions, selected/focused state, core fixed data (headword).
///   • Emerald               — positive/success semantics ONLY: the authoritative translation result, "saved" state,
///                             synonyms, "Good/Easy" review outcomes.
///   • Amber                 — attention/pending semantics ONLY: due-for-review counters, memory/mnemonic helpers,
///                             "Hard" review outcome.
///   • Purple                — reserved exclusively as the "AI deep-dive insight" marker (grammar, etymology,
///                             context meaning) so the eye learns one color == AI-generated analysis.
///   • Danger (red)          — errors and destructive actions ONLY, never decorative.
///   • Neutral (TextSecondary/TextMuted/CardElevatedBg) — the default for everything else (examples, collocations,
///                             tags, usage notes). Most of the UI should read as neutral; color should be rare
///                             and meaningful, not decorative.
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

    /// <summary>Consistent corner-radius scale; prefer these over arbitrary CornerRadius values in new code.</summary>
    public static class Radius
    {
        public const double S = 8, M = 10, L = 12, XL = 16, Pill = 999;
    }

    /// <summary>Consistent type scale (pixels); prefer these over arbitrary FontSize values in new code.</summary>
    public static class Type
    {
        public const double Caption = 11.5, Small = 12.5, Body = 13.5, BodyLarge = 15, Title = 18, Headline = 22, Display = 30;
    }

    public static IBrush WindowBg { get; } = Frozen(0xFF090D14);
    public static IBrush SidebarBg { get; } = Frozen(0xFF0D131D);
    public static IBrush CardBg { get; } = Frozen(0xFF131B29);
    public static IBrush CardElevatedBg { get; } = Frozen(0xFF1A2538);
    public static IBrush InputBg { get; } = Frozen(0xFF080C12);
    public static IBrush BorderSubtle { get; } = Frozen(0xFF212E42);
    public static IBrush BorderStrong { get; } = Frozen(0xFF334766);

    public static IBrush HeroGradient { get; } = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
        GradientStops =
        [
            new GradientStop(Color.FromUInt32(0xFF162238), 0.0),
            new GradientStop(Color.FromUInt32(0xFF121A2A), 0.6),
            new GradientStop(Color.FromUInt32(0xFF151E32), 1.0)
        ]
    }.ToImmutable();

    public static IBrush BrandGradient { get; } = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
        GradientStops =
        [
            new GradientStop(Color.FromUInt32(0xFF3B82F6), 0.0),
            new GradientStop(Color.FromUInt32(0xFF6366F1), 1.0)
        ]
    }.ToImmutable();

    public static IBrush Accent { get; } = Frozen(0xFF3B82F6);
    public static IBrush AccentHover { get; } = Frozen(0xFF2563EB);
    public static IBrush AccentSoft { get; } = Frozen(0x283B82F6);
    public static IBrush AccentBright { get; } = Frozen(0xFF60A5FA);

    public static IBrush Emerald { get; } = Frozen(0xFF10B981);
    public static IBrush EmeraldSoft { get; } = Frozen(0x2610B981);
    public static IBrush Amber { get; } = Frozen(0xFFF59E0B);
    public static IBrush AmberSoft { get; } = Frozen(0x26F59E0B);
    public static IBrush Purple { get; } = Frozen(0xFF8B5CF6);
    public static IBrush PurpleSoft { get; } = Frozen(0x268B5CF6);
    public static IBrush Danger { get; } = Frozen(0xFFEF4444);
    public static IBrush DangerSoft { get; } = Frozen(0x26EF4444);

    public static IBrush TextPrimary { get; } = Frozen(0xFFF8FAFC);
    public static IBrush TextSecondary { get; } = Frozen(0xFF94A3B8);
    public static IBrush TextMuted { get; } = Frozen(0xFF64748B);

    public static IBrush Frozen(uint argb) => new SolidColorBrush(Color.FromUInt32(argb)).ToImmutable();
}
