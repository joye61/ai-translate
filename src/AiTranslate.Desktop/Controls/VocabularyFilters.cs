using AiTranslate.Core;
using AiTranslate.Vocabulary;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace AiTranslate.Desktop.Controls;

internal sealed class VocabularyFilters : UserControl
{
    private readonly TextBox _search = new() { PlaceholderText = "搜索词条、释义或标签", FontSize = AppTheme.Type.Small };
    private readonly ComboBox _sort = Selector("排序", ["最近收录", "复习时间", "字母顺序", "查询次数", "难度优先", "掌握度最低"]);
    private readonly ComboBox _types = Selector("词条类型", ["全部类型", .. Enum.GetValues<VocabularyItemType>().Select(UiLabels.ItemType)]);
    private readonly ComboBox _stages = Selector("记忆阶段", ["全部阶段", .. Enum.GetValues<MemoryStage>().Select(UiLabels.Stage)]);
    private readonly CheckBox _starred = new() { Content = "只看星标" };
    private readonly CheckBox _due = new() { Content = "只看到期" };
    private readonly TextBlock _filterLabel = new() { Text = "筛选" };
    private bool _resetting;
    internal event Action? Changed;

    internal VocabularyFilters(Action export)
    {
        AutomationProperties.SetName(_search, "搜索词条");
        var filter = StudioUi.CreateSecondaryButton(new MdiIcon("filter-variant", 15, AppTheme.TextSecondary), _filterLabel, () => { });
        var popup = new StackPanel
        {
            Width = 200, Spacing = AppTheme.Space.S,
            Children =
            {
                StudioUi.CreateToolbarField("类型", _types), StudioUi.CreateToolbarField("阶段", _stages),
                _starred, _due, StudioUi.CreateCompactButton("refresh", "重置筛选", ResetCriteria)
            }
        };
        filter.Flyout = new Flyout { Content = popup };
        var tools = new WrapPanel
        {
            [Grid.ColumnProperty] = 1, ItemSpacing = AppTheme.Space.S, LineSpacing = AppTheme.Space.S,
            Children = { _sort, filter, StudioUi.CreateIconButton("export-variant", "导出当前列表", export) }
        };
        Content = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = AppTheme.Space.S,
            Children = { _search, tools }
        };

        _search.TextChanged += (_, _) => NotifyChange();
        _sort.SelectionChanged += (_, _) => NotifyChange();
        _stages.SelectionChanged += (_, _) => NotifyChange();
        _types.SelectionChanged += (_, _) => NotifyChange();
        _starred.IsCheckedChanged += (_, _) => NotifyChange();
        _due.IsCheckedChanged += (_, _) => NotifyChange();
    }

    private static ComboBox Selector(string name, string[] labels)
    {
        var combo = new ComboBox
        {
            ItemsSource = labels, SelectedIndex = 0, FontSize = AppTheme.Type.Small,
            VerticalAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetName(combo, name);
        return combo;
    }

    private int CriteriaCount => (_types.SelectedIndex > 0 ? 1 : 0) + (_stages.SelectedIndex > 0 ? 1 : 0)
        + (_starred.IsChecked == true ? 1 : 0) + (_due.IsChecked == true ? 1 : 0);
    internal bool HasQuery => CriteriaCount > 0 || !string.IsNullOrWhiteSpace(_search.Text);

    private void NotifyChange()
    {
        if (_resetting) return;
        _filterLabel.Text = CriteriaCount == 0 ? "筛选" : $"筛选 · {CriteriaCount}";
        Changed?.Invoke();
    }

    internal void Clear()
    {
        _resetting = true;
        _search.Text = "";
        ResetCriteria();
    }

    private void ResetCriteria()
    {
        _resetting = true;
        _types.SelectedIndex = _stages.SelectedIndex = 0;
        _starred.IsChecked = _due.IsChecked = false;
        _resetting = false;
        NotifyChange();
    }

    internal VocabularyQueryFilter Query(string? notebookId) => new()
    {
        NotebookId = notebookId, SearchKeyword = _search.Text,
        Stage = _stages.SelectedIndex > 0 ? (MemoryStage)(_stages.SelectedIndex - 1) : null,
        ItemType = _types.SelectedIndex > 0 ? (VocabularyItemType)(_types.SelectedIndex - 1) : null,
        OnlyStarred = _starred.IsChecked == true, OnlyDue = _due.IsChecked == true,
        SortOrder = (VocabularySortOrder)_sort.SelectedIndex
    };
}
