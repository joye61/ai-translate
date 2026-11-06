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

/// <summary>Saved entries grouped into notebooks. Each notebook is a list that can be sorted, reviewed and exported.</summary>
public sealed partial class VocabularyVaultView : UserControl
{
    private static readonly string[] SortLabels = ["最近添加", "字母顺序", "待复习优先"];

    private readonly IVocabularyService _service;
    private readonly VocabularyEntryDetail _detail;
    private readonly Action<string> _setStatus;
    private readonly NotebookShelf _shelf;
    private readonly VocabularyReviewView _reviewPage;
    private readonly Grid _browse;
    private readonly ItemsControl _entries = new();
    private readonly ScrollViewer _scroll = new() { Padding = AppTheme.ScrollContentInset };
    private readonly StackPanel _empty = new() { Spacing = AppTheme.Space.M, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _title = TextStyles.Create("", TextRole.PageTitle, wrap: false);
    private readonly TextBlock _summary = TextStyles.Create("", TextRole.FieldLabel, wrap: false);
    private readonly TextBlock _due = TextStyles.Create("", TextRole.FieldLabel, wrap: false);
    private readonly ComboBox _sort = new()
    {
        ItemsSource = SortLabels, SelectedIndex = 0, FontSize = AppTheme.Type.Small, Height = 34, MinWidth = 132,
        VerticalAlignment = VerticalAlignment.Center, CornerRadius = new CornerRadius(AppTheme.Radius.M),
        Padding = new Thickness(AppTheme.Space.M, 0, AppTheme.Space.S, 0),
        SelectionBoxItemTemplate = new FuncDataTemplate<string>((label, _) =>
        {
            var prefix = TextStyles.Create("排序", TextRole.FieldLabel, wrap: false);
            prefix.Foreground = AppTheme.TextMuted;
            prefix.VerticalAlignment = VerticalAlignment.Center;
            var value = TextStyles.Create(label, TextRole.Body, wrap: false);
            value.VerticalAlignment = VerticalAlignment.Center;
            return new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = AppTheme.Space.S, Children = { prefix, value }
            };
        })
    };
    private readonly Button _review;
    private readonly Button _export;
    private readonly Button _more;
    private readonly Button _hide;
    private readonly HashSet<string> _revealed = [];
    private readonly Border _hiddenBadge;
    private string _bookId = "default";
    private IReadOnlyList<VocabularyNotebookSummary> _summaries = [];

    internal Control Toolbar { get; } = TextStyles.Create("生词本", TextRole.PageTitle);

