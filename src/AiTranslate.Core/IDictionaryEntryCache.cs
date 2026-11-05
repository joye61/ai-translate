namespace AiTranslate.Core;

public sealed record DictionaryCacheStamp(string Key, long Revision);

public interface IDictionaryEntryCache
{
    DictionaryEntryResult? Read(string key);
    IEnumerable<(string Source, string Key)> FindCompletions(string prefix, string? targetLanguage, string readerLanguage,
        CancellationToken cancellationToken);
    bool Write(string key, DictionaryEntryResult result, long requestTicks);
    bool TryAdd(string key, DictionaryEntryResult result, long requestTicks);
    bool TryReplace(DictionaryCacheStamp previous, DictionaryEntryResult result, long requestTicks);
}
