using AiTranslate.Core;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace AiTranslate.Desktop.Controls;

internal static class ExamplePracticeCard
{
    public static void Open(StackPanel result, SentenceCloze cloze)
    {
        var scroll = result.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault();
        var offset = scroll?.Offset ?? default;
        var launcher = TopLevel.GetTopLevel(result)?.FocusManager?.GetFocusedElement() as InputElement;
        var reading = result.Children.Where(c => c.IsVisible).ToArray();
        foreach (var control in reading) control.IsVisible = false;

        var prompt = new SelectableTextBlock
        {
            Text = cloze.Masked, TextWrapping = TextWrapping.Wrap, FontSize = AppTheme.Type.Title, Foreground = AppTheme.TextPrimary
        };
        var content = new StackPanel
        {
            Spacing = AppTheme.Space.M, Children = { TextStyles.Create("回忆例句中的表达", TextRole.SectionTitle), prompt }
        };
        var card = StudioUi.CreateBentoCard(content);
        card.MaxWidth = 860;
        card.HorizontalAlignment = HorizontalAlignment.Center;
        var actions = new WrapPanel { ItemSpacing = AppTheme.Space.S, LineSpacing = AppTheme.Space.S };
        var back = StudioUi.CreateSecondaryButton("arrow-left", "返回结果", () =>
        {
            result.Children.Remove(card);
            foreach (var control in reading) control.IsVisible = true;
            launcher?.Focus();
            if (scroll is not null) { scroll.UpdateLayout(); scroll.Offset = offset; }
        });
        var reveal = StudioUi.CreatePrimaryButton("eye-outline", "揭晓答案", () =>
        {
            prompt.Text = cloze.Source;
            content.Children.Insert(2, StudioUi.CreateInfoTile("原句用词", cloze.Answer));
            if (!string.IsNullOrWhiteSpace(cloze.Translation))
                content.Children.Insert(3, StudioUi.CreateInfoTile("例句译文", cloze.Translation));
            actions.Children.RemoveAt(0);
            back.Focus();
        });
        actions.Children.Add(reveal);
        actions.Children.Add(back);
        content.Children.Add(actions);
        result.Children.Add(card);
        scroll?.ScrollToHome();
        reveal.Focus();
    }
}
