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

    public static Border Annotation(string label, SelectableTextBlock text) => new()
    {
        Background = AppTheme.Hues.Sky.Soft,
        CornerRadius = new CornerRadius(AppTheme.Radius.S),
        Padding = new Thickness(AppTheme.Space.S, AppTheme.Space.XXS),
        Child = StudioUi.CreateKeyValue(label, text, "AnnotationLabel", labelTop: 0)
    };

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
        var grid = new ReadingGrid(columns);
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

    // A single reading surface with fine rules between entries. Keep the first and last
    // entry flush with the section when a frame would repeat the surrounding container.
    public static Control RuledRows(IEnumerable<Control> entries, bool framed = false)
    {
        var rows = new StackPanel();
        var inset = framed ? AppTheme.Space.M : 0;
        foreach (var entry in entries)
        {
            var first = rows.Children.Count == 0;
            rows.Children.Add(new Border
            {
                BorderBrush = AppTheme.BorderSubtle, BorderThickness = new Thickness(0, first ? 0 : 1, 0, 0),
                Padding = new Thickness(inset, first && !framed ? 0 : AppTheme.Space.M, inset, AppTheme.Space.M),
                Child = entry
            });
        }
        if (!framed && rows.Children.LastOrDefault() is Border last)
            last.Padding = new Thickness(0, last.Padding.Top, 0, 0);
        return framed ? TableFrame(rows) : rows;
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

    /// <summary>Inset for quoted text, with an optional playback action.</summary>
    public static Border Quote(Control content, Control? action = null)
    {
        var mark = new MdiIcon("format-quote-open", 16, AppTheme.TextMuted)
        {
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 3, 0, 0)
        };
        var grid = new QuoteGrid(mark, content, action)
        {
            ColumnDefinitions = new ColumnDefinitions(action is null ? "Auto,*" : "Auto,*,Auto"),
            ColumnSpacing = AppTheme.Space.S, Children = { mark, content }
        };
        Grid.SetColumn(content, 1);
        if (action is not null)
        {
            grid.RowDefinitions = new RowDefinitions("Auto,Auto");
            action.HorizontalAlignment = HorizontalAlignment.Right;
            action.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(action, 2);
            grid.Children.Add(action);
        }
        return new Border
        {
            Background = AppTheme.InputBg, CornerRadius = new CornerRadius(AppTheme.Radius.M),
            Padding = new Thickness(AppTheme.Space.M, AppTheme.Space.S), Child = grid
        };
    }

    private sealed class QuoteGrid(Control mark, Control content, Control? action) : Grid
    {
        protected override Size MeasureOverride(Size availableSize)
        {
            if (action is not null)
            {
                mark.Measure(Size.Infinity);
                content.Measure(Size.Infinity);
                action.Measure(Size.Infinity);
                var inlineWidth = mark.DesiredSize.Width + content.DesiredSize.Width + action.DesiredSize.Width + 2 * ColumnSpacing;
                var below = availableSize.Width < 520 && inlineWidth > availableSize.Width;
                SetColumnSpan(content, below ? 2 : 1);
                SetColumn(action, below ? 1 : 2);
                SetRow(action, below ? 1 : 0);
                SetColumnSpan(action, below ? 2 : 1);
                action.Margin = below ? new Thickness(0, AppTheme.Space.XXS, 0, 0) : default;
            }
            return base.MeasureOverride(availableSize);
        }
    }

    /// <summary>Indent the entire clause row so wrapped text stays at its own level.</summary>
    public static Control Tree(IEnumerable<(int Depth, Control Content)> nodes)
    {
        var tree = new StackPanel { Spacing = AppTheme.Space.S };
        foreach (var (depth, content) in nodes)
        {
            content.Margin += new Thickness(Math.Max(0, depth) * AppTheme.Space.XL, 0, 0, 0);
            tree.Children.Add(content);
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
