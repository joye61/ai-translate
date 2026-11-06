using System.Diagnostics;
using AiTranslate.Ai;
using AiTranslate.Core;
using AiTranslate.Ocr;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using SkiaSharp;

namespace AiTranslate.Desktop;

/// <summary>
/// Full-screen screenshot selection & in-place AI translation overlay window.
/// Ported from XCapture's overlay translation pipeline, enhanced with one-click Vocabulary Notebook ingestion.
/// </summary>
internal sealed class ScreenshotOverlayWindow : Window
{
    private readonly DisplayFrame _frame;
    private readonly LocalOcrService _ocr;
    private readonly AppSettingsStore _settings;
    private readonly Action<string, string?> _onOpenInDictionary;
    private readonly Action<string, string> _onQuickSaveToNotebook;
    private readonly Action _onClosed;

    private readonly Bitmap _screenBitmap;
    private readonly Border _dimMaskTop, _dimMaskBottom, _dimMaskLeft, _dimMaskRight;
    private readonly Border _selectionFrame;
    private readonly OcrScanEffect _scanEffect;
    private readonly Image _translatedImageView;
    private readonly Border _toolbar;
    private readonly ComboBox _languageCombo;
    private readonly Button _toggleOrigBtn;
    private readonly Button _retryBtn;
    private readonly Button _copyTextBtn;
    private readonly Button _addToNotebookBtn;
    private readonly Button _openDictBtn;
    private readonly Button _saveImgBtn;
    private readonly Button _copyImgBtn;
    private readonly TextBlock _statusText;

    private Point _dragStart;
    private Rect _selectionDip;
    private bool _isDragging;
    private bool _hasSelection;
    private bool _showingOriginal;
    private bool _busy;

    private CancellationTokenSource? _cts;
    private byte[]? _croppedPng;
    private OcrResult? _ocrResult;
    private IReadOnlyList<TranslationRegion> _regions = [];
    private TranslationImage? _composedImage;
    private string? _sourceText;
    private string? _translatedFullText;
    private string _targetLangId;
    private bool _syncingCombo;

