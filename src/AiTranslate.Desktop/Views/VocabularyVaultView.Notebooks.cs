using System.Text;
using AiTranslate.Core;
using AiTranslate.Desktop.Controls;
using AiTranslate.Vocabulary;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace AiTranslate.Desktop.Views;

public sealed partial class VocabularyVaultView
{
    private MenuFlyout BuildNotebookMenu(VocabularyNotebook book)
    {
        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        var rename = new MenuItem { Header = "重命名", Icon = new FontAwesomeIcon("pencil", 16, AppTheme.TextSecondary) };
        rename.Click += (_, _) => EditNotebook(book);
        var delete = new MenuItem
        {
            Header = book.IsSystemDefault ? "默认生词本不能删除" : "删除生词本",
            Icon = new FontAwesomeIcon("regular/trash-can", 16, book.IsSystemDefault ? AppTheme.TextMuted : AppTheme.Danger),
            IsEnabled = !book.IsSystemDefault
        };
        if (!book.IsSystemDefault)
        {
            // Destructive rows read red at rest and on hover; Fluent's item template takes these from resources.
            delete.Resources["MenuFlyoutItemForeground"] = AppTheme.Danger;
            delete.Resources["MenuFlyoutItemForegroundPointerOver"] = AppTheme.Danger;
            delete.Resources["MenuFlyoutItemForegroundPressed"] = AppTheme.Danger;
            delete.Resources["MenuFlyoutItemBackgroundPointerOver"] = AppTheme.DangerSoft;
            delete.Resources["MenuFlyoutItemBackgroundPressed"] = AppTheme.DangerSoft;
        }
        delete.Click += (_, _) => DeleteNotebook(book);
        menu.Items.Add(rename);
        menu.Items.Add(new Separator());
        menu.Items.Add(delete);
        return menu;
    }

    private async void EditNotebook(VocabularyNotebook? book)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        await VaultDialogs.PromptName(owner, book is null ? "新建生词本" : "重命名生词本", book?.Name, name =>
        {
            if (_service.GetNotebooks().Any(n => n.Id != book?.Id && string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("已有同名生词本，请换一个名称。");
            var saved = book is null ? _service.CreateNotebook(name) : _service.RenameNotebook(book.Id, name);
            SelectBook(saved.Id);
            _setStatus(book is null ? $"已创建「{saved.Name}」" : $"已重命名为「{saved.Name}」");
        });
    }

    private async void DeleteNotebook(VocabularyNotebook book)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        var fallback = _summaries.First(s => s.Notebook.IsSystemDefault).Notebook;
        var count = _summaries.First(s => s.Notebook.Id == book.Id).EntryCount;
        var message = count == 0 ? $"删除生词本「{book.Name}」？" :
            $"删除生词本「{book.Name}」？其中的 {count} 个词条将移入「{fallback.Name}」，复习进度会保留。";
        if (!await VaultDialogs.ConfirmDelete(owner, "删除生词本", message)) return;
        try
        {
            _service.DeleteNotebook(book.Id);
            SelectBook(fallback.Id);
            _setStatus($"已删除「{book.Name}」");
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            _setStatus($"删除失败。{ex.Message}");
        }
    }

    private async void ExportEntries()
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        var book = Current.Notebook;
        var filter = new VocabularyQueryFilter { NotebookId = book.Id, SortOrder = (VocabularySortOrder)Math.Max(0, _sort.SelectedIndex) };
        var safeName = string.Concat(book.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        try
        {
            var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = $"导出「{book.Name}」", SuggestedFileName = $"{safeName}_{DateTime.Now:yyyyMMdd}", DefaultExtension = "md",
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
            _setStatus($"已导出「{book.Name}」至 {file.Name}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _setStatus($"导出失败：{ex.Message}"); }
    }
}
