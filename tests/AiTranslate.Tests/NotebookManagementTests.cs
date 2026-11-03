using AiTranslate.Core;
using AiTranslate.Desktop;
using AiTranslate.Desktop.Controls;
using AiTranslate.Desktop.Views;
using AiTranslate.Speech;
using AiTranslate.Vocabulary;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace AiTranslate.Tests;

[Collection("Dictionary UI")]
public sealed class NotebookManagementTests
{
    [Fact]
    public void MovingAndDeletingBooksPreservesNotesAndReviewStateAfterRestart()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(directory.FullName, "vocabulary.json");
            var store = new VocabularyService(path);
            var entry = store.QueryEntries(new()).First();
            var book = store.CreateNotebook("翻译练习", "");
            var renamed = store.UpdateNotebook(book.Id, "论文用语", "阅读时摘录");
            Assert.Equal(book.Id, renamed.Id);
            Assert.Throws<InvalidOperationException>(() => store.UpdateNotebook(book.Id, store.GetNotebooks()[0].Name, ""));
            var reviewed = store.SubmitReview(entry.Id, ReviewRating.Good);
            store.UpdateEntryDetails(entry.Id, book.Id, VocabularyItemType.TechnicalTerm, "课堂释义", ["阅读"], "我的例句");
            var moved = Assert.Single(store.QueryEntries(new() { NotebookId = book.Id, SearchKeyword = "我的例句" }));
            Assert.Equal(reviewed.NextReviewAt, moved.NextReviewAt);
            Assert.Equal(reviewed.TotalReviews, moved.TotalReviews);
            Assert.Equal(entry.ContextSentence, moved.ContextSentence);
            Assert.Equal("阅读", Assert.Single(moved.Tags));
            Assert.Equal("课堂释义", moved.Translation);
            Assert.Equal(VocabularyItemType.TechnicalTerm, moved.ItemType);
            Assert.Throws<InvalidOperationException>(() => store.DeleteNotebook("default"));

            store.DeleteNotebook(book.Id);
            var reopened = new VocabularyService(path);
            Assert.DoesNotContain(reopened.GetNotebooks(), n => n.Id == book.Id);
            var saved = Assert.Single(reopened.QueryEntries(new() { NotebookId = "default", Tag = "阅读", SearchKeyword = "我的例句" }));
            Assert.Equal(moved.Id, saved.Id);
            Assert.Equal(moved.NextReviewAt, saved.NextReviewAt);
            Assert.Equal(moved.IsStarred, saved.IsStarred);
            Assert.Throws<InvalidOperationException>(() => reopened.UpdateEntryDetails(saved.Id, book.Id, saved.ItemType, "错误归档", [], ""));
            var fresh = reopened.AddOrUpdateFromDictionary(new DictionaryEntryResult { SourceText = "new capture", PrimaryTranslation = "新摘录" }, book.Id);
            Assert.Equal("default", fresh.NotebookId);
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task SelectedNotebookScopesTheListSearchCollectionAndReview()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(DictionaryInputTests));
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            await session.Dispatch(() =>
            {
                var service = new VocabularyService(Path.Combine(directory.FullName, "words.json"));
                using var speech = new KokoroSpeechService(directory.FullName);
                string? collectId = null, reviewId = null;
                var view = new VocabularyVaultView(service, speech, new AppSettingsStore(Path.Combine(directory.FullName, "settings.json")),
                    (_, _, _, _, _) => { }, id => collectId = id, id => reviewId = id, _ => { });
                // MainWindow's minimum workspace after its 232 DIP navigation and title/status bars.
                var window = new Window { Width = 788, Height = 560, Content = view };
                window.Show();
                try
                {
                    void Layout() { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
                    void Click(string name, Control? within = null)
                    {
                        var button = (within ?? view).GetVisualDescendants().OfType<Button>().Single(b =>
                            AutomationProperties.GetName(b) == name);
                        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Layout();
                    }
                    var book = service.CreateNotebook("测试收藏", "");
                    var entry = service.QueryEntries(new()).First();
                    service.UpdateEntryDetails(entry.Id, book.Id, entry.ItemType, entry.Translation, [], "测试注释");
                    view.RefreshAll();
                    Layout();
                    Click($"{book.Name}，1 条");
                    Assert.True(view.GetVisualDescendants().OfType<ItemsControl>().Single(c => c.ItemTemplate?.Match(entry) == true).Bounds.Width
                        > view.GetVisualDescendants().OfType<NotebookShelf>().Single().Bounds.Width);
                    var rows = view.GetVisualDescendants().OfType<Button>().Where(b => AutomationProperties.GetName(b)?.StartsWith("查看词条 ") == true);
                    Assert.Equal("查看词条 " + entry.Headword, AutomationProperties.GetName(Assert.Single(rows)));
                    Click("复习");
                    Click("查词收录");
                    Assert.Equal(book.Id, reviewId);
                    Assert.Equal(book.Id, collectId);
                    var search = view.GetVisualDescendants().OfType<TextBox>().Single(t => AutomationProperties.GetName(t) == "搜索词条");
                    search.Text = "unmatched query";
                    Layout();
                    Assert.Empty(rows);
                    Click("清除搜索与筛选");
                    Assert.Single(rows);
                    Click("新建词本");
                    var editor = Assert.Single(window.OwnedWindows.OfType<Window>());
                    var nameBox = editor.GetVisualDescendants().OfType<TextBox>().Single(t => AutomationProperties.GetName(t) == "词本名称");
                    Assert.True(nameBox.Bounds.Height > 0);
                    nameBox.Text = "课后复习";
                    Click("保存", editor);
                    Assert.Contains(service.GetNotebooks(), n => n.Name == "课后复习");
                    Assert.Empty(rows);
                    Click("编辑当前词本");
                    editor = Assert.Single(window.OwnedWindows.OfType<Window>());
                    Click("删除", editor);
                    var confirm = Assert.Single(editor.OwnedWindows.OfType<Window>());
                    Click("确认删除", confirm);
                    Assert.DoesNotContain(service.GetNotebooks(), n => n.Name == "课后复习");
                }
                finally { window.Close(); }
            }, CancellationToken.None);
        }
        finally { directory.Delete(true); }
    }
}
