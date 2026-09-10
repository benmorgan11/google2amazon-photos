namespace PhotoMigration.Core;

public enum AmazonCaptureTimeMetadataWriteVerificationFailureKind
{
    InvalidInput,
    MissingTemporaryFile,
    LinkedTemporaryFile,
    NonRegularTemporaryFile,
    ExistingFinalDestination,
    ExifToolFailure,
    TimedOut,
    MalformedOutput,
    MissingCaptureTimeValues,
    CaptureTimeMismatch,
    MissingImageDataHash,
    InvalidImageDataHash,
    MediaHashMismatch
}

public abstract record AmazonCaptureTimeMetadataWriteVerificationResult(
    AmazonCaptureTimeMetadataWriteSuccessResult WriteResult,
    string TemporaryCopyPath,
    string IntendedFinalDestinationPath,
    IReadOnlyList<AmazonCaptureTimeAssignment> ExpectedAssignments,
    string BaselineImageDataHashSha256);

public sealed record AmazonCaptureTimeMetadataWriteVerifiedResult(
    AmazonCaptureTimeMetadataWriteSuccessResult WriteResult,
    string TemporaryCopyPath,
    string IntendedFinalDestinationPath,
    IReadOnlyList<AmazonCaptureTimeAssignment> ExpectedAssignments,
    string BaselineImageDataHashSha256,
    IReadOnlyList<AmazonCaptureTimeAssignment> ActualAssignments,
    string ActualImageDataHashSha256,
    string ExifToolExecutablePath,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors,
    string StandardOutput,
    string StandardError)
    : AmazonCaptureTimeMetadataWriteVerificationResult(
        WriteResult,
        TemporaryCopyPath,
        IntendedFinalDestinationPath,
        ExpectedAssignments,
        BaselineImageDataHashSha256);

public sealed record AmazonCaptureTimeMetadataWriteVerificationFailureResult(
    AmazonCaptureTimeMetadataWriteSuccessResult WriteResult,
    string TemporaryCopyPath,
    string IntendedFinalDestinationPath,
    IReadOnlyList<AmazonCaptureTimeAssignment> ExpectedAssignments,
    string BaselineImageDataHashSha256,
    AmazonCaptureTimeMetadataWriteVerificationFailureKind FailureKind,
    string Message,
    IReadOnlyList<AmazonCaptureTimeAssignment>? ActualAssignments = null,
    string? ActualImageDataHashSha256 = null,
    string? ExifToolExecutablePath = null,
    int? ExitCode = null,
    TimeSpan? Timeout = null,
    string? TerminationError = null,
    IReadOnlyList<string>? Warnings = null,
    IReadOnlyList<string>? Errors = null,
    string StandardOutput = "",
    string StandardError = "")
    : AmazonCaptureTimeMetadataWriteVerificationResult(
        WriteResult,
        TemporaryCopyPath,
        IntendedFinalDestinationPath,
        ExpectedAssignments,
        BaselineImageDataHashSha256);
