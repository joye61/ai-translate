using System.Text;
using AiTranslate.Core;
using AiTranslate.Desktop.Controls;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace AiTranslate.Desktop.Views;

public sealed partial class VocabularyVaultView
{
    private Window? Owner => TopLevel.GetTopLevel(this) as Window;

    private static Window Dialog(Window owner, string title, Control body, double width = 440, double? height = null)
    {
        var window = new Window
        {
            Icon = owner.Icon,
            Title = title, Width = Math.Min(width, owner.ClientSize.Width), MaxHeight = owner.ClientSize.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, CanMinimize = false, CanMaximize = false,
            Background = AppTheme.CardBg, FontFamily = AppTheme.BodyFont,
            SizeToContent = height.HasValue ? SizeToContent.Manual : SizeToContent.Height
        };
        if (height.HasValue) window.Height = Math.Min(height.Value, owner.ClientSize.Height);
        body.Margin = new Thickness(AppTheme.Space.XL, 0, AppTheme.Space.XL, AppTheme.Space.XL);
        WindowTitleBar.SetDialogContent(window, body);
        return window;
    }

    private static StackPanel Field(string name, Control editor)
    {
        AutomationProperties.SetName(editor, name);
        return new StackPanel { Spacing = AppTheme.Space.XXS, Children = { TextStyles.Create(name, TextRole.FieldLabel), editor } };
    }

    private static TextBox Input(string? text, string hint, bool multiline = false) => new()
    {
        Text = text, PlaceholderText = hint, AcceptsReturn = multiline, TextWrapping = TextWrapping.Wrap,
        MinHeight = multiline ? 64 : 34, MaxHeight = multiline ? 120 : 48, FontSize = AppTheme.Type.Body,
        CornerRadius = new CornerRadius(AppTheme.Radius.S), Background = AppTheme.InputBg
    };

