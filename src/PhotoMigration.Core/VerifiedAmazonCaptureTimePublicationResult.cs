namespace PhotoMigration.Core;

public enum VerifiedAmazonCaptureTimePublicationFailureKind
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

public abstract record VerifiedAmazonCaptureTimePublicationResult(
    AmazonCaptureTimeMetadataWriteVerifiedResult VerificationResult,
    string FormerTemporaryPath,
    string FinalDestinationPath);

public sealed record VerifiedAmazonCaptureTimePublicationSuccessResult(
    AmazonCaptureTimeMetadataWriteVerifiedResult VerificationResult,
    string FormerTemporaryPath,
    string FinalDestinationPath,
    long PublishedByteCount)
    : VerifiedAmazonCaptureTimePublicationResult(
        VerificationResult,
        FormerTemporaryPath,
        FinalDestinationPath);

public sealed record VerifiedAmazonCaptureTimePublicationFailureResult(
    AmazonCaptureTimeMetadataWriteVerifiedResult VerificationResult,
    string FormerTemporaryPath,
    string FinalDestinationPath,
    VerifiedAmazonCaptureTimePublicationFailureKind FailureKind,
    string Message)
    : VerifiedAmazonCaptureTimePublicationResult(
        VerificationResult,
        FormerTemporaryPath,
        FinalDestinationPath);

public sealed record VerifiedJpegGpsAndCaptureTimePublicationResult(
    VerifiedAmazonCaptureTimePublicationSuccessResult CaptureTimePublication,
    JpegGpsMetadataWriteVerifiedResult GpsVerification);
