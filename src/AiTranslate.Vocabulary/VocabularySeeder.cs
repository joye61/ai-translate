using AiTranslate.Core;

namespace AiTranslate.Vocabulary;

/// <summary>A fresh install starts with the default notebook only; every other entry is saved by the user.</summary>
internal static class VocabularySeeder
{
    public static VocabularyStoreSnapshot CreateDefaultSeededStore() => new()
    {
        Notebooks = [new() { Id = "default", Name = "默认生词本", IsSystemDefault = true }],
        Entries = [],
        ReviewLogs = []
    };
}
