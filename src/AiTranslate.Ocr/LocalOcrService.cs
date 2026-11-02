using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using AiTranslate.Core;
using Microsoft.ML.OnnxRuntime;
using RapidOcrNet;
using SkiaSharp;
using OcrResult = AiTranslate.Core.OcrResult;

namespace AiTranslate.Ocr;

/// <summary>
/// Offline ONNX detection/recognition pipeline ported from XCapture.
/// Models are bundled; screenshots stay in memory; sessions are reused and observe cancellation.
/// </summary>
public sealed class LocalOcrService : IOcrProgressService
{
    private readonly string _modelsDirectory;
    private readonly SemaphoreSlim _gate = new(1);
    private readonly object _state = new();
    private readonly CancellationTokenSource _lifetime = new();
    private TextDetector? _detector;
    private TextRecognizer? _recognizer;
    private bool _disposed;

    public LocalOcrService() : this(Path.Combine(AppContext.BaseDirectory, "OcrModels")) { }
    public LocalOcrService(string modelsDirectory) => _modelsDirectory = Path.GetFullPath(modelsDirectory);

    public Task<OcrResult> RecognizeAsync(byte[] png, CancellationToken cancellationToken = default) =>
        RecognizeRequestAsync(png, null, cancellationToken);

    public Task<OcrResult> RecognizeAsync(byte[] png, IProgress<OcrProgress> progress, CancellationToken cancellationToken = default) =>
        RecognizeRequestAsync(png, progress, cancellationToken);

