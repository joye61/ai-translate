using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace AiTranslate.Desktop.Controls;

internal sealed class SuggestingTextBox : TextBox
{
    private readonly Run _prefix = new() { Foreground = Brushes.Transparent };
    private readonly Run _suffix = new() { Foreground = AppTheme.TextMuted };
    private readonly TextBlock _preview;
    private TextPresenter? _presenter;
    private CancellationTokenSource? _pending;
    private bool _refreshQueued;
    private string? _requestedPrefix;
    private (string Prefix, string Text)? _match;

    public Func<string, CancellationToken, Task<string?>>? FindSuggestion { get; set; }
    public string? AcceptedSuggestion { get; private set; }
    protected override Type StyleKeyOverride => typeof(TextBox);

    public SuggestingTextBox()
    {
        _preview = new TextBlock
        {
            IsVisible = false, IsHitTestVisible = false, Opacity = 0.72,
            TextWrapping = TextWrapping.Wrap, Inlines = [_prefix, _suffix]
        };
        AddHandler(KeyDownEvent, AcceptSuggestion, RoutingStrategies.Tunnel);
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        if (_presenter is not null) _presenter.PropertyChanged -= OnPresenterChanged;
        if (_preview.Parent is Panel previous) previous.Children.Remove(_preview);
        base.OnApplyTemplate(e);
        _presenter = e.NameScope.Find<TextPresenter>("PART_TextPresenter");
        if (_presenter?.GetVisualParent() is not Panel panel) return;
        // Share the native text viewport, font, padding, scrolling and wrap width.
        _preview.HorizontalAlignment = _presenter.HorizontalAlignment;
        _preview.VerticalAlignment = _presenter.VerticalAlignment;
        panel.Children.Insert(0, _preview);
        _presenter.PropertyChanged += OnPresenterChanged;
        RefreshSuggestion();
    }

    private void OnPresenterChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == TextPresenter.PreeditTextProperty) RefreshSuggestion();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == TextProperty) AcceptedSuggestion = null;
        if (e.Property == IsFocusedProperty)
            RefreshSuggestion();
        else if (!_refreshQueued && (e.Property == TextProperty || e.Property == CaretIndexProperty ||
            e.Property == SelectionStartProperty || e.Property == SelectionEndProperty))
        {
            // TextBox reports text, caret and selection separately during one edit. Read them together before rendering.
            _refreshQueued = true;
            Dispatcher.UIThread.Post(() =>
            {
                _refreshQueued = false;
                RefreshSuggestion();
            }, DispatcherPriority.Send);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        ClearSuggestion();
    }

    public void ResetSuggestions()
    {
        AcceptedSuggestion = null;
        ClearSuggestion();
        RefreshSuggestion();
    }

    private string? EligiblePrefix() =>
        IsFocused && _presenter is not null && string.IsNullOrEmpty(_presenter.PreeditText) &&
        SelectionStart == SelectionEnd && Text is { Length: > 0 } text && CaretIndex == text.Length &&
        !string.IsNullOrWhiteSpace(text) ? text : null;

    private void ClearSuggestion()
    {
        CancelRequest();
        _match = null;
        _preview.IsVisible = false;
    }

    private void CancelRequest()
    {
        _pending?.Cancel();
        _pending = null;
        _requestedPrefix = null;
    }

    private async void RefreshSuggestion()
    {
        var prefix = EligiblePrefix();
        if (!IsFocused || string.IsNullOrEmpty(Text)) _match = null;
        if (prefix == _requestedPrefix) return;
        CancelRequest();
        if (prefix is null || FindSuggestion is not { } find)
        {
            _preview.IsVisible = false;
            return;
        }
        _requestedPrefix = prefix;
        if (_match is { } match && match.Text.Length > prefix.Length && match.Text.StartsWith(prefix, StringComparison.Ordinal))
        {
            // Backspace widens the search. Keep the known completion on screen until the new winner arrives.
            ShowPreview(prefix, match.Text);
            if (prefix.StartsWith(match.Prefix, StringComparison.Ordinal)) return;
        }
        else _preview.IsVisible = false;
        using var request = new CancellationTokenSource();
        _pending = request;
        try
        {
            await Task.Delay(45, request.Token);
            var candidate = await find(prefix, request.Token);
            if (request.IsCancellationRequested || prefix != EligiblePrefix()) return;
            if (candidate is null || candidate.Length <= prefix.Length || !candidate.StartsWith(prefix, StringComparison.Ordinal))
            {
                // An exact match has no visible suffix, but backspacing can reveal it again.
                if (_match?.Text != prefix) _match = null;
                _preview.IsVisible = false;
                return;
            }
            _match = (prefix, candidate);
            ShowPreview(prefix, candidate);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception ex)
        {
            // A locked/unavailable history must leave typing and focus traversal usable.
            if (!request.IsCancellationRequested) ClearSuggestion();
            System.Diagnostics.Trace.TraceWarning("Local completion unavailable: {0}", ex.Message);
        }
        finally
        {
            if (_pending == request) _pending = null;
        }
    }

    private void ShowPreview(string prefix, string candidate)
    {
        _prefix.Text = prefix;
        // A legal break at the caret keeps a long completion from reflowing the already typed word.
        // This marker belongs only to the preview. Tab inserts the original suffix.
        _suffix.Text = "\u200B" + candidate[prefix.Length..];
        _preview.IsVisible = true;
    }

    private void AcceptSuggestion(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Tab || e.KeyModifiers != KeyModifiers.None || !_preview.IsVisible || _match is not { Text: var candidate } ||
            EligiblePrefix() is not { } prefix || prefix != _requestedPrefix) return;
        e.Handled = true;
        SelectedText = candidate[prefix.Length..];
        AcceptedSuggestion = Text == candidate ? candidate : null;
    }
}
