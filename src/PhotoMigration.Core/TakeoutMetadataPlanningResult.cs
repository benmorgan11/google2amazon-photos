namespace PhotoMigration.Core;

public abstract record TakeoutPlanningSidecarState(InventoryEntry MediaEntry);

public sealed record MatchedTakeoutSidecarState(ParsedMediaResult AnalysisResult)
    : TakeoutPlanningSidecarState(AnalysisResult.MediaEntry);

public sealed record UnmatchedTakeoutSidecarState(UnmatchedMediaResult AnalysisResult)
    : TakeoutPlanningSidecarState(AnalysisResult.MediaEntry);

public sealed record InvalidTakeoutSidecarState(InvalidSidecarResult AnalysisResult)
    : TakeoutPlanningSidecarState(AnalysisResult.MediaEntry);

public sealed record AmbiguousTakeoutSidecarState(
    AmbiguousMediaSidecarResult AnalysisResult)
    : TakeoutPlanningSidecarState(AnalysisResult.MediaEntry);

public sealed record TakeoutMetadataPlanningItem(
    InventoryEntry MediaEntry,
    TakeoutPlanningSidecarState SidecarState,
    MediaMetadataPlan MetadataPlan);

public sealed class TakeoutMetadataPlanningResult
{
    internal TakeoutMetadataPlanningResult(
        TakeoutAnalysisResult analysisResult,
        IReadOnlyList<TakeoutMetadataPlanningItem> items)
    {
        AnalysisResult = analysisResult;
        Items = items;
    }

    public TakeoutAnalysisResult AnalysisResult { get; }

    public IReadOnlyList<TakeoutMetadataPlanningItem> Items { get; }

    public int TotalMediaCount => Items.Count;

    public int MatchedSidecarCount => Items.Count(
        item => item.SidecarState is MatchedTakeoutSidecarState);

    public int UnmatchedMediaCount => Items.Count(
        item => item.SidecarState is UnmatchedTakeoutSidecarState);

    public int InvalidSidecarCount => Items.Count(
        item => item.SidecarState is InvalidTakeoutSidecarState);

    public int AmbiguousSidecarCount => Items.Count(
        item => item.SidecarState is AmbiguousTakeoutSidecarState);

    public int NoChangeMetadataPlanCount => CountPlans(
        MediaMetadataPlanStatus.NoMetadataChangesProposed);

    public int SafeChangeMetadataPlanCount => CountPlans(
        MediaMetadataPlanStatus.SafeMetadataChangesProposed);

    public int ReviewRequiredMetadataPlanCount => CountPlans(
        MediaMetadataPlanStatus.ReviewRequired);

    public int EmbeddedMetadataUnavailableCount => CountPlans(
        MediaMetadataPlanStatus.EmbeddedMetadataUnavailable);

    public int EmbeddedFormatNotSupportedYetCount => CountPlans(
        MediaMetadataPlanStatus.EmbeddedMetadataFormatNotSupportedYet);

    private int CountPlans(MediaMetadataPlanStatus status) =>
        Items.Count(item => item.MetadataPlan.Status == status);
}