    private async Task<OcrResult> RecognizeRequestAsync(byte[] png, IProgress<OcrProgress>? progress, CancellationToken cancellationToken)
    {
        CancellationTokenSource request;
        lock (_state)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        }
        using (request)
        {
            var token = request.Token;
            try
            {
                token.ThrowIfCancellationRequested();
                var size = ValidatePng(png);
                request.CancelAfter(TimeSpan.FromSeconds(45 * OcrLayout.Tiles(size.Width, size.Height).Count));
                await _gate.WaitAsync(token).ConfigureAwait(false);
                var work = Task.Run(() =>
                {
                    try
                    {
                        token.ThrowIfCancellationRequested();
                        return Recognize(png, progress, token);
                    }
                    finally { _gate.Release(); }
                });
                _ = work.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                return await work.WaitAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_lifetime.IsCancellationRequested)
            {
                throw new TimeoutException("文字识别超时，请缩小截图区域后重试。");
            }
        }
    }

    private void EnsureModels(CancellationToken token)
    {
        if (_detector != null) return;
        VerifyModel("det.onnx", "a431985659dc921974177a95adcfbb90fd9e51989a5e04d70d0b75f597b6e61d");
        VerifyModel("rec.onnx", "da72dc72ca4dc220df0dfde68c1dedc31c58d3e76a25871122e5056227d50092");
        VerifyModel("keys.txt", "d1979e9f794c464c0d2e0b70a7fe14dd978e9dc644c0e71f14158cdf8342af1b");
        token.ThrowIfCancellationRequested();
        using var options = new SessionOptions
        {
            IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4),
            InterOpNumThreads = 1,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
        };
        options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
        options.AddSessionConfigEntry("session.inter_op.allow_spinning", "0");
        var detector = new TextDetector();
        var recognizer = new TextRecognizer();
        try
        {
            detector.InitModel(Path.Combine(_modelsDirectory, "det.onnx"), options);
            token.ThrowIfCancellationRequested();
            options.IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 4, 1, 2);
            recognizer.InitModel(Path.Combine(_modelsDirectory, "rec.onnx"), Path.Combine(_modelsDirectory, "keys.txt"), options);
            token.ThrowIfCancellationRequested();
            _detector = detector; _recognizer = recognizer;
        }
        catch
        {
            detector.Dispose(); recognizer.Dispose();
            throw;
        }
    }

    private void VerifyModel(string file, string sha256)
    {
        var path = Path.Combine(_modelsDirectory, file);
        if (!File.Exists(path)) throw new InvalidOperationException("本地文字识别模型缺失，请确认 OcrModels 目录完整。");
        using var stream = File.OpenRead(path);
        if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("本地文字识别模型校验失败。");
    }

    private OcrResult Recognize(byte[] png, IProgress<OcrProgress>? progress, CancellationToken token)
    {
        using var data = SKData.CreateCopy(png);
        using var codec = SKCodec.Create(data) ?? throw new InvalidOperationException("无法读取截图，请重新截取后重试。");
        using var image = SKBitmap.Decode(codec) ?? throw new InvalidOperationException("无法读取截图，请重新截取后重试。");
        if (IsUniform(image, token)) return new OcrResult("", [], image.Width, image.Height);
        progress?.Report(new OcrProgress(OcrStage.Preparing, 0, 0));
        EnsureModels(token);
        var boxes = new List<DetectedBox>();
        var tiles = OcrLayout.Tiles(image.Width, image.Height);
        for (var i = 0; i < tiles.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            progress?.Report(new OcrProgress(OcrStage.Detecting, i, tiles.Count));
            var tile = tiles[i];
            using var subset = new SKBitmap();
            if (!image.ExtractSubset(subset, tile)) throw new InvalidOperationException("无法读取截图区域。");
            if (IsUniform(subset, token)) continue;
            const int padding = 24;
            using var padded = OcrUtils.MakePadding(subset, padding);
            var ratio = Math.Min(1.5, 960d / Math.Max(padded.Width, padded.Height));
            int Round32(double value) => Math.Max(32, (int)Math.Round(value / 32) * 32);
            var scale = new ScaleParam(padded.Width, padded.Height, Round32(padded.Width * ratio), Round32(padded.Height * ratio));
            var detected = _detector!.GetTextBoxes(padded, scale, .6f, .3f, 1.5f, token)
                ?? throw new InvalidOperationException("文字区域检测失败，请重试。");
            foreach (var box in detected)
            {
                var points = box.BoxPoints.Select(p => new SKPointI(
                    Math.Clamp(p.X - padding + tile.Left, 0, image.Width),
                    Math.Clamp(p.Y - padding + tile.Top, 0, image.Height))).ToArray();
                var bounds = OcrLayout.Bounds(points);
                if (bounds.Width < 3 || bounds.Height < 3) continue;
                boxes.Add(new DetectedBox(points, box.Score, i));
            }
        }
        token.ThrowIfCancellationRequested();
        var merged = OcrLayout.JoinRowFragments(OcrLayout.MergeTiles(boxes, token), token);
        var lines = new OcrLine?[merged.Count];
        var completed = 0;
        var progressLock = new object();
        progress?.Report(new OcrProgress(OcrStage.Recognizing, 0, merged.Count));
        Parallel.For(0, merged.Count, new ParallelOptions
        {
            MaxDegreeOfParallelism = merged.Count >= 8 && Environment.ProcessorCount >= 4 ? 2 : 1,
            CancellationToken = token,
        }, index =>
        {
            try
            {
                var box = merged[index];
                token.ThrowIfCancellationRequested();
                using var crop = OcrUtils.GetRotateCropImage(image, box.Points, out var context);
                if (crop.Width < 2 || crop.Height < 2) return;
                using var bounded = crop.Width * 48L > crop.Height * 4096L
                    ? crop.Resize(new SKSizeI(4096, 48), OcrUtils.NetworkSampling) : null;
                var text = _recognizer!.GetTextLine(bounded ?? crop, token);
                if (text.Chars == null || text.CharScores is not { Length: > 0 } || text.CharScores.Average() < .5f) return;
                var value = string.Concat(text.Chars).Trim();
                if (value.Length > 0)
                {
                    var words = CalRecBoxes.Build(text, context, cls180: false, returnSingleCharBox: true);
                    var imageBounds = new RectD(0, 0, image.Width, image.Height);
                    lines[index] = new OcrLine(value, box.Bounds)
                    {
                        Confidence = text.CharScores.Average(),
                        Words = words?.Select(word => new OcrWord(word.Text,
                            OcrLayout.Bounds(word.BoxPoints).Intersect(imageBounds), word.Score)).ToArray() ?? [],
                    };
                }
            }
            finally
            {
                lock (progressLock)
                    progress?.Report(new OcrProgress(OcrStage.Recognizing, ++completed, merged.Count));
            }
        });
        token.ThrowIfCancellationRequested();
        var rows = OcrLayout.ReadingRows(lines.OfType<OcrLine>());
        var ordered = rows.SelectMany(row => row).ToArray();
        return new OcrResult(OcrLayout.ComposeText(rows), ordered, image.Width, image.Height);
    }

    private static SKSizeI ValidatePng(byte[] png)
    {
        ArgumentNullException.ThrowIfNull(png);
        if (png.Length is < 24 or > 67_108_864 || !png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            throw new ArgumentException("文字识别需要有效的 PNG 截图（最大 64 MB）。", nameof(png));
        var width = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16, 4));
        var height = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20, 4));
        if (width is 0 or > 8192 || height is 0 or > 8192 || (long)width * height > 16_777_216)
            throw new ArgumentException("截图尺寸过大，请缩小区域后提取文字。", nameof(png));
        return new SKSizeI((int)width, (int)height);
    }

    private static bool IsUniform(SKBitmap image, CancellationToken token)
    {
        if (image.ColorType is not (SKColorType.Bgra8888 or SKColorType.Rgba8888)) return false;
        var pixels = MemoryMarshal.Cast<byte, uint>(image.GetPixelSpan());
        if (pixels.IsEmpty) return true;
        var first = pixels[0];
        for (var i = 1; i < pixels.Length; i++)
        {
            if ((i & 65535) == 0) token.ThrowIfCancellationRequested();
            if (pixels[i] != first) return false;
        }
        return true;
    }

    public void Dispose()
    {
        lock (_state)
        {
            if (_disposed) return;
            _disposed = true;
            _lifetime.Cancel();
        }
        _ = DisposeWhenIdleAsync();
    }

    private async Task DisposeWhenIdleAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { _recognizer?.Dispose(); _detector?.Dispose(); }
        finally { _gate.Release(); }
    }
}
