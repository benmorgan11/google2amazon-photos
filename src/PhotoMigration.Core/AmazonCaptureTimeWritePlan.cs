namespace PhotoMigration.Core;

public enum AmazonCaptureTimeWritePlanStatus
{
    Ready,
    NoChangeRequired,
    AttentionRequired,
    UnsupportedFormat
}

public enum AmazonCaptureTimeMediaFormat
{
    Jpeg,
    Heic,
    Heif,
    Png,
    Mov,
    Mp4
}

public enum AmazonCaptureTimeAssignmentGroup
{
    Exif,
    QuickTime,
    Keys
}

public enum AmazonCaptureTimeAssignmentTag
{
    DateTimeOriginal,
    OffsetTimeOriginal,
    CreateDate,
    CreationDate
}

public sealed record AmazonCaptureTimeAssignment(
    AmazonCaptureTimeAssignmentGroup Group,
    AmazonCaptureTimeAssignmentTag Tag,
    string Value);

public enum AmazonCaptureTimeWritePlanAttentionReason
{
    InvalidSidecar,
    AmbiguousSidecar,
    NoUsableCaptureTime,
    EmbeddedCaptureTimeRequiresReview,
    EmbeddedMetadataUnavailable
}

public abstract record AmazonCaptureTimeWritePlanResult(
    AmazonCaptureTimeWritePlanStatus Status,
    TakeoutMetadataPlanningItem PlanningItem);

public sealed record AmazonCaptureTimeWriteReadyResult(
    TakeoutMetadataPlanningItem PlanningItem,
    DateTimeOffset UtcCaptureInstant,
    AmazonCaptureTimeMediaFormat MediaFormat,
    IReadOnlyList<AmazonCaptureTimeAssignment> Assignments,
    InventoryEntry MatchedSidecarEntry,
    SidecarMatchRule SidecarMatchRule)
    : AmazonCaptureTimeWritePlanResult(
        AmazonCaptureTimeWritePlanStatus.Ready,
        PlanningItem);

public sealed record AmazonCaptureTimeNoChangeRequiredResult(
    TakeoutMetadataPlanningItem PlanningItem,
    IReadOnlyList<EmbeddedCaptureTimeCandidate> RetainedEmbeddedCaptureTimes)
    : AmazonCaptureTimeWritePlanResult(
        AmazonCaptureTimeWritePlanStatus.NoChangeRequired,
        PlanningItem);

public sealed record AmazonCaptureTimeAttentionRequiredResult(
    TakeoutMetadataPlanningItem PlanningItem,
    AmazonCaptureTimeWritePlanAttentionReason Reason)
    : AmazonCaptureTimeWritePlanResult(
        AmazonCaptureTimeWritePlanStatus.AttentionRequired,
        PlanningItem);

public sealed record AmazonCaptureTimeUnsupportedFormatResult(
    TakeoutMetadataPlanningItem PlanningItem,
    string Extension)
    : AmazonCaptureTimeWritePlanResult(
        AmazonCaptureTimeWritePlanStatus.UnsupportedFormat,
        PlanningItem);
