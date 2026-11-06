namespace AiTranslate.Core;

/// <summary>Reading intent, independent of a model or desktop entry point.</summary>
public enum DictionaryLookupIntent { Learning, Quick }

public enum DictionaryStageState { NotRequested, Running, Completed, Failed, Unresolved, Invalidated, NotApplicable, InsufficientEvidence }

public sealed record DictionaryQualityIssue(string Field, string SourceQuote, string Reason, string Severity = "major");

public sealed record DictionaryStageUsage(string Role, int Requests, int InputTokens, int OutputTokens, long ElapsedMilliseconds);

public sealed record DictionarySectionCheck(EntryBlock Block, string ContentHash, bool Changed);

// A manual whole-entry review can span several columns and the head translation. Keep its
// cumulative patch scope so a later cache hit can replay a missed notebook write.
public sealed record DictionaryReviewSync
{
    public IReadOnlyList<string> KnownTranslations { get; init => field = value ?? []; } = [];
    public IReadOnlyList<string> ChangedHeadFields { get; init => field = value ?? []; } = [];
}

/// <summary>
/// Durable scope and outcome, not a promise of correctness. Legacy entries have no processing record.
/// Content hashes exclude this metadata. A later content edit invalidates the matching review.
/// </summary>
public sealed record DictionaryProcessing
{
    public const int CurrentPolicyVersion = 1;
    public int PolicyVersion { get; init; } = CurrentPolicyVersion;
    public DictionaryLookupIntent Intent { get; init; }
    public DictionaryStageState Core { get; init; }
    public DictionaryStageState Extension { get; init; }
    public DictionaryStageState Review { get; init; }
    public DictionaryReviewSync? ReviewSync { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public bool CanResumeExtension => Intent == DictionaryLookupIntent.Learning &&
        Extension is DictionaryStageState.Failed or DictionaryStageState.Invalidated;
    public string ReviewedContentHash { get; init; } = "";
    public string Message { get; init; } = "";
    public IReadOnlyList<DictionaryQualityIssue> Issues { get; init => field = value ?? []; } = [];
    public IReadOnlyList<DictionaryStageUsage> Usage { get; init => field = value ?? []; } = [];
    public IReadOnlyList<DictionarySectionCheck> SectionChecks { get; init => field = value ?? []; } = [];
}
