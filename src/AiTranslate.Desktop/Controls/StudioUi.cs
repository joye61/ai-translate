using Avalonia;
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
    public static Border CreateBentoCard(Control child, Thickness? padding = null, IBrush? background = null, IBrush? borderBrush = null) => new()
    {
        Background = background ?? AppTheme.CardBg,
        BorderBrush = borderBrush ?? AppTheme.BorderSubtle,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(14),
        Padding = padding ?? new Thickness(20, 18),
        Child = child
    };

    public static Border CreateHeroCard(Control child, Thickness? padding = null) => new()
    {
        Background = AppTheme.HeroGradient,
        BorderBrush = AppTheme.BorderStrong,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(16),
        Padding = padding ?? new Thickness(24, 20),
        Child = child
    };

    public static StackPanel CreateSectionHeader(string mdiIcon, string title, string? subtitle, IBrush accentBrush)
    {
        var stack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Children =
            {
                new Border
                {
                    Width = 30,
                    Height = 30,
                    CornerRadius = new CornerRadius(8),
                    Background = AppTheme.InputBg,
                    BorderBrush = AppTheme.BorderSubtle,
                    BorderThickness = new Thickness(1),
                    Child = new MdiIcon(mdiIcon, 16, accentBrush)
                },
                new TextBlock
                {
                    Text = title,
                    FontSize = 15,
                    FontWeight = FontWeight.Bold,
                    Foreground = AppTheme.TextPrimary,
                    VerticalAlignment = VerticalAlignment.Center
                }
            }
        };
        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            stack.Children.Add(new TextBlock
            {
                Text = subtitle,
                FontSize = 12,
                Foreground = AppTheme.TextMuted,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 1, 0, 0)
            });
        }
        return stack;
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
            Background = AppTheme.BrandGradient,
            Foreground = Brushes.White,
            CornerRadius = new CornerRadius(10),
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

    public static Button CreateSecondaryButton(string mdiIcon, string text, Action onClick)
    {
        var btn = new Button
        {
            Background = AppTheme.CardElevatedBg,
            Foreground = AppTheme.TextPrimary,
            BorderBrush = AppTheme.BorderSubtle,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(13, 8),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 7,
                Children =
                {
                    new MdiIcon(mdiIcon, 15, AppTheme.TextSecondary),
                    new TextBlock
                    {
                        Text = text,
                        FontSize = 12.5,
                        FontWeight = FontWeight.Medium,
                        Foreground = AppTheme.TextPrimary,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                }
            }
        };
        btn.Click += (_, _) => onClick();
        return btn;
    }

    public static Border CreateInfoTile(string title, string content, string? mdiIcon = null, IBrush? iconBrush = null)
    {
        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6
        };
        if (!string.IsNullOrWhiteSpace(mdiIcon))
            header.Children.Add(new MdiIcon(mdiIcon, 14, iconBrush ?? AppTheme.AccentBright));
        header.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = iconBrush ?? AppTheme.TextSecondary
        });

        return new Border
        {
            Background = AppTheme.InputBg,
            BorderBrush = AppTheme.BorderSubtle,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 11),
            Child = new StackPanel
            {
                Spacing = 5,
                Children =
                {
                    header,
                    new TextBlock
                    {
                        Text = content,
                        FontSize = 13.5,
                        LineHeight = 20,
                        Foreground = AppTheme.TextPrimary,
                        TextWrapping = TextWrapping.Wrap
                    }
                }
            }
        };
    }

    public static Border CreateMetricKpiCard(string label, string value, string subtext, string mdiIcon, IBrush accentBrush) => new()
    {
        Background = AppTheme.CardBg,
        BorderBrush = AppTheme.BorderSubtle,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(12),
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
