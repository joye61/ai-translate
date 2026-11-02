namespace AiTranslate.Core;

/// <summary>Offline recognition of an encoded screenshot. Coordinates use original image pixels.</summary>
public interface IOcrService : IDisposable
{
    Task<OcrResult> RecognizeAsync(byte[] png, CancellationToken cancellationToken = default);
}

public interface IOcrProgressService : IOcrService
{
    Task<OcrResult> RecognizeAsync(byte[] png, IProgress<OcrProgress> progress, CancellationToken cancellationToken = default);
}

public enum OcrStage { Preparing, Detecting, Recognizing }
public sealed record OcrProgress(OcrStage Stage, int Completed, int Total);

public sealed record OcrResult(string Text, IReadOnlyList<OcrLine> Lines, int Width, int Height);

public sealed record OcrLine(string Text, RectD Bounds)
{
    public double? Confidence { get; init; }
    /// <summary>CTC alignment estimates in original image pixels.</summary>
    public IReadOnlyList<OcrWord> Words { get; init; } = [];
}

public sealed record OcrWord(string Text, RectD Bounds, double Confidence);
