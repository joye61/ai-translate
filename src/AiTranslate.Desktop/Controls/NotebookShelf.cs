using AiTranslate.Vocabulary;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace AiTranslate.Desktop.Controls;

/// <summary>The groups. One tap selects; the list beside it belongs to the selected group.</summary>
internal sealed class NotebookShelf : UserControl
{
    private readonly StackPanel _books = new() { Spacing = AppTheme.Space.XXS };
    private readonly Action<string> _select;
    private string? _selectedId;

    internal NotebookShelf(Action<string> select, Action create)
    {
        _select = select;
        var inset = AppTheme.ScrollContentInset;
        var heading = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = inset,
            Children = { TextStyles.Create("生词本", TextRole.SectionTitle, wrap: false) }
        };
        heading.Children[0].VerticalAlignment = VerticalAlignment.Center;
        var add = StudioUi.CreateIconButton("plus", "新建生词本", create);
        Grid.SetColumn(add, 1);
        heading.Children.Add(add);
        Content = StudioUi.CreateBentoCard(new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"), RowSpacing = AppTheme.Space.M,
            Children =
            {
                heading,
                new ScrollViewer { [Grid.RowProperty] = 1, Content = _books, Padding = inset }
            }
        }, new Thickness(0, AppTheme.Space.M));
    }

    internal void Display(IReadOnlyList<VocabularyNotebookSummary> summaries, string selectedId)
    {
        _selectedId = selectedId;
        _books.Children.Clear();
        foreach (var summary in summaries) _books.Children.Add(BookRow(summary));
    }

    private Button BookRow(VocabularyNotebookSummary summary)
    {
        var (book, count, due) = (summary.Notebook, summary.EntryCount, summary.DueCount);
        var active = _selectedId == book.Id;
        var ink = active ? AppTheme.AccentBright : AppTheme.TextMuted;
        // A due notebook carries a coral dot beside its count; the count itself stays quiet.
        var tally = new StackPanel
        {
            [Grid.ColumnProperty] = 2, Orientation = Orientation.Horizontal, Spacing = AppTheme.Space.XS, VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = count.ToString(), FontSize = AppTheme.Type.Caption, Foreground = active ? AppTheme.AccentBright : AppTheme.TextMuted, VerticalAlignment = VerticalAlignment.Center }
            }
        };
        if (due > 0)
            tally.Children.Insert(0, new Avalonia.Controls.Shapes.Ellipse { Width = 6, Height = 6, Fill = AppTheme.Accent, VerticalAlignment = VerticalAlignment.Center });
        var row = new Button
        {
            Classes = { "nav" },
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            MinHeight = 40, Padding = new Thickness(AppTheme.Space.M, AppTheme.Space.XS), BorderThickness = default,
            CornerRadius = new CornerRadius(AppTheme.Radius.S),
            Background = active ? AppTheme.AccentSoft : Brushes.Transparent,
            Content = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = AppTheme.Space.S,
                Children =
                {
                    new FontAwesomeIcon(active ? "folder-open" : "folder", 16, ink) { VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock
                    {
                        [Grid.ColumnProperty] = 1, Text = book.Name, Foreground = active ? AppTheme.AccentBright : AppTheme.TextPrimary,
                        FontSize = AppTheme.Type.Small, FontWeight = active ? FontWeight.SemiBold : FontWeight.Normal,
                        VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxLines = 2,
                        TextTrimming = TextTrimming.CharacterEllipsis
                    },
                    tally
                }
            }
        };
        row.Classes.Set("active", active);
        AutomationProperties.SetName(row, $"{book.Name}，{count} 条");
        ToolTip.SetTip(row, due > 0 ? $"{book.Name} · {due} 条待复习" : book.Name);
        row.Click += (_, _) => _select(book.Id);
        return row;
    }
}
