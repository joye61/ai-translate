namespace AiTranslate.Core;

/// <summary>Verified translations in source order. The key identifies the target and ordered source texts.</summary>
public interface IVerifiedTranslationCache
{
    string[]? ReadTranslations(string key);
    void WriteTranslations(string key, string[] translations, long requestTicks);
}
