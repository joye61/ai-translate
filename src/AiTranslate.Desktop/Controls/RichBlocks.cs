using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;

namespace AiTranslate.Desktop.Controls;

/// <summary>
/// Document-style building blocks for structured AI output: lists, tables, callouts, indented trees and meters.
/// All colors come from <see cref="AppTheme"/>; content controls are supplied by the caller.
/// </summary>
internal static class RichBlocks
{
    public enum CalloutTone { Neutral, Insight, Attention }

    /// <summary>Bulleted or numbered list. Markers align with the first text line of each item.</summary>
    public static Control List(IEnumerable<Control> items, bool numbered = false)
    {
        var list = new StackPanel { Spacing = AppTheme.Space.S };
        Grid.SetIsSharedSizeScope(list, true);
        var n = 0;
        foreach (var item in items)
        {
            n++;
            Control marker = numbered
                ? StudioUi.CreateOrdinal(n)
                : new Ellipse
                {
                    Width = 5, Height = 5, Fill = AppTheme.Accent, VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(AppTheme.Space.XXS, 9, 0, 0)
                };
            var row = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition(GridLength.Auto) { SharedSizeGroup = "ListMarker" },
                    new ColumnDefinition(GridLength.Star)
                },
                ColumnSpacing = AppTheme.Space.M - 2,
                Children = { marker, item }
            };
            Grid.SetColumn(item, 1);
            list.Children.Add(row);
        }
        return list;
    }

    /// <summary>
    /// Bordered white table with a quiet header and hairline row separators; highlighted rows get the inset tint.
    /// <paramref name="columns"/> uses Grid syntax, e.g. "Auto,*,Auto".
    /// </summary>
    public static Control Table(string columns, IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<Control>> rows,
        IReadOnlySet<int>? highlightedRows = null)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(columns) };
        var columnCount = grid.ColumnDefinitions.Count;
        var r = 0;
        if (headers.Count > 0)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            AddBand(r, AppTheme.InputBg, topRule: false);
            for (var c = 0; c < headers.Count && c < columnCount; c++)
                AddCell(r, c, TextStyles.Create(headers[c], TextRole.Label, wrap: false));
            r++;
        }
        for (var i = 0; i < rows.Count; i++, r++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            AddBand(r, highlightedRows?.Contains(i) == true ? AppTheme.AccentFaint : null, topRule: r > 0);
            for (var c = 0; c < rows[i].Count && c < columnCount; c++) AddCell(r, c, rows[i][c]);
        }
        return TableFrame(grid);

        void AddBand(int row, IBrush? background, bool topRule)
        {
            var band = new Border
            {
                Background = background,
                BorderBrush = AppTheme.BorderSubtle, BorderThickness = new Thickness(0, topRule ? 1 : 0, 0, 0)
            };
            Grid.SetRow(band, row);
            Grid.SetColumnSpan(band, columnCount);
            grid.Children.Add(band);
        }

        void AddCell(int row, int column, Control content)
        {
            var cell = new Border
            {
                Padding = new Thickness(column == 0 ? AppTheme.Space.M : AppTheme.Space.S, AppTheme.Space.S,
                    column == columnCount - 1 ? AppTheme.Space.M : AppTheme.Space.S, AppTheme.Space.S),
                Child = content
            };
            if (content is TextBlock text && grid.ColumnDefinitions[column].Width.IsAuto) text.MaxWidth = 200;
            Grid.SetRow(cell, row);
            Grid.SetColumn(cell, column);
            grid.Children.Add(cell);
        }
    }

    public static Border TableFrame(Control rows)
    {
        const double stroke = 1;
        return new Border
        {
            Background = AppTheme.CardBg, BorderBrush = AppTheme.BorderSubtle, BorderThickness = new Thickness(stroke),
            CornerRadius = new CornerRadius(AppTheme.Radius.M),
            // The outer border draws its stroke around the radius centerline. Clip the rows at its inner edge.
            Child = new Border
            {
                CornerRadius = new CornerRadius(AppTheme.Radius.M - stroke / 2), ClipToBounds = true, Child = rows
            }
        };
    }

    /// <summary>Soft tinted note with a leading icon. Attention uses amber (memory aids, cautions), Insight the AI accent.</summary>
    public static Control Callout(string icon, Control content, CalloutTone tone = CalloutTone.Neutral)
    {
        var (background, iconBrush) = tone switch
        {
            CalloutTone.Insight => (AppTheme.InsightSoft, AppTheme.Insight),
            CalloutTone.Attention => (AppTheme.AmberSoft, AppTheme.Amber),
            _ => (AppTheme.CardElevatedBg, AppTheme.TextSecondary)
        };
        var glyph = new MdiIcon(icon, 16, iconBrush) { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 3, 0, 0) };
        Grid.SetColumn(content, 1);
        return new Border
        {
            Background = background, CornerRadius = new CornerRadius(AppTheme.Radius.M),
            Padding = new Thickness(AppTheme.Space.M, AppTheme.Space.S + 2, AppTheme.Space.L, AppTheme.Space.S + 2),
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = AppTheme.Space.S + 2,
                Children = { glyph, content }
            }
        };
    }

    /// <summary>Indented outline. Nested levels hang off a hairline guide so hierarchy reads at a glance.</summary>
    public static Control Tree(IEnumerable<(int Depth, Control Content)> nodes)
    {
        const double indent = 22;
        var tree = new StackPanel { Spacing = AppTheme.Space.S };
        foreach (var (depth, content) in nodes)
        {
            if (depth <= 0)
            {
                tree.Children.Add(content);
                continue;
            }
            tree.Children.Add(new Border
            {
                Margin = new Thickness((depth - 1) * indent + 7, 0, 0, 0),
                BorderBrush = AppTheme.BorderStrong, BorderThickness = new Thickness(1.5, 0, 0, 0),
                Padding = new Thickness(indent - 8, 1, 0, 1), Child = content
            });
        }
        return tree;
    }

    /// <summary>Segmented 1…max scale with end labels, e.g. 口语 ▮▮▮▯▯ 正式.</summary>
    public static Control Meter(int value, int max, string low, string high)
    {
        var segments = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        for (var i = 1; i <= max; i++)
            segments.Children.Add(new Border
            {
                Width = 22, Height = 6, CornerRadius = new CornerRadius(AppTheme.Radius.Pill),
                Background = i <= value ? AppTheme.Insight : AppTheme.BorderStrong
            });
        var lowText = TextStyles.Create(low, TextRole.Meta, wrap: false);
        var highText = TextStyles.Create(high, TextRole.Meta, wrap: false);
        lowText.VerticalAlignment = highText.VerticalAlignment = VerticalAlignment.Center;
        return new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = AppTheme.Space.S, Children = { lowText, segments, highText }
        };
    }
}
