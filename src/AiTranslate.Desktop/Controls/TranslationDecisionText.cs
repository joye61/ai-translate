using AiTranslate.Core;

namespace AiTranslate.Desktop.Controls;

internal static class TranslationDecisionText
{
    internal static string Anchors(TranslationDecision decision) => $"{decision.SourceQuote} → {decision.TargetQuote}";

    internal static string Explanation(TranslationDecision decision) =>
        string.IsNullOrWhiteSpace(decision.Alternative) || string.IsNullOrWhiteSpace(decision.Condition)
            ? decision.Reason
            : $"{decision.Reason}\n备选译法　{decision.Alternative}\n适用条件　{decision.Condition}";

    internal static string Format(TranslationDecision decision) => $"{Anchors(decision)}\n{Explanation(decision)}";
}
