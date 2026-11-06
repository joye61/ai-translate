using AiTranslate.Core;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace AiTranslate.Desktop.Controls;

/// <summary>Keep the reader's choices while rows stream in and when returning from a linked lookup.</summary>
internal sealed class DictionaryDisclosures
{
    private readonly Dictionary<(EntryBlock, string, int), bool> _choices = [];

    public Button Toggle(EntryBlock block, string group, int ordinal, string subject, string label,
        Control details)
    {
        var key = (block, group, ordinal);
        var icon = new MdiIcon("chevron-down", 16, AppTheme.TextSecondary);
        var caption = TextStyles.Create(label, TextRole.FieldLabel, wrap: false);
        caption.VerticalAlignment = VerticalAlignment.Center;
        var button = new Button
        {
            Classes = { "ghost" }, Background = Brushes.Transparent, BorderThickness = default,
            Padding = new Thickness(AppTheme.Space.S, AppTheme.Space.XXS),
            CornerRadius = new CornerRadius(AppTheme.Radius.S), MinHeight = 30,
            VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Right,
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = AppTheme.Space.XXS, Children = { caption, icon }
            }
        };
        void Show(bool open)
        {
            details.IsVisible = open;
            icon.IconName = open ? "chevron-up" : "chevron-down";
            string action = open ? "收起" : "展开";
            AutomationProperties.SetName(button, $"{action}「{subject}」的{label}");
            ToolTip.SetTip(button, action + label);
        }
        Show(_choices.GetValueOrDefault(key));
        button.Click += (_, _) =>
        {
            _choices[key] = !details.IsVisible;
            Show(_choices[key]);
        };
        return button;
    }
}
