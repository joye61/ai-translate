using AiTranslate.Ai;
using AiTranslate.Core;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace AiTranslate.Desktop.Controls;

/// <summary>One group per reading so a double click cannot start a second AI request.</summary>
internal sealed class BlockReviewActions
{
    internal const string PendingSyncStatus = "已保存到本地缓存，生词本将在下次查词时重试同步";
    public static string ResultStatus(string title, DictionaryEntryResult result, bool changed) =>
        result.NotebookSyncPending ? $"「{title}」{PendingSyncStatus}" :
        changed ? $"「{title}」已更新并保存" : $"「{title}」已复核，保留原内容";
    public event Action<EntryBlock>? Requested;
    private const string ButtonLabel = "智能复核";
    private sealed record ActionView(Button Button, TextBlock Label, MdiIcon Icon, TextBlock Error, Action<DictionaryEntryResult> Render);
    private readonly Dictionary<EntryBlock, ActionView> _actions = [];
    public bool IsBusy { get; private set; }

    public static TextBlock CreateError()
    {
        var error = TextStyles.Create("", TextRole.Annotation);
        error.Foreground = AppTheme.Danger;
        error.IsVisible = false;
        return error;
    }

    public Button Create(EntryBlockSpec spec, TextBlock error, Action<DictionaryEntryResult> render)
    {
        var label = TextStyles.Create(ButtonLabel, TextRole.Label, wrap: false);
        label.Foreground = AppTheme.Insight;
        var icon = new MdiIcon("sparkles", 14, AppTheme.Insight) { VerticalAlignment = VerticalAlignment.Center };
        var button = new Button
        {
            Classes = { "ai-review" }, MinHeight = 28, Padding = new Thickness(AppTheme.Space.M - 2, 2),
            CornerRadius = new CornerRadius(AppTheme.Radius.Pill), Background = AppTheme.InsightSoft,
            BorderBrush = Brushes.Transparent, BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right, IsEnabled = !IsBusy,
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = AppTheme.Space.XS, Children = { icon, label } }
        };
        AutomationProperties.SetName(button, $"{ButtonLabel} · {spec.Title}");
        ToolTip.SetTip(button, $"核对「{spec.Title}」。内容和语境相同时复用该栏的核对结果");
        button.Click += (_, _) => { if (!IsBusy) Requested?.Invoke(spec.Block); };
        _actions[spec.Block] = new(button, label, icon, error, render);
        return button;
    }

    public void SetBusy(bool busy, EntryBlock? active = null)
    {
        IsBusy = busy;
        foreach (var (block, view) in _actions)
        {
            view.Button.IsEnabled = !busy;
            view.Label.Text = busy && block == active ? "复核中…" : view.Error.IsVisible ? "重新复核" : ButtonLabel;
            view.Icon.IconName = view.Error.IsVisible ? "refresh" : "sparkles";
        }
    }

    public async Task<DictionaryEntryResult> ReviewAsync(IAiDictionaryService service, DictionaryEntryResult original,
        EntryBlock block, CancellationToken token)
    {
        var view = _actions[block];
        view.Error.IsVisible = false;
        SetBusy(true, block);
        var acceptingFrames = true;
        var showingDraft = false;
        var result = original;
        string? feedback = null;
        try
        {
            result = await service.ReviewBlockAsync(original, block, token, frame => Dispatcher.UIThread.Post(() =>
            {
                if (!acceptingFrames || token.IsCancellationRequested) return;
                view.Render(frame);
                showingDraft = true;
            }));
            token.ThrowIfCancellationRequested();
            feedback = SectionChanged(original, result, block) ? "已更新" : "无需调整";
            return result;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            view.Error.Text = $"{ex.Message.TrimEnd('。', ' ')}。已保留原内容。";
            view.Error.IsVisible = true;
            throw;
        }
        finally
        {
            acceptingFrames = false;
            if (showingDraft || result.NotebookSyncPending) view.Render(token.IsCancellationRequested ? original : result);
            SetBusy(false);
            if (feedback is not null) Feedback(block, feedback);
        }
    }

    internal static bool SectionChanged(DictionaryEntryResult original, DictionaryEntryResult result, EntryBlock block) =>
        !ReferenceEquals(original, result) && (result.Processing?.SectionChecks.LastOrDefault(s => s.Block == block)?.Changed ?? true);

    private void Feedback(EntryBlock block, string message)
    {
        if (!_actions.TryGetValue(block, out var view)) return;
        view.Label.Text = message;
        view.Icon.IconName = "check";
        DispatcherTimer.RunOnce(() =>
        {
            if (view.Label.Text != message) return;
            view.Label.Text = ButtonLabel;
            view.Icon.IconName = "sparkles";
        }, TimeSpan.FromSeconds(3));
    }
}
