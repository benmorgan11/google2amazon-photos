namespace PhotoMigration.Core;

public enum MediaMetadataPlanStatus
{
    NoMetadataChangesProposed,
    SafeMetadataChangesProposed,
    ReviewRequired,
    EmbeddedMetadataUnavailable,
    EmbeddedMetadataFormatNotSupportedYet
}

public enum MediaMetadataPlanReviewReason
{
    LowConfidenceCreationTimeFallback,
    CaptureTimeReviewRequired,
    CaptureTimeConflict,
    EmbeddedCaptureTimeParsingIssues,
    LocationReviewRequired,
    LocationConflict,
    EmbeddedGpsParsingIssues,
    EmbeddedLocationBuildingIssues,
    ExifToolDiagnostics
}

public abstract record MediaMetadataPlan(
    MediaMetadataPlanStatus Status,
    EmbeddedMetadataReadResult EmbeddedReadResult,
    TakeoutSidecarMetadata SidecarMetadata);

public sealed record SuccessfulMediaMetadataPlan(
    MediaMetadataPlanStatus Status,
    EmbeddedMetadataReadSuccessResult SuccessfulRead,
    TakeoutSidecarMetadata SidecarMetadata,
    EmbeddedCaptureTimeParseResult CaptureTimeParseResult,
    EmbeddedGpsParseResult GpsParseResult,
    EmbeddedLocationBuildResult LocationBuildResult,
    CaptureTimeDecision CaptureTimeDecision,
    LocationDecision LocationDecision,
    IReadOnlyList<MediaMetadataPlanReviewReason> ReviewReasons)
    : MediaMetadataPlan(Status, SuccessfulRead, SidecarMetadata);

public sealed record UnsupportedEmbeddedMetadataFormatPlan(
    EmbeddedMetadataUnsupportedMediaResult UnsupportedRead,
    TakeoutSidecarMetadata SidecarMetadata)
    : MediaMetadataPlan(
        MediaMetadataPlanStatus.EmbeddedMetadataFormatNotSupportedYet,
        UnsupportedRead,
        SidecarMetadata);

public sealed record MissingMediaMetadataPlan(
    EmbeddedMetadataMissingMediaResult MissingRead,
    TakeoutSidecarMetadata SidecarMetadata)
    : MediaMetadataPlan(
        MediaMetadataPlanStatus.EmbeddedMetadataUnavailable,
        MissingRead,
        SidecarMetadata);

public sealed record ExifToolFailureMetadataPlan(
    EmbeddedMetadataExifToolFailureResult FailureRead,
    TakeoutSidecarMetadata SidecarMetadata)
    : MediaMetadataPlan(
        MediaMetadataPlanStatus.EmbeddedMetadataUnavailable,
        FailureRead,
        SidecarMetadata);

public sealed record EmbeddedMetadataTimeoutPlan(
    EmbeddedMetadataReadTimedOutResult TimeoutRead,
    TakeoutSidecarMetadata SidecarMetadata)
    : MediaMetadataPlan(
        MediaMetadataPlanStatus.EmbeddedMetadataUnavailable,
        TimeoutRead,
        SidecarMetadata);

public sealed record MalformedEmbeddedMetadataJsonPlan(
    EmbeddedMetadataMalformedJsonResult MalformedRead,
    TakeoutSidecarMetadata SidecarMetadata)
    : MediaMetadataPlan(
        MediaMetadataPlanStatus.EmbeddedMetadataUnavailable,
        MalformedRead,
        SidecarMetadata);
