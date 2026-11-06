using AiTranslate.Ai;
using AiTranslate.Core;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace AiTranslate.Desktop.Controls;

internal enum ComposerActivity { Ready, Translating, Reviewing }

/// <summary>Wording and left-hand control of a composer, so the grammar studio can reuse the translation input without its language picker.</summary>
internal sealed record ComposerProfile(string Prompt, string InputName, string SendLabel, Control? Lead = null);

internal sealed class DictionaryComposer : Grid
{
    private readonly string _prompt;
    private readonly string _sendLabel;
    private readonly SuggestingTextBox _input = new()
    {
        Classes = { "chat-input" },
        AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
        MinHeight = 48, MaxHeight = 168,
        FontSize = AppTheme.Type.BodyLarge, Foreground = AppTheme.TextPrimary,
        Background = AppTheme.CardBg, BorderThickness = default
    };
    private readonly ComboBox _language;
    private readonly Button _send;
    private readonly TextBlock _hint = TextStyles.Create("Tab 补全  ·  Enter 发送  ·  Shift + Enter 换行", TextRole.Meta, wrap: false);
    private readonly FontAwesomeIcon _sendIcon = new("arrow-up", 20, AppTheme.CardBg);
    private readonly FlowingBorderEffect _flow = new();
    private ComposerActivity _activity;

    public string? LanguageId => (_language.SelectedItem as TranslationLanguage)?.Id;
    public void FocusInput() => _input.Focus();

