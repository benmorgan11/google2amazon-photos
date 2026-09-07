namespace PhotoMigration.Core;

public enum SidecarLocationStatus
{
    Complete,
    Incomplete,
    ZeroZeroPlaceholder,
    Missing
}

public enum SidecarLocationSource
{
    GeoData
}

public enum EmbeddedSidecarLocationComparisonKind
{
    Match,
    CoordinateConflict,
    AltitudeConflict,
    NoUsableSidecarLocation,
    SidecarZeroZeroPlaceholder,
    IncompleteSidecarLocation
}

public enum LocationReviewReason
{
    ConflictingEmbeddedCandidates,
    EmbeddedGpsParsingIssuesWithoutCompleteLocation,
    EmbeddedLocationBuildingIssuesWithoutCompleteLocation,
    IncompleteSidecarLocation
}

public sealed record SidecarLocationAssessment(
    SidecarLocationStatus Status,
    double? Latitude,
    double? Longitude,
    double? Altitude,
    SidecarLocationSource Source,
    TakeoutSidecarMetadata SourceMetadata);

public sealed record EmbeddedSidecarLocationComparison(
    EmbeddedLocationCandidate EmbeddedCandidate,
    SidecarLocationAssessment SidecarLocation,
    EmbeddedSidecarLocationComparisonKind Kind);

public sealed record SidecarLocationProposal(
    double Latitude,
    double Longitude,
    double? Altitude,
    SidecarLocationSource Source,
    TakeoutSidecarMetadata SourceMetadata);

public abstract record LocationDecision(
    EmbeddedGpsParseResult EmbeddedGpsResult,
    EmbeddedLocationBuildResult EmbeddedLocationResult,
    TakeoutSidecarMetadata SidecarMetadata,
    SidecarLocationAssessment SidecarLocation,
    IReadOnlyList<EmbeddedSidecarLocationComparison> SidecarComparisons);

public sealed record KeepEmbeddedLocationDecision(
    EmbeddedGpsParseResult EmbeddedGpsResult,
    EmbeddedLocationBuildResult EmbeddedLocationResult,
    TakeoutSidecarMetadata SidecarMetadata,
    SidecarLocationAssessment SidecarLocation,
    IReadOnlyList<EmbeddedSidecarLocationComparison> SidecarComparisons)
    : LocationDecision(
        EmbeddedGpsResult,
        EmbeddedLocationResult,
        SidecarMetadata,
        SidecarLocation,
        SidecarComparisons);

public sealed record ProposeSidecarLocationDecision(
    EmbeddedGpsParseResult EmbeddedGpsResult,
    EmbeddedLocationBuildResult EmbeddedLocationResult,
    TakeoutSidecarMetadata SidecarMetadata,
    SidecarLocationAssessment SidecarLocation,
    IReadOnlyList<EmbeddedSidecarLocationComparison> SidecarComparisons,
    SidecarLocationProposal Proposal)
    : LocationDecision(
        EmbeddedGpsResult,
        EmbeddedLocationResult,
        SidecarMetadata,
        SidecarLocation,
        SidecarComparisons);

public sealed record ReviewRequiredLocationDecision(
    EmbeddedGpsParseResult EmbeddedGpsResult,
    EmbeddedLocationBuildResult EmbeddedLocationResult,
    TakeoutSidecarMetadata SidecarMetadata,
    SidecarLocationAssessment SidecarLocation,
    IReadOnlyList<EmbeddedSidecarLocationComparison> SidecarComparisons,
    IReadOnlyList<LocationReviewReason> Reasons)
    : LocationDecision(
        EmbeddedGpsResult,
        EmbeddedLocationResult,
        SidecarMetadata,
        SidecarLocation,
        SidecarComparisons);

public sealed record LocationMissingDecision(
    EmbeddedGpsParseResult EmbeddedGpsResult,
    EmbeddedLocationBuildResult EmbeddedLocationResult,
    TakeoutSidecarMetadata SidecarMetadata,
    SidecarLocationAssessment SidecarLocation,
    IReadOnlyList<EmbeddedSidecarLocationComparison> SidecarComparisons)
    : LocationDecision(
        EmbeddedGpsResult,
        EmbeddedLocationResult,
        SidecarMetadata,
        SidecarLocation,
        SidecarComparisons);
