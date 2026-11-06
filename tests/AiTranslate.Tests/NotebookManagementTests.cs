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
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace AiTranslate.Tests;

[Collection("Dictionary UI")]
public sealed class NotebookManagementTests
{
    private static DictionaryEntryResult Word(string text, string meaning) =>
        new() { SourceText = text, PrimaryTranslation = meaning, DetectedSourceLanguage = "en" };

    private static string[] Texts(Control root) => root.GetVisualDescendants().OfType<TextBlock>()
        .Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? t.Inlines?.Text ?? "").ToArray();

    private static Button FindButton(Control root, string name) => root.GetVisualDescendants().OfType<Button>()
        .Single(b => b.IsEffectivelyVisible && AutomationProperties.GetName(b) == name);

    private static void Click(Control button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

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
                store.AddOrUpdateFromDictionary(new DictionaryEntryResult { SourceText = "tea", PrimaryTranslation = "茶" });

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
                Assert.Contains("待复习 1 词", icon.ToolTipText);
                return true;
            }, CancellationToken.None);
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public void MovingMergingAndDeletingBooksKeepsContextAndReviewStateAfterRestart()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(directory.FullName, "vocabulary.json");
            var store = new VocabularyService(path);
            var fresh = Assert.Single(store.GetNotebooks());
            Assert.True(fresh.IsSystemDefault);
            Assert.Throws<InvalidOperationException>(() => store.DeleteNotebook(fresh.Id));
            var entry = store.AddOrUpdateFromDictionary(Word("bank", "河岸"), contextSentence: "We sat by the bank.", sourceTitle: "阅读");
            entry = store.SaveEntry(entry with { UserPersonalNote = "河边", Tags = ["随笔"], IsStarred = true });
            var book = store.CreateNotebook("翻译练习");
            Assert.Equal(book.Id, store.CreateNotebook(" 翻译练习 ").Id);
            var renamed = store.RenameNotebook(book.Id, "论文用语");
            Assert.Equal(book.Id, renamed.Id);
            Assert.Throws<InvalidOperationException>(() => store.RenameNotebook(book.Id, fresh.Name));
            var reviewed = store.SubmitReview(entry.Id, ReviewRating.Good);

            var moved = store.MoveEntry(entry.Id, book.Id);
            Assert.Equal(entry.Id, moved.Id);
            Assert.Equal(book.Id, moved.NotebookId);
            Assert.Equal((reviewed.NextReviewAt, reviewed.TotalReviews), (moved.NextReviewAt, moved.TotalReviews));
            Assert.Equal(entry.ContextSentence, moved.ContextSentence);
            Assert.Equal("阅读", moved.SourceTitle);
            Assert.Equal(("河边", "随笔", true), (moved.UserPersonalNote, Assert.Single(moved.Tags), moved.IsStarred));
            Assert.Empty(store.QueryEntries(new() { NotebookId = "default" }));

            // The same word saved again in the default book is absorbed by the copy that is already there.
            var twin = store.AddOrUpdateFromDictionary(Word("bank", "河岸"), contextSentence: "We sat by the bank.");
            Assert.NotEqual(moved.Id, twin.Id);
            var kept = store.MoveEntry(twin.Id, book.Id);
            Assert.Equal(moved.Id, kept.Id);
            Assert.Single(store.QueryEntries(new() { NotebookId = book.Id }));
            Assert.Empty(store.QueryEntries(new() { NotebookId = "default" }));
            Assert.Equal(moved.Id, store.MoveEntry(kept.Id, book.Id).Id);

            store.DeleteNotebook(book.Id);
            var reopened = new VocabularyService(path);
            Assert.DoesNotContain(reopened.GetNotebooks(), n => n.Id == book.Id);
            var saved = Assert.Single(reopened.QueryEntries(new() { NotebookId = "default" }));
            Assert.Equal(moved.Id, saved.Id);
            Assert.Equal(moved.NextReviewAt, saved.NextReviewAt);
            Assert.Equal("河边", saved.UserPersonalNote);
            Assert.Throws<InvalidOperationException>(() => reopened.MoveEntry(saved.Id, book.Id));
            Assert.Equal("default", reopened.AddOrUpdateFromDictionary(Word("new capture", "新摘录"), book.Id).NotebookId);

            reopened.DeleteEntry(saved.Id);
            Assert.DoesNotContain(reopened.QueryEntries(new()), e => e.Id == saved.Id);
        }
        finally { directory.Delete(true); }
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    public async Task HidingTranslationsIsPerNotebookAndEachEntryCanBeRevealedTemporarily(double scale)
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(DictionaryInputTests));
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            await session.Dispatch(() =>
            {
                var path = Path.Combine(directory.FullName, "words.json");
                var service = new VocabularyService(path);
                using var speech = new KokoroSpeechService(directory.FullName);
                var dictionary = new AiDictionaryService(() => null, new DictionaryEntryCache(Path.Combine(directory.FullName, "cache.db")));
                var view = new VocabularyVaultView(service, speech, new AppSettingsStore(Path.Combine(directory.FullName, "settings.json")),
                    (_, _, _, _, _) => { }, _ => { }, dictionary);
                var window = new Window { Width = 788, Height = 560, Content = view };
                window.SetRenderScaling(scale);
                window.Show();
                try
                {
                    void Layout() { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
                    void Press(string name) { Click(FindButton(view, name)); Layout(); }
                    var other = service.CreateNotebook("另一本");
                    service.AddOrUpdateFromDictionary(new DictionaryEntryResult
                    {
                        SourceText = "light", PrimaryTranslation = "光", DetectedSourceLanguage = "en",
                        Senses = [new("n.", "光线"), new("adj.", "轻的"), new("adj.", "明亮的")]
                    });
                    service.AddOrUpdateFromDictionary(Word("moon", "月亮"), other.Id);
                    view.RefreshAll();
                    Layout();

                    double SummaryTop() => view.GetVisualDescendants().OfType<TextBlock>()
                        .First(t => t.IsEffectivelyVisible && t.Text?.StartsWith("共 ") == true).TranslatePoint(default, window)!.Value.Y;
                    var summaryTop = SummaryTop();

                    // Each part of speech keeps its own line and meanings.
                    Assert.Contains("光线", Texts(view));
                    Assert.Contains("轻的；明亮的", Texts(view));
                    Assert.DoesNotContain(view.GetVisualDescendants().OfType<Button>(),
                        b => AutomationProperties.GetName(b)?.StartsWith("显示释义 ") == true);

                    Press("隐藏列表释义，自测记忆");
                    // The badge appears in this row; the count text must not move by even a fraction of a pixel.
                    Assert.Equal(summaryTop, SummaryTop());
                    Assert.True(service.GetNotebooks().Single(n => n.IsSystemDefault).HideTranslations);
                    Assert.False(service.GetNotebooks().Single(n => n.Id == other.Id).HideTranslations);
                    Assert.DoesNotContain("光线", Texts(view));
                    Assert.Contains("light", Texts(view));

                    Press("显示释义 light");
                    Assert.Contains("光线", Texts(view));
                    Press("再次隐藏释义 light");
                    Assert.DoesNotContain("光线", Texts(view));
                    Press("显示释义 light");

                    // Another notebook is unaffected, and coming back forgets the temporary reveal.
                    Press("另一本，1 条");
                    Assert.Contains("月亮", Texts(view));
                    Assert.DoesNotContain(view.GetVisualDescendants().OfType<Button>(),
                        b => AutomationProperties.GetName(b)?.Contains("释义 moon") == true);
                    Press("默认生词本，1 条");
                    Assert.DoesNotContain("光线", Texts(view));

                    Assert.True(new VocabularyService(path).GetNotebooks().Single(n => n.IsSystemDefault).HideTranslations);
                    Press("显示列表释义");
                    Assert.Equal(summaryTop, SummaryTop());
                    Assert.Contains("光线", Texts(view));
                    Assert.DoesNotContain(view.GetVisualDescendants().OfType<Button>(),
                        b => AutomationProperties.GetName(b)?.StartsWith("显示释义 ") == true);
                }
                finally { window.Close(); }
            }, CancellationToken.None);
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task NotebookPageManagesGroupsAndTheirEntriesWithoutSearchStarsOrNotes()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(DictionaryInputTests));
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            await session.Dispatch(() =>
            {
                var service = new VocabularyService(Path.Combine(directory.FullName, "words.json"));
                using var speech = new KokoroSpeechService(directory.FullName);
                var dictionary = new AiDictionaryService(() => null, new DictionaryEntryCache(Path.Combine(directory.FullName, "cache.db")));
                string? status = null;
                var view = new VocabularyVaultView(service, speech, new AppSettingsStore(Path.Combine(directory.FullName, "settings.json")),
                    (_, _, _, _, _) => { }, s => status = s, dictionary);
                // MainWindow's minimum workspace after its 232 DIP navigation and title/status bars.
                var window = new Window { Width = 788, Height = 560, Content = view };
                window.Show();
                try
                {
                    void Layout() { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
                    void Press(string name, Control? within = null) { Click(FindButton(within ?? view, name)); Layout(); }
                    foreach (var text in new[] { "tea", "apple", "bread", "cheese", "dinner", "egg", "fruit", "grape", "honey", "juice" })
                        service.AddOrUpdateFromDictionary(Word(text, text + "的释义"));
                    var coffee = service.AddOrUpdateFromDictionary(Word("coffee", "咖啡"));
                    view.RefreshAll();
                    Layout();

                    Assert.DoesNotContain(view.GetVisualDescendants().OfType<TextBox>(), t => t.IsEffectivelyVisible);
                    var names = view.GetVisualDescendants().OfType<Button>().Select(AutomationProperties.GetName).ToArray();
                    Assert.DoesNotContain("查词收录", names);
                    Assert.DoesNotContain(names, n => n?.Contains("星标") == true || n?.Contains("编辑") == true);
                    Assert.Contains("开始复习", names);
                    Assert.Contains("导出当前生词本", names);
                    Assert.Contains("查看词条 juice", names);

                    var scroller = view.GetVisualDescendants().OfType<ScrollViewer>().Single(s => s.Content is ItemsControl);
                    var list = (ItemsControl)scroller.Content!;
                    Assert.Equal(11, ((IReadOnlyList<VocabularyEntry>)list.ItemsSource!).Count);
                    var card = scroller.GetVisualAncestors().OfType<Border>().First();
                    var bar = scroller.GetVisualDescendants().OfType<ScrollBar>().Single(b => b.Name == "PART_VerticalScrollBar");
                    Assert.True(bar.IsVisible);
                    var left = list.TranslatePoint(default, card)!.Value.X;
                    var right = left + list.Bounds.Width;
                    var track = bar.TranslatePoint(default, card)!.Value.X;
                    Assert.Equal(left, card.Bounds.Width - right);
                    Assert.True(right <= track);
                    Assert.True(list.Bounds.Width > view.GetVisualDescendants().OfType<NotebookShelf>().Single().Bounds.Width);

                    // Groups: create, select, rename.
                    Press("新建生词本");
                    var editor = Assert.Single(window.OwnedWindows.OfType<Window>());
                    var nameBox = editor.GetVisualDescendants().OfType<TextBox>().Single(t => AutomationProperties.GetName(t) == "生词本名称");
                    nameBox.Text = "默认生词本";
                    Press("保存", editor);
                    Assert.Contains("已有同名生词本，请换一个名称。", Texts(editor));
                    nameBox.Text = "课后复习";
                    Press("保存", editor);
                    var book = Assert.Single(service.GetNotebooks(), n => n.Name == "课后复习");
                    Assert.Empty(window.OwnedWindows);
                    Assert.Contains(Texts(view), t => t == "课后复习");
                    Assert.Contains(Texts(view), t => t == "这个生词本还没有词条");
                    Assert.DoesNotContain(view.GetVisualDescendants().OfType<Button>(),
                        b => b.IsEffectivelyVisible && AutomationProperties.GetName(b) is "开始复习" or "导出当前生词本");

                    // Entries: move to another group, then delete without a prompt.
                    Press("默认生词本，11 条");
                    var coffeeRow = FindButton(view, "查看词条 coffee").GetVisualAncestors().OfType<Grid>().First();
                    var move = coffeeRow.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == "移动到其他生词本");
                    var menu = Assert.IsType<MenuFlyout>(move.Flyout);
                    var target = Assert.Single(menu.Items.OfType<MenuItem>(), i => i.IsEnabled);
                    Assert.Equal("课后复习", target.Header);
                    target.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    Layout();
                    Assert.Equal(book.Id, service.FindByHeadword("coffee")!.NotebookId);
                    Assert.Contains("课后复习", status);
                    Assert.DoesNotContain(view.GetVisualDescendants().OfType<Button>(), b => AutomationProperties.GetName(b) == "查看词条 coffee");

                    var juiceRow = FindButton(view, "查看词条 juice").GetVisualAncestors().OfType<Grid>().First();
                    Click(juiceRow.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == "从生词本删除"));
                    Layout();
                    Assert.Empty(window.OwnedWindows);
                    Assert.Null(service.FindByHeadword("juice"));
                    Assert.Contains("juice", status);
                    Assert.Equal(9, service.QueryEntries(new() { NotebookId = "default" }).Count);

                    // Sorting reorders the same list.
                    var sort = view.GetVisualDescendants().OfType<ComboBox>().Single(c => AutomationProperties.GetName(c) == "排序");
                    sort.SelectedIndex = 1;
                    Layout();
                    var shown = ((IReadOnlyList<VocabularyEntry>)list.ItemsSource!).Select(e => e.Headword).ToArray();
                    Assert.Equal(shown.Order(StringComparer.OrdinalIgnoreCase), shown);

                    // The row opens the same reading a lookup shows.
                    Press("查看词条 apple");
                    var detail = Assert.Single(window.OwnedWindows.OfType<Window>());
                    Assert.Contains("apple", Texts(detail));
                    Assert.Contains("apple的释义", Texts(detail));
                    detail.Close();
                    Layout();

                    // Group menu: rename, then delete moves entries back to the default group.
                    Press("课后复习，1 条");
                    var more = FindButton(view, "更多操作");
                    var groupMenu = Assert.IsType<MenuFlyout>(more.Flyout).Items.OfType<MenuItem>().ToArray();
                    Assert.All(groupMenu, item => Assert.True(item.IsEnabled));
                    groupMenu.Single(i => (string?)i.Header == "重命名").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    Layout();
                    editor = Assert.Single(window.OwnedWindows.OfType<Window>());
                    nameBox = editor.GetVisualDescendants().OfType<TextBox>().Single(t => AutomationProperties.GetName(t) == "生词本名称");
                    Assert.Equal("课后复习", nameBox.Text);
                    nameBox.Text = "口语";
                    Press("保存", editor);
                    Assert.Equal("口语", service.GetNotebooks().Single(n => n.Id == book.Id).Name);

                    groupMenu = Assert.IsType<MenuFlyout>(FindButton(view, "更多操作").Flyout).Items.OfType<MenuItem>().ToArray();
                    groupMenu.Single(i => (string?)i.Header == "删除生词本").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    Layout();
                    editor = Assert.Single(window.OwnedWindows.OfType<Window>());
                    Press("确认删除", editor);
                    Assert.DoesNotContain(service.GetNotebooks(), n => n.Id == book.Id);
                    Assert.Equal("default", service.FindByHeadword("coffee")!.NotebookId);
                    var defaultMenu = Assert.IsType<MenuFlyout>(FindButton(view, "更多操作").Flyout).Items.OfType<MenuItem>().ToArray();
                    Assert.False(defaultMenu.Single(i => ((string?)i.Header)?.Contains("删除") == true).IsEnabled);
                }
                finally { window.Close(); }
            }, CancellationToken.None);
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task ReviewShowsTheWordFirstThenTheMeaningAndSchedulesEachCardOnce()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(DictionaryInputTests));
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            await session.Dispatch(() =>
            {
                var path = Path.Combine(directory.FullName, "words.json");
                var store = new VocabularyService(path);
                using var speech = new KokoroSpeechService(directory.FullName);
                var dictionary = new AiDictionaryService(() => null, new DictionaryEntryCache(Path.Combine(directory.FullName, "cache.db")));
                var view = new VocabularyVaultView(store, speech, new AppSettingsStore(Path.Combine(directory.FullName, "settings.json")),
                    (_, _, _, _, _) => { }, _ => { }, dictionary);
                var window = new Window { Content = view, Width = 788, Height = 560 };
                window.Show();
                try
                {
                    void Layout() { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
                    void Press(string name) { Click(FindButton(view, name)); Layout(); }
                    var book = store.CreateNotebook("阅读");
                    var meanings = new Dictionary<string, string> { ["good"] = "好的", ["bad"] = "坏的" };
                    foreach (var (word, meaning) in meanings)
                        store.AddOrUpdateFromDictionary(Word(word, meaning), book.Id, contextSentence: $"This is {word}.");
                    VocabularyEntry Saved(string word) => store.QueryEntries(new() { NotebookId = book.Id }).Single(e => e.Headword == word);
                    string Shown() => meanings.Keys.Single(w => Texts(view).Contains(w));

                    view.StartReview(book.Id);
                    Layout();
                    var first = Shown();
                    var second = meanings.Keys.Single(w => w != first);
                    Assert.Contains("0 / 2", Texts(view));
                    Assert.DoesNotContain(meanings[first], Texts(view));
                    Assert.DoesNotContain(view.GetVisualDescendants().OfType<TextBox>(), t => t.IsEffectivelyVisible);
                    Assert.Equal(0, Saved(first).TotalReviews);
                    foreach (var name in new[] { "认识", "模糊", "不认识" }) Assert.True(FindButton(view, name).IsEffectivelyVisible);

                    Press("不认识");
                    Assert.Contains(meanings[first], Texts(view));
                    Assert.Contains("稍后会再出现", Texts(view));
                    Assert.Equal(1, Saved(first).TotalReviews);
                    Assert.Equal(MemoryStage.Learning, Saved(first).Stage);
                    Press("下一个");

                    Assert.Equal(second, Shown());
                    Press("认识");
                    Assert.Contains(meanings[second], Texts(view));
                    Assert.DoesNotContain("稍后会再出现", Texts(view));
                    Assert.Contains("1 / 2", Texts(view));
                    Press("下一个");

                    // The forgotten card is back, and this retry is not scheduled a second time.
                    Assert.Equal(first, Shown());
                    Press("认识");
                    Assert.Equal(1, Saved(first).TotalReviews);
                    Assert.Contains("2 / 2", Texts(view));
                    Press("完成");
                    Assert.Contains("本轮复习完成", Texts(view));
                    Assert.Equal(1, Saved(second).TotalReviews);
                    Assert.True(Saved(second).NextReviewAt > Saved(second).LastReviewedAt);
                    Assert.Equal($"This is {first}.", Saved(first).ContextSentence);

                    // Nothing is due now, so another round is extra practice. Keys answer and move on.
                    Press("再练一轮");
                    Assert.Contains("今天没有到期的词，先提前巩固一轮。", Texts(view));
                    var practiced = Shown();
                    window.KeyPressQwerty(PhysicalKey.Digit1, RawInputModifiers.None);
                    Layout();
                    Assert.Equal(2, Saved(practiced).TotalReviews);
                    Assert.Contains(meanings[practiced], Texts(view));
                    window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
                    Layout();
                    Assert.Equal(meanings.Keys.Single(w => w != practiced), Shown());
                    Assert.DoesNotContain(meanings.Values, m => Texts(view).Contains(m));
                    window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
                    Layout();
                    Assert.True(FindButton(view, "开始复习").IsEffectivelyVisible);
                    Assert.Equal(1, Saved(meanings.Keys.Single(w => w != practiced)).TotalReviews);
                }
                finally { window.Close(); }
            }, CancellationToken.None);
        }
        finally { directory.Delete(true); }
    }
}
