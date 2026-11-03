namespace AiTranslate.Core;

public sealed record DictionaryCacheStamp(string Key, long Revision);

public interface IDictionaryEntryCache
{
    DictionaryEntryResult? Read(string key);
    bool Write(string key, DictionaryEntryResult result, long requestTicks);
    bool TryReplace(DictionaryCacheStamp previous, DictionaryEntryResult result, long requestTicks);
}
