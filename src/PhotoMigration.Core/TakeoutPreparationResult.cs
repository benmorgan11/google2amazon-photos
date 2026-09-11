namespace PhotoMigration.Core;

public enum TakeoutPreparationOutcomeKind
{
    PublishedUnchanged,
    PublishedWithGps,
    PublishedJpegGps = PublishedWithGps,
    PublishedWithCaptureTime,
    PublishedWithGpsAndCaptureTime,
    AttentionRequired,
    Failed
}

public enum TakeoutPreparationAttentionReason
{
    InvalidSidecar,
    AmbiguousSidecar,
    MetadataReviewRequired,
    EmbeddedMetadataUnavailable,
    EmbeddedFormatNotSupported,
    WriteFormatNotSupported,
    JpegWriteReviewRequired,
    CaptureTimeAttentionRequired
}

public enum TakeoutPreparationFailureStage
{
    DestinationPlanning,
    Staging,
    ImageDataHashReading,
    JpegWritePlanning,
    JpegWriting,
    JpegVerification,
    CaptureTimeWriting,
    CaptureTimeVerification,
    Publication
}

public sealed record TakeoutPreparationItemOutcome(
    TakeoutMetadataPlanningItem PlanningItem,
    DestinationPathPlanningItem? DestinationPlanningItem,
    TakeoutPreparationOutcomeKind Kind,
    TakeoutPreparationAttentionReason? AttentionReason,
    TakeoutPreparationFailureStage? FailureStage,
    string Message,
    object RetainedResult);

public sealed class TakeoutPreparationResult
{
    internal TakeoutPreparationResult(
        TakeoutMetadataPlanningResult metadataPlanningResult,
        DestinationPathPlanningResult destinationPlanningResult,
        IReadOnlyList<TakeoutPreparationItemOutcome> outcomes)
    {
        MetadataPlanningResult = metadataPlanningResult;
        DestinationPlanningResult = destinationPlanningResult;
        Outcomes = outcomes;
        PublishedUnchangedFiles = ForKind(
            TakeoutPreparationOutcomeKind.PublishedUnchanged);
        PublishedWithGpsFiles = ForKind(
            TakeoutPreparationOutcomeKind.PublishedWithGps);
        PublishedWithCaptureTimeFiles = ForKind(
            TakeoutPreparationOutcomeKind.PublishedWithCaptureTime);
        PublishedWithGpsAndCaptureTimeFiles = ForKind(
            TakeoutPreparationOutcomeKind.PublishedWithGpsAndCaptureTime);
        AttentionRequiredFiles = ForKind(
            TakeoutPreparationOutcomeKind.AttentionRequired);
        FailedFiles = ForKind(TakeoutPreparationOutcomeKind.Failed);
    }

    public TakeoutMetadataPlanningResult MetadataPlanningResult { get; }

    public TakeoutAnalysisResult AnalysisResult =>
        MetadataPlanningResult.AnalysisResult;

    public DestinationPathPlanningResult DestinationPlanningResult { get; }

    public IReadOnlyList<TakeoutPreparationItemOutcome> Outcomes { get; }

    public IReadOnlyList<TakeoutPreparationItemOutcome> PublishedUnchangedFiles { get; }

    public IReadOnlyList<TakeoutPreparationItemOutcome> PublishedWithGpsFiles { get; }

    public IReadOnlyList<TakeoutPreparationItemOutcome> PublishedJpegGpsFiles =>
        PublishedWithGpsFiles;

    public IReadOnlyList<TakeoutPreparationItemOutcome>
        PublishedWithCaptureTimeFiles { get; }

    public IReadOnlyList<TakeoutPreparationItemOutcome>
        PublishedWithGpsAndCaptureTimeFiles { get; }

    public IReadOnlyList<TakeoutPreparationItemOutcome> AttentionRequiredFiles { get; }

    public IReadOnlyList<TakeoutPreparationItemOutcome> FailedFiles { get; }

    public int TotalMediaCount => Outcomes.Count;

    public int PublishedUnchangedCount => PublishedUnchangedFiles.Count;

    public int PublishedWithGpsCount => PublishedWithGpsFiles.Count;

    public int PublishedJpegGpsCount => PublishedWithGpsCount;

    public int PublishedWithCaptureTimeCount =>
        PublishedWithCaptureTimeFiles.Count;

    public int PublishedWithGpsAndCaptureTimeCount =>
        PublishedWithGpsAndCaptureTimeFiles.Count;

    public int PublishedCount =>
        PublishedUnchangedCount
        + PublishedWithGpsCount
        + PublishedWithCaptureTimeCount
        + PublishedWithGpsAndCaptureTimeCount;

    public int AttentionRequiredCount => AttentionRequiredFiles.Count;

    public int FailedCount => FailedFiles.Count;

    private IReadOnlyList<TakeoutPreparationItemOutcome> ForKind(
        TakeoutPreparationOutcomeKind kind) =>
        Outcomes.Where(outcome => outcome.Kind == kind).ToList().AsReadOnly();
}
