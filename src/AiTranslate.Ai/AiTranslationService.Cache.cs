using System.Text.Json;

namespace AiTranslate.Ai;

public sealed partial class AiTranslationService
{
    private async Task<IReadOnlyList<TranslationRegionText>> TranslateWithCacheAsync(
        TranslationSourceRegion[] source, TranslationLanguage language, string mode, bool forceRefresh,
        Func<bool, Task<IReadOnlyList<TranslationRegionText>>> translate, CancellationToken token)
    {
        if (_cache is null) return await TranslateVerifiedAsync(source, language, translate, token).ConfigureAwait(false);

        var started = DateTime.UtcNow.Ticks;
        // Neighbouring OCR regions provide context. Preserve their order and whitespace, but remap transient IDs on a hit.
        var key = JsonSerializer.Serialize(new { mode, target = language.Id, source = source.Select(r => r.Text) });
        if (!forceRefresh)
        {
            var hit = await Task.Run(() => _cache.ReadTranslations(key), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (hit?.Length == source.Length && hit.All(t => !string.IsNullOrWhiteSpace(t)) && hit.Sum(t => (long)t.Length) <= MaxResultLength)
                return source.Select((r, i) => new TranslationRegionText(r.Id, hit[i])).ToArray();
        }

        IReadOnlyList<TranslationRegionText> result;
        if (_transport is null && !forceRefresh)
        {
            var offline = new List<TranslationRegionText>(source.Length);
            foreach (var region in source)
            {
                if (!BuiltInShowcaseLexicon.TryGet(region.Text, out var entry) || entry.TargetLanguage != language.Id)
                    throw new AiException("本地没有这条译文，请在「引擎与快捷键」中配置 AI 引擎。");
                offline.Add(new(region.Id, entry.PrimaryTranslation));
            }
            result = offline;
        }
        else result = await TranslateVerifiedAsync(source, language, translate, token).ConfigureAwait(false);

        token.ThrowIfCancellationRequested();
        await Task.Run(() => _cache.WriteTranslations(key, result.Select(r => r.Text).ToArray(), started), token).ConfigureAwait(false);
        return result;
    }
}
