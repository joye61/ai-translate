using System.Runtime.InteropServices;
using AiTranslate.Core;
using Interop.UIAutomationClient;

namespace AiTranslate.Platform.Windows;

internal sealed class WindowsTextSelection
{
    private IUIAutomation? _automation;

    // Empty means the provider saw only a caret. Null permits the legacy copy fallback.
    internal string? Read(PointD point, bool atPointer)
    {
        try
        {
            var automation = _automation ??= new CUIAutomation8();
            var focused = automation.GetFocusedElement();
            if (focused is { CurrentIsPassword: not 0 } || focused?.CurrentProcessId == Environment.ProcessId) return "";
            var focusedText = SelectedText(focused);
            if (!atPointer || !string.IsNullOrWhiteSpace(focusedText)) return focusedText;

            // In browsers the hit can be a text leaf while its document owns the selection.
            var element = automation.ElementFromPoint(new tagPOINT { x = (int)point.X, y = (int)point.Y });
            var walker = automation.ControlViewWalker;
            for (var depth = 0; element is not null && depth < 8; depth++)
            {
                if (element.CurrentIsPassword != 0 || element.CurrentProcessId == Environment.ProcessId) return "";
                if (SelectedText(element) is { } text) return text;
                element = walker.GetParentElement(element);
            }
            return focusedText;
        }
        catch (COMException) { return null; } // UI providers can disappear as focus changes.
    }

    private static string? SelectedText(IUIAutomationElement? element)
    {
        if (element?.GetCurrentPattern(UIA_PatternIds.UIA_TextPatternId) is not IUIAutomationTextPattern pattern ||
            pattern.SupportedTextSelection == SupportedTextSelection.SupportedTextSelection_None) return null;
        var ranges = pattern.GetSelection();
        if (ranges is null) return "";
        var selections = new List<string>();
        for (var i = 0; i < ranges.Length; i++)
        {
            var text = ranges.GetElement(i).GetText(-1);
            if (!string.IsNullOrWhiteSpace(text)) selections.Add(text);
        }
        return string.Join("\n", selections);
    }
}
