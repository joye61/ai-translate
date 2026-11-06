using AiTranslate.Ai;
using AiTranslate.Core;
using AiTranslate.Desktop.Controls;
using AiTranslate.Speech;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace AiTranslate.Desktop.Views;

public sealed partial class VocabularyVaultView
{
    private async void ShowEntry(VocabularyEntry entry)
    {
        if (Owner is not { } owner) return;
        var reading = Section();
        reading.Classes.Add("dictionary-reading");
        var passage = entry.ItemType.IsPassage();
        // Passages show the source as their own block below; a giant headline would duplicate it.
        if (!passage) reading.Children.Add(TextStyles.Selectable(entry.Headword, TextStyles.HeadwordRole(entry.Headword)));
        Window? dialog = null;
        var result = entry.ToDictionaryResult();
        using var lifetime = new CancellationTokenSource();
        var feedback = TextStyles.Create("", TextRole.Annotation);
        feedback.IsVisible = false;
        BlockReviewActions? reviews = _dictionary is null ? null : new();
        void Lookup(string query, string sentence, bool fromTranslation)
        {
            dialog?.Close();
            var target = fromTranslation ? DictionaryTranslationDirection.ReverseTarget(result, query)
                : entry.TargetLanguage;
            _lookup(query, sentence, TranslationTriggerSource.ManualInput, "", target);
        }
        var translations = new DictionaryTranslations();
        DictionaryReadingText.EnableSelectionLookup(reading, (q, context) => Lookup(q, context, false));
        translations.ShowEntry(result, (q, context) => Lookup(q, context, true), reviews);
        var accent = PhonicsAudioBar.SourceAccent(entry.SourceLanguage);
        if (!string.IsNullOrWhiteSpace(entry.Pronunciation)) reading.Children.Add(TextStyles.Create(entry.Pronunciation, TextRole.Phonetic));
        if (PhonicsAudioBar.HasEnglishAudio(result) && result.ItemType == VocabularyItemType.Word)
            reading.Children.Add(PhonicsAudioBar.CreateAccentStrip(_speech, _settings, entry.Headword, entry.UsPhonetic, entry.UkPhonetic));
        else if (accent == SpeechAccent.AmericanEnglish && !passage)
            reading.Children.Add(PhonicsAudioBar.CreateWordPhonicsStrip(_speech, _settings, entry.Headword,
                entry.UsPhonetic, entry.UkPhonetic, entry.ContextSentence));
        else if (accent is { } voice)
            reading.Children.Add(PhonicsAudioBar.CreateSentenceReadButton(_speech, _settings, entry.Headword, "朗读原文", voice));
        reading.Children.Add(translations);

        var context = $"{entry.ContextSentence}\n{entry.ContextTranslation}".Trim();
        if (context.Length > 0 && context != entry.Headword)
        {
            var pair = new StackPanel { Spacing = AppTheme.Space.XXS };
            pair.Children.Add(TextStyles.Create("收录时的语境", TextRole.Label));
            if (!string.IsNullOrWhiteSpace(entry.ContextSentence))
                pair.Children.Add(TextStyles.Selectable(entry.ContextSentence, TextRole.ExampleSource));
            if (!string.IsNullOrWhiteSpace(entry.ContextTranslation))
                pair.Children.Add(RichText.Create(entry.ContextTranslation, TextRole.ExampleTarget));
            reading.Children.Add(RichBlocks.Quote(pair));
        }
        // The notebook renders through the same block pipeline as a live lookup, so both read identically.
        var details = new DictionaryDetails(_speech, _settings, Lookup, reviews: reviews);
        details.ShowEntry(result, complete: true);
        reading.Children.Add(details);

        var filing = Section();
        var bookName = _books.FirstOrDefault(n => n.Id == entry.NotebookId)?.Name ?? "默认词本";
        filing.Children.Add(TextStyles.Create(entry.Headword, TextRole.PageTitle));
        filing.Children.Add(DetailFields("归档", ("词本", bookName), ("类型", UiLabels.ItemType(entry.ItemType)),
            ("收录时间", $"{entry.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm}"), ("查询次数", $"{entry.LookupCount} 次")));
        filing.Children.Add(StudioUi.CreateInfoTile("来源", string.Join(" · ", new[] { UiLabels.CaptureSource(entry.CaptureSource), entry.SourceTitle }.Where(s => !string.IsNullOrWhiteSpace(s)))));
        filing.Children.Add(StudioUi.CreateInfoTile("标签", entry.Tags.Count == 0 ? "暂无标签" : string.Join("　·　", entry.Tags)));
        filing.Children.Add(StudioUi.CreateInfoTile("个人笔记", string.IsNullOrWhiteSpace(entry.UserPersonalNote) ? "暂无笔记" : entry.UserPersonalNote));

        var progress = Section();
        RenderProgress(progress, entry);
        var tabs = new TabControl
        {
            Padding = default,
            Resources = { ["TabControlTopPlacementItemMargin"] = new Thickness(AppTheme.Space.XL, 0, AppTheme.Space.XL, 2) },
            ItemsSource = new[] { Tab("释义与语境", reading), Tab("收录与笔记", filing), Tab("复习进度", progress) }
        };
        var body = new DockPanel();
        DockPanel.SetDock(feedback, Dock.Bottom);
        feedback.Margin = new Thickness(AppTheme.Space.XL, AppTheme.Space.XS);
        body.Children.Add(feedback);
        body.Children.Add(tabs);
        var window = Dialog(owner, "词条详情", body, 680, 620);
        dialog = window;
        window.Closed += (_, _) => lifetime.Cancel();
        if (reviews is not null) reviews.Requested += block => _ = Review(block);
        filing.Children.Add(StudioUi.CreateSecondaryButton("pencil-outline", "编辑词条", () => { window.Close(); EditEntry(entry); }));
        reading.Children.Add(StudioUi.CreateSecondaryButton("sparkles", "在词典中重新解析", () =>
        {
            window.Close();
            _lookup(entry.Headword, entry.GetDictionaryContext(), entry.CaptureSource, entry.SourceTitle, entry.TargetLanguage);
        }));
        await window.ShowDialog(owner);

        async Task Review(EntryBlock block)
        {
            var title = EntryBlockCatalog.For(result.ItemType).Single(s => s.Block == block).Title;
            feedback.IsVisible = true;
            feedback.Text = $"AI 正在复核「{title}」…";
            try
            {
                var updated = await reviews!.ReviewAsync(_dictionary!, result, block, lifetime.Token);
                if (lifetime.IsCancellationRequested) return;
                var changed = !ReferenceEquals(updated, result);
                // A failed notebook write leaves the card on its previous revision. Keep the
                // committed block shown by ReviewAsync and its fresh stamp for the next request.
                if (updated.NotebookSyncPending) result = updated;
                else if (changed)
                {
                    // Read this card's projection. Other notebooks and the cache can have additional sections.
                    var saved = _service.QueryEntries(new() { NotebookId = entry.NotebookId }).FirstOrDefault(card => card.Id == entry.Id);
                    result = saved?.ToDictionaryResult() ?? updated;
                    translations.ShowEntry(result, (q, context) => Lookup(q, context, true), reviews);
                    details.ShowEntry(result, complete: true);
                }
                feedback.Text = BlockReviewActions.ResultStatus(title, updated, changed);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (Exception ex) { feedback.Text = $"「{title}」复核未完成。{ex.Message}"; }
        }
    }

    private static StackPanel Section() => new()
    {
        Spacing = AppTheme.Space.M, Margin = new Thickness(AppTheme.Space.XL, AppTheme.Space.M, AppTheme.Space.XL, 0)
    };
    private static Border DetailFields(string title, params (string Label, string Value)[] fields)
    {
        var rows = new StackPanel { Spacing = AppTheme.Space.XXS };
        Grid.SetIsSharedSizeScope(rows, true);
        foreach (var (label, value) in fields)
            rows.Children.Add(StudioUi.CreateKeyValue(label, TextStyles.Selectable(value, TextRole.Body)));
        return StudioUi.CreateInfoTile(title, rows);
    }
    private static TabItem Tab(string title, StackPanel section) => new() { Header = title, Content = new ScrollViewer { Content = section } };

    private void RenderProgress(StackPanel panel, VocabularyEntry entry)
    {
        var now = DateTimeOffset.UtcNow;
        var intervals = SpacedRepetitionScheduler.PreviewIntervals(entry, now);
        panel.Children.Clear();
        panel.Children.Add(TextStyles.Create(entry.Headword, TextRole.PageTitle));
        panel.Children.Add(DetailFields("学习进度", ("阶段", UiLabels.Stage(entry.Stage)), ("掌握度", $"{entry.MasteryLevel} / 5"),
            ("复习记录", $"复习 {entry.TotalReviews} 次　·　连续记得 {entry.CorrectStreak} 次"),
            ("下次复习", $"{entry.NextReviewAt.ToLocalTime():yyyy-MM-dd HH:mm}")));
        panel.Children.Add(DetailFields("记忆指标", ("稳定度", $"{entry.StabilityDays:F1} 天"),
            ("难度", $"{entry.Difficulty:F1} / 10"), ("预计保持率", $"{entry.EstimateRetrievability(now):P0}")));
        panel.Children.Add(TextStyles.Create("这次记得怎么样？", TextRole.SectionTitle));
        var rates = new WrapPanel { ItemSpacing = AppTheme.Space.S, LineSpacing = AppTheme.Space.S };
        foreach (var (rating, label, interval) in new[]
        {
            (ReviewRating.Again, "重来", intervals.AgainInterval), (ReviewRating.Hard, "困难", intervals.HardInterval),
            (ReviewRating.Good, "良好", intervals.GoodInterval), (ReviewRating.Easy, "简单", intervals.EasyInterval)
        })
        {
            var button = StudioUi.CreateSecondaryButton("refresh", $"{label} · {SpacedRepetitionScheduler.FormatInterval(interval)}", () =>
            {
                VocabularyEntry updated;
                try { updated = _service.SubmitReview(entry.Id, rating); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    _setStatus($"复习记录保存失败。{ex.Message}");
                    return;
                }
                RenderProgress(panel, updated);
                _setStatus($"已记录「{entry.Headword}」的复习，下次复习 {updated.NextReviewAt.ToLocalTime():MM-dd HH:mm}");
            });
            rates.Children.Add(button);
        }
        panel.Children.Add(rates);
    }

    private async void ShowOverview()
    {
        if (Owner is not { } owner) return;
        var stats = _service.GetDashboardStats();
        var body = Section();
        body.Children.Add(TextStyles.Create("所有词本的学习情况", TextRole.PageTitle));
        body.Children.Add(StudioUi.CreateInfoTile("今日复习", $"当前到期 {stats.DueTodayCount} 条　·　今日已复习 {stats.ReviewedTodayCount} 次"));
        body.Children.Add(StudioUi.CreateInfoTile("词库", $"共 {stats.TotalEntries} 条　·　星标 {stats.StarredCount} 条"));
        body.Children.Add(StudioUi.CreateInfoTile("记忆阶段", $"新词 {stats.NewCount}　·　学习中 {stats.LearningCount}　·　复习中 {stats.ReviewingCount}　·　已掌握 {stats.MasteredCount}"));
        body.Children.Add(StudioUi.CreateInfoTile("平均记忆保持率", stats.NewCount == stats.TotalEntries ? "暂无复习数据" : $"{stats.AverageRetentionRate:F1}%"));
        await Dialog(owner, "学习概览", new ScrollViewer { Content = body }).ShowDialog(owner);
    }
}
