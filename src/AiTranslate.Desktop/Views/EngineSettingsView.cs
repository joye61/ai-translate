using AiTranslate.Ai;
using AiTranslate.Desktop.Controls;
using AiTranslate.Speech;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace AiTranslate.Desktop.Views;

/// <summary>
/// AI Provider, KokoroSharp Neural Speech Studio & Global Hotkey Configuration View.
/// </summary>
public sealed class EngineSettingsView : UserControl
{
    private readonly AppSettingsStore _settingsStore;
    private readonly ISpeechSynthesisService _speechService;
    private readonly Action<string> _setStatus;

    private readonly ComboBox _providerCombo;
    private readonly TextBox _modelIdBox;
    private readonly TextBox _baseUrlBox;
    private readonly TextBox _apiKeyBox;

    private readonly ComboBox _usVoiceCombo;
    private readonly ComboBox _ukVoiceCombo;
    private readonly CheckBox _autoPronounceCheck;
    internal Control Toolbar { get; }

    public EngineSettingsView(AppSettingsStore settingsStore, ISpeechSynthesisService speechService, Action<string> setStatus)
    {
        _settingsStore = settingsStore;
        _speechService = speechService;
        _setStatus = setStatus;

        var current = _settingsStore.Current;

        _providerCombo = new ComboBox
        {
            Width = 248,
            CornerRadius = new CornerRadius(8),
            ItemsSource = AiProviders.All,
            SelectedItem = AiProviders.Get(current.ProviderId)
        };
        Toolbar = StudioUi.CreateToolbarField("模型服务商", _providerCombo);

        _modelIdBox = new TextBox
        {
            Text = current.ModelId,
            PlaceholderText = "例如 deepseek-chat / gpt-4o-mini / qwen2.5:7b",
            Background = AppTheme.InputBg,
            Foreground = AppTheme.TextPrimary,
            CornerRadius = new CornerRadius(8)
        };

        _baseUrlBox = new TextBox
        {
            Text = current.CustomBaseUrl ?? AiProviders.Get(current.ProviderId).BaseUrl,
            PlaceholderText = "https://api.deepseek.com/",
            Background = AppTheme.InputBg,
            Foreground = AppTheme.TextPrimary,
            CornerRadius = new CornerRadius(8)
        };

        _providerCombo.SelectionChanged += (_, _) =>
        {
            if (_providerCombo.SelectedItem is AiProviderInfo p)
            {
                _baseUrlBox.Text = p.BaseUrl;
                if (p.DefaultModels.Count > 0)
                    _modelIdBox.Text = p.DefaultModels[0].Id;
            }
        };

        _apiKeyBox = new TextBox
        {
            Text = current.ApiKey,
            PasswordChar = '•',
            PlaceholderText = "输入 API Key（本地 Ollama 可留空）",
            Background = AppTheme.InputBg,
            Foreground = AppTheme.TextPrimary,
            CornerRadius = new CornerRadius(8)
        };

        var usVoices = _speechService.GetAvailableVoices(SpeechAccent.AmericanEnglish);
        _usVoiceCombo = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            CornerRadius = new CornerRadius(8),
            ItemsSource = usVoices,
            SelectedItem = usVoices.FirstOrDefault(v => v.VoiceId == current.PreferredUsVoiceId) ?? usVoices[0]
        };