    public ScreenshotOverlayWindow(
        DisplayFrame frame,
        LocalOcrService ocr,
        AppSettingsStore settings,
        Action<string, string?> onOpenInDictionary,
        Action<string, string> onQuickSaveToNotebook,
        Action onClosed)
    {
        _frame = frame;
        _ocr = ocr;
        _settings = settings;
        _onOpenInDictionary = onOpenInDictionary;
        _onQuickSaveToNotebook = onQuickSaveToNotebook;
        _onClosed = onClosed;
        _targetLangId = DictionaryTranslationDirection.GetMode(settings.Current);

        Title = "AI 截图原位翻译 (XCapture Engine)";
        WindowDecorations = WindowDecorations.None;
        Topmost = true;
        ShowInTaskbar = false;
        Background = Brushes.Transparent;
        Cursor = new Cursor(StandardCursorType.Cross);

        var scale = frame.Display.ScaleFactor > 0 ? frame.Display.ScaleFactor : 1.0;
        Position = new PixelPoint((int)frame.Display.Bounds.X, (int)frame.Display.Bounds.Y);
        Width = frame.Display.Bounds.Width / scale;
        Height = frame.Display.Bounds.Height / scale;

        _screenBitmap = CreateBitmapFromFrame(frame);
        var bgImage = new Image { Source = _screenBitmap, Stretch = Stretch.Fill, IsHitTestVisible = false };

        var maskBrush = AppTheme.Frozen(0x88060A10);
        _dimMaskTop = new Border { Background = maskBrush, IsHitTestVisible = false };
        _dimMaskBottom = new Border { Background = maskBrush, IsHitTestVisible = false };
        _dimMaskLeft = new Border { Background = maskBrush, IsHitTestVisible = false };
        _dimMaskRight = new Border { Background = maskBrush, IsHitTestVisible = false };

        _selectionFrame = new Border
        {
            BorderBrush = AppTheme.AccentBright,
            BorderThickness = new Thickness(2),
            Background = Brushes.Transparent,
            IsHitTestVisible = false,
            IsVisible = false
        };

        _scanEffect = new OcrScanEffect();
        _translatedImageView = new Image { Stretch = Stretch.Fill, IsHitTestVisible = false, IsVisible = false };


        _languageCombo = new ComboBox
        {
            Width = 150,
            Height = 32,
            CornerRadius = new CornerRadius(7),
            FontFamily = AppTheme.BodyFont,
            FontSize = 12.5,
            ItemsSource = DictionaryTranslationDirection.Modes,
            ItemTemplate = new FuncDataTemplate<TranslationLanguage>((lang, _) => new TextBlock
            {
                Text = lang?.DisplayName,
                FontFamily = AppTheme.BodyFont,
                FontSize = 12.5,
                VerticalAlignment = VerticalAlignment.Center
            }),
            SelectedItem = DictionaryTranslationDirection.Modes.First(m => m.Id == _targetLangId)
        };
        var systemLanguage = DictionaryTranslationDirection.ReaderLanguage();
        var alternate = systemLanguage.Id == "en" ? "简体中文" : "英语";
        ToolTip.SetTip(_languageCombo, $"自动模式下，外语译为{systemLanguage.ChineseName}，{systemLanguage.ChineseName}译为{alternate}");
        AutomationProperties.SetName(_languageCombo, "翻译方向");
        _languageCombo.SelectionChanged += (_, _) =>
        {
            if (_syncingCombo || _languageCombo.SelectedItem is not TranslationLanguage lang) return;
            if (lang.Id != DictionaryTranslationDirection.GetMode(settings.Current))
                settings.Update(settings.Current with { DictionaryTargetMode = lang.Id });
            _ = TranslateSelectionAsync(lang.Id);
        };

        _toggleOrigBtn = CreateToolbarButton("regular/image", "对比查看原图 / 译图", ToggleOriginalView);
        _retryBtn = CreateToolbarButton("arrows-rotate", "重新识别并翻译", () => _ = TranslateSelectionAsync(_targetLangId, forceRetry: true));
        _copyTextBtn = CreateToolbarButton("regular/copy", "复制完整译文文本", CopyTranslatedTextAsync);
        _addToNotebookBtn = CreateToolbarButton("regular/bookmark", "一键加入生词本 / 金句库", SaveToVocabularyNotebook);
        _openDictBtn = CreateToolbarButton("book-open", "在主词典工作台中深度精析", OpenInMainDictionary);
        _saveImgBtn = CreateToolbarButton("floppy-disk", "保存译文图片 (PNG)", () => _ = ExportTranslatedImageAsync(saveFile: true));
        _copyImgBtn = CreateToolbarButton("check", "复制译文图片到剪贴板并完成", () => _ = ExportTranslatedImageAsync(saveFile: false), isPrimary: true);
        var closeBtn = CreateToolbarButton("xmark", "关闭截图翻译 (Esc)", Close, isDanger: true);

        _statusText = new TextBlock
        {
            FontFamily = AppTheme.BodyFont,
            FontSize = 12,
            Foreground = AppTheme.TextSecondary,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 4, 8, 2)
        };

