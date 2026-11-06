using System.Text.Json;

namespace AiTranslate.Ai;

public sealed partial class AiTranslationService
{
    private async Task<string> TranslateTextWithCacheAsync(
        string sourceText, TranslationLanguage language, bool forceRefresh, CancellationToken token)
    {
        async Task<string> TranslateAsync()
        {
            var result = await TranslateVerifiedAsync([new("text", sourceText)], language, async retry =>
                [new("text", await TranslateTextCoreAsync(sourceText, language, retry, token).ConfigureAwait(false))], token).ConfigureAwait(false);
            return result[0].Text;
        }
        if (_cache is null) return await TranslateAsync().ConfigureAwait(false);

        var started = DateTime.UtcNow.Ticks;
        // Preserve the existing plain-text cache key across upgrades.
        var key = JsonSerializer.Serialize(new { mode = "text", target = language.Id, source = new[] { sourceText } });
        if (!forceRefresh)
        {
            var hit = await Task.Run(() => _cache.ReadTranslations(key), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (hit is [var text] && !string.IsNullOrWhiteSpace(text) && text.Length <= MaxResultLength) return text;
        }

        string translation;
        if (_transport is null && !forceRefresh)
        {
            if (!BuiltInShowcaseLexicon.TryGet(sourceText, out var entry) || entry.TargetLanguage != language.Id)
                throw new AiException("本地没有这条译文，请在「应用设置」中配置 AI 引擎。");
            translation = entry.PrimaryTranslation;
        }
        else translation = await TranslateAsync().ConfigureAwait(false);

        token.ThrowIfCancellationRequested();
        await Task.Run(() => _cache.WriteTranslations(key, [translation], started), token).ConfigureAwait(false);
        return translation;
    }
}