        var ukVoices = _speechService.GetAvailableVoices(SpeechAccent.BritishEnglish);
        _ukVoiceCombo = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            CornerRadius = new CornerRadius(8),
            ItemsSource = ukVoices,
            SelectedItem = ukVoices.FirstOrDefault(v => v.VoiceId == current.PreferredUkVoiceId) ?? ukVoices[0]
        };

        _autoPronounceCheck = new CheckBox
        {
            Content = "查词完成后自动朗读美音发音",
            IsChecked = current.AutoPronounceOnLookup,
            Foreground = AppTheme.TextPrimary
        };

        var speechStatus = TextStyles.Create(_speechService.EngineStatus, TextRole.Secondary);

        var saveBtn = StudioUi.CreatePrimaryButton("floppy-disk", "保存翻译与语音设置", () =>
        {
            var provider = (_providerCombo.SelectedItem as AiProviderInfo) ?? AiProviders.All[0];
            var usVoice = (_usVoiceCombo.SelectedItem as KokoroSpeakerProfile)?.VoiceId ?? "af_heart";
            var ukVoice = (_ukVoiceCombo.SelectedItem as KokoroSpeakerProfile)?.VoiceId ?? "bf_emma";
            var updated = _settingsStore.Current with
            {
                ProviderId = provider.Id,
                ModelId = string.IsNullOrWhiteSpace(_modelIdBox.Text) ? "deepseek-chat" : _modelIdBox.Text.Trim(),
                CustomBaseUrl = string.IsNullOrWhiteSpace(_baseUrlBox.Text) ? provider.BaseUrl : _baseUrlBox.Text.Trim(),
                ApiKey = _apiKeyBox.Text?.Trim() ?? "",
                PreferredUsVoiceId = usVoice,
                PreferredUkVoiceId = ukVoice,
                AutoPronounceOnLookup = _autoPronounceCheck.IsChecked == true
            };
            _settingsStore.Update(updated);
            _setStatus($"已保存设置：AI [{provider.Name}] · 美音 [{usVoice}] · 英音 [{ukVoice}]");
        });

        var loadVoice = StudioUi.CreateSecondaryButton("wave-square", "加载离线模型", () =>
        {
            _ = _speechService.EnsureKokoroModelReadyAsync();
        });
        loadVoice.IsVisible = !_speechService.IsKokoroModelLoaded;
        _speechService.StatusChanged += (_, status) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            speechStatus.Text = status;
            loadVoice.IsVisible = !_speechService.IsKokoroModelLoaded;
        });

        var testPhonicsBtn = SpeechPlaybackButton.Create(click => StudioUi.CreateSecondaryButton("volume-high", "试听自然拼读", click),
            playback => _speechService.SpeakPhonicsBreakdownAsync("serendipity", "/ˌser.ənˈdɪp.ə.ti/", playback: playback));

        var aiConfigCard = StudioUi.CreateBentoCard(new StackPanel
        {
            Spacing = AppTheme.Space.M,
            Children =
            {
                StudioUi.CreateSectionHeader("wand-magic-sparkles", "翻译引擎", null, AppTheme.Hues.Coral),
                Field("接口地址 Base URL", _baseUrlBox),
                Field("模型 Model ID", _modelIdBox),
                Field("API Key", _apiKeyBox)
            }
        });

        var speechCard = StudioUi.CreateBentoCard(new StackPanel
        {
            Spacing = AppTheme.Space.M,
            Children =
            {
                StudioUi.CreateSectionHeader("microphone-lines", ISpeechSynthesisService.DisplayName, null, AppTheme.Hues.Violet),
                speechStatus,
                Field("美式发音人", _usVoiceCombo),
                Field("英式发音人", _ukVoiceCombo),
                _autoPronounceCheck,
                new WrapPanel
                {
                    ItemSpacing = AppTheme.Space.S,
                    LineSpacing = AppTheme.Space.S,
                    Children = { loadVoice, testPhonicsBtn }
                }
            }
        });

        var shortcuts = new AdaptiveColumns(2, 235)
        {
            ColumnSpacing = AppTheme.Space.XXL,
            RowSpacing = AppTheme.Space.XS
        };
        var hotkeys = new[]
        {
            ("language", "呼出工作台", current.ShowWindowHotkey.ToString()),
            ("i-cursor", "划词翻译", current.SelectionTranslateHotkey.ToString()),
            ("crop-simple", "截图翻译", current.ScreenshotTranslateHotkey.ToString()),
            ("paste", "剪贴板翻译", current.ClipboardTranslateHotkey.ToString())
        };
        foreach (var (icon, label, shortcut) in hotkeys) shortcuts.Children.Add(ShortcutRow(icon, label, shortcut));
        var hotkeysCard = StudioUi.CreateBentoCard(new StackPanel
        {
            Spacing = AppTheme.Space.M,
            Children =
            {
                StudioUi.CreateSectionHeader("regular/keyboard", "全局快捷键", null, AppTheme.Hues.Sky),
                shortcuts
            }
        });

        var editors = new AdaptiveColumns(2, 300)
        {
            ColumnSpacing = AppTheme.Space.L, RowSpacing = AppTheme.Space.L, Children = { aiConfigCard, speechCard }
        };
        saveBtn.HorizontalAlignment = HorizontalAlignment.Right;
        var sections = new StackPanel
        {
            Spacing = AppTheme.Space.M,
            Margin = new Thickness(AppTheme.Space.XXL, 0, AppTheme.Space.XXL, AppTheme.Space.XL),
            Children = { editors, saveBtn, hotkeysCard }
        };

        Content = new ScrollViewer { Content = sections };
    }

    private static StackPanel Field(string label, Control editor) => new()
    {
        Spacing = AppTheme.Space.XS,
        Children = { TextStyles.Create(label, TextRole.FieldLabel), editor }
    };

    private static Border ShortcutRow(string icon, string label, string shortcut)
    {
        var name = TextStyles.Create(label, TextRole.Body, wrap: false);
        name.VerticalAlignment = VerticalAlignment.Center;
        var keycap = StudioUi.CreateKeycap(shortcut, large: true);
        Grid.SetColumn(name, 1);
        Grid.SetColumn(keycap, 2);
        return new Border
        {
            Background = AppTheme.InputBg,
            CornerRadius = new CornerRadius(AppTheme.Radius.S),
            Padding = new Thickness(AppTheme.Space.M, AppTheme.Space.S),
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
                ColumnSpacing = AppTheme.Space.S + 2,
                Children = { new FontAwesomeIcon(icon, 16, AppTheme.TextSecondary) { VerticalAlignment = VerticalAlignment.Center }, name, keycap }
            }
        };
    }
}
