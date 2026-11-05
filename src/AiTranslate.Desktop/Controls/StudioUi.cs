using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace AiTranslate.Desktop.Controls;

/// <summary>
/// Shared modern Bento-grid UI building blocks used across Views.
/// Keeps view modules clean, decoupled, and visually consistent.
/// </summary>
public static class StudioUi
{
    public static StackPanel CreateToolbarField(string label, Control editor)
    {
        AutomationProperties.SetName(editor, label);
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = AppTheme.Space.S,
            Children =
            {
                new TextBlock
                {
                    Text = label, FontSize = AppTheme.Type.Small, Foreground = AppTheme.TextSecondary,
                    VerticalAlignment = VerticalAlignment.Center
                },
                editor
            }
        };
    }

    public static Border CreateBentoCard(Control child, Thickness? padding = null, IBrush? background = null, IBrush? borderBrush = null) => new()
    {
        Background = background ?? AppTheme.CardBg,
        BorderBrush = borderBrush ?? AppTheme.BorderSubtle,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(AppTheme.Radius.L),
        BoxShadow = AppTheme.CardShadow,
        Padding = padding ?? new Thickness(20, 18),
        Child = child
    };

    public static Border CreateHeroCard(Control child, Thickness? padding = null) => new()
    {
        Background = AppTheme.HeroGradient,
        BorderBrush = AppTheme.BorderSubtle,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(AppTheme.Radius.L),
        BoxShadow = AppTheme.CardShadow,
        Padding = padding ?? new Thickness(24, 20),
        Child = child
    };

    public static StackPanel CreateSectionHeader(string mdiIcon, string title, string? subtitle, IBrush accentBrush) =>
        CreateSectionHeader(mdiIcon, title, subtitle, accentBrush, out _);

    /// <summary>Section title row whose icon tile is tinted with a category <paramref name="hue"/>.</summary>
    public static StackPanel CreateSectionHeader(string mdiIcon, string title, string? subtitle, Hue hue)
    {
        var header = CreateSectionHeader(mdiIcon, title, subtitle, hue.Ink, out _);
        ((Border)header.Children[0]).Background = hue.Soft;
        return header;
    }

    /// <summary>Section title row; <paramref name="subtitleText"/> lets callers update a live count or hint in place.</summary>
    public static StackPanel CreateSectionHeader(string mdiIcon, string title, string? subtitle, IBrush accentBrush,
        out TextBlock subtitleText)
    {
        subtitleText = new TextBlock
        {
            Text = subtitle,
            IsVisible = !string.IsNullOrWhiteSpace(subtitle),
            FontSize = AppTheme.Type.Small,
            Foreground = AppTheme.TextMuted,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 1, 0, 0)
        };
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = AppTheme.Space.S + 2,
            Children =
            {
                new Border
                {
                    Width = 28,
                    Height = 28,
                    CornerRadius = new CornerRadius(AppTheme.Radius.S),
                    Background = ReferenceEquals(accentBrush, AppTheme.Insight) ? AppTheme.InsightSoft : AppTheme.InputBg,
                    Child = new MdiIcon(mdiIcon, 16, accentBrush)
                },
                new TextBlock
                {
                    Text = title,
                    FontSize = AppTheme.Type.BodyLarge,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = AppTheme.TextPrimary,
                    VerticalAlignment = VerticalAlignment.Center
                },
                subtitleText
            }
        };
    }

    /// <summary>Square ghost button for toolbars; same 34px height as <see cref="CreateSecondaryButton(string,string,Action)"/>.</summary>
    public static Button CreateIconButton(string mdiIcon, string tooltip, Action onClick)
    {
        var btn = new Button
        {
            Classes = { "ghost" },
            Width = 34,
            Height = 34,
            Padding = default,
            Background = AppTheme.CardBg,
            BorderBrush = AppTheme.BorderStrong,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(AppTheme.Radius.M),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Content = new MdiIcon(mdiIcon, 17, AppTheme.TextSecondary)
        };
        AutomationProperties.SetName(btn, tooltip);
        ToolTip.SetTip(btn, tooltip);
        btn.Click += (_, _) => onClick();
        return btn;
    }

    /// <summary>Borderless, low-emphasis inline action (e.g. inside example tiles). Background appears on hover only.</summary>
    public static Button CreateCompactButton(string mdiIcon, string text, Action onClick)
    {
        var btn = new Button
        {
            Classes = { "ghost", "compact" },
            Background = Brushes.Transparent,
            BorderThickness = default,
            CornerRadius = new CornerRadius(AppTheme.Radius.S),
            Padding = new Thickness(AppTheme.Space.S, AppTheme.Space.XXS),
            MinHeight = 26,
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = AppTheme.Space.XS,
                Children =
                {
                    new MdiIcon(mdiIcon, 14),
                    new TextBlock
                    {
                        Text = text,
                        FontSize = AppTheme.Type.Caption,
                        FontWeight = FontWeight.Medium,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                }
            }
        };
        AutomationProperties.SetName(btn, text);
        btn.Click += (_, _) => onClick();
        return btn;
    }

    /// <summary>Short typed label (part of speech, register, tag). Neutral warm gray unless a category <paramref name="hue"/> is given.</summary>
    public static Border CreateTag(string text, TextRole role = TextRole.PartOfSpeech, Hue? hue = null, bool wrap = false)
    {
        var label = TextStyles.Create(text, role, wrap);
        if (hue is not null) label.Foreground = hue.Ink;
        return new Border
        {
            Background = hue?.Soft ?? AppTheme.CardElevatedBg, CornerRadius = new CornerRadius(AppTheme.Radius.Pill),
            Padding = new Thickness(AppTheme.Space.S, 2), VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = label
        };
    }

    /// <summary>Non-interactive category pill tinted with <paramref name="hue"/> (item type, origin).</summary>
    public static Border CreateChip(string text, Hue hue, string? mdiIcon = null) =>
        CreateChip(text, mdiIcon, hue.Ink, hue.Soft);

    /// <summary>Colored status dot followed by a quiet label, e.g. ● 学习中.</summary>
    public static StackPanel CreateDotLabel(string text, Hue hue) => new()
    {
        Orientation = Orientation.Horizontal, Spacing = AppTheme.Space.XS, VerticalAlignment = VerticalAlignment.Center,
        Children =
        {
            new Avalonia.Controls.Shapes.Ellipse { Width = 7, Height = 7, Fill = hue.Solid, VerticalAlignment = VerticalAlignment.Center },
            new TextBlock
            {
                Text = text, FontSize = AppTheme.Type.Caption, FontWeight = FontWeight.Medium, Foreground = hue.Ink,
                VerticalAlignment = VerticalAlignment.Center
            }
        }
    };

    /// <summary>Neutral, non-interactive pill for metadata (item type, part of speech, origin).</summary>
    public static Border CreateChip(string text, string? mdiIcon = null, IBrush? foreground = null, IBrush? background = null)
    {
        var fg = foreground ?? AppTheme.TextSecondary;
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = AppTheme.Space.XXS };
        if (mdiIcon is not null) content.Children.Add(new MdiIcon(mdiIcon, 13, fg));
        content.Children.Add(new TextBlock
        {
            Text = text, FontSize = AppTheme.Type.Caption, FontWeight = FontWeight.Medium, Foreground = fg,
            VerticalAlignment = VerticalAlignment.Center
        });
        return new Border
        {
            Background = background ?? AppTheme.CardElevatedBg,
            CornerRadius = new CornerRadius(AppTheme.Radius.Pill),
            Padding = new Thickness(AppTheme.Space.S, 3),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = content
        };
    }

    public static Border CreateBadge(string text, IBrush bg, IBrush fg) => new()
    {
        Background = bg,
        CornerRadius = new CornerRadius(999),
        Padding = new Thickness(10, 3),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock
        {
            Text = text,
            FontSize = 11.5,
            FontWeight = FontWeight.SemiBold,
            Foreground = fg
        }
    };

    public static Button CreatePrimaryButton(string mdiIcon, string text, Action onClick)
    {
        var btn = new Button
        {
            Classes = { "primary" },
            Background = AppTheme.BrandGradient,
            Foreground = Brushes.White,
            CornerRadius = new CornerRadius(AppTheme.Radius.M),
            Padding = new Thickness(16, 9),
            BorderThickness = new Thickness(0),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new MdiIcon(mdiIcon, 16, Brushes.White),
                    new TextBlock
                    {
                        Text = text,
                        FontSize = 13,
                        FontWeight = FontWeight.SemiBold,
                        Foreground = Brushes.White,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                }
            }
        };
        btn.Click += (_, _) => onClick();
        return btn;
    }

    public static Button CreateSecondaryButton(string mdiIcon, string text, Action onClick) =>
        CreateSecondaryButton(new MdiIcon(mdiIcon, 15, AppTheme.TextSecondary),
            new TextBlock { Text = text }, onClick);

    internal static Button CreateSecondaryButton(MdiIcon icon, TextBlock label, Action onClick)
    {
        label.FontSize = AppTheme.Type.Small;
        label.FontWeight = FontWeight.Medium;
        label.Foreground = AppTheme.TextPrimary;
        label.VerticalAlignment = VerticalAlignment.Center;
        var btn = new Button
        {
            Classes = { "ghost" },
            [!AutomationProperties.NameProperty] = label[!TextBlock.TextProperty],
            Background = AppTheme.CardBg,
            Foreground = AppTheme.TextPrimary,
            BorderBrush = AppTheme.BorderStrong,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(AppTheme.Radius.M),
            Padding = new Thickness(13, 8),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 7,
                Children = { icon, label }
            }
        };
        btn.Click += (_, _) => onClick();
        return btn;
    }

    public static Border CreateInfoTile(string title, string content, string? mdiIcon = null, IBrush? iconBrush = null) =>
        CreateInfoTile(title, RichText.Create(content, TextRole.Body), mdiIcon, iconBrush);

    /// <summary>Grouped block on the inset surface. The title is always a neutral label; only the icon carries semantic color.</summary>
    public static Border CreateInfoTile(string title, Control content, string? mdiIcon = null, IBrush? iconBrush = null)
    {
        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = AppTheme.Space.XS
        };
        if (!string.IsNullOrWhiteSpace(mdiIcon))
            header.Children.Add(new MdiIcon(mdiIcon, 14, iconBrush ?? AppTheme.TextMuted) { VerticalAlignment = VerticalAlignment.Center });
        header.Children.Add(TextStyles.Create(title, TextRole.Label, wrap: false));

        return new Border
        {
            Background = AppTheme.InputBg,
            CornerRadius = new CornerRadius(AppTheme.Radius.M),
            Padding = new Thickness(AppTheme.Space.L - 2, AppTheme.Space.M - 1),
            Child = new StackPanel
            {
                Spacing = AppTheme.Space.XXS,
                Children = { header, content }
            }
        };
    }

    /// <summary>Label column + value, aligned across rows that share a <see cref="Grid.IsSharedSizeScopeProperty"/> ancestor.</summary>
    public static Grid CreateKeyValue(string label, Control value, string sharedGroup = "KvLabel", double labelTop = 1)
    {
        var key = TextStyles.Create(label, TextRole.Label);
        key.Margin = new Thickness(0, labelTop, AppTheme.Space.M, 0);
        var grid = new KeyValueGrid(key, value)
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Auto) { SharedSizeGroup = sharedGroup },
                new ColumnDefinition(GridLength.Star)
            },
            Children = { key, value }
        };
        Grid.SetColumn(value, 1);
        return grid;
    }

    private sealed class KeyValueGrid(TextBlock key, Control value) : Grid
    {
        protected override Size MeasureOverride(Size availableSize)
        {
            value.Measure(Size.Infinity);
            // A short value can lend its unused space to a long label. When both are long,
            // leave the value at least half the row and let each side wrap.
            var budget = Math.Max(availableSize.Width / 2, availableSize.Width - value.DesiredSize.Width);
            key.MaxWidth = Math.Max(0, budget - key.Margin.Left - key.Margin.Right);
            return base.MeasureOverride(availableSize);
        }
    }

    /// <summary>
    /// The one ordinal marker for every numbered sequence (senses, collocations, examples, key points, table rows):
    /// a two-digit mono numeral on a warm-gray rounded tile, 24px tall to sit on a 22–24px first text line.
    /// </summary>
    public static Border CreateOrdinal(int number)
    {
        var numeral = TextStyles.Create(number.ToString("D2"), TextRole.Numeral, wrap: false);
        numeral.HorizontalAlignment = HorizontalAlignment.Center;
        numeral.VerticalAlignment = VerticalAlignment.Center;
        numeral.LineHeight = double.NaN;
        return new Border
        {
            Background = AppTheme.CardElevatedBg, MinWidth = 28, Height = 22, Padding = new Thickness(AppTheme.Space.XXS, 0),
            VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left,
            CornerRadius = new CornerRadius(AppTheme.Radius.S - 2), Child = numeral
        };
    }

    /// <summary>Keyboard shortcut rendered as a quiet warm-gray keycap.</summary>
    public static Border CreateKeycap(string shortcut, bool large = false) => new()
    {
        Background = AppTheme.CardElevatedBg,
        CornerRadius = new CornerRadius(6),
        Padding = large ? new Thickness(AppTheme.Space.S, 3) : new Thickness(6, 2),
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Right,
        Child = new TextBlock
        {
            Text = shortcut, FontSize = large ? AppTheme.Type.Small : 11,
            FontWeight = large ? FontWeight.Medium : FontWeight.Normal,
            Foreground = large ? AppTheme.TextSecondary : AppTheme.TextMuted
        }
    };

    public static Border CreateMetricKpiCard(string label, string value, string subtext, string mdiIcon, IBrush accentBrush) => new()
    {
        Background = AppTheme.CardBg,
        BorderBrush = AppTheme.BorderSubtle,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(AppTheme.Radius.L),
        BoxShadow = AppTheme.CardShadow,
        Padding = new Thickness(16, 12),
        MinWidth = 185,
        Child = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            ColumnSpacing = 12,
            Children =
            {
                new Border
                {
                    Width = 40,
                    Height = 40,
                    CornerRadius = new CornerRadius(10),
                    Background = AppTheme.InputBg,
                    BorderBrush = AppTheme.BorderSubtle,
                    BorderThickness = new Thickness(1),
                    Child = new MdiIcon(mdiIcon, 20, accentBrush)
                },
                new StackPanel
                {
                    [Grid.ColumnProperty] = 1,
                    Spacing = 1,
                    VerticalAlignment = VerticalAlignment.Center,
                    Children =
                    {
                        new TextBlock { Text = value, FontSize = 20, FontWeight = FontWeight.Bold, Foreground = AppTheme.TextPrimary },
                        new TextBlock { Text = label, FontSize = 12, FontWeight = FontWeight.Medium, Foreground = AppTheme.TextSecondary },
                        new TextBlock { Text = subtext, FontSize = 10.5, Foreground = AppTheme.TextMuted }
                    }
                }
            }
        }
    };
}
