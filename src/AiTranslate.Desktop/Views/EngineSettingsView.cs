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
    private readonly CheckBox _autoAddWordCheck;

    private readonly ComboBox _usVoiceCombo;
    private readonly ComboBox _ukVoiceCombo;
    private readonly CheckBox _autoPronounceCheck;
    private readonly TextBlock _kokoroEngineStatusText;

    public EngineSettingsView(AppSettingsStore settingsStore, ISpeechSynthesisService speechService, Action<string> setStatus)
    {
        _settingsStore = settingsStore;
        _speechService = speechService;
        _setStatus = setStatus;

        var current = _settingsStore.Current;

        _providerCombo = new ComboBox
        {
            Width = 380,
            CornerRadius = new CornerRadius(8),
            ItemsSource = AiProviders.All,
            SelectedItem = AiProviders.Get(current.ProviderId)
        };

        _modelIdBox = new TextBox
        {
            Text = current.ModelId,
            PlaceholderText = "例如 deepseek-chat / gpt-4o-mini / qwen2.5:7b",
            Width = 380,
            Background = AppTheme.InputBg,
            Foreground = AppTheme.TextPrimary,
            CornerRadius = new CornerRadius(8)
        };

        _baseUrlBox = new TextBox
        {
            Text = current.CustomBaseUrl ?? AiProviders.Get(current.ProviderId).BaseUrl,
            PlaceholderText = "https://api.deepseek.com/",
            Width = 460,
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
            PlaceholderText = "填入你的 DeepSeek / OpenAI / 硅基流动 API Key（本地 Ollama 无需填写）",
            Width = 460,
            Background = AppTheme.InputBg,
            Foreground = AppTheme.TextPrimary,
            CornerRadius = new CornerRadius(8)
        };

        _autoAddWordCheck = new CheckBox
        {
            Content = "灵动划词查询单个生词时，自动将其加入默认生词本",
            IsChecked = current.AutoAddLookedUpWordsToNotebook,
            Foreground = AppTheme.TextPrimary
        };

        var usVoices = _speechService.GetAvailableVoices(SpeechAccent.AmericanEnglish);
        _usVoiceCombo = new ComboBox
        {
            Width = 380,
            CornerRadius = new CornerRadius(8),
            ItemsSource = usVoices,
            SelectedItem = usVoices.FirstOrDefault(v => v.VoiceId == current.PreferredUsVoiceId) ?? usVoices[0]
        };

        var ukVoices = _speechService.GetAvailableVoices(SpeechAccent.BritishEnglish);
        _ukVoiceCombo = new ComboBox
        {
            Width = 380,
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

        _kokoroEngineStatusText = new TextBlock
        {
            Text = _speechService.EngineStatus,
            FontSize = 12.5,
            Foreground = AppTheme.Emerald,
            TextWrapping = TextWrapping.Wrap
        };
        _speechService.StatusChanged += (_, s) => Avalonia.Threading.Dispatcher.UIThread.Post(() => _kokoroEngineStatusText.Text = s);

        var saveBtn = StudioUi.CreatePrimaryButton("content-save", "保存 AI 与 Kokoro 语音配置", () =>
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
                AutoAddLookedUpWordsToNotebook = _autoAddWordCheck.IsChecked == true,
                PreferredUsVoiceId = usVoice,
                PreferredUkVoiceId = ukVoice,
                AutoPronounceOnLookup = _autoPronounceCheck.IsChecked == true
            };
            _settingsStore.Update(updated);
            _setStatus($"已保存设置：AI [{provider.Name}] · 美音 [{usVoice}] · 英音 [{ukVoice}]");
        });

        var initKokoroBtn = StudioUi.CreateSecondaryButton("waveform", "初始化/加载 Kokoro-82M 离线模型", () =>
        {
            _ = _speechService.EnsureKokoroModelReadyAsync();
        });

        var testPhonicsBtn = StudioUi.CreateSecondaryButton("volume-high", "试听自然拼读拆解 (Serendipity)", () =>
        {
            _ = _speechService.SpeakPhonicsBreakdownAsync("serendipity", "/ˌser.ənˈdɪp.ə.ti/", SpeechAccent.AmericanEnglish);
        });

        var aiConfigCard = StudioUi.CreateBentoCard(new StackPanel
        {
            Spacing = 12,
            Children =
            {
                StudioUi.CreateSectionHeader("sparkles", "全量 AI 词典与原位翻译引擎配置", "All-AI Translation & Lexicon Engine", AppTheme.AccentBright),
                new TextBlock { Text = "AI 模型供应商：", Foreground = AppTheme.TextSecondary, FontSize = 13 },
                _providerCombo,
                new TextBlock { Text = "API 基础端点 (Base URL)：", Foreground = AppTheme.TextSecondary, FontSize = 13 },
                _baseUrlBox,
                new TextBlock { Text = "模型标识 (Model ID)：", Foreground = AppTheme.TextSecondary, FontSize = 13 },
                _modelIdBox,
                new TextBlock { Text = "API Key 密钥：", Foreground = AppTheme.TextSecondary, FontSize = 13 },
                _apiKeyBox,
                _autoAddWordCheck,
                saveBtn
            }
        });

        var kokoroCard = StudioUi.CreateBentoCard(new StackPanel
        {
            Spacing = 10,
            Children =
            {
                StudioUi.CreateSectionHeader("account-voice", "KokoroSharp 神经网络语音与自然拼读引擎", "Kokoro-82M Neural TTS & IPA Phonics", AppTheme.Amber),
                _kokoroEngineStatusText,
                new TextBlock { Text = "首选美式发音人 (American English Speaker)：", Foreground = AppTheme.TextSecondary, FontSize = 13 },
                _usVoiceCombo,
                new TextBlock { Text = "首选英式发音人 (British RP Speaker)：", Foreground = AppTheme.TextSecondary, FontSize = 13 },
                _ukVoiceCombo,
                _autoPronounceCheck,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 10,
                    Children = { initKokoroBtn, testPhonicsBtn }
                }
            }
        });

        var hotkeysCard = StudioUi.CreateBentoCard(new StackPanel
        {
            Spacing = 10,
            Children =
            {
                StudioUi.CreateSectionHeader("keyboard-outline", "跨平台热键与系统托盘常驻说明", "Windows / macOS / Linux & System Tray", AppTheme.TextSecondary),
                StudioUi.CreateInfoTile("呼出 / 激活主工作台窗口", current.ShowWindowHotkey.ToString(), "translate", AppTheme.AccentBright),
                StudioUi.CreateInfoTile("全局划词速译 (自动提取选中文本并无感还原原剪贴板)", current.SelectionTranslateHotkey.ToString(), "cursor-text", AppTheme.AccentBright),
                StudioUi.CreateInfoTile("XCapture 引擎截图原位翻译 (RapidOCR + 原位擦除排版重绘)", current.ScreenshotTranslateHotkey.ToString(), "crop-free", AppTheme.AccentBright),
                StudioUi.CreateInfoTile("立即读取并精翻剪贴板", current.ClipboardTranslateHotkey.ToString(), "clipboard-text-outline", AppTheme.AccentBright)
            }
        });

        var rightCol = new StackPanel { Spacing = 18, Children = { kokoroCard, hotkeysCard } };

        var layoutGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("1.05*,0.95*"),
            ColumnSpacing = 18,
            Margin = new Thickness(24, 20),
            Children = { aiConfigCard, rightCol }
        };
        Grid.SetColumn(rightCol, 1);

        Content = new ScrollViewer { Content = layoutGrid };
    }
}