        var barRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Children =
            {
                new FontAwesomeIcon("language", 16, AppTheme.AccentBright) { Margin = new Thickness(6, 0, 2, 0) },
                _languageCombo,
                CreateSeparator(),
                _toggleOrigBtn,
                _retryBtn,
                _copyTextBtn,
                CreateSeparator(),
                _addToNotebookBtn,
                _openDictBtn,
                CreateSeparator(),
                _saveImgBtn,
                closeBtn,
                _copyImgBtn
            }
        };

        _toolbar = new Border
        {
            Background = AppTheme.Frozen(0xF7FFFFFF),
            BorderBrush = AppTheme.BorderSubtle,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(AppTheme.Radius.L),
            BoxShadow = AppTheme.PopupShadow,
            Padding = new Thickness(8, 6),
            IsVisible = false,
            Cursor = new Cursor(StandardCursorType.Arrow),
            Child = new StackPanel { Spacing = 2, Children = { barRow, _statusText } }
        };

        Content = new Canvas
        {
            Background = Brushes.Transparent,
            Children =
            {
                bgImage,
                _dimMaskTop,
                _dimMaskBottom,
                _dimMaskLeft,
                _dimMaskRight,
                _translatedImageView,
                _scanEffect,
                _selectionFrame,
                _toolbar
            }
        };

        SizeChanged += (_, _) =>
        {
            bgImage.Width = ClientSize.Width;
            bgImage.Height = ClientSize.Height;
            UpdateMaskAndSelectionLayout();
        };

        PointerPressed += OnOverlayPointerPressed;
        PointerMoved += OnOverlayPointerMoved;
        PointerReleased += OnOverlayPointerReleased;
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
    }

    protected override void OnClosed(EventArgs e)
    {
        _cts?.Cancel();
        _cts = null;
        _composedImage?.Dispose();
        _screenBitmap.Dispose();
        _onClosed();
        base.OnClosed(e);
    }

    private void OnOverlayPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_hasSelection || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _isDragging = true;
        _dragStart = e.GetPosition(this);
        _selectionDip = new Rect(_dragStart, new Size(0, 0));
        _selectionFrame.IsVisible = true;
        UpdateMaskAndSelectionLayout();
        e.Handled = true;
    }

    private void OnOverlayPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isDragging) return;
        var current = e.GetPosition(this);
        var x = Math.Clamp(Math.Min(_dragStart.X, current.X), 0, ClientSize.Width);
        var y = Math.Clamp(Math.Min(_dragStart.Y, current.Y), 0, ClientSize.Height);
        var r = Math.Clamp(Math.Max(_dragStart.X, current.X), 0, ClientSize.Width);
        var b = Math.Clamp(Math.Max(_dragStart.Y, current.Y), 0, ClientSize.Height);
        _selectionDip = new Rect(x, y, r - x, b - y);
        UpdateMaskAndSelectionLayout();
        e.Handled = true;
    }

    private void OnOverlayPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isDragging) return;
        _isDragging = false;
        if (_selectionDip.Width < 16 || _selectionDip.Height < 16)
        {
            _selectionFrame.IsVisible = false;
            _selectionDip = default;
            UpdateMaskAndSelectionLayout();
            return;
        }
        _hasSelection = true;
        Cursor = new Cursor(StandardCursorType.Arrow);
        _toolbar.IsVisible = true;
        UpdateMaskAndSelectionLayout();
        _ = TranslateSelectionAsync(_targetLangId);
        e.Handled = true;
    }

    private void UpdateMaskAndSelectionLayout()
    {
        var w = Math.Max(1, ClientSize.Width);
        var h = Math.Max(1, ClientSize.Height);
        if (!_selectionFrame.IsVisible || _selectionDip.Width <= 0 || _selectionDip.Height <= 0)
        {
            Canvas.SetLeft(_dimMaskTop, 0); Canvas.SetTop(_dimMaskTop, 0);
            _dimMaskTop.Width = w; _dimMaskTop.Height = h;
            _dimMaskBottom.Width = _dimMaskLeft.Width = _dimMaskRight.Width = 0;
            return;
        }

        var s = _selectionDip;
        Canvas.SetLeft(_dimMaskTop, 0); Canvas.SetTop(_dimMaskTop, 0);
        _dimMaskTop.Width = w; _dimMaskTop.Height = Math.Max(0, s.Y);
        Canvas.SetLeft(_dimMaskBottom, 0); Canvas.SetTop(_dimMaskBottom, s.Bottom);
        _dimMaskBottom.Width = w; _dimMaskBottom.Height = Math.Max(0, h - s.Bottom);
        Canvas.SetLeft(_dimMaskLeft, 0); Canvas.SetTop(_dimMaskLeft, s.Y);
        _dimMaskLeft.Width = Math.Max(0, s.X); _dimMaskLeft.Height = s.Height;
        Canvas.SetLeft(_dimMaskRight, s.Right); Canvas.SetTop(_dimMaskRight, s.Y);
        _dimMaskRight.Width = Math.Max(0, w - s.Right); _dimMaskRight.Height = s.Height;

        Canvas.SetLeft(_selectionFrame, s.X); Canvas.SetTop(_selectionFrame, s.Y);
        _selectionFrame.Width = s.Width; _selectionFrame.Height = s.Height;
        Canvas.SetLeft(_scanEffect, s.X); Canvas.SetTop(_scanEffect, s.Y);
        _scanEffect.Width = s.Width; _scanEffect.Height = s.Height;
        Canvas.SetLeft(_translatedImageView, s.X); Canvas.SetTop(_translatedImageView, s.Y);
        _translatedImageView.Width = s.Width; _translatedImageView.Height = s.Height;

        if (_toolbar.IsVisible)
        {
            _toolbar.Measure(new Size(w, double.PositiveInfinity));
            var tw = _toolbar.DesiredSize.Width;
            var th = _toolbar.DesiredSize.Height;
            var tx = Math.Clamp(s.Right - tw, 12, Math.Max(12, w - tw - 12));
            var ty = s.Bottom + 10;
            if (ty + th > h - 12)
                ty = s.Y - th - 10 >= 12 ? s.Y - th - 10 : Math.Max(12, h - th - 12);
            Canvas.SetLeft(_toolbar, tx);
            Canvas.SetTop(_toolbar, ty);
        }
    }

    private async Task TranslateSelectionAsync(string modeId, bool forceRetry = false)
    {
        if (!_hasSelection) return;
        _targetLangId = modeId;
        _syncingCombo = true;
        try { _languageCombo.SelectedItem = DictionaryTranslationDirection.Modes.First(m => m.Id == modeId); }
        finally { _syncingCombo = false; }

        if (forceRetry)
        {
            // "重新识别并翻译" promises a fresh OCR pass, not just a re-translation of stale regions.
            _ocrResult = null;
            _regions = [];
        }
        _cts?.Cancel();
        using var request = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        _cts = request;
        var token = request.Token;

        _busy = true;
        _showingOriginal = false;
        _translatedImageView.IsVisible = false;
        _scanEffect.Start(Stopwatch.GetTimestamp());
        UpdateToolbarState("正在使用 RapidOCR 离线识别选区文字…");

        try
        {
            var density = _frame.Display.PixelWidth / Math.Max(1.0, ClientSize.Width);
            _croppedPng ??= await Task.Run(() => CropSelectionPng(_frame, _selectionDip, density), token);

            if (_ocrResult is null)
            {
                var ocr = await _ocr.RecognizeAsync(_croppedPng, token);
                if (string.IsNullOrWhiteSpace(ocr.Text))
                    throw new AiException("选区中未识别到可翻译的文字，请按 Esc 重新框选。");
                var regions = await Task.Run(() =>
                    TranslationLayout.CreateRegions(ocr, TranslationTableLayout.FindCells(_croppedPng, ocr)), token);
                token.ThrowIfCancellationRequested();
                _ocrResult = ocr;
                _sourceText = ocr.Text;
                _regions = regions;
            }

            var sourceText = _sourceText ?? "";
            var languageId = await Task.Run(() => DictionaryTranslationDirection.ResolveTargetId(sourceText, modeId), token);
            var lang = TranslationLanguages.Get(languageId);
            UpdateToolbarState($"正在原位翻译为 {lang.DisplayName}…");
            var transport = _settings.CreateTransport() ?? throw new AiException("请在「应用设置」中配置 AI 引擎后再翻译。");
            using var ownedTransport = transport as IDisposable;
            var service = new AiTranslationService(transport);
            var regionTexts = await service.TranslateRegionsAsync(
                _regions.Select(r => new TranslationSourceRegion(r.Id, r.Text)).ToArray(), languageId, token);
            token.ThrowIfCancellationRequested();

            _composedImage?.Dispose();
            var rtl = TranslationLanguages.Get(languageId).IsRightToLeft;
            _composedImage = TranslationImage.Create(_croppedPng, _ocrResult, _regions, regionTexts, rtl, density);
            _translatedFullText = _composedImage.Text;
            _translatedImageView.Source = _composedImage.Bitmap;
            _translatedImageView.IsVisible = true;
            _scanEffect.Stop();
            _busy = false;
            UpdateToolbarState($"已完成原位翻译（共 {_regions.Count} 个排版区块）");
        }
        catch (OperationCanceledException)
        {
            if (_cts != request) return;
            _scanEffect.Stop();
            _busy = false;
            UpdateToolbarState("已取消或超时，请点击重试。", isError: true);
        }
        catch (Exception ex)
        {
            if (_cts != request) return;
            _scanEffect.Stop();
            _busy = false;
            UpdateToolbarState(ex.Message, isError: true);
        }
        finally
        {
            if (_cts == request) _cts = null;
        }
    }

    private void ToggleOriginalView()
    {
        if (_composedImage is null || _busy) return;
        _showingOriginal = !_showingOriginal;
        _translatedImageView.IsVisible = !_showingOriginal;
        UpdateToolbarState(_showingOriginal ? "正在对比查看原图" : "正在查看 AI 原位译图");
    }

    private async void CopyTranslatedTextAsync()
    {
        if (string.IsNullOrWhiteSpace(_translatedFullText) || Clipboard is null) return;
        await Clipboard.SetTextAsync(_translatedFullText);
        UpdateToolbarState("已复制完整译文到剪贴板");
    }

    private void SaveToVocabularyNotebook()
    {
        if (string.IsNullOrWhiteSpace(_sourceText) || string.IsNullOrWhiteSpace(_translatedFullText)) return;
        try { _onQuickSaveToNotebook(_sourceText.Trim(), _translatedFullText.Trim()); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            UpdateToolbarState($"收录失败。{ex.Message}");
            return;
        }
        UpdateToolbarState("已将截图原文与译文收录至「生词本」！");
    }

    private void OpenInMainDictionary()
    {
        if (string.IsNullOrWhiteSpace(_sourceText)) return;
        var text = _sourceText.Trim();
        Close();
        _onOpenInDictionary(text, "截图翻译识别");
    }

    private async Task ExportTranslatedImageAsync(bool saveFile)
    {
        if (_composedImage is null || _busy) return;
        try
        {
            var png = _composedImage.ExportPng();
            if (saveFile)
            {
                Topmost = false;
                var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "保存完整译文图片",
                    SuggestedFileName = $"AiTranslate_{DateTime.Now:yyyyMMdd_HHmmss}.png",
                    DefaultExtension = "png",
                    FileTypeChoices = [new("PNG 译文图片") { Patterns = ["*.png"] }],
                    ShowOverwritePrompt = true
                });
                Topmost = true;
                if (file is null) return;
                await using var stream = await file.OpenWriteAsync();
                if (stream.CanSeek) stream.SetLength(0);
                await stream.WriteAsync(png);
                UpdateToolbarState("完整译文图片已保存");
            }
            else
            {
                if (Clipboard is not null)
                {
                    using var ms = new MemoryStream(png);
                    using var bmp = new Bitmap(ms);
                    await Clipboard.SetBitmapAsync(bmp);
                }
                Close();
            }
        }
        catch (Exception ex)
        {
            UpdateToolbarState($"导出失败：{ex.Message}", isError: true);
        }
    }

    private void UpdateToolbarState(string status, bool isError = false)
    {
        var hasResult = _composedImage is not null && !_busy;
        _retryBtn.IsEnabled = !_busy;
        _toggleOrigBtn.IsEnabled = hasResult;
        _copyTextBtn.IsEnabled = hasResult;
        _addToNotebookBtn.IsEnabled = hasResult;
        _openDictBtn.IsEnabled = !string.IsNullOrWhiteSpace(_sourceText) && !_busy;
        _saveImgBtn.IsEnabled = hasResult;
        _copyImgBtn.IsEnabled = hasResult;
        _statusText.Text = status;
        _statusText.Foreground = isError ? AppTheme.Danger : AppTheme.TextSecondary;
        UpdateMaskAndSelectionLayout();
    }

    private static Bitmap CreateBitmapFromFrame(DisplayFrame frame)
    {
        using var skBmp = new SKBitmap(new SKImageInfo(frame.Display.PixelWidth, frame.Display.PixelHeight, SKColorType.Bgra8888, SKAlphaType.Premul));
        System.Runtime.InteropServices.Marshal.Copy(frame.Pixels, 0, skBmp.GetPixels(), frame.Pixels.Length);
        using var data = skBmp.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = new MemoryStream(data.ToArray());
        return new Bitmap(stream);
    }

    private static byte[] CropSelectionPng(DisplayFrame frame, Rect selectionDip, double density)
    {
        var x = Math.Clamp((int)Math.Round(selectionDip.X * density), 0, frame.Display.PixelWidth - 1);
        var y = Math.Clamp((int)Math.Round(selectionDip.Y * density), 0, frame.Display.PixelHeight - 1);
        var w = Math.Clamp((int)Math.Round(selectionDip.Width * density), 1, frame.Display.PixelWidth - x);
        var h = Math.Clamp((int)Math.Round(selectionDip.Height * density), 1, frame.Display.PixelHeight - y);

        using var full = new SKBitmap(new SKImageInfo(frame.Display.PixelWidth, frame.Display.PixelHeight, SKColorType.Bgra8888, SKAlphaType.Premul));
        System.Runtime.InteropServices.Marshal.Copy(frame.Pixels, 0, full.GetPixels(), frame.Pixels.Length);
        using var subset = new SKBitmap();
        if (!full.ExtractSubset(subset, new SKRectI(x, y, x + w, y + h)))
            throw new InvalidOperationException("无法裁剪所选屏幕区域。");
        using var encoded = subset.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    private static Button CreateToolbarButton(string icon, string tooltip, Action onClick, bool isPrimary = false, bool isDanger = false)
    {
        var fg = isPrimary ? Brushes.White : isDanger ? AppTheme.Danger : AppTheme.TextPrimary;
        var bg = isPrimary ? AppTheme.Accent : Brushes.Transparent;
        var btn = new Button
        {
            Classes = { isPrimary ? "primary" : "ghost" },
            Width = 34,
            Height = 32,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(AppTheme.Radius.S),
            Background = bg,
            BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Content = new FontAwesomeIcon(icon, 17, fg)
        };
        ToolTip.SetTip(btn, tooltip);
        AutomationProperties.SetName(btn, tooltip);
        btn.Click += (_, _) => onClick();
        return btn;
    }

    private static Border CreateSeparator() => new()
    {
        Width = 1,
        Height = 20,
        Margin = new Thickness(3, 0),
        Background = AppTheme.BorderSubtle,
        VerticalAlignment = VerticalAlignment.Center
    };
}