    private static async Task ShowEditor(Window owner, string title, StackPanel fields, Action save, Func<Window, Task<bool>>? remove = null)
    {
        var error = TextStyles.Create("", TextRole.FieldLabel);
        error.Foreground = AppTheme.Danger;
        error.IsVisible = false;
        var buttons = new WrapPanel { ItemSpacing = AppTheme.Space.S, LineSpacing = AppTheme.Space.S };
        var body = new Grid
        {
            RowDefinitions = new RowDefinitions("*,Auto,Auto"), RowSpacing = AppTheme.Space.M,
            Children = { new ScrollViewer { Content = fields }, error, buttons }
        };
        Grid.SetRow(error, 1);
        Grid.SetRow(buttons, 2);
        var dialog = Dialog(owner, title, body);
        void Fail(Exception ex) { error.Text = ex.Message; error.IsVisible = true; }
        var submit = StudioUi.CreatePrimaryButton("check", "保存", () =>
        {
            try { save(); dialog.Close(); }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException) { Fail(ex); }
        });
        AutomationProperties.SetName(submit, "保存");
        buttons.Children.Add(submit);
        buttons.Children.Add(StudioUi.CreateSecondaryButton("close", "取消", dialog.Close));
        if (remove is not null)
        {
            buttons.Children.Add(StudioUi.CreateCompactButton("delete-outline", "删除", async () =>
            {
                try { if (await remove(dialog)) dialog.Close(); }
                catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException) { Fail(ex); }
            }));
        }
        await dialog.ShowDialog(owner);
    }

    private static async Task<bool> ConfirmDelete(Window owner, string title, string message)
    {
        var buttons = new WrapPanel { ItemSpacing = AppTheme.Space.S };
        var dialog = Dialog(owner, title, new StackPanel
        {
            Spacing = AppTheme.Space.L, Children = { TextStyles.Create(message, TextRole.Secondary), buttons }
        });
        var cancel = StudioUi.CreateSecondaryButton("close", "取消", () => dialog.Close(false));
        buttons.Children.Add(cancel);
        var confirm = StudioUi.CreateSecondaryButton("delete-outline", "确认删除", () => dialog.Close(true));
        buttons.Children.Add(confirm);
        dialog.Opened += (_, _) => cancel.Focus();
        return await dialog.ShowDialog<bool>(owner);
    }

    private async void EditNotebook(VocabularyNotebook? book)
    {
        if (Owner is not { } owner) return;
        var name = Input(book?.Name, "例如：论文阅读");
        var description = Input(book?.Description, "这个词本用来记录什么？", true);
        var fields = new StackPanel { Spacing = AppTheme.Space.M, Children = { Field("词本名称", name), Field("说明（选填）", description) } };
        if (book?.IsSystemDefault == true)
            fields.Children.Add(TextStyles.Create("这是默认词本，用于接收日常查词和快速捕获的内容。", TextRole.FieldLabel));
        await ShowEditor(owner, book is null ? "新建词本" : "编辑词本", fields, () =>
        {
            var value = name.Text?.Trim();
            if (string.IsNullOrEmpty(value)) throw new InvalidOperationException("请输入词本名称。");
            if (_service.GetNotebooks().Any(n => n.Id != book?.Id && string.Equals(n.Name, value, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("已有同名词本，请换一个名称。");
            var saved = book is null ? _service.CreateNotebook(value, description.Text ?? "") :
                _service.UpdateNotebook(book.Id, value, description.Text ?? "");
            _shelf.ClearSearch();
            SelectBook(saved.Id);
            _setStatus(book is null ? $"已创建「{saved.Name}」" : $"已更新「{saved.Name}」");
        }, book is null || book.IsSystemDefault ? null : async editor =>
        {
            var targetName = _books.First(n => n.Id == "default").Name;
            if (!await ConfirmDelete(editor, "删除词本",
                $"删除「{book.Name}」？其中的词条将移入默认词本「{targetName}」，笔记与复习进度都会保留。")) return false;
            _service.DeleteNotebook(book.Id);
            SelectBook("default");
            _setStatus($"已删除「{book.Name}」，词条已归入默认词本");
            return true;
        });
    }

    private async void EditEntry(VocabularyEntry entry)
    {
        if (Owner is not { } owner) return;
        var notebook = new ComboBox
        {
            ItemsSource = _books, SelectedItem = _books.FirstOrDefault(n => n.Id == entry.NotebookId),
            ItemTemplate = new FuncDataTemplate<VocabularyNotebook>((n, _) => TextStyles.Create(n?.Name ?? "", TextRole.Secondary)),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var translation = Input(entry.Translation, "输入释义", true);
        var types = Enum.GetValues<VocabularyItemType>();
        var type = new ComboBox { ItemsSource = types.Select(UiLabels.ItemType).ToArray(),
            SelectedIndex = Array.IndexOf(types, entry.ItemType), HorizontalAlignment = HorizontalAlignment.Stretch };
        var organization = new Grid { ColumnDefinitions = new ColumnDefinitions("2*,*"), ColumnSpacing = AppTheme.Space.S };
        var typeField = Field("词条类型", type);
        Grid.SetColumn(typeField, 1);
        organization.Children.Add(Field("所属词本", notebook));
        organization.Children.Add(typeField);
        var tags = Input(string.Join("，", entry.Tags), "多个标签用逗号分隔");
        var note = Input(entry.UserPersonalNote, "写下造句或阅读心得", true);
        var fields = new StackPanel
        {
            Spacing = AppTheme.Space.M,
            Children = { TextStyles.Create(entry.Headword, TextRole.PageTitle), Field("释义", translation),
                organization, Field("标签", tags), Field("个人笔记", note) }
        };
        await ShowEditor(owner, "编辑词条", fields, () =>
        {
            if (string.IsNullOrWhiteSpace(translation.Text)) throw new InvalidOperationException("请输入释义。");
            if (notebook.SelectedItem is not VocabularyNotebook target) throw new InvalidOperationException("请选择所属词本。");
            if (type.SelectedIndex < 0) throw new InvalidOperationException("请选择词条类型。");
            var labels = (tags.Text ?? "").Split([',', '，', ';', '；'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            _service.UpdateEntryDetails(entry.Id, target.Id, types[type.SelectedIndex], translation.Text, labels, note.Text ?? "");
            _setStatus($"已保存「{entry.Headword}」至「{target.Name}」");
        }, async editor =>
        {
            if (!await ConfirmDelete(editor, "删除词条",
                $"删除「{entry.Headword}」？此词条及其笔记将从词库中移除。")) return false;
            _service.DeleteEntry(entry.Id);
            _setStatus($"已删除「{entry.Headword}」");
            return true;
        });
    }

    private async void ExportEntries()
    {
        if (Owner is not { } owner) return;
        var filter = _filters.Query(_bookId);
        try
        {
            var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "导出当前词条列表", SuggestedFileName = $"AiTranslate_{DateTime.Now:yyyyMMdd}", DefaultExtension = "md",
                FileTypeChoices = [new("Markdown 笔记") { Patterns = ["*.md"] }, new("Anki 卡片") { Patterns = ["*.tsv"] },
                    new("CSV 表格") { Patterns = ["*.csv"] }, new("JSON 词条") { Patterns = ["*.json"] }]
            });
            if (file is null) return;
            var content = Path.GetExtension(file.Name).ToLowerInvariant() switch
            {
                ".tsv" => _service.ExportToAnkiTsv(filter), ".csv" => _service.ExportToCsv(filter),
                ".json" => _service.ExportToJson(filter), _ => _service.ExportToMarkdown(filter)
            };
            await using var stream = await file.OpenWriteAsync();
            if (stream.CanSeek) stream.SetLength(0);
            await using var writer = new StreamWriter(stream, Encoding.UTF8);
            await writer.WriteAsync(content);
            _setStatus($"已导出当前词条至 {file.Name}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _setStatus($"导出失败：{ex.Message}"); }
    }
}
