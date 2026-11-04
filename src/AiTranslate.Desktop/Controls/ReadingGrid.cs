using Avalonia;
using Avalonia.Controls;

namespace AiTranslate.Desktop.Controls;

/// <summary>Fit columns to the widest cell in each column before asking text to wrap.</summary>
internal class ReadingGrid : Grid
{
    private readonly double[] _growth;

    public ReadingGrid(string columns)
    {
        ColumnDefinitions = new ColumnDefinitions(columns);
        _growth = ColumnDefinitions.Select(c => c.Width.IsStar ? c.Width.Value : 0).ToArray();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var widths = new double[_growth.Length];
        foreach (var cell in Children)
        {
            // Rules and expanded details span the table and do not set any one column's width.
            var column = ContentColumn(cell);
            if (!cell.IsVisible || column < 0) continue;
            cell.Measure(Size.Infinity);
            widths[column] = Math.Max(widths[column], Math.Ceiling(cell.DesiredSize.Width));
        }

        var available = Math.Max(0, availableSize.Width - ColumnSpacing * (widths.Length - 1));
        Reflow(available, widths);
        var spare = available - widths.Sum();
        var active = Enumerable.Range(0, widths.Length).Where(i => widths[i] > 0).ToArray();
        if (spare < 0)
        {
            // Short columns keep their natural width. Only the columns still too large share the deficit.
            var remaining = available;
            double Weight(int i) => Math.Max(1, _growth[i]);
            var remainingWeight = active.Sum(Weight);
            foreach (var i in active.OrderBy(i => widths[i] / Weight(i)))
            {
                widths[i] = Math.Min(widths[i], remaining * Weight(i) / remainingWeight);
                remaining -= widths[i];
                remainingWeight -= Weight(i);
            }
        }
        else if (double.IsFinite(spare))
        {
            var weight = active.Sum(i => _growth[i]);
            if (weight > 0)
                foreach (var i in active) widths[i] += spare * _growth[i] / weight;
        }

        for (var i = 0; i < widths.Length; i++)
            ColumnDefinitions[i].Width = new GridLength(widths[i]);
        return base.MeasureOverride(availableSize);
    }

    protected virtual int ContentColumn(Control cell) => GetColumnSpan(cell) == 1 ? GetColumn(cell) : -1;
    protected virtual void Reflow(double available, double[] widths) { }

    public void AddCell(Control cell, int row, int column)
    {
        SetRow(cell, row);
        SetColumn(cell, column);
        Children.Add(cell);
    }
}
