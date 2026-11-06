using AiTranslate.Ai;
using AiTranslate.Core;
using AiTranslate.Desktop.Controls;
using AiTranslate.Speech;
using AiTranslate.Vocabulary;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace AiTranslate.Desktop.Views;

/// <summary>A saved entry reads exactly like the lookup it was saved from, including section review.</summary>
internal sealed class VocabularyEntryDetail(IVocabularyService vocabulary, IAiDictionaryService dictionary,
    ISpeechSynthesisService speech, AppSettingsStore settings,
    Action<string, string?, TranslationTriggerSource, string, string> lookup)
{
    /// <summary>Where the card stands in review, so the learning state is visible next to the reading.</summary>
    private static Control StatusLine(VocabularyEntry entry)
    {
        var now = DateTimeOffset.Now;
        var next = entry.Stage == MemoryStage.Mastered ? "已掌握，无需复习"
            : entry.NextReviewAt <= now ? "现在可复习" : $"下次复习 {entry.NextReviewAt.ToLocalTime():M月d日}";
        var line = new WrapPanel { ItemSpacing = AppTheme.Space.L, LineSpacing = AppTheme.Space.XXS };
        line.Children.Add(StudioUi.CreateDotLabel(UiLabels.Stage(entry.Stage), HueMap.Stage(entry.Stage)));
        foreach (var text in new[] { $"已复习 {entry.TotalReviews} 次", next, $"收录于 {entry.CreatedAt.ToLocalTime():yyyy年M月d日}" })
        {
            var meta = TextStyles.Create(text, TextRole.Meta, wrap: false);
            meta.VerticalAlignment = VerticalAlignment.Center;
            line.Children.Add(meta);
        }
        return line;
    }

    internal async Task ShowAsync(Window owner, VocabularyEntry entry)
    {
        var result = entry.ToDictionaryResult();
        Window? dialog = null;
        using var lifetime = new CancellationTokenSource();
        void Lookup(string query, string sentence, bool fromTranslation)
        {
            dialog?.Close();
            var target = fromTranslation ? DictionaryTranslationDirection.ReverseTarget(result, query) : entry.TargetLanguage;
            lookup(query, sentence, TranslationTriggerSource.ManualInput, "", target);
        }

        var context = string.IsNullOrWhiteSpace(result.LookupContext) ? null : result.LookupContext;
        var page = new DictionaryResultView(entry.Headword, entry.TargetLanguage, speech, settings, Lookup, contextSentence: context);
        page.ShowEntry(result, complete: true);
        var feedback = TextStyles.Create("", TextRole.Annotation);
        feedback.IsVisible = false;
        var footer = new StackPanel
        {
            Spacing = AppTheme.Space.XS, Children = { feedback, StatusLine(entry) }
        };
        var body = new DockPanel();
        var footerBar = new Border
        {
            Background = AppTheme.InputBg, BorderBrush = AppTheme.BorderSubtle, BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(AppTheme.Space.XL, AppTheme.Space.M), Child = footer
        };
        DockPanel.SetDock(footerBar, Dock.Bottom);
        body.Children.Add(footerBar);
        body.Children.Add(new ScrollViewer
        {
            Content = page, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Padding = new Thickness(AppTheme.Space.XL, 0, AppTheme.Space.XL, AppTheme.Space.L)
        });
        dialog = VaultDialogs.Create(owner, "词条详情", body, 720, 680, flushBottom: true);
        dialog.Closed += (_, _) => lifetime.Cancel();
        page.BlockReviews.Requested += block => _ = Review(block);
        await dialog.ShowDialog(owner);

        async Task Review(EntryBlock block)
        {
            var title = EntryBlockCatalog.For(result.ItemType).Single(s => s.Block == block).Title;
            feedback.IsVisible = true;
            TextStyles.Apply(feedback, TextRole.Annotation);
            feedback.Text = $"AI 正在复核「{title}」…";
            try
            {
                var updated = await page.BlockReviews.ReviewAsync(dictionary, result, block, lifetime.Token);
                if (lifetime.IsCancellationRequested) return;
                var changed = !ReferenceEquals(updated, result);
                // A failed notebook write leaves the card on its previous revision. Keep the
                // committed block shown by ReviewAsync and its fresh stamp for the next request.
                if (updated.NotebookSyncPending) result = updated;
                else if (changed)
                {
                    // Read this card's projection. Other notebooks and the cache can have additional sections.
                    var saved = vocabulary.QueryEntries(new() { NotebookId = entry.NotebookId }).FirstOrDefault(card => card.Id == entry.Id);
                    result = saved?.ToDictionaryResult() ?? updated;
                    page.ShowEntry(result, complete: true);
                }
                feedback.Text = BlockReviewActions.ResultStatus(title, updated, changed);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (Exception ex)
            {
                feedback.Foreground = AppTheme.Danger;
                feedback.Text = $"「{title}」复核未完成。{ex.Message}";
            }
        }
    }
}
