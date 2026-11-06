using System.Globalization;

namespace AiTranslate.Desktop.Controls;

internal static class StreamingText
{
    /// <summary>Spread append-only network bursts over frames without splitting a visible character.</summary>
    public static string Advance(string shown, string target, TimeSpan elapsed)
    {
        // A revised translation must replace the old wording, rather than append to it.
        if (shown == target || !target.StartsWith(shown, StringComparison.Ordinal)) return target;
        var boundaries = StringInfo.ParseCombiningCharacters(target);
        var visible = Array.BinarySearch(boundaries, shown.Length);
        if (visible < 0) visible = ~visible;
        var remaining = boundaries.Length - visible;
        // Short bursts read at roughly 60 characters/s; longer bursts catch up instead of building a queue.
        var count = Math.Max(1, (int)Math.Ceiling(Math.Max(60, remaining / 0.12) * Math.Clamp(elapsed.TotalSeconds, 0, 0.1)));
        var end = Math.Min(boundaries.Length, visible + count);
        return end == boundaries.Length ? target : target[..boundaries[end]];
    }
}
