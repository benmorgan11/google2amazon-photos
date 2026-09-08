namespace PhotoMigration.Core;

public enum JpegMetadataAssignmentGroup
{
    Exif
}

public enum JpegMetadataAssignmentTag
{
    GpsLatitude,
    GpsLatitudeReference,
    GpsLongitude,
    GpsLongitudeReference,
    GpsAltitude,
    GpsAltitudeReference
}

public sealed record JpegMetadataAssignmentSource(
    SidecarLocationSource Source,
    TakeoutSidecarMetadata SidecarMetadata,
    InventoryEntry SidecarEntry,
    SidecarMatchRule MatchRule);

public sealed record JpegMetadataAssignment(
    JpegMetadataAssignmentGroup Group,
    JpegMetadataAssignmentTag Tag,
    string Value,
    JpegMetadataAssignmentSource Source);

public abstract record JpegMetadataWritePlanReviewReason;

public sealed record ExistingMediaMetadataPlanReviewReason(
    MediaMetadataPlanReviewReason Reason)
    : JpegMetadataWritePlanReviewReason;

public sealed record SidecarPhotoTakenTimeTimezoneReviewReason(
    SidecarCaptureTimeProposal Proposal)
    : JpegMetadataWritePlanReviewReason;

public sealed record MetadataPlanReviewRequiredReason
    : JpegMetadataWritePlanReviewReason;

public enum JpegMetadataWritePlanInputIssueKind
{
    InvalidMetadataPlan,
    MismatchedMediaEntry,
    MismatchedSourcePath,
    MismatchedTemporaryPath,
    MismatchedFinalDestination,
    InvalidSidecarProvenance,
    InvalidLocationProposal
}

public sealed record JpegMetadataWritePlanInputIssue(
    JpegMetadataWritePlanInputIssueKind Kind,
    string Message);

public abstract record JpegMetadataWritePlanResult(
    TakeoutMetadataPlanningItem TakeoutPlanningItem,
    VerifiedMediaFileStagingSuccessResult StagingResult,
    ImageDataHashReadSuccessResult BaselineHashResult)
{
    public MediaMetadataPlan MetadataPlan => TakeoutPlanningItem.MetadataPlan;
}

public sealed record JpegMetadataWriteReadyResult(
    TakeoutMetadataPlanningItem TakeoutPlanningItem,
    VerifiedMediaFileStagingSuccessResult StagingResult,
    ImageDataHashReadSuccessResult BaselineHashResult,
    IReadOnlyList<JpegMetadataAssignment> Assignments)
    : JpegMetadataWritePlanResult(
        TakeoutPlanningItem,
        StagingResult,
        BaselineHashResult);

public sealed record JpegMetadataWriteNoChangesResult(
    TakeoutMetadataPlanningItem TakeoutPlanningItem,
    VerifiedMediaFileStagingSuccessResult StagingResult,
    ImageDataHashReadSuccessResult BaselineHashResult)
    : JpegMetadataWritePlanResult(
        TakeoutPlanningItem,
        StagingResult,
        BaselineHashResult);

public sealed record JpegMetadataWriteReviewRequiredResult(
    TakeoutMetadataPlanningItem TakeoutPlanningItem,
    VerifiedMediaFileStagingSuccessResult StagingResult,
    ImageDataHashReadSuccessResult BaselineHashResult,
    IReadOnlyList<JpegMetadataAssignment> ProposedAssignments,
    IReadOnlyList<JpegMetadataWritePlanReviewReason> ReviewReasons)
    : JpegMetadataWritePlanResult(
        TakeoutPlanningItem,
        StagingResult,
        BaselineHashResult);

public sealed record JpegMetadataWriteInvalidInputResult(
    TakeoutMetadataPlanningItem TakeoutPlanningItem,
    VerifiedMediaFileStagingSuccessResult StagingResult,
    ImageDataHashReadSuccessResult BaselineHashResult,
    IReadOnlyList<JpegMetadataWritePlanInputIssue> Issues)
    : JpegMetadataWritePlanResult(
        TakeoutPlanningItem,
        StagingResult,
        BaselineHashResult);

public sealed record JpegMetadataWriteUnsupportedFormatResult(
    TakeoutMetadataPlanningItem TakeoutPlanningItem,
    VerifiedMediaFileStagingSuccessResult StagingResult,
    ImageDataHashReadSuccessResult BaselineHashResult,
    string Extension)
    : JpegMetadataWritePlanResult(
        TakeoutPlanningItem,
        StagingResult,
        BaselineHashResult);
