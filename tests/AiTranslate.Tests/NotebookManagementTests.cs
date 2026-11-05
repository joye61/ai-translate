using AiTranslate.Ai;
using AiTranslate.Core;
using AiTranslate.Desktop;
using AiTranslate.Desktop.Controls;
using AiTranslate.Desktop.Tray;
using AiTranslate.Desktop.Views;
using AiTranslate.Speech;
using AiTranslate.Vocabulary;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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
    public async Task LookingUpCollectedCoffeeCanRefreshTheNotebookWithATrayIcon()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(DictionaryInputTests));
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            await session.Dispatch(async () =>
            {
                var store = new VocabularyService(Path.Combine(directory.FullName, "words.json"));
                var settings = new AppSettingsStore(Path.Combine(directory.FullName, "settings.json"));
                var dictionary = new AiDictionaryService(() => null, new DictionaryEntryCache(Path.Combine(directory.FullName, "cache.db")),
                    refreshCollectedEntries: store.RefreshCollectedEntries);
                var coffee = await dictionary.LookupOrTranslateAsync("coffee", "zh-Hans");
                var stamp = coffee.CacheStamp!;
                var card = store.AddOrUpdateFromDictionary(coffee with { CacheStamp = stamp with { Revision = stamp.Revision - 1 } });

                static void Ignore() { }
                using var tray = new SystemTrayService(settings, store, Ignore, Ignore, Ignore, Ignore, Ignore, Ignore, Ignore);
                var result = await dictionary.LookupOrTranslateAsync("coffee", "zh-Hans");
                Assert.Equal(DictionaryResultOrigin.Cache, result.Origin);
                Assert.Equal(stamp.Revision, store.FindByHeadword("coffee")!.DictionaryRevision);

                var icon = Assert.Single(TrayIcon.GetIcons(Application.Current!)!);
                var previousTip = icon.ToolTipText;
                await Task.Run(() => store.SubmitReview(card.Id, ReviewRating.Good));
                Dispatcher.UIThread.RunJobs();
                Assert.NotEqual(previousTip, icon.ToolTipText);
                Assert.Contains(store.GetDashboardStats().DueTodayCount.ToString(), icon.ToolTipText);
                return true;
            }, CancellationToken.None);
        }
        finally { directory.Delete(recursive: true); }
    }

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
    public async Task ReviewOpensWithTheMeaningAndRecordsOnlyAnExplicitRating()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(DictionaryInputTests));
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            await session.Dispatch(() =>
            {
                var path = Path.Combine(directory.FullName, "words.json");
                var store = new VocabularyService(path);
                var book = store.CreateNotebook("阅读", "");
                var entry = store.AddOrUpdateFromDictionary(new DictionaryEntryResult
                {
                    SourceText = "good", PrimaryTranslation = "好的", DetectedSourceLanguage = "en"
                }, book.Id, contextSentence: "She is good at drawing.");
                using var speech = new KokoroSpeechService(directory.FullName);
                var review = new FsrsReviewArenaView(store, speech, new AppSettingsStore(Path.Combine(directory.FullName, "settings.json")), _ => { });
                var window = new Window { Content = review, Width = 788, Height = 560 };
                window.Show();
                try
                {
                    review.RefreshDecksAndStart(book.Id);
                    window.UpdateLayout();
                    var texts = review.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToArray();
                    Assert.Contains(entry.Headword, texts);
                    Assert.Contains(entry.Translation, texts);
                    Assert.Contains(entry.ContextSentence, texts);
                    Assert.Empty(review.GetVisualDescendants().OfType<TextBox>());
                    Assert.Equal(0, store.QueryEntries(new() { NotebookId = book.Id }).Single().TotalReviews);
                    var ratings = review.GetVisualDescendants().OfType<Button>()
                        .Where(b => AutomationProperties.GetName(b)?.Contains("，下次") == true).ToArray();
                    Assert.Equal(4, ratings.Length);
                    ratings.Single(b => AutomationProperties.GetName(b)!.StartsWith(UiLabels.Rating(ReviewRating.Good)))
                        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    window.UpdateLayout();
                    Assert.Contains(review.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "本轮复习已完成");
                    var saved = new VocabularyService(path).QueryEntries(new() { NotebookId = book.Id }).Single();
                    Assert.Equal(1, saved.TotalReviews);
                    Assert.True(saved.NextReviewAt > saved.LastReviewedAt);
                    Assert.Equal(entry.ContextSentence, saved.ContextSentence);
                }
                finally { window.Close(); }
            }, CancellationToken.None);
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
                    var entries = view.GetVisualDescendants().OfType<ScrollViewer>().Single(s => s.Content is ItemsControl);
                    var list = (ItemsControl)entries.Content!;
                    var card = entries.GetVisualAncestors().OfType<Border>().First();
                    var bar = entries.GetVisualDescendants().OfType<ScrollBar>().Single(b => b.Name == "PART_VerticalScrollBar");
                    Assert.True(bar.IsVisible);
                    var left = list.TranslatePoint(default, card)!.Value.X;
                    var right = left + list.Bounds.Width;
                    var track = bar.TranslatePoint(default, card)!.Value.X;
                    Assert.Equal(left, card.Bounds.Width - right);
                    Assert.True(right <= track);
                    Assert.Equal(card.Bounds.Width - card.BorderThickness.Right, track + bar.Bounds.Width);
                    Click($"{book.Name}，1 条");
                    Assert.False(bar.IsVisible);
                    Assert.Equal(left, list.TranslatePoint(default, card)!.Value.X);
                    Assert.Equal(right - left, list.Bounds.Width);
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
