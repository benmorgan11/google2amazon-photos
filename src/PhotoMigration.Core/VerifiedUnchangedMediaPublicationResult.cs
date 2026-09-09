namespace PhotoMigration.Core;

public enum VerifiedUnchangedMediaPublicationFailureKind
{
    InvalidOutputRoot,
    InvalidPath,
    MissingTemporaryFile,
    LinkedPath,
    NonRegularTemporaryFile,
    ChangedTemporaryFile,
    ExistingDestination,
    MoveFailure
}

public abstract record VerifiedUnchangedMediaPublicationResult(
    VerifiedMediaFileStagingSuccessResult StagingResult,
    string FormerTemporaryPath,
    string FinalDestinationPath);

public sealed record VerifiedUnchangedMediaPublicationSuccessResult(
    VerifiedMediaFileStagingSuccessResult StagingResult,
    string FormerTemporaryPath,
    string FinalDestinationPath,
    long PublishedByteCount,
    string PublishedSha256)
    : VerifiedUnchangedMediaPublicationResult(
        StagingResult,
        FormerTemporaryPath,
        FinalDestinationPath);

public sealed record VerifiedUnchangedMediaPublicationFailureResult(
    VerifiedMediaFileStagingSuccessResult StagingResult,
    string FormerTemporaryPath,
    string FinalDestinationPath,
    VerifiedUnchangedMediaPublicationFailureKind FailureKind,
    string Message,
    long? ActualByteCount = null,
    string? ActualSha256 = null)
    : VerifiedUnchangedMediaPublicationResult(
        StagingResult,
        FormerTemporaryPath,
        FinalDestinationPath);
