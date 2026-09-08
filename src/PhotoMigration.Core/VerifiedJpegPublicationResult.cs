namespace PhotoMigration.Core;

public enum VerifiedJpegPublicationFailureKind
{
    InvalidOutputRoot,
    MismatchedPaths,
    InvalidPath,
    MissingTemporaryFile,
    LinkedPath,
    NonRegularTemporaryFile,
    ExistingDestination,
    MoveFailure
}

public abstract record VerifiedJpegPublicationResult(
    JpegGpsMetadataWriteVerifiedResult VerificationResult,
    string FormerTemporaryPath,
    string FinalDestinationPath);

public sealed record VerifiedJpegPublicationSuccessResult(
    JpegGpsMetadataWriteVerifiedResult VerificationResult,
    string FormerTemporaryPath,
    string FinalDestinationPath,
    long PublishedByteCount)
    : VerifiedJpegPublicationResult(
        VerificationResult,
        FormerTemporaryPath,
        FinalDestinationPath);

public sealed record VerifiedJpegPublicationFailureResult(
    JpegGpsMetadataWriteVerifiedResult VerificationResult,
    string FormerTemporaryPath,
    string FinalDestinationPath,
    VerifiedJpegPublicationFailureKind FailureKind,
    string Message)
    : VerifiedJpegPublicationResult(
        VerificationResult,
        FormerTemporaryPath,
        FinalDestinationPath);
