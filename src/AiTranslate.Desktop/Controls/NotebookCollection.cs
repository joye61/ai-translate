using AiTranslate.Core;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;

namespace AiTranslate.Desktop.Controls;

internal sealed class NotebookBookmarkButton : Button
{
    private readonly TextBlock _label = TextStyles.Create("", TextRole.Label, wrap: false);
    private readonly FontAwesomeIcon _icon = new() { IconSize = 16 };
    private bool _saved;

    protected override Type StyleKeyOverride => typeof(Button);

    public NotebookBookmarkButton(Action<bool> setCollected)
    {
        _label.VerticalAlignment = VerticalAlignment.Center;
        Content = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = AppTheme.Space.XS,
            Children = { _icon, _label }
        };
        Height = 34;
        MinWidth = 104;
        Padding = new Thickness(AppTheme.Space.S, 0);
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(AppTheme.Radius.M);
        HorizontalContentAlignment = HorizontalAlignment.Center;
        VerticalContentAlignment = VerticalAlignment.Center;
        Click += (_, _) => setCollected(!_saved);
        SetSaved(false);
    }

    public void SetSaved(bool saved)
    {
        _saved = saved;
        Classes.Set("primary", !saved);
        Classes.Set("ghost", saved);
        Background = saved ? AppTheme.CardElevatedBg : AppTheme.BrandGradient;
        BorderBrush = saved ? AppTheme.BorderStrong : Brushes.Transparent;
        _icon.Foreground = _label.Foreground = saved ? AppTheme.TextSecondary : AppTheme.CardBg;
        _icon.IconName = saved ? "bookmark" : "regular/bookmark";
        _label.Text = saved ? "取消收录" : "加入生词表";
        AutomationProperties.SetName(this, _label.Text);
        ToolTip.SetTip(this, saved ? "从当前生词本取消收录" : "将此词条加入当前生词表");
    }
}

internal sealed class NotebookCollection : StackPanel
{
    private readonly ComboBox _picker;
    private readonly NotebookBookmarkButton _bookmark;
    private readonly Func<string, bool> _isCollected;

    public NotebookCollection(Func<string, bool> isCollected, Action<string> select, Action<VocabularyNotebook, bool> setCollected)
    {
        _isCollected = isCollected;
        Orientation = Orientation.Horizontal;
        Spacing = AppTheme.Space.XS;
        _picker = new ComboBox
        {
            Classes = { "chip" }, MinWidth = 112, MaxWidth = 208, VerticalAlignment = VerticalAlignment.Center,
            ItemTemplate = new FuncDataTemplate<VocabularyNotebook>((book, _) =>
            {
                var name = TextStyles.Create(book?.Name, TextRole.Body, wrap: false);
                name.TextTrimming = TextTrimming.CharacterEllipsis;
                Grid.SetColumn(name, 1);
                return new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = AppTheme.Space.S,
                    Children = { new FontAwesomeIcon("folder", 14, AppTheme.TextMuted), name }
                };
            }),
            SelectionBoxItemTemplate = new FuncDataTemplate<VocabularyNotebook>((book, _) =>
            {
                var label = TextStyles.Create(book?.Name, TextRole.FieldLabel, wrap: false);
                label.TextTrimming = TextTrimming.CharacterEllipsis;
                Grid.SetColumn(label, 1);
                return new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = AppTheme.Space.XS,
                    Children = { new FontAwesomeIcon("book", 14, AppTheme.TextSecondary), label }
                };
            })
        };
        AutomationProperties.SetName(_picker, "收录到");
        _bookmark = new NotebookBookmarkButton(collect =>
        {
            if (_picker.SelectedItem is not VocabularyNotebook book) return;
            setCollected(book, collect);
            RefreshBookmark();
        });
        _picker.SelectionChanged += (_, _) =>
        {
            if (_picker.SelectedItem is not VocabularyNotebook book) return;
            select(book.Id);
            ToolTip.SetTip(_picker, book.Name);
            RefreshBookmark();
        };
        Children.Add(_picker);
        Children.Add(_bookmark);
    }

    public void SetNotebooks(IReadOnlyList<VocabularyNotebook> books, string id)
    {
        _picker.ItemsSource = books;
        _picker.SelectedItem = books.FirstOrDefault(b => b.Id == id) ?? books.FirstOrDefault();
        RefreshBookmark();
    }

    private void RefreshBookmark()
    {
        if (_picker.SelectedItem is VocabularyNotebook book) _bookmark.SetSaved(_isCollected(book.Id));
    }
}
