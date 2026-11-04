using AiTranslate.Ai;
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

internal enum ComposerActivity { Ready, Translating, ReviewingBlock }

internal sealed class DictionaryComposer : Grid
{
    private const string InputPrompt = "输入要翻译的单词、短语或段落…";
    private readonly TextBlock _hint = TextStyles.Create(null, TextRole.Meta, wrap: false);
    private readonly TextBox _input = new()
    {
        Classes = { "chat-input" },
        PlaceholderText = InputPrompt,
        AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
        MinHeight = 48, MaxHeight = 168,
        FontSize = AppTheme.Type.BodyLarge, Foreground = AppTheme.TextPrimary,
        Background = AppTheme.CardBg, BorderThickness = default,
        Padding = new Thickness(AppTheme.Space.XXS, AppTheme.Space.XS)
    };
    private readonly ComboBox _language;
    private readonly Button _send;
    private readonly MdiIcon _sendIcon = new("arrow-up", 20, AppTheme.CardBg);
    private readonly FlowingBorderEffect _flow = new();
    private bool _generating;

    public string? LanguageId => (_language.SelectedItem as TranslationLanguage)?.Id;
    public void FocusInput() => _input.Focus();

    public DictionaryComposer(AppSettingsStore settings, Action<string> submit, Action stop)
    {
        AutomationProperties.SetName(_input, "待翻译文本");
        var initialMode = DictionaryTranslationDirection.GetMode(settings.Current);
        _language = new ComboBox
        {
            Classes = { "chip" }, MinWidth = 120,
            ItemsSource = DictionaryTranslationDirection.Modes,
            ItemTemplate = new FuncDataTemplate<TranslationLanguage>((item, _) => Label(item?.DisplayName)),
            SelectionBoxItemTemplate = new FuncDataTemplate<TranslationLanguage>((item, _) => ChipLabel("translate", item?.DisplayName)),
            SelectedItem = DictionaryTranslationDirection.Modes.First(m => m.Id == initialMode)
        };
        _language.SelectionChanged += (_, _) =>
        {
            if (LanguageId is { } mode && mode != DictionaryTranslationDirection.GetMode(settings.Current))
                settings.Update(settings.Current with { DictionaryTargetMode = mode });
        };
        AutomationProperties.SetName(_language, "翻译方向");
        ToolTip.SetTip(_language, "翻译方向 · 自动模式下，中文译为英语，英文译为简体中文");
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
            if (_generating && string.IsNullOrWhiteSpace(_input.Text)) stop();
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
            if (!IsEffectivelyEnabled || string.IsNullOrWhiteSpace(_input.Text)) return;
            var query = _input.Text.Trim();
            _input.Clear();
            submit(query);
        }

        _hint.VerticalAlignment = VerticalAlignment.Center;
        _input.GotFocus += (_, _) => _hint.Opacity = 1;
        _input.LostFocus += (_, _) => _hint.Opacity = 0.6;
        _hint.Opacity = 0.6;
        var footer = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = AppTheme.Space.M,
            Children = { _language, _hint, _send }
        };
        Grid.SetColumn(_hint, 1);
        Grid.SetColumn(_send, 2);
        var card = StudioUi.CreateBentoCard(new StackPanel
        {
            Spacing = AppTheme.Space.S, Children = { _input, footer }
        }, padding: new Thickness(AppTheme.Space.L, AppTheme.Space.M, AppTheme.Space.M, AppTheme.Space.M));
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

    public void SetActivity(ComposerActivity activity)
    {
        _generating = activity == ComposerActivity.Translating;
        _flow.IsVisible = _generating;
        IsEnabled = activity != ComposerActivity.ReviewingBlock;
        _input.PlaceholderText = IsEnabled ? InputPrompt : "栏目复核中…";
        _hint.Text = IsEnabled ? "Enter 发送  ·  Shift + Enter 换行" : "复核完成后可继续输入";
        UpdateAction();
    }

    private void UpdateAction()
    {
        var hasText = !string.IsNullOrWhiteSpace(_input.Text);
        var stop = _generating && !hasText;
        _sendIcon.IconName = stop ? "stop" : "arrow-up";
        var label = stop ? "停止生成" : "发送翻译";
        AutomationProperties.SetName(_send, label);
        ToolTip.SetTip(_send, stop ? label : "发送翻译 (Enter) · Shift+Enter 换行");
        _send.IsEnabled = _generating || hasText;
        _sendIcon.Foreground = _send.IsEnabled ? AppTheme.CardBg : AppTheme.TextMuted;
    }

    private static Control ChipLabel(string icon, string? text) => new StackPanel
    {
        Orientation = Orientation.Horizontal, Spacing = AppTheme.Space.XS,
        Children =
        {
            new MdiIcon(icon, 14, AppTheme.TextSecondary) { VerticalAlignment = VerticalAlignment.Center },
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
