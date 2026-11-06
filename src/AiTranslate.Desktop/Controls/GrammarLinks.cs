using AiTranslate.Core;

namespace AiTranslate.Desktop.Controls;

/// <summary>
/// Bridge from the grammar blocks of a translation (and the notebook) to the grammar studio. The app shell sets these once;
/// without them the grammar rows are plain text, which keeps tests and the capture popup free of navigation.
/// </summary>
internal static class GrammarLinks
{
    /// <summary>Opens a grammar point by its canonical name and studied-language code.</summary>
    internal static Action<string, string>? Open { get; set; }

    /// <summary>True when the local grammar library already holds the grammar point.</summary>
    internal static Func<string, string, bool>? IsKnown { get; set; }

    internal static bool Available => Open is not null;

    internal static bool Knows(string topic, string language) => !string.IsNullOrWhiteSpace(topic) && IsKnown?.Invoke(topic, language) == true;

    /// <summary>The name a highlight links under: its canonical topic, or its label for results cached before topics existed.</summary>
    internal static string TopicOf(GrammarHighlight highlight) =>
        string.IsNullOrWhiteSpace(highlight.Topic) ? highlight.Label : highlight.Topic;
}
