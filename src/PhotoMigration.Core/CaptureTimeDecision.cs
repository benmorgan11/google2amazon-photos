namespace PhotoMigration.Core;

public enum EmbeddedSidecarCaptureTimeComparisonKind
{
    Match,
    Conflict,
    CannotCompareUnknownEmbeddedTimezone,
    NotComparedNoSidecarPhotoTakenTime
}

public enum CaptureTimeReviewReason
{
    ConflictingEmbeddedCandidates,
    MixedEmbeddedTimezoneKnowledge,
    EmbeddedParsingIssuesWithoutValidCandidate
}

public enum SidecarCaptureTimeSource
{
    PhotoTakenTime,
    CreationTime
}

public enum SidecarTimestampTimezoneStatus
{
    OriginalLocalTimezoneUnknown
}

public sealed record EmbeddedSidecarCaptureTimeComparison(
    EmbeddedCaptureTimeCandidate EmbeddedCandidate,
    DateTimeOffset? SidecarPhotoTakenTime,
    EmbeddedSidecarCaptureTimeComparisonKind Kind);

public sealed record SidecarCaptureTimeProposal(
    DateTimeOffset Instant,
    SidecarCaptureTimeSource Source,
    SidecarTimestampTimezoneStatus TimezoneStatus,
    TakeoutSidecarMetadata SourceMetadata);

public abstract record CaptureTimeDecision(
    IReadOnlyList<EmbeddedCaptureTimeCandidate> EmbeddedCandidates,
    IReadOnlyList<EmbeddedCaptureTimeParsingIssue> EmbeddedIssues,
    IReadOnlyList<EmbeddedSidecarCaptureTimeComparison> SidecarComparisons,
    TakeoutSidecarMetadata SidecarMetadata);

public sealed record KeepEmbeddedCaptureTimeDecision(
    IReadOnlyList<EmbeddedCaptureTimeCandidate> EmbeddedCandidates,
    IReadOnlyList<EmbeddedCaptureTimeParsingIssue> EmbeddedIssues,
    IReadOnlyList<EmbeddedSidecarCaptureTimeComparison> SidecarComparisons,
    TakeoutSidecarMetadata SidecarMetadata)
    : CaptureTimeDecision(
        EmbeddedCandidates,
        EmbeddedIssues,
        SidecarComparisons,
        SidecarMetadata);

public sealed record ProposeSidecarPhotoTakenTimeDecision(
    IReadOnlyList<EmbeddedCaptureTimeCandidate> EmbeddedCandidates,
    IReadOnlyList<EmbeddedCaptureTimeParsingIssue> EmbeddedIssues,
    IReadOnlyList<EmbeddedSidecarCaptureTimeComparison> SidecarComparisons,
    TakeoutSidecarMetadata SidecarMetadata,
    SidecarCaptureTimeProposal Proposal)
    : CaptureTimeDecision(
        EmbeddedCandidates,
        EmbeddedIssues,
        SidecarComparisons,
        SidecarMetadata);

public sealed record ProposeLowConfidenceSidecarCreationTimeDecision(
    IReadOnlyList<EmbeddedCaptureTimeCandidate> EmbeddedCandidates,
    IReadOnlyList<EmbeddedCaptureTimeParsingIssue> EmbeddedIssues,
    IReadOnlyList<EmbeddedSidecarCaptureTimeComparison> SidecarComparisons,
    TakeoutSidecarMetadata SidecarMetadata,
    SidecarCaptureTimeProposal Proposal)
    : CaptureTimeDecision(
        EmbeddedCandidates,
        EmbeddedIssues,
        SidecarComparisons,
        SidecarMetadata);

public sealed record ReviewRequiredCaptureTimeDecision(
    IReadOnlyList<EmbeddedCaptureTimeCandidate> EmbeddedCandidates,
    IReadOnlyList<EmbeddedCaptureTimeParsingIssue> EmbeddedIssues,
    IReadOnlyList<EmbeddedSidecarCaptureTimeComparison> SidecarComparisons,
    TakeoutSidecarMetadata SidecarMetadata,
    CaptureTimeReviewReason Reason)
    : CaptureTimeDecision(
        EmbeddedCandidates,
        EmbeddedIssues,
        SidecarComparisons,
        SidecarMetadata);

public sealed record CaptureTimeMissingDecision(
    IReadOnlyList<EmbeddedCaptureTimeCandidate> EmbeddedCandidates,
    IReadOnlyList<EmbeddedCaptureTimeParsingIssue> EmbeddedIssues,
    IReadOnlyList<EmbeddedSidecarCaptureTimeComparison> SidecarComparisons,
    TakeoutSidecarMetadata SidecarMetadata)
    : CaptureTimeDecision(
        EmbeddedCandidates,
        EmbeddedIssues,
        SidecarComparisons,
        SidecarMetadata);
