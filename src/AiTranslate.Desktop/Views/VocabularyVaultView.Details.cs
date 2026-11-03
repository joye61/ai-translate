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
        var passage = entry.ItemType.IsPassage();
        // Passages show the source as their own block below; a giant headline would duplicate it.
        if (!passage) reading.Children.Add(TextStyles.Selectable(entry.Headword, TextRole.Display));
        Window? dialog = null;
        var result = entry.ToDictionaryResult();
        void Lookup(string query, string sentence, bool fromTranslation)
        {
            dialog?.Close();
            var target = fromTranslation ? DictionaryTranslationDirection.ReverseTarget(result, query, _settings.Current.DictionaryReaderLanguage)
                : entry.TargetLanguage;
            _lookup(query, sentence, TranslationTriggerSource.ManualInput, "", target);
        }
        reading.Children.Add(DictionaryReadingText.Primary(result, (q, context) => Lookup(q, context, true)));
        var accent = PhonicsAudioBar.SourceAccent(entry.SourceLanguage);
        if (!string.IsNullOrWhiteSpace(entry.Pronunciation)) reading.Children.Add(TextStyles.Create(entry.Pronunciation, TextRole.Phonetic));
        if (accent == SpeechAccent.AmericanEnglish && !passage)
            reading.Children.Add(PhonicsAudioBar.CreateWordPhonicsStrip(_speech, _settings, entry.Headword,
                entry.UsPhonetic, entry.UkPhonetic, entry.ContextSentence));
        else if (accent is { } voice)
            reading.Children.Add(PhonicsAudioBar.CreateSentenceReadButton(_speech, _settings, entry.Headword, "朗读原文", voice));

        var context = $"{entry.ContextSentence}\n{entry.ContextTranslation}".Trim();
        if (context.Length > 0 && context != entry.Headword)
            reading.Children.Add(StudioUi.CreateInfoTile("收录时的语境", context, "format-quote-close", AppTheme.TextSecondary));
        // The notebook renders through the same block pipeline as a live lookup, so both read identically.
        var details = new DictionaryDetails(_speech, _settings, Lookup, cloze => ExamplePracticeCard.Open(reading, cloze));
        details.ShowEntry(result, complete: true);
        reading.Children.Add(details);

        var filing = Section();
        var bookName = _books.FirstOrDefault(n => n.Id == entry.NotebookId)?.Name ?? "默认词本";
        filing.Children.Add(TextStyles.Create(entry.Headword, TextRole.PageTitle));
        filing.Children.Add(StudioUi.CreateInfoTile("归档", $"{bookName}　·　{UiLabels.ItemType(entry.ItemType)}\n{entry.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm} 收录　·　查询 {entry.LookupCount} 次"));
        filing.Children.Add(StudioUi.CreateInfoTile("来源", string.Join(" · ", new[] { UiLabels.CaptureSource(entry.CaptureSource), entry.SourceTitle }.Where(s => !string.IsNullOrWhiteSpace(s)))));
        filing.Children.Add(StudioUi.CreateInfoTile("标签", entry.Tags.Count == 0 ? "暂无标签" : string.Join("　·　", entry.Tags)));
        filing.Children.Add(StudioUi.CreateInfoTile("个人笔记", string.IsNullOrWhiteSpace(entry.UserPersonalNote) ? "暂无笔记" : entry.UserPersonalNote));

        var progress = Section();
        RenderProgress(progress, entry);
        var tabs = new TabControl
        {
            ItemsSource = new[] { Tab("释义与语境", reading), Tab("收录与笔记", filing), Tab("复习进度", progress) }
        };
        var window = Dialog(owner, "词条详情", tabs, 680, 620);
        dialog = window;
        filing.Children.Add(StudioUi.CreateSecondaryButton("pencil-outline", "编辑词条", () => { window.Close(); EditEntry(entry); }));
        reading.Children.Add(StudioUi.CreateSecondaryButton("sparkles", "在词典中重新解析", () =>
        {
            window.Close();
            _lookup(entry.Headword, entry.GetDictionaryContext(), entry.CaptureSource, entry.SourceTitle, entry.TargetLanguage);
        }));
        await window.ShowDialog(owner);
    }

    private static StackPanel Section() => new() { Spacing = AppTheme.Space.M, Margin = new Thickness(0, AppTheme.Space.M, AppTheme.Space.S, 0) };
    private static TabItem Tab(string title, StackPanel section) => new() { Header = title, Content = new ScrollViewer { Content = section } };

    private void RenderProgress(StackPanel panel, VocabularyEntry entry)
    {
        var now = DateTimeOffset.UtcNow;
        var intervals = SpacedRepetitionScheduler.PreviewIntervals(entry, now);
        panel.Children.Clear();
        panel.Children.Add(TextStyles.Create(entry.Headword, TextRole.PageTitle));
        panel.Children.Add(StudioUi.CreateInfoTile("学习进度",
            $"{UiLabels.Stage(entry.Stage)}　·　掌握度 {entry.MasteryLevel} / 5\n复习 {entry.TotalReviews} 次　·　连续答对 {entry.CorrectStreak} 次\n下次复习　{entry.NextReviewAt.ToLocalTime():yyyy-MM-dd HH:mm}"));
        panel.Children.Add(StudioUi.CreateInfoTile("记忆指标",
            $"稳定度 {entry.StabilityDays:F1} 天　·　难度 {entry.Difficulty:F1} / 10　·　预计保持率 {entry.EstimateRetrievability(now):P0}"));
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
                var updated = _service.SubmitReview(entry.Id, rating);
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
