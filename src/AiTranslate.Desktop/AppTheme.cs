using Avalonia;
using Avalonia.Media;

namespace AiTranslate.Desktop;

/// <summary>
/// Next-generation Obsidian + Electric Indigo + Emerald Cognitive Studio design tokens.
/// Replaces dated, cramped dictionary UIs with a spacious, atmospheric Bento-grid visual hierarchy.
/// </summary>
public static class AppTheme
{
    public static readonly FontFamily BodyFont = new("Inter, Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI, PingFang SC, Noto Sans CJK SC, sans-serif");
    public static readonly FontFamily MonoFont = new("Cascadia Code, JetBrains Mono, Consolas, monospace");

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
