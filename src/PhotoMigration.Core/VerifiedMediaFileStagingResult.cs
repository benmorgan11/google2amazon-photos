namespace PhotoMigration.Core;

public enum VerifiedMediaFileStagingFailureKind
{
    InvalidPlan,
    InvalidRoot,
    OverlappingRoots,
    LinkedPath,
    MissingSource,
    InvalidSourceFile,
    SourceChanged,
    ExistingDestination,
    DirectoryCreationFailure,
    TemporaryFileCreationFailure,
    CopyFailure,
    VerificationFailure,
    CleanupFailure
}

public abstract record VerifiedMediaFileStagingResult(
    DestinationPathPlanningItem PlanningItem);

public sealed record VerifiedMediaFileStagingSuccessResult(
    DestinationPathPlanningItem PlanningItem,
    string TemporaryCopyPath,
    string IntendedFinalDestinationPath,
    long CopiedByteCount,
    string SourceSha256,
    string TemporaryCopySha256)
    : VerifiedMediaFileStagingResult(PlanningItem);

public sealed record VerifiedMediaFileStagingFailureResult(
    DestinationPathPlanningItem PlanningItem,
    VerifiedMediaFileStagingFailureKind FailureKind,
    string Message,
    string? TemporaryCopyPath = null,
    VerifiedMediaFileStagingFailureKind? FailureBeforeCleanup = null)
    : VerifiedMediaFileStagingResult(PlanningItem);
