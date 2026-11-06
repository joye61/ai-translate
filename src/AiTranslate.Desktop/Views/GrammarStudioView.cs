using AiTranslate.Ai;
using AiTranslate.Core;
using AiTranslate.Desktop.Controls;
using AiTranslate.Speech;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace AiTranslate.Desktop.Views;

/// <summary>
/// The grammar studio page: one input that accepts a grammar point in any wording or a sentence to analyze, and one reading
/// column for the answer. Repeat questions resolve from the local grammar library, which the welcome state also lists.
/// </summary>
public sealed class GrammarStudioView : UserControl
{
    private sealed record Reading(GrammarLookupResult? Result, GrammarTopic? Topic);

    /// <summary>Renders only the newest streamed snapshot per UI frame.</summary>
    private sealed class PreviewPump(Action<GrammarTopic> render)
    {
        private GrammarTopic? _pending;
        private int _queued;
        private volatile bool _closed;

        public void Push(GrammarTopic topic)
        {
            if (_closed) return;
            Volatile.Write(ref _pending, topic);
            if (Interlocked.Exchange(ref _queued, 1) == 0) Dispatcher.UIThread.Post(Flush, DispatcherPriority.Background);
        }

        private void Flush()
        {
            Volatile.Write(ref _queued, 0);
            if (_closed) return;
            if (Interlocked.Exchange(ref _pending, null) is { } topic) render(topic);
        }

        public void Close() => _closed = true;
    }

    private readonly IAiGrammarService _grammar;
    private readonly Action<string> _setStatus;
    private readonly Action<string, string> _lookup;
    private readonly GrammarBlocks _blocks;
    private readonly DictionaryComposer _composer;
    private readonly ScrollViewer _scroll;
    private readonly Control _welcome;
    private readonly WrapPanel _library = new() { HorizontalAlignment = HorizontalAlignment.Center, ItemSpacing = AppTheme.Space.S, LineSpacing = AppTheme.Space.S };
    private readonly TextBlock _libraryCaption = TextStyles.Apply(new TextBlock { TextAlignment = TextAlignment.Center }, TextRole.Meta);
    private readonly StackPanel _libraryPanel;
    private readonly List<(Reading Reading, Vector Offset)> _history = [];
    private Reading? _current;
    private string _language = "en";
    private CancellationTokenSource? _request;