    public DictionaryComposer(AppSettingsStore settings, Func<string, string, CancellationToken, Task<string?>> findSuggestion,
        Action<string, bool> submit, Action stop, ComposerProfile? profile = null)
    {
        _prompt = profile?.Prompt ?? "输入要翻译的单词、短语或段落…";
        _sendLabel = profile?.SendLabel ?? "发送翻译";
        _input.PlaceholderText = _prompt;
        AutomationProperties.SetName(_input, profile?.InputName ?? "待翻译文本");
        var initialMode = DictionaryTranslationDirection.GetMode(settings.Current);
        _language = new ComboBox
        {
            Classes = { "chip" }, MinWidth = 120,
            ItemsSource = DictionaryTranslationDirection.Modes,
            ItemTemplate = new FuncDataTemplate<TranslationLanguage>((item, _) => Label(item?.DisplayName)),
            SelectionBoxItemTemplate = new FuncDataTemplate<TranslationLanguage>((item, _) => ChipLabel("language", item?.DisplayName)),
            SelectedItem = DictionaryTranslationDirection.Modes.First(m => m.Id == initialMode)
        };
        _input.FindSuggestion = (prefix, token) => findSuggestion(prefix, LanguageId ?? initialMode, token);
        _language.SelectionChanged += (_, _) =>
        {
            _input.ResetSuggestions();
            if (LanguageId is { } mode && mode != DictionaryTranslationDirection.GetMode(settings.Current))
                settings.Update(settings.Current with { DictionaryTargetMode = mode });
        };
        AutomationProperties.SetName(_language, "翻译方向");
        var systemLanguage = DictionaryTranslationDirection.ReaderLanguage();
        var alternate = systemLanguage.Id == "en" ? "简体中文" : "英语";
        ToolTip.SetTip(_language, $"自动模式下，外语译为{systemLanguage.ChineseName}，{systemLanguage.ChineseName}译为{alternate}");
        _language.HorizontalAlignment = HorizontalAlignment.Left;
        _send = new Button
        {
            Classes = { "primary", "send" }, Content = _sendIcon,
            Width = 34, Height = 34, Padding = default, BorderThickness = default,
            CornerRadius = new CornerRadius(AppTheme.Radius.Pill), Background = AppTheme.BrandGradient,
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        _send.Click += (_, _) =>
        {
            if (_activity != ComposerActivity.Ready && !DictionaryQuery.HasContent(_input.Text)) stop();
            else Send();
            _input.Focus();
        };
        _input.TextChanged += (_, _) => UpdateAction();
        _input.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Enter || e.KeyModifiers is not (KeyModifiers.None or KeyModifiers.Control or KeyModifiers.Meta)) return;
            var presenter = _input.GetVisualDescendants().OfType<TextPresenter>().FirstOrDefault();
            if (!string.IsNullOrEmpty(presenter?.PreeditText)) return;
            e.Handled = true;
            Send();
        }, RoutingStrategies.Tunnel);

        void Send()
        {
            if (!IsEffectivelyEnabled) return;
            var query = _input.Text ?? "";
            if (!DictionaryQuery.HasContent(query)) return;
            var cacheOnly = query == _input.AcceptedSuggestion;
            _input.Clear();
            submit(query, cacheOnly);
        }

        _hint.VerticalAlignment = VerticalAlignment.Center;
        _input.GotFocus += (_, _) => _hint.Opacity = 1;
        _input.LostFocus += (_, _) => _hint.Opacity = 0.6;
        _hint.Opacity = 0.6;
        var lead = profile?.Lead ?? _language;
        var footer = new Grid
        {
            Margin = AppTheme.ScrollContentInset,
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = AppTheme.Space.M,
            Children = { lead, _hint, _send }
        };
        Grid.SetColumn(_hint, 1);
        Grid.SetColumn(_send, 2);
        var card = StudioUi.CreateBentoCard(new StackPanel
        {
            Spacing = AppTheme.Space.S, Children = { _input, footer }
        }, padding: new Thickness(0, AppTheme.Space.M));
        card.ClearValue(Border.BorderBrushProperty);
        card.BoxShadow = default;
        SizeChanged += (_, e) => _hint.IsVisible = e.NewSize.Width >= 640;
        card.Classes.Add("chat-composer");
        // Keep the shadow outside the rounded clip so it can fade into the page on every side.
        Children.Add(new Border { CornerRadius = card.CornerRadius, BoxShadow = AppTheme.CardShadow, IsHitTestVisible = false });
        var surface = new Grid { Children = { card, _flow } };
        surface.SizeChanged += (_, e) => surface.Clip = new RectangleGeometry(new Rect(e.NewSize),
            card.CornerRadius.TopLeft, card.CornerRadius.TopLeft);
        Children.Add(surface);
        SetActivity(ComposerActivity.Ready);
    }

    public void SetActivity(ComposerActivity activity, string? progress = null)
    {
        _activity = activity;
        _flow.IsVisible = activity != ComposerActivity.Ready;
        _input.PlaceholderText = progress ?? _prompt;
        UpdateAction();
    }

    private void UpdateAction()
    {
        var hasText = DictionaryQuery.HasContent(_input.Text);
        var stop = _activity != ComposerActivity.Ready && !hasText;
        _sendIcon.IconName = stop ? "stop" : "arrow-up";
        _sendIcon.IconSize = stop ? 16 : 20;
        var label = !stop ? _sendLabel : _activity == ComposerActivity.Reviewing ? "停止复核" : "停止生成";
        AutomationProperties.SetName(_send, label);
        ToolTip.SetTip(_send, stop ? label : $"{_sendLabel} (Enter) · Shift+Enter 换行");
        _send.IsEnabled = stop || hasText;
        _sendIcon.Foreground = _send.IsEnabled ? AppTheme.CardBg : AppTheme.TextMuted;
    }

    private static Control ChipLabel(string icon, string? text) => new StackPanel
    {
        Orientation = Orientation.Horizontal, Spacing = AppTheme.Space.XS,
        Children =
        {
            new FontAwesomeIcon(icon, 14, AppTheme.TextSecondary) { VerticalAlignment = VerticalAlignment.Center },
            new TextBlock
            {
                Text = text, FontSize = AppTheme.Type.Small, Foreground = AppTheme.TextPrimary,
                VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis
            }
        }
    };

    private static TextBlock Label(string? text) => new()
    {
        Text = text, FontSize = AppTheme.Type.Small, Foreground = AppTheme.TextPrimary,
        TextTrimming = TextTrimming.CharacterEllipsis
    };
}
