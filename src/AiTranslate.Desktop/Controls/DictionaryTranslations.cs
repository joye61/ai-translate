using System.Diagnostics;
using AiTranslate.Core;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;

namespace AiTranslate.Desktop.Controls;

/// <summary>The main reading and its optional literal counterpart, shared by all lookup surfaces.</summary>
internal sealed class DictionaryTranslations : Border
{
    private sealed class Reading : Border
    {
        private readonly Grid _header;
        private readonly TextBlock _error = BlockReviewActions.CreateError();
        private readonly EntryBlockSpec _spec;
        public Reading(EntryBlockSpec spec, Control text)
        {
            _spec = spec;
            var label = TextStyles.Create(spec.Title, TextRole.Label);
            label.VerticalAlignment = VerticalAlignment.Center;
            _header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = AppTheme.Space.S,
                Children = { label }, IsVisible = false };
            Child = new StackPanel { Spacing = AppTheme.Space.S, Children = { _header, text, _error } };
            BorderBrush = AppTheme.BorderSubtle;
        }

        public void ShowLabel(bool paired) => _header.IsVisible = paired || _header.Children.Count > 1;

        public void EnableReview(BlockReviewActions reviews, Action<DictionaryEntryResult> render)
        {
            if (_header.Children.Count > 1) return;
            var button = reviews.Create(_spec, _error, render);
            Grid.SetColumn(button, 1);
            _header.Children.Add(button);
            _header.IsVisible = true;
        }
    }

    private readonly SelectableTextBlock _preview = TextStyles.Selectable(null, TextRole.Translation);
    private readonly Border _primary = new();
    private readonly SelectableTextBlock _literal = TextStyles.Selectable(null, TextRole.PassageTranslation);
    private readonly Reading _naturalRow, _literalRow;
    private readonly Grid _readings = new() { ColumnDefinitions = new ColumnDefinitions("*,*"), RowDefinitions = new RowDefinitions("Auto,Auto") };
    private readonly bool _smoothPreview;
    private readonly DispatcherTimer _frames = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private string _targetPrimary = "", _targetLiteral = "";
    private VocabularyItemType _previewType;
    private TextRole _bodyRole = TextRole.Translation;
    private long _lastFrame;
    private bool _attached;

    public bool HasLinkedTerms => _primary.Child is DictionaryReadingText { HasLinkedTerms: true };

    public DictionaryTranslations(bool smoothPreview = false)
    {
        _smoothPreview = smoothPreview;
        _primary.Child = _preview;
        _naturalRow = new(EntryBlockCatalog.PrimaryTranslation, _primary);
        _literalRow = new(EntryBlockCatalog.LiteralTranslation, _literal);
        _readings.Children.AddRange([_naturalRow, _literalRow]);
        Child = _readings;
        BorderBrush = AppTheme.BorderSubtle;
        CornerRadius = new CornerRadius(AppTheme.Radius.L);
        IsVisible = false;
        _frames.Tick += (_, _) =>
        {
            var now = Stopwatch.GetTimestamp();
            AdvanceFrame(Stopwatch.GetElapsedTime(_lastFrame, now));
            _lastFrame = now;
        };
        AttachedToVisualTree += (_, _) =>
        {
            _attached = true;
            if (_primary.Child == _preview && HasPendingText) StartFrames();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _attached = false;
            _frames.Stop();
        };
    }

    public void ShowPreview(string text, VocabularyItemType type)
    {
        _previewType = type;
        _targetPrimary = text;
        _primary.Child = _preview;
        UpdatePreview(_smoothPreview);
    }

    public void ShowEntry(DictionaryEntryResult entry, Action<string, string>? lookup, BlockReviewActions? reviews = null)
    {
        _targetPrimary = entry.PrimaryTranslation;
        _targetLiteral = entry.LiteralTranslation;
        _previewType = entry.ItemType;
        if (lookup is null)
        {
            _primary.Child = _preview;
            UpdatePreview(_smoothPreview && entry.Origin is not (DictionaryResultOrigin.Cache or DictionaryResultOrigin.BuiltIn));
        }
        else
        {
            FinishPreview();
            void RenderPrimary(DictionaryEntryResult frame) =>
                _primary.Child = TextStyles.Apply(DictionaryReadingText.Primary(frame, lookup), _bodyRole);
            RenderPrimary(entry);
            if (reviews is not null)
            {
                _naturalRow.EnableReview(reviews, RenderPrimary);
                if (_literalRow.IsVisible)
                    _literalRow.EnableReview(reviews, draft => _literal.Text = draft.LiteralTranslation);
            }
            if (_literalRow.IsVisible) DictionaryReadingText.EnableSelectionLookup(_literal, lookup);
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // Two reading columns need room for both headers and a comfortable line of body text.
        var columns = _naturalRow.IsVisible && _literalRow.IsVisible && availableSize.Width >= 640;
        Grid.SetColumnSpan(_naturalRow, columns ? 1 : 2);
        Grid.SetColumn(_literalRow, columns ? 1 : 0);
        Grid.SetColumnSpan(_literalRow, columns ? 1 : 2);
        Grid.SetRow(_literalRow, columns ? 0 : 1);
        _literalRow.BorderThickness = columns ? new Thickness(1, 0, 0, 0) : new Thickness(0, 1, 0, 0);
        return base.MeasureOverride(availableSize);
    }

    private bool HasPendingText => (_preview.Text ?? "") != _targetPrimary || (_literal.Text ?? "") != _targetLiteral;

    private void UpdatePreview(bool animate)
    {
        if (!animate) FinishPreview();
        else if (_attached && !_frames.IsEnabled && HasPendingText)
        {
            AdvanceFrame(_frames.Interval);
            if (HasPendingText) StartFrames();
        }
    }

    private void StartFrames()
    {
        _lastFrame = Stopwatch.GetTimestamp();
        _frames.Start();
    }

    internal void AdvanceFrame(TimeSpan elapsed)
    {
        if (!_attached || _primary.Child != _preview) return;
        _preview.Text = StreamingText.Advance(_preview.Text ?? "", _targetPrimary, elapsed);
        _literal.Text = StreamingText.Advance(_literal.Text ?? "", _targetLiteral, elapsed);
        UpdateVisibility();
        if (!HasPendingText) _frames.Stop();
    }

    // Complete, stopped and failed requests expose everything already received and leave no animation queued.
    public void FinishPreview()
    {
        _frames.Stop();
        _preview.Text = _targetPrimary;
        _literal.Text = _targetLiteral;
        UpdateVisibility();
    }

    private void UpdateVisibility()
    {
        _naturalRow.IsVisible = !string.IsNullOrWhiteSpace(_preview.Text);
        _literalRow.IsVisible = !string.IsNullOrWhiteSpace(_literal.Text);
        IsVisible = _naturalRow.IsVisible || _literalRow.IsVisible;
        var paired = _naturalRow.IsVisible && _literalRow.IsVisible;
        _naturalRow.ShowLabel(paired);
        _literalRow.ShowLabel(true);
        Background = paired ? AppTheme.CardBg : null;
        BorderThickness = paired ? new Thickness(1) : default;
        _naturalRow.Padding = _literalRow.Padding = paired ? new Thickness(AppTheme.Space.L) : default;
        var role = paired || _previewType.IsPassage() ? TextRole.PassageTranslation : TextRole.Translation;
        if (role != _bodyRole) TextStyles.Apply(_preview, _bodyRole = role);
    }
}
