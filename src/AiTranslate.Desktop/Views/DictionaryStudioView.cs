using AiTranslate.Ai;
using AiTranslate.Core;
using AiTranslate.Desktop.Controls;
using AiTranslate.Speech;
using AiTranslate.Vocabulary;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace AiTranslate.Desktop.Views;

public sealed class DictionaryStudioView : UserControl
{
    private const double ReadingWidth = 760;
    private readonly IAiDictionaryService _dictionaryService;
    private readonly IVocabularyService _vocabularyService;
    private readonly ISpeechSynthesisService _speech;
    private readonly AppSettingsStore _settings;
    private readonly Action<string> _setStatus;
    private readonly DictionaryComposer _composer;
    private readonly ScrollViewer _scroll;
    private readonly Control _welcome;
    private CancellationTokenSource? _lookup;
    private sealed record Reading(DictionaryEntryResult Entry, string? Context, TranslationTriggerSource Source, string Title,
        DictionaryDisclosures Disclosures);
    private readonly List<(Reading Reading, Vector Offset)> _history = [];
    private Reading? _current;
    private NotebookCollection? _collection;
    private string? _notebookId;

    public DictionaryStudioView(IAiDictionaryService dictionaryService, IVocabularyService vocabularyService,
        ISpeechSynthesisService speechService, AppSettingsStore settingsStore, Action<string> setStatus)
    {
        _dictionaryService = dictionaryService;
        _vocabularyService = vocabularyService;
        _speech = speechService;
        _settings = settingsStore;
        _setStatus = setStatus;
        _composer = new DictionaryComposer(_settings, dictionaryService.FindSuggestionAsync,
            (query, cacheOnly) => _ = RunLookupAsync(query, null, TranslationTriggerSource.ManualInput, "手动查询", null,
                cacheOnly: cacheOnly), StopLookup);

        _scroll = new ScrollViewer
        {
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(AppTheme.Space.XXL, AppTheme.Space.L, AppTheme.Space.XXL, AppTheme.Space.XXXL)
        };
        var samples = new WrapPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center, ItemSpacing = AppTheme.Space.S, LineSpacing = AppTheme.Space.S,
            Margin = new Thickness(0, AppTheme.Space.S, 0, 0)
        };
        foreach (var sample in new[] { "coffee", "hard", "take for granted", "The early bird catches the worm.", "举一反三" })
            samples.Children.Add(CreateSampleChip(sample));
        _welcome = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
            MaxWidth = 560, Spacing = AppTheme.Space.M, Margin = new Thickness(AppTheme.Space.XXL),
            Children =
            {
                AppIcons.CreateLogo(56),
                new TextBlock
                {
                    Text = "查一个词，译一段话", TextAlignment = TextAlignment.Center, FontSize = AppTheme.Type.Headline,
                    FontWeight = FontWeight.SemiBold, Foreground = AppTheme.TextPrimary, Margin = new Thickness(0, AppTheme.Space.XS, 0, 0)
                },
                new TextBlock
                {
                    Text = "输入单词、词组或整段原文，AI 会逐步给出译义、用法、搭配与双语例句。", TextAlignment = TextAlignment.Center,
                    TextWrapping = TextWrapping.Wrap, FontSize = AppTheme.Type.Body, LineHeight = 21, Foreground = AppTheme.TextSecondary
                },
                new TextBlock
                {
                    Text = "试试这些", TextAlignment = TextAlignment.Center, FontSize = AppTheme.Type.Caption,
                    Foreground = AppTheme.TextMuted, Margin = new Thickness(0, AppTheme.Space.M, 0, 0)
                },
                samples
            }
        };
        var fade = new Border
        {
            Height = 28, VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false, Background = AppTheme.WindowFadeBottom,
            Margin = new Thickness(0, 0, 12, 0)
        };
        _composer.MaxWidth = ReadingWidth;
        _composer.Margin = new Thickness(AppTheme.Space.XXL, 0, AppTheme.Space.XXL, AppTheme.Space.L);
        Grid.SetRow(_composer, 1);
        Content = new Grid
        {
            RowDefinitions = new RowDefinitions("*,Auto"),
            Children = { _scroll, fade, _welcome, _composer }
        };
    }

    private Button CreateSampleChip(string text)
    {
        var chip = new Button
        {
            Classes = { "ghost" }, Background = AppTheme.CardBg, BorderBrush = AppTheme.BorderSubtle, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(AppTheme.Radius.Pill), Padding = new Thickness(AppTheme.Space.M, AppTheme.Space.XS),
            Content = new TextBlock { Text = text, FontSize = AppTheme.Type.Small, Foreground = AppTheme.TextSecondary }
        };
        ToolTip.SetTip(chip, $"查询 “{text}”");
        chip.Click += (_, _) => _ = LookupAsync(text, null, TranslationTriggerSource.ManualInput, "手动查询");
        return chip;
    }

    public void RefreshNotebooks(string? preferredId = null)
    {
        _notebookId = preferredId ?? _notebookId ?? _settings.Current.DefaultNotebookId;
        _collection?.SetNotebooks(_vocabularyService.GetNotebooks(), _notebookId);
    }

    internal void FocusInput() => _composer.FocusInput();

    public Task LookupAsync(string query, string? contextSentence, TranslationTriggerSource source, string sourceTitle, string? targetLanguage = null) =>
        RunLookupAsync(query, contextSentence, source, sourceTitle, targetLanguage);

    private async Task RunLookupAsync(string query, string? contextSentence, TranslationTriggerSource source, string sourceTitle,
        string? language, DictionaryEntryResult? refreshing = null, bool linked = false, bool cacheOnly = false)
    {
        query = DictionaryQuery.CleanInput(query);
        if (query.Length == 0) return;
        language ??= DictionaryTranslationDirection.ResolveTargetId(query, _composer.LanguageId ?? DictionaryTranslationDirection.GetMode(_settings.Current));
        if (!linked && refreshing is null) _history.Clear();
        StopLookup();
        using var request = new CancellationTokenSource();
        _lookup = request;
        var page = BeginResult(query, language, contextSentence);
        _composer.SetActivity(ComposerActivity.Translating);
        _setStatus("正在翻译…");
        try
        {
            var result = await _dictionaryService.LookupOrTranslateAsync(query, language, contextSentence,
                text => Dispatcher.UIThread.Post(() =>
                {
                    if (_lookup == request) page.ShowTranslation(text);
                }), request.Token, partial => Dispatcher.UIThread.Post(() =>
                {
                    if (_lookup == request) page.ShowEntry(partial, complete: false);
                }), forceRefresh: refreshing is not null, cacheOnly: cacheOnly);
            if (_lookup == request) FinishResult(page, result, contextSentence, source, sourceTitle);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (_lookup == request)
            {
                if (refreshing is not null)
                {
                    page = BeginResult(query, language, contextSentence);
                    FinishResult(page, refreshing, contextSentence, source, sourceTitle, pronounce: false);
                    _setStatus($"刷新失败，已保留原结果。{ex.Message}");
                }
                else
                {
                    page.ShowError(ex.Message);
                    page.ShowActions(StudioUi.CreateSecondaryButton("refresh", "重新查询",
                        () => _ = RunLookupAsync(query, contextSentence, source, sourceTitle, language, linked: linked)));
                    _setStatus("翻译未完成");
                }
            }
        }
        finally
        {
            if (_lookup == request)
            {
                _lookup = null;
                _composer.SetActivity(ComposerActivity.Ready);
            }
        }
    }

    public void DisplayResult(DictionaryEntryResult result, TranslationTriggerSource source, string sourceTitle)
    {
        StopLookup();
        _history.Clear();
        var context = string.IsNullOrWhiteSpace(result.LookupContext) ? null : result.LookupContext;
        FinishResult(BeginResult(result.SourceText, result.TargetLanguage, context), result, context, source, sourceTitle);
    }

    internal void StopLookup()
    {
        if (_lookup is not { } request) return;
        _lookup = null;
        if (_current is null) (_scroll.Content as DictionaryResultView)?.MarkStopped();
        _composer.SetActivity(ComposerActivity.Ready);
        _setStatus(_current is null ? "已停止生成" : "已取消本次请求");
        request.Cancel();
    }

    private DictionaryResultView BeginResult(string query, string language, string? context = null,
        DictionaryDisclosures? disclosures = null)
    {
        _welcome.IsVisible = false;
        _current = null;
        _collection = null;
        var page = new DictionaryResultView(query, language, _speech, _settings, LookupRelated, _history.Count > 0 ? GoBack : null,
            context, disclosures)
        {
            MaxWidth = ReadingWidth
        };
        page.BlockReviews.Requested += block =>
        {
            if (_current is { } current && ReferenceEquals(_scroll.Content, page))
                _ = SupplementAsync(page, current, block);
        };
        _scroll.Content = page;
        _scroll.ScrollToHome();
        return page;
    }

    internal void LookupRelated(string text, string sentence, bool fromTranslation)
    {
        if (_current is not { } parent || !DictionaryQuery.HasContent(text)) return;
        var mode = _composer.LanguageId ?? DictionaryTranslationDirection.AutoId;
        if (fromTranslation) mode = DictionaryTranslationDirection.ReverseTarget(parent.Entry, text);
        else if (mode != DictionaryTranslationDirection.AutoId) mode = parent.Entry.TargetLanguage;
        var target = DictionaryTranslationDirection.ResolveTargetId(text, mode);
        if (_history.Count == 16) _history.RemoveAt(0);
        _history.Add((parent, _scroll.Offset));
        _ = RunLookupAsync(text, sentence, parent.Source, parent.Title, target, linked: true);
    }

    private void GoBack()
    {
        if (_history.Count == 0) return;
        StopLookup();
        var (previous, offset) = _history[^1];
        _history.RemoveAt(_history.Count - 1);
        var page = BeginResult(previous.Entry.SourceText, previous.Entry.TargetLanguage, previous.Context, previous.Disclosures);
        FinishResult(page, previous.Entry, previous.Context, previous.Source, previous.Title, pronounce: false);
        _scroll.UpdateLayout();
        _scroll.Offset = offset;
        _setStatus("已返回上一级查询");
    }

    private void FinishResult(DictionaryResultView page, DictionaryEntryResult result, string? contextSentence,
        TranslationTriggerSource source, string sourceTitle, bool pronounce = true)
    {
        _current = new(result, contextSentence, source, sourceTitle, page.Disclosures);
        page.ShowEntry(result, complete: true);
        _collection = new NotebookCollection(
            id => _vocabularyService.IsCollected(result, id),
            id => _notebookId = id,
            (book, collect) =>
            {
                try
                {
                    if (collect)
                        _vocabularyService.AddOrUpdateFromDictionary(result, book.Id, contextSentence, source, sourceTitle);
                    else
                        _vocabularyService.RemoveFromNotebook(result, book.Id);
                    _setStatus(collect ? $"已收录到「{book.Name}」" : $"已从「{book.Name}」取消收录");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _setStatus($"未能保存词本更改：{ex.Message}");
                }
            });
        RefreshNotebooks();
        var tools = new WrapPanel
        {
            ItemSpacing = AppTheme.Space.S, LineSpacing = AppTheme.Space.S, Children = { _collection }
        };
        tools.Children.Add(DictionaryDetails.CreateCopyButton(async () =>
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(result.PrimaryTranslation);
                _setStatus("已复制译文");
            }
        }));
        var accent = PhonicsAudioBar.SourceAccent(result.DetectedSourceLanguage);
        if (accent is { } voice && (result.ItemType.IsPassage() || voice != SpeechAccent.AmericanEnglish))
            tools.Children.Add(SpeechPlaybackButton.Create(click => StudioUi.CreateIconButton("volume-high", "朗读原文", click),
                playback => PhonicsAudioBar.SpeakSentence(_speech, _settings, result.SourceText, voice, playback), page.SourcePlayback));
        tools.Children.Add(StudioUi.CreateIconButton("refresh", "重新用 AI 翻译（忽略缓存）",
            () => _ = RunLookupAsync(result.SourceText, contextSentence, source, sourceTitle, result.TargetLanguage, result)));
        var extras = new WrapPanel { ItemSpacing = AppTheme.Space.XXS, LineSpacing = AppTheme.Space.XXS, HorizontalAlignment = HorizontalAlignment.Right };
        if (!result.ItemType.IsPassage())
        {
            var reading = _current;
            var missing = result.MissingUsageExamples().Count();
            if (missing > 0)
            {
                var fill = StudioUi.CreateCompactButton("format-quote-close", $"补全例句 · {missing}",
                    () => _ = SupplementAsync(page, reading));
                ToolTip.SetTip(fill, "只为缺双语例句的句型和搭配补例句，每次最多八处");
                extras.Children.Add(fill);
            }
        }
        page.ShowActions(tools, extras);
        _setStatus(result.NotebookSyncPending ? BlockReviewActions.PendingSyncStatus : "翻译完成");
        if (pronounce && _settings.Current.AutoPronounceOnLookup && accent is { } autoVoice)
            _ = _speech.SpeakAsync(result.SourceText, autoVoice, result.UsPhonetic, _settings.Current.SpeechSpeed,
                autoVoice == SpeechAccent.AmericanEnglish ? _settings.Current.PreferredUsVoiceId : null, playback: page.SourcePlayback);
    }

    private async Task SupplementAsync(DictionaryResultView page, Reading reading, EntryBlock? block = null)
    {
        if (_lookup is not null || !ReferenceEquals(_current, reading)) return;
        using var request = new CancellationTokenSource();
        _lookup = request;
        _composer.SetActivity(block is null ? ComposerActivity.Translating : ComposerActivity.ReviewingBlock);
        var title = EntryBlockCatalog.For(reading.Entry.ItemType).FirstOrDefault(s => s.Block == block)?.Title;
        if (block is null) page.BlockReviews.SetBusy(true);
        _setStatus(block is null ? "正在补全双语例句…" : $"AI 正在复核「{title}」…");
        try
        {
            var result = block is { } target
                ? await page.BlockReviews.ReviewAsync(_dictionaryService, reading.Entry, target, request.Token)
                : await _dictionaryService.CompleteExamplesAsync(reading.Entry, reading.Context, request.Token);
            if (_lookup != request) return;
            if (ReferenceEquals(result, reading.Entry))
            {
                _setStatus(block is null ? "本轮未发现可补充的例句" : BlockReviewActions.ResultStatus(title!, result, changed: false));
                return;
            }
            var offset = _scroll.Offset;
            FinishResult(page, result, reading.Context, reading.Source, reading.Title, pronounce: false);
            _scroll.UpdateLayout();
            _scroll.Offset = offset;
            _setStatus(block is not null ? BlockReviewActions.ResultStatus(title!, result, changed: true) :
                result.NotebookSyncPending ? BlockReviewActions.PendingSyncStatus :
                $"已补全 {reading.Entry.MissingUsageExamples().Count() - result.MissingUsageExamples().Count()} 处双语例句");
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (_lookup == request)
            {
                _setStatus($"未能完成{(block is null ? "例句补全" : $"「{title}」复核")}。{ex.Message}");
            }
        }
        finally
        {
            if (block is null) page.BlockReviews.SetBusy(false);
            if (_lookup == request)
            {
                _lookup = null;
                _composer.SetActivity(ComposerActivity.Ready);
            }
        }
    }
}
