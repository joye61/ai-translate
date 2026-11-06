using AiTranslate.Ai;
using AiTranslate.Core;
using Avalonia;

namespace AiTranslate.Desktop;

/// <summary>
/// Decides what a right-click lookup on selected text does. A selection with a stored translation shows the same card as
/// the global copy popup. Anything else shows a small prompt, and only a click on it starts a regular lookup.
/// </summary>
internal sealed class SelectionLookupRouter(IAiDictionaryService dictionary, AppSettingsStore settings,
    SelectionPopupWindow card, SelectionPromptWindow prompt)
{
    private CancellationTokenSource? _probe;

    public async Task RouteAsync(string query, PixelPoint anchor, Action openLookup)
    {
        _probe?.Cancel();
        var probe = _probe = new CancellationTokenSource();
        var stored = await HasStoredTranslationAsync(query, probe.Token);
        if (probe.IsCancellationRequested) return;
        if (stored)
        {
            prompt.Hide();
            await card.ShowAndTranslateAsync(new CapturedTextEvent(query, TranslationTriggerSource.SelectionPopup,
                new PointD(anchor.X, anchor.Y), "AI Translate"));
        }
        else
        {
            card.Hide();
            prompt.ShowAt(anchor, query, openLookup);
        }
    }

    // A cache-only lookup throws on a miss and never reaches the AI provider.
    private async Task<bool> HasStoredTranslationAsync(string query, CancellationToken token)
    {
        try
        {
            await dictionary.LookupOrTranslateAsync(query,
                DictionaryTranslationDirection.ResolveTargetId(query, DictionaryTranslationDirection.GetMode(settings.Current)),
                cancellationToken: token, cacheOnly: true, intent: DictionaryLookupIntent.Quick);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
