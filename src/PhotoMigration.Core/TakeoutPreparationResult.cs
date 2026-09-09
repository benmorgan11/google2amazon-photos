namespace PhotoMigration.Core;

public enum TakeoutPreparationOutcomeKind
{
    PublishedUnchanged,
    PublishedJpegGps,
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
    JpegWriteReviewRequired
}

public enum TakeoutPreparationFailureStage
{
    DestinationPlanning,
    Staging,
    ImageDataHashReading,
    JpegWritePlanning,
    JpegWriting,
    JpegVerification,
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
        PublishedJpegGpsFiles = ForKind(
            TakeoutPreparationOutcomeKind.PublishedJpegGps);
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

    public IReadOnlyList<TakeoutPreparationItemOutcome> PublishedJpegGpsFiles { get; }

    public IReadOnlyList<TakeoutPreparationItemOutcome> AttentionRequiredFiles { get; }

    public IReadOnlyList<TakeoutPreparationItemOutcome> FailedFiles { get; }

    public int TotalMediaCount => Outcomes.Count;

    public int PublishedUnchangedCount => PublishedUnchangedFiles.Count;

    public int PublishedJpegGpsCount => PublishedJpegGpsFiles.Count;

    public int PublishedCount =>
        PublishedUnchangedCount + PublishedJpegGpsCount;

    public int AttentionRequiredCount => AttentionRequiredFiles.Count;

    public int FailedCount => FailedFiles.Count;

    private IReadOnlyList<TakeoutPreparationItemOutcome> ForKind(
        TakeoutPreparationOutcomeKind kind) =>
        Outcomes.Where(outcome => outcome.Kind == kind).ToList().AsReadOnly();
}