    public GrammarStudioView(IAiGrammarService grammar, ISpeechSynthesisService speech, AppSettingsStore settings,
        Action<string> setStatus, Action<string, string> lookupInDictionary)
    {
        _grammar = grammar;
        _setStatus = setStatus;
        _lookup = lookupInDictionary;
        _blocks = new GrammarBlocks(speech, settings, title => _grammar.IsKnown(title, null), title => OpenLinked(title), lookupInDictionary);

        var lead = StudioUi.CreateChip("语法精解", "graduation-cap", AppTheme.Insight, AppTheme.InsightSoft);
        lead.HorizontalAlignment = HorizontalAlignment.Left;
        ToolTip.SetTip(lead, "输入语法点，或粘贴一个句子");
        _composer = new DictionaryComposer(settings, (prefix, _, token) => _grammar.FindSuggestionAsync(prefix, token),
            (query, cacheOnly) => _ = AskAsync(query), StopRequest,
            new ComposerProfile("输入语法点，或粘贴一个想分析的句子…", "待讲解的语法点或句子", "发送", lead));

        _scroll = new ScrollViewer
        {
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Padding = new Thickness(AppTheme.Space.XXL, AppTheme.Space.L, AppTheme.Space.XXL, AppTheme.Space.XXXL)
        };

        var samples = new WrapPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center, ItemSpacing = AppTheme.Space.S, LineSpacing = AppTheme.Space.S,
            Margin = new Thickness(0, AppTheme.Space.S, 0, 0)
        };
        foreach (var sample in new[] { "过去进行时", "现在完成时与一般过去时的区别", "She was reading when I called her.", "If I had known, I would have told you.", "定语从句" })
            samples.Children.Add(CreateChip(sample, () => _ = AskAsync(sample), $"讲解 “{sample}”"));
        _libraryPanel = new StackPanel { Spacing = AppTheme.Space.S, Margin = new Thickness(0, AppTheme.Space.XL, 0, 0), IsVisible = false, Children = { _libraryCaption, _library } };
        var tile = new Border
        {
            Width = 64, Height = 64, CornerRadius = new CornerRadius(AppTheme.Radius.Pill), Background = AppTheme.InsightSoft,
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = new FontAwesomeIcon("graduation-cap", 30, AppTheme.Insight)
        };
        _welcome = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
            MaxWidth = 600, Spacing = AppTheme.Space.M, Margin = new Thickness(AppTheme.Space.XXL),
            Children =
            {
                tile,
                new TextBlock
                {
                    Text = "把语法讲明白", TextAlignment = TextAlignment.Center, FontSize = AppTheme.Type.Headline,
                    FontWeight = FontWeight.SemiBold, Foreground = AppTheme.TextPrimary, Margin = new Thickness(0, AppTheme.Space.XS, 0, 0)
                },
                new TextBlock
                {
                    Text = "输入一个语法点，或粘贴一个句子，AI 会像老师一样讲清公式、用法、易混点和常见错误，还附带小练习。讲过的语法会存进本地语法库，下次直接打开，不再消耗 AI。",
                    TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, FontSize = AppTheme.Type.Body, LineHeight = 21,
                    Foreground = AppTheme.TextSecondary
                },
                new TextBlock
                {
                    Text = "试试这些", TextAlignment = TextAlignment.Center, FontSize = AppTheme.Type.Caption,
                    Foreground = AppTheme.TextMuted, Margin = new Thickness(0, AppTheme.Space.M, 0, 0)
                },
                samples,
                _libraryPanel
            }
        };
        var fade = new Border
        {
            Height = 28, VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false, Background = AppTheme.WindowFadeBottom,
            Margin = new Thickness(0, 0, 12, 0)
        };
        _composer.Margin = new Thickness(AppTheme.Space.XXL, 0, AppTheme.Space.XXL, AppTheme.Space.L);
        Grid.SetRow(_composer, 1);
        Content = new Grid { RowDefinitions = new RowDefinitions("*,Auto"), Children = { _scroll, fade, _welcome, _composer } };
        RefreshLibrary();
    }

    internal void FocusInput() => _composer.FocusInput();

    /// <summary>Reloads the library shelf on the welcome state. Cheap and local.</summary>
    public void RefreshLibrary()
    {
        var recent = _grammar.Recent(10);
        _library.Children.Clear();
        foreach (var topic in recent)
            _library.Children.Add(CreateChip(topic.Title, () => _ = OpenTopicAsync(topic.Title, topic.Language), "已在本地语法库，直接打开", known: true));
        _libraryCaption.Text = $"我的语法库  ·  共 {_grammar.LibraryCount} 个语法点";
        _libraryPanel.IsVisible = recent.Count > 0;
    }

    private Button CreateChip(string text, Action click, string tip, bool known = false)
    {
        var label = new TextBlock { Text = text, FontSize = AppTheme.Type.Small, Foreground = AppTheme.TextSecondary, VerticalAlignment = VerticalAlignment.Center };
        Control content = label;
        if (known)
            content = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = AppTheme.Space.XS,
                Children = { new FontAwesomeIcon("circle-check", 13, AppTheme.Hues.Sage.Ink) { VerticalAlignment = VerticalAlignment.Center }, label }
            };
        var chip = new Button
        {
            Classes = { "ghost" }, Background = AppTheme.CardBg, BorderBrush = AppTheme.BorderSubtle, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(AppTheme.Radius.Pill), Padding = new Thickness(AppTheme.Space.M, AppTheme.Space.XS), Content = content
        };
        ToolTip.SetTip(chip, tip);
        chip.Click += (_, _) => click();
        return chip;
    }

    // ── Asking ─────────────────────────────────────────────────────────────

    public async Task AskAsync(string query)
    {
        query = DictionaryQuery.CleanInput(query);
        if (query.Length == 0) return;
        _history.Clear();
        StopRequest(announce: false);
        using var request = new CancellationTokenSource();
        _request = request;
        _welcome.IsVisible = false;
        _current = null;

        var loading = new LoadingPage(query);
        Show(loading.Root);
        GrammarTopicView? live = null;
        var pump = new PreviewPump(topic =>
        {
            if (_request != request) return;
            if (live is null)
            {
                live = NewTopicView(topic.Title, null);
                Show(live);
            }
            live.Show(topic, complete: false);
        });
        _composer.SetActivity(ComposerActivity.Translating, "正在讲解…");
        _setStatus("正在查询语法库…");
        try
        {
            var result = await _grammar.AskAsync(query, request.Token, pump.Push, stage => Dispatcher.UIThread.Post(() =>
            {
                if (_request != request) return;
                loading.SetStage(stage);
                live?.SetStage(stage);
                _setStatus(stage);
            })).ConfigureAwait(true);
            pump.Close();
            if (_request != request) return;
            _language = result.Language;
            if (result.Topic is { } topic) FinishTopic(live, topic, null);
            else
            {
                _current = new(result, null);
                Show(BuildReading(_current, back: false));
                _setStatus(result.Kind == GrammarLookupKind.NotGrammar ? "没有识别出语法点"
                    : result.Kind == GrammarLookupKind.Sentence ? $"找到 {result.Sentence!.Points.Count} 个语法点" : "请选择要了解的语法点");
            }
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { pump.Close(); }
        catch (Exception ex)
        {
            pump.Close();
            if (_request != request) return;
            ShowFailure(ex.Message, () => _ = AskAsync(query));
        }
        finally
        {
            if (_request == request)
            {
                _request = null;
                _composer.SetActivity(ComposerActivity.Ready);
            }
        }
    }

    public Task OpenTopicAsync(string title, string? language) => RunOpenAsync(title, language, pushHistory: false);

    private void OpenLinked(string title) => _ = RunOpenAsync(title, _language, pushHistory: true);

    private async Task RunOpenAsync(string title, string? language, bool pushHistory, bool forceRefresh = false, GrammarTopic? previous = null)
    {
        title = DictionaryQuery.CleanInput(title);
        if (title.Length == 0) return;
        if (pushHistory && _current is not null)
        {
            if (_history.Count == 16) _history.RemoveAt(0);
            _history.Add((_current, _scroll.Offset));
        }
        else if (!pushHistory && previous is null) _history.Clear();
        StopRequest(announce: false);
        using var request = new CancellationTokenSource();
        _request = request;
        _welcome.IsVisible = false;
        _current = null;

        var view = NewTopicView(title, _history.Count > 0 ? GoBack : null);
        Show(view);
        var pump = new PreviewPump(topic =>
        {
            if (_request == request) view.Show(topic, complete: false);
        });
        _composer.SetActivity(ComposerActivity.Translating, "正在讲解…");
        _setStatus("正在查询语法库…");
        try
        {
            var topic = await _grammar.OpenTopicAsync(title, language, request.Token, pump.Push, stage => Dispatcher.UIThread.Post(() =>
            {
                if (_request != request) return;
                view.SetStage(stage);
                _setStatus(stage);
            }), forceRefresh).ConfigureAwait(true);
            pump.Close();
            if (_request == request) FinishTopic(view, topic, _history.Count > 0 ? GoBack : null);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { pump.Close(); }
        catch (Exception ex)
        {
            pump.Close();
            if (_request != request) return;
            if (previous is not null)
            {
                FinishTopic(null, previous, _history.Count > 0 ? GoBack : null);
                _setStatus($"刷新失败，已保留原讲解。{ex.Message}");
            }
            else ShowFailure(ex.Message, () => _ = RunOpenAsync(title, language, pushHistory: false, previous: null), view);
        }
        finally
        {
            if (_request == request)
            {
                _request = null;
                _composer.SetActivity(ComposerActivity.Ready);
            }
        }
    }

    private async Task CompleteAsync(GrammarTopicView view, GrammarTopic topic)
    {
        if (_request is not null) return;
        using var request = new CancellationTokenSource();
        _request = request;
        _composer.SetActivity(ComposerActivity.Translating, "正在补全进阶内容…");
        _setStatus("正在补全进阶内容…");
        var pump = new PreviewPump(preview =>
        {
            if (_request == request) view.Show(preview, complete: false);
        });
        try
        {
            var completed = await _grammar.CompleteTopicAsync(topic, request.Token, pump.Push, stage => Dispatcher.UIThread.Post(() =>
            {
                if (_request == request) _setStatus(stage);
            })).ConfigureAwait(true);
            pump.Close();
            if (_request == request) FinishTopic(view, completed, _history.Count > 0 ? GoBack : null);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
            pump.Close();
            view.Show(topic, complete: true);
        }
        catch (Exception ex)
        {
            pump.Close();
            if (_request != request) return;
            view.Show(topic, complete: true);
            _setStatus($"未能补全进阶内容。{ex.Message}");
        }
        finally
        {
            if (_request == request)
            {
                _request = null;
                _composer.SetActivity(ComposerActivity.Ready);
            }
        }
    }

    internal void StopRequest() => StopRequest(announce: true);

    private void StopRequest(bool announce)
    {
        if (_request is not { } request) return;
        _request = null;
        _composer.SetActivity(ComposerActivity.Ready);
        if (announce)
        {
            _setStatus("已停止生成");
            if (_current is null && _scroll.Content is GrammarTopicView view) view.ShowError("已停止生成。可以重新发送，已写好的核心内容会保留在语法库里。");
            else if (_current is null) ShowWelcome();
        }
        request.Cancel();
    }

    // ── Showing ────────────────────────────────────────────────────────────

    private GrammarTopicView NewTopicView(string title, Action? goBack)
    {
        var view = new GrammarTopicView(title, _blocks, goBack);
        return view;
    }

    private void FinishTopic(GrammarTopicView? view, GrammarTopic topic, Action? goBack)
    {
        _language = topic.Language;
        _current = new(null, topic);
        view ??= NewTopicView(topic.Title, goBack);
        if (!ReferenceEquals(_scroll.Content, view)) Show(view);
        view.Show(topic, complete: true);
        AttachActions(view, topic);
        RefreshLibrary();
        _setStatus(topic.Origin == GrammarOrigin.Library ? $"「{topic.Title}」来自本地语法库" : $"已讲解「{topic.Title}」并存入语法库");
    }

    private void AttachActions(GrammarTopicView view, GrammarTopic topic)
    {
        view.Actions.Content = StudioUi.CreateIconButton("arrows-rotate", "重新用 AI 讲解（忽略语法库）",
            () => _ = RunOpenAsync(topic.Title, topic.Language, pushHistory: false, forceRefresh: true, previous: topic));
        view.CompleteRequested += () => _ = CompleteAsync(view, topic);
    }

    private Control BuildReading(Reading reading, bool back)
    {
        Action? goBack = back ? GoBack : null;
        if (reading.Topic is { } topic)
        {
            var view = NewTopicView(topic.Title, goBack);
            view.Show(topic, complete: true);
            AttachActions(view, topic);
            return view;
        }
        var result = reading.Result!;
        return result.Kind switch
        {
            GrammarLookupKind.Sentence => GrammarResultViews.Sentence(result.Sentence!, title => _grammar.IsKnown(title, null), title => OpenLinked(title), _lookup, goBack),
            GrammarLookupKind.Choices => GrammarResultViews.Choices(result.Message, result.Choices, title => _grammar.IsKnown(title, null), title => OpenLinked(title), goBack),
            _ => new StackPanel
            {
                Margin = new Thickness(0, AppTheme.Space.XXXL, 0, 0),
                Children = { StudioUi.CreateEmptyState("circle-question", AppTheme.Hues.Neutral, "没有识别出语法点", result.Message, 440) }
            }
        };
    }

    private void GoBack()
    {
        if (_history.Count == 0) return;
        StopRequest(announce: false);
        var (previous, offset) = _history[^1];
        _history.RemoveAt(_history.Count - 1);
        _current = previous;
        Show(BuildReading(previous, back: _history.Count > 0));
        _scroll.UpdateLayout();
        _scroll.Offset = offset;
        _setStatus("已返回上一级");
    }

    private void ShowFailure(string message, Action retry, GrammarTopicView? view = null)
    {
        if (view is not null)
        {
            view.ShowError(message);
            view.Actions.Content = StudioUi.CreateSecondaryButton("arrows-rotate", "重试", retry);
        }
        else
        {
            var panel = new StackPanel { Spacing = AppTheme.Space.M, Margin = new Thickness(0, AppTheme.Space.XXL, 0, 0) };
            panel.Children.Add(new Border
            {
                Background = AppTheme.DangerSoft, CornerRadius = new CornerRadius(AppTheme.Radius.M),
                Padding = new Thickness(AppTheme.Space.M, AppTheme.Space.S + 2),
                Child = new SelectableTextBlock { Text = message, FontSize = AppTheme.Type.Small, Foreground = AppTheme.Danger, TextWrapping = TextWrapping.Wrap }
            });
            panel.Children.Add(StudioUi.CreateSecondaryButton("arrows-rotate", "重试", retry));
            Show(panel);
        }
        _setStatus("语法讲解未完成");
    }

    private void Show(Control page)
    {
        _welcome.IsVisible = false;
        _scroll.Content = page;
        _scroll.ScrollToHome();
    }

    private void ShowWelcome()
    {
        _scroll.Content = null;
        _welcome.IsVisible = true;
        RefreshLibrary();
    }

    /// <summary>Placeholder while the planner decides what the input is; the stage text mirrors the status bar.</summary>
    private sealed class LoadingPage
    {
        private readonly TextBlock _stage = TextStyles.Create("正在查询本地语法库…", TextRole.Secondary);
        public StackPanel Root { get; }

        public LoadingPage(string query)
        {
            var shown = query.Length > 120 ? query[..120] + "…" : query;
            var quote = TextStyles.Create(shown, TextRole.PassageTranslation);
            Root = new StackPanel
            {
                Spacing = AppTheme.Space.M,
                Children =
                {
                    StudioUi.CreateChip("语法精解", "graduation-cap", AppTheme.Insight, AppTheme.InsightSoft),
                    quote,
                    _stage,
                    new LoadingSkeleton { Margin = new Thickness(0, AppTheme.Space.L, 0, 0) }
                }
            };
        }

        public void SetStage(string stage) => _stage.Text = stage;
    }
}