    public VocabularyVaultView(IVocabularyService vocabularyService, ISpeechSynthesisService speechService,
        AppSettingsStore settingsStore, Action<string, string?, TranslationTriggerSource, string, string> onLookupInStudio,
        Action<string> setStatus, IAiDictionaryService dictionary)
    {
        _service = vocabularyService;
        _setStatus = setStatus;
        _detail = new(vocabularyService, dictionary, speechService, settingsStore, onLookupInStudio);
        _shelf = new NotebookShelf(SelectBook, () => EditNotebook(null));
        _reviewPage = new VocabularyReviewView(vocabularyService, speechService, settingsStore, ShowReviewedEntry, ExitReview, setStatus);
        _reviewPage.IsVisible = false;

        _title.TextTrimming = TextTrimming.CharacterEllipsis;
        _review = StudioUi.CreatePrimaryButton("brain", "复习", () => StartReview(_bookId));
        AutomationProperties.SetName(_review, "开始复习");
        ToolTip.SetTip(_review, "复习当前生词本");
        _more = StudioUi.CreateIconButton("ellipsis", "更多操作", () => { });
        _export = StudioUi.CreateIconButton("share-from-square", "导出当前生词本", ExportEntries);
        _hide = StudioUi.CreateIconButton("eye", "隐藏列表释义", ToggleHiddenTranslations);
        _hiddenBadge = StudioUi.CreateChip("释义已隐藏", AppTheme.Hues.Coral, "eye-slash");
        _sort.SelectionChanged += (_, _) => { ReloadEntries(); _scroll.ScrollToHome(); };
        AutomationProperties.SetName(_sort, "排序");

        var tools = new StackPanel
        {
            [Grid.ColumnProperty] = 1, Orientation = Orientation.Horizontal, Spacing = AppTheme.Space.S,
            Children = { _sort, _hide, _export, _more }
        };
        Grid.SetColumn(_review, 1);
        _review.HorizontalAlignment = HorizontalAlignment.Right;
        _review.VerticalAlignment = VerticalAlignment.Center;
        _due.Foreground = AppTheme.AccentBright;
        _due.FontWeight = FontWeight.SemiBold;
        // The badge makes this row taller when it appears, and a fractional centering offset blurs the text by half a pixel.
        // A fixed height taller than the badge keeps the text on the same pixel row either way.
        _summary.VerticalAlignment = _due.VerticalAlignment = VerticalAlignment.Center;
        _hiddenBadge.VerticalAlignment = VerticalAlignment.Center;
        var summary = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = AppTheme.Space.S, VerticalAlignment = VerticalAlignment.Center,
            Height = 28, Children = { _summary, _due, _hiddenBadge }
        };
        _title.VerticalAlignment = VerticalAlignment.Center;
        var divider = new Border
        {
            Height = 1, Background = AppTheme.BorderSubtle, Margin = new Thickness(0, AppTheme.Space.XS, 0, 0)
        };
        Grid.SetRow(divider, 2);
        Grid.SetColumnSpan(divider, 2);
        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto"),
            ColumnSpacing = AppTheme.Space.M, RowSpacing = AppTheme.Space.S, Margin = AppTheme.ScrollContentInset,
            Children = { _title, _review, summary, tools, divider }
        };
        Grid.SetRow(summary, 1);
        Grid.SetRow(tools, 1);

        _entries.ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel());
        _entries.ItemTemplate = new FuncDataTemplate<VocabularyEntry>((entry, _) => entry is null ? null : EntryRow(entry));
        _scroll.Content = _entries;
        var listCard = StudioUi.CreateBentoCard(new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"), RowSpacing = AppTheme.Space.S,
            Children = { header, new Grid { [Grid.RowProperty] = 1, Children = { _scroll, _empty } } }
        }, new Thickness(0, AppTheme.Space.L, 0, AppTheme.Space.S));
        _empty.Margin = new Thickness(AppTheme.Space.XXL, 72, AppTheme.Space.XXL, AppTheme.Space.XXL);
        _empty.VerticalAlignment = VerticalAlignment.Top;

        _browse = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("1*,3*"), ColumnSpacing = AppTheme.Space.L,
            Margin = new Thickness(AppTheme.Space.XXL, 0, AppTheme.Space.XXL, AppTheme.Space.L),
            Children = { _shelf, listCard }
        };
        _browse.ColumnDefinitions[0].MinWidth = 168;
        _browse.ColumnDefinitions[0].MaxWidth = 248;
        Grid.SetColumn(listCard, 1);
        Content = new Grid { Children = { _browse, _reviewPage } };
    }

    private VocabularyNotebookSummary Current => _summaries.FirstOrDefault(s => s.Notebook.Id == _bookId) ?? _summaries[0];

    private void SelectBook(string id)
    {
        _bookId = id;
        _revealed.Clear();
        RefreshAll();
        _scroll.ScrollToHome();
    }

    private void ToggleHiddenTranslations()
    {
        try
        {
            var hidden = !Current.Notebook.HideTranslations;
            _service.SetNotebookTranslationsHidden(_bookId, hidden);
            _revealed.Clear();
            _setStatus(hidden ? "已隐藏列表中的释义，点击词条右侧的眼睛可临时查看" : "已显示列表中的释义");
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            _setStatus($"设置失败。{ex.Message}");
        }
        RefreshAll();
    }

    private void ShowHideState(bool hidden)
    {
        _hiddenBadge.IsVisible = hidden;
        var icon = (FontAwesomeIcon)_hide.Content!;
        icon.IconName = hidden ? "eye-slash" : "eye";
        icon.Foreground = hidden ? AppTheme.AccentBright : AppTheme.TextSecondary;
        _hide.Background = hidden ? AppTheme.AccentSoft : AppTheme.CardBg;
        _hide.BorderBrush = hidden ? AppTheme.Accent : AppTheme.BorderStrong;
        var tip = hidden ? "显示列表释义" : "隐藏列表释义，自测记忆";
        AutomationProperties.SetName(_hide, tip);
        ToolTip.SetTip(_hide, tip);
    }

    public void RefreshAll()
    {
        _summaries = _service.GetNotebookSummaries();
        _bookId = Current.Notebook.Id;
        var (book, count, due) = Current;
        _shelf.Display(_summaries, _bookId);
        _title.Text = book.Name;
        ToolTip.SetTip(_title, book.Name);
        _summary.Text = $"共 {count} 条";
        _due.Text = due > 0 ? $"{due} 条待复习" : "";
        _due.IsVisible = due > 0;
        ShowHideState(book.HideTranslations);
        // Nothing to review, sort or export in an empty notebook, so those controls leave instead of greying out.
        _review.IsVisible = _export.IsVisible = _hide.IsVisible = count > 0;
        _more.Flyout = BuildNotebookMenu(book);
        ReloadEntries();
    }

    /// <summary>Starts a review of one notebook, or of the first notebook with words waiting when none is named.</summary>
    public void StartReview(string? notebookId = null)
    {
        if (_summaries.Count == 0) RefreshAll();
        var target = notebookId ?? _summaries.FirstOrDefault(s => s.DueCount > 0)?.Notebook.Id ?? _bookId;
        _bookId = _summaries.Any(s => s.Notebook.Id == target) ? target : _bookId;
        var book = Current.Notebook;
        RefreshAll();
        _browse.IsVisible = false;
        _reviewPage.IsVisible = true;
        _reviewPage.Start(book.Id, book.Name);
    }

    private void ExitReview()
    {
        _reviewPage.IsVisible = false;
        _browse.IsVisible = true;
        RefreshAll();
    }

    private void ReloadEntries()
    {
        var entries = _service.QueryEntries(new VocabularyQueryFilter
        {
            NotebookId = _bookId, SortOrder = (VocabularySortOrder)Math.Max(0, _sort.SelectedIndex)
        });
        _entries.ItemsSource = entries;
        _empty.IsVisible = entries.Count == 0;
        _sort.IsVisible = entries.Count > 1;
        if (!_empty.IsVisible) return;
        _empty.Children.Clear();
        _empty.Children.Add(StudioUi.CreateEmptyState("book-bookmark", AppTheme.Hues.Coral, "这个生词本还没有词条",
            "查词或划词翻译后，点击结果里的「加入生词表」，词条就会出现在这里。", 320));
    }

    private Control EntryRow(VocabularyEntry entry)
    {
        var passage = entry.ItemType.IsPassage();
        var title = TextStyles.Create(entry.Headword, passage ? TextRole.ExampleSource : TextRole.SectionTitle);
        title.MaxLines = 2;
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        // The type tag sits beside the headword. A short headword keeps it on the same baseline; a long passage leads with it.
        var heading = new Grid { ColumnSpacing = AppTheme.Space.S };
        Border? kind = null;
        if (entry.ItemType != VocabularyItemType.Word)
        {
            kind = StudioUi.CreateTag(UiLabels.ItemType(entry.ItemType), TextRole.Meta, HueMap.ItemType(entry.ItemType));
            kind.VerticalAlignment = VerticalAlignment.Top;
            kind.Margin = new Thickness(0, 1, 0, 0);
        }
        if (kind is null)
            heading.Children.Add(title);
        else if (passage)
        {
            heading.ColumnDefinitions = new ColumnDefinitions("Auto,*");
            Grid.SetColumn(title, 1);
            heading.Children.Add(kind);
            heading.Children.Add(title);
        }
        else
        {
            heading.ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*");
            title.MaxWidth = 400;
            Grid.SetColumn(kind, 1);
            heading.Children.Add(title);
            heading.Children.Add(kind);
        }
        var hidden = Current.Notebook.HideTranslations;
        // Hidden mode swaps the meanings for a placeholder until the learner reveals this one entry.
        var meaningHost = new ContentControl { Content = hidden && !_revealed.Contains(entry.Id) ? MeaningMask() : MeaningBlock(entry) };
        var open = new Button
        {
            Classes = { "row-open" }, Background = Brushes.Transparent, BorderThickness = default,
            Padding = new Thickness(AppTheme.Space.M, AppTheme.Space.M - 2), CornerRadius = new CornerRadius(AppTheme.Radius.M),
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = new StackPanel { Spacing = AppTheme.Space.XXS, Children = { heading, meaningHost } }
        };
        AutomationProperties.SetName(open, $"查看词条 {entry.Headword}");
        open.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this) is Window owner) await _detail.ShowAsync(owner, entry);
        };
        var stage = StudioUi.CreateDotLabel(UiLabels.Stage(entry.Stage), HueMap.Stage(entry.Stage));
        var stageSlot = new Border { Width = 72, Margin = new Thickness(AppTheme.Space.S, 0, AppTheme.Space.S, 0), VerticalAlignment = VerticalAlignment.Center, Child = stage };
        var move = RowAction("folder-open", "移动到其他生词本", () => { });
        move.Flyout = BuildMoveMenu(entry);
        var delete = RowAction("regular/trash-can", "从生词本删除", () => DeleteEntry(entry));
        delete.Classes.Add("destructive");
        // The icons close the row, right-aligned: [reveal] [move] [delete].
        var cells = new List<Control> { open, stageSlot };
        if (hidden) cells.Add(RevealAction(entry, meaningHost));
        cells.Add(move);
        cells.Add(delete);
        var grid = new Grid { ColumnSpacing = AppTheme.Space.XXS };
        for (var i = 0; i < cells.Count; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(i == 0 ? GridLength.Star : GridLength.Auto));
            Grid.SetColumn(cells[i], i);
            grid.Children.Add(cells[i]);
        }
        var row = new Border
        {
            Classes = { "entry-row" }, CornerRadius = new CornerRadius(AppTheme.Radius.M),
            Margin = new Thickness(0, 1), Padding = new Thickness(0, 0, AppTheme.Space.S, 0), Child = grid
        };
        // The actions stay visible while their menu is open even though the pointer has moved onto it.
        move.Flyout.Opened += (_, _) => row.Classes.Add("menu-open");
        move.Flyout.Closed += (_, _) => row.Classes.Remove("menu-open");
        return row;
    }

    /// <summary>Eye button of a hidden-mode row. It only changes this entry and is forgotten when the notebook changes.</summary>
    private Button RevealAction(VocabularyEntry entry, ContentControl meaningHost)
    {
        var button = RowAction("eye", "", () => { });
        void Sync()
        {
            var shown = _revealed.Contains(entry.Id);
            meaningHost.Content = shown ? MeaningBlock(entry) : MeaningMask();
            ((FontAwesomeIcon)button.Content!).IconName = shown ? "eye-slash" : "eye";
            var name = shown ? $"再次隐藏释义 {entry.Headword}" : $"显示释义 {entry.Headword}";
            AutomationProperties.SetName(button, name);
            ToolTip.SetTip(button, shown ? "再次隐藏释义" : "临时显示释义");
        }
        button.Click += (_, _) =>
        {
            if (!_revealed.Remove(entry.Id)) _revealed.Add(entry.Id);
            Sync();
        };
        Sync();
        return button;
    }

    private static Control MeaningMask() => new Border
    {
        Height = 10, Width = 140, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6),
        CornerRadius = new CornerRadius(AppTheme.Radius.Pill), Background = AppTheme.CardElevatedBg
    };

    /// <summary>One line per part of speech, so a word with several classes keeps its meanings apart.</summary>
    private static Control MeaningBlock(VocabularyEntry entry)
    {
        (string Pos, string Meaning)[] groups = entry.ItemType.IsPassage() ? [] : entry.Senses
            .Where(s => !string.IsNullOrWhiteSpace(s.DefinitionZh))
            .GroupBy(s => s.PartOfSpeech?.Trim() ?? "")
            .Select(g => (Pos: g.Key, Meaning: string.Join("；", g.Select(s => s.DefinitionZh.Trim()).Distinct().Take(3))))
            .ToArray();
        if (groups.Length == 0)
        {
            var single = TextStyles.Create(entry.Translation, TextRole.Secondary);
            single.MaxLines = 2;
            single.TextTrimming = TextTrimming.CharacterEllipsis;
            return single;
        }
        const int MaxClasses = 4;
        var list = new StackPanel { Spacing = AppTheme.Space.XXS };
        Grid.SetIsSharedSizeScope(list, true);
        foreach (var (pos, meaning) in groups.Take(MaxClasses))
        {
            var text = TextStyles.Create(meaning, TextRole.Secondary);
            text.MaxLines = 2;
            text.TextTrimming = TextTrimming.CharacterEllipsis;
            var tag = string.IsNullOrEmpty(pos)
                ? new Border { Width = 0 }
                : StudioUi.CreateTag(pos, hue: HueMap.PartOfSpeech(pos));
            tag.VerticalAlignment = VerticalAlignment.Top;
            tag.Margin = new Thickness(0, 1, 0, 0);
            Grid.SetColumn(text, 1);
            list.Children.Add(new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition(GridLength.Auto) { SharedSizeGroup = "PosTag", MinWidth = 40 },
                    new ColumnDefinition(GridLength.Star)
                },
                ColumnSpacing = AppTheme.Space.S, Children = { tag, text }
            });
        }
        if (groups.Length > MaxClasses)
            list.Children.Add(TextStyles.Create($"另有 {groups.Length - MaxClasses} 个词性", TextRole.Meta, wrap: false));
        return list;
    }

    private static Button RowAction(string icon, string name, Action action)
    {
        var button = StudioUi.CreateIconButton(icon, name, action);
        button.Classes.Remove("ghost");
        button.Classes.Add("row-action");
        button.Width = button.Height = 32;
        button.CornerRadius = new CornerRadius(AppTheme.Radius.S);
        button.Content = new FontAwesomeIcon(icon, 16);
        button.Background = Brushes.Transparent;
        button.BorderThickness = default;
        button.VerticalAlignment = VerticalAlignment.Center;
        return button;
    }

    private MenuFlyout BuildMoveMenu(VocabularyEntry entry)
    {
        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        var targets = _summaries.Where(s => s.Notebook.Id != entry.NotebookId).Select(s => s.Notebook).ToArray();
        menu.Items.Add(new MenuItem { Header = targets.Length == 0 ? "还没有其他生词本" : "移动到", IsEnabled = false });
        if (targets.Length > 0) menu.Items.Add(new Separator());
        foreach (var target in targets)
        {
            var item = new MenuItem { Header = target.Name, Icon = new FontAwesomeIcon("folder", 16, AppTheme.TextSecondary), MaxWidth = 280 };
            item.Click += (_, _) => MoveEntry(entry, target);
            menu.Items.Add(item);
        }
        return menu;
    }

    private void MoveEntry(VocabularyEntry entry, VocabularyNotebook target)
    {
        try
        {
            var kept = _service.MoveEntry(entry.Id, target.Id);
            _setStatus(kept.Id == entry.Id ? $"已将「{entry.Headword}」移到「{target.Name}」" : $"「{entry.Headword}」已在「{target.Name}」中，两条记录已合并");
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            _setStatus($"移动失败。{ex.Message}");
        }
        RefreshAll();
    }

    private void DeleteEntry(VocabularyEntry entry)
    {
        try
        {
            _service.DeleteEntry(entry.Id);
            _setStatus($"已从生词本删除「{entry.Headword}」");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _setStatus($"删除失败。{ex.Message}");
        }
        RefreshAll();
    }

    private async void ShowReviewedEntry(VocabularyEntry entry)
    {
        if (TopLevel.GetTopLevel(this) is Window owner) await _detail.ShowAsync(owner, entry);
    }
}
