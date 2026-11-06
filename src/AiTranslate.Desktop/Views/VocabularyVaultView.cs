using AiTranslate.Ai;
using AiTranslate.Core;
using AiTranslate.Desktop.Controls;
using AiTranslate.Speech;
using AiTranslate.Vocabulary;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;

namespace AiTranslate.Desktop.Views;

public sealed partial class VocabularyVaultView : UserControl
{
    private readonly IVocabularyService _service;
    private readonly IAiDictionaryService? _dictionary;
    private readonly ISpeechSynthesisService _speech;
    private readonly AppSettingsStore _settings;
    private readonly Action<string, string?, TranslationTriggerSource, string, string> _lookup;
    private readonly Action<string> _setStatus;
    private readonly NotebookShelf _shelf;
    private readonly VocabularyFilters _filters;
    private readonly ItemsControl _entries = new();
    private readonly ScrollViewer _scroll = new() { Padding = AppTheme.ScrollContentInset };
    private readonly StackPanel _empty = new() { Spacing = AppTheme.Space.M, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _title = TextStyles.Create("", TextRole.PageTitle);
    private readonly TextBlock _description = TextStyles.Create("", TextRole.FieldLabel);
    private readonly TextBlock _resultCount = TextStyles.Create("", TextRole.FieldLabel);
    private readonly Button _review;
    private string? _bookId;
    private IReadOnlyList<VocabularyNotebook> _books = [];
    private int _bookTotal;

    internal Control Toolbar { get; } = TextStyles.Create("生词本", TextRole.PageTitle);

    public VocabularyVaultView(IVocabularyService vocabularyService, ISpeechSynthesisService speechService,
        AppSettingsStore settingsStore, Action<string, string?, TranslationTriggerSource, string, string> onLookupInStudio,
        Action<string?> onCollect, Action<string> onSwitchToReview, Action<string> setStatus, IAiDictionaryService? dictionary = null)
    {
        _service = vocabularyService;
        _dictionary = dictionary;
        _speech = speechService;
        _settings = settingsStore;
        _lookup = onLookupInStudio;
        _setStatus = setStatus;
        _shelf = new NotebookShelf(SelectBook, () => EditNotebook(null), () => EditNotebook(CurrentBook), ShowOverview);
        _filters = new VocabularyFilters(ExportEntries);
        _filters.Changed += () => { ReloadEntries(); _scroll.ScrollToHome(); };
        _review = StudioUi.CreateSecondaryButton("brain", "复习", () => onSwitchToReview(_bookId ?? "all"));
        ToolTip.SetTip(_review, "复习当前词本");
        var collect = StudioUi.CreatePrimaryButton("plus", "查词收录", () => onCollect(_bookId));
        AutomationProperties.SetName(collect, "查词收录");
        var actions = new WrapPanel { [Grid.ColumnProperty] = 1, ItemSpacing = AppTheme.Space.S, LineSpacing = AppTheme.Space.S,
            VerticalAlignment = VerticalAlignment.Top, Children = { _review, collect } };
        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = AppTheme.Space.M,
            Children = { _title, actions }
        };
        _description.MaxLines = 2;
        _entries.ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel());
        _entries.ItemTemplate = new FuncDataTemplate<VocabularyEntry>((entry, _) => entry is null ? null : EntryRow(entry));
        _scroll.Content = _entries;
        var listCard = StudioUi.CreateBentoCard(new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"), RowSpacing = AppTheme.Space.M,
            Children =
            {
                new StackPanel
                {
                    Margin = AppTheme.ScrollContentInset, Spacing = AppTheme.Space.M,
                    Children = { header, _description, _filters, _resultCount }
                },
                new Grid { [Grid.RowProperty] = 1, Children = { _scroll, _empty } }
            }
        }, new Thickness(0, AppTheme.Space.L));
        _empty.Margin = new Thickness(AppTheme.Space.S, AppTheme.Space.XXL);
        _empty.VerticalAlignment = VerticalAlignment.Top;

        var workspace = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("1*,3*"), ColumnSpacing = AppTheme.Space.L,
            Margin = new Thickness(AppTheme.Space.XXL, 0, AppTheme.Space.XXL, AppTheme.Space.L),
            Children = { _shelf, listCard }
        };
        workspace.ColumnDefinitions[0].MinWidth = 168;
        workspace.ColumnDefinitions[0].MaxWidth = 248;
        Grid.SetColumn(listCard, 1);
        Content = workspace;
    }

    private VocabularyNotebook? CurrentBook => _books.FirstOrDefault(n => n.Id == _bookId);

    private void SelectBook(string? id)
    {
        _bookId = id;
        RefreshAll();
        _scroll.ScrollToHome();
    }

    public void RefreshAll()
    {
        _books = _service.GetNotebooks();
        var book = CurrentBook;
        _bookId = book?.Id;
        var stats = _service.GetDashboardStats();
        _bookTotal = book is null ? stats.TotalEntries : stats.CountByNotebook.GetValueOrDefault(book.Id);
        _shelf.Display(_books, stats, _bookId);
        _title.Text = book?.Name ?? "全部词条";
        _title.MaxLines = 2;
        _title.TextTrimming = TextTrimming.CharacterEllipsis;
        _description.Text = book is null ? $"来自 {_books.Count} 个词本的单词、短语和句子" : book.Description;
        _description.IsVisible = !string.IsNullOrWhiteSpace(_description.Text);
        _review.IsEnabled = _bookTotal > 0;
        ReloadEntries();
    }

    private void ReloadEntries()
    {
        var entries = _service.QueryEntries(_filters.Query(_bookId));
        _entries.ItemsSource = entries;
        _resultCount.Text = _filters.HasQuery ? $"找到 {entries.Count} 条 · 共 {_bookTotal} 条" : $"共 {entries.Count} 条";
        _empty.IsVisible = entries.Count == 0;
        if (!_empty.IsVisible) return;
        _empty.Children.Clear();
        _empty.Children.Add(new MdiIcon(_filters.HasQuery ? "magnify" : "notebook-outline", 30, AppTheme.TextMuted));
        var hint = TextStyles.Create(_filters.HasQuery ? "没有匹配的词条" : "这个词本还没有词条", TextRole.SectionTitle);
        hint.HorizontalAlignment = HorizontalAlignment.Center;
        _empty.Children.Add(hint);
        if (_filters.HasQuery)
        {
            var clear = StudioUi.CreateSecondaryButton("refresh", "清除搜索与筛选", _filters.Clear);
            clear.HorizontalAlignment = HorizontalAlignment.Center;
            _empty.Children.Add(clear);
        }
        else _empty.Children.Add(TextStyles.Create("点击「查词收录」，把查询结果加入词本。", TextRole.FieldLabel));
    }

    private Control EntryRow(VocabularyEntry entry)
    {
        var title = TextStyles.Create(entry.Headword, TextRole.SectionTitle);
        title.MaxLines = 2;
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        var meaning = TextStyles.Create(entry.Translation, TextRole.Secondary);
        meaning.MaxLines = 2;
        meaning.TextTrimming = TextTrimming.CharacterEllipsis;
        var book = _bookId is null ? _books.FirstOrDefault(n => n.Id == entry.NotebookId)?.Name : null;
        var meta = new WrapPanel { ItemSpacing = AppTheme.Space.S, LineSpacing = AppTheme.Space.XXS };
        meta.Children.Add(StudioUi.CreateChip(UiLabels.ItemType(entry.ItemType), HueMap.ItemType(entry.ItemType)));
        meta.Children.Add(StudioUi.CreateDotLabel(UiLabels.Stage(entry.Stage), HueMap.Stage(entry.Stage)));
        if (book is not null) meta.Children.Add(TextStyles.Create(book, TextRole.Meta));
        var open = new Button
        {
            Classes = { "ghost" }, Background = Brushes.Transparent, BorderThickness = default,
            Padding = new Thickness(AppTheme.Space.S), CornerRadius = new CornerRadius(AppTheme.Radius.S),
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = new StackPanel { Spacing = AppTheme.Space.XXS, Children = { title, meaning, meta } }
        };
        AutomationProperties.SetName(open, $"查看词条 {entry.Headword}");
        open.Click += (_, _) => ShowEntry(entry);
        var star = RowAction(entry.IsStarred ? "star" : "star-outline", entry.IsStarred ? "取消星标" : "添加星标",
            () =>
            {
                try { _service.ToggleStar(entry.Id); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _setStatus($"星标保存失败。{ex.Message}"); }
            }, entry.IsStarred ? AppTheme.Amber : AppTheme.TextMuted);
        var edit = RowAction("pencil-outline", "编辑或移动词条", () => EditEntry(entry), AppTheme.TextSecondary);
        Grid.SetColumn(star, 1);
        Grid.SetColumn(edit, 2);
        return new Border
        {
            BorderBrush = AppTheme.BorderSubtle, BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(0, AppTheme.Space.XXS),
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Children = { open, star, edit }
            }
        };
    }

    private static Button RowAction(string icon, string name, Action action, IBrush ink)
    {
        var button = StudioUi.CreateIconButton(icon, name, action);
        button.Content = new MdiIcon(icon, 16, ink);
        button.Background = Brushes.Transparent;
        button.BorderThickness = default;
        return button;
    }
}
