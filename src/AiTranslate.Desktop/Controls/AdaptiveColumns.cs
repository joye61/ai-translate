using Avalonia;
using Avalonia.Controls;

namespace AiTranslate.Desktop.Controls;

/// <summary>Equal-width groups that fold into pairs or a single column, keeping controls and tab order intact.</summary>
internal sealed class AdaptiveColumns(int wideColumns, double minColumnWidth) : Grid
{
    protected override Size MeasureOverride(Size availableSize)
    {
        var columns = wideColumns;
        while (columns > 1 && columns * minColumnWidth + (columns - 1) * ColumnSpacing > availableSize.Width)
            columns /= 2;
        var rows = (Children.Count + columns - 1) / columns;
        if (ColumnDefinitions.Count != columns || RowDefinitions.Count != rows)
        {
            ColumnDefinitions.Clear();
            RowDefinitions.Clear();
            for (var i = 0; i < columns; i++) ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            for (var i = 0; i < rows; i++) RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        }
        for (var i = 0; i < Children.Count; i++)
        {
            SetRow(Children[i], i / columns);
            SetColumn(Children[i], i % columns);
        }
        return base.MeasureOverride(availableSize);
    }
}
