namespace AiTranslate.Core;

/// <summary>Persistent verified plain-text translations. The key includes the source text and target language.</summary>
public interface IVerifiedTranslationCache
{
    string[]? ReadTranslations(string key);
    void WriteTranslations(string key, string[] translations, long requestTicks);
}
