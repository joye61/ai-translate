using AiTranslate.Core;
using AiTranslate.Vocabulary;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace AiTranslate.Desktop.Controls;

internal sealed class NotebookShelf : UserControl
{
    private readonly TextBox _search = new() { PlaceholderText = "查找词本", FontSize = AppTheme.Type.Small };
    private readonly StackPanel _books = new() { Spacing = AppTheme.Space.XXS };
    private readonly Action<string?> _select;
    private readonly Button _edit;
    private IReadOnlyList<VocabularyNotebook> _notebooks = [];
    private VocabularyDashboardStats? _stats;
    private string? _selectedId;

    internal NotebookShelf(Action<string?> select, Action create, Action edit, Action overview)
    {
        _select = select;
        var inset = AppTheme.ScrollContentInset;
        var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = inset };
        heading.Children.Add(new TextBlock
        {
            Text = "我的词本", FontWeight = FontWeight.SemiBold, FontSize = AppTheme.Type.BodyLarge,
            Foreground = AppTheme.TextPrimary, VerticalAlignment = VerticalAlignment.Center
        });
        var add = StudioUi.CreateIconButton("plus", "新建词本", create);
        Grid.SetColumn(add, 1);
        heading.Children.Add(add);
        AutomationProperties.SetName(_search, "查找词本");
        _search.Margin = inset;
        _search.TextChanged += (_, _) => Render();
        _edit = StudioUi.CreateCompactButton("pencil-outline", "编辑当前词本", edit);
        var footer = new StackPanel
        {
            [Grid.RowProperty] = 3, Spacing = AppTheme.Space.XXS, Margin = inset,
            Children = { _edit, StudioUi.CreateCompactButton("chart-timeline-variant", "学习概览", overview) }
        };
        var panel = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"),
            RowSpacing = AppTheme.Space.M,
            Children = { heading, _search, new ScrollViewer { [Grid.RowProperty] = 2, Content = _books, Padding = inset }, footer }
        };
        Grid.SetRow(_search, 1);
        Content = StudioUi.CreateBentoCard(panel, new Thickness(0, AppTheme.Space.M));
    }

    internal void Display(IReadOnlyList<VocabularyNotebook> notebooks, VocabularyDashboardStats stats, string? selectedId)
    {
        _notebooks = notebooks;
        _stats = stats;
        _selectedId = selectedId;
        _edit.IsVisible = selectedId is not null;
        Render();
    }

    internal void ClearSearch() => _search.Text = "";

    private void Render()
    {
        if (_stats is null) return;
        _books.Children.Clear();
        _books.Children.Add(BookRow(null, "全部词条", "bookshelf", _stats.TotalEntries));
        var matches = _notebooks.Where(n => n.Name.Contains(_search.Text?.Trim() ?? "", StringComparison.OrdinalIgnoreCase));
        foreach (var book in matches)
            _books.Children.Add(BookRow(book.Id, book.Name, book.IconName, _stats.CountByNotebook.GetValueOrDefault(book.Id)));
        if (_books.Children.Count == 1)
            _books.Children.Add(new TextBlock { Text = "没有匹配的词本", Foreground = AppTheme.TextMuted, Margin = new Thickness(AppTheme.Space.S) });
    }

    private Button BookRow(string? id, string name, string icon, int count)
    {
        var active = _selectedId == id;
        var ink = active ? AppTheme.AccentBright : AppTheme.TextSecondary;
        var hue = id is null ? AppTheme.Hues.Coral : HueMap.ForKey(id);
        var row = new Button
        {
            Classes = { "nav" },
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            MinHeight = 42, Padding = new Thickness(AppTheme.Space.S), BorderThickness = default,
            CornerRadius = new CornerRadius(AppTheme.Radius.S),
            Background = active ? AppTheme.AccentSoft : Brushes.Transparent,
            Content = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = AppTheme.Space.S,
                Children =
                {
                    new Border
                    {
                        Width = 26, Height = 26, CornerRadius = new CornerRadius(7),
                        Background = active ? AppTheme.CardBg : hue.Soft,
                        VerticalAlignment = VerticalAlignment.Center,
                        Child = new MdiIcon(icon, 15, hue.Ink)
                    },
                    new TextBlock
                    {
                        [Grid.ColumnProperty] = 1, Text = name, Foreground = ink, FontSize = AppTheme.Type.Small,
                        FontWeight = active ? FontWeight.SemiBold : FontWeight.Normal, VerticalAlignment = VerticalAlignment.Center,
                        TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis
                    },
                    new TextBlock { [Grid.ColumnProperty] = 2, Text = count.ToString(), FontSize = AppTheme.Type.Caption,
                        Foreground = ink, VerticalAlignment = VerticalAlignment.Center }
                }
            }
        };
        row.Classes.Set("active", active);
        AutomationProperties.SetName(row, $"{name}，{count} 条");
        ToolTip.SetTip(row, name);
        row.Click += (_, _) => _select(id);
        return row;
    }
}
