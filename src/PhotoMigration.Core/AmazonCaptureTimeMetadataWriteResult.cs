namespace PhotoMigration.Core;

public enum AmazonCaptureTimeMetadataWriteStatus
{
    PendingVerification
}

public enum AmazonCaptureTimeMetadataWriteFailureKind
{
    InvalidPath,
    InvalidOutputRoot,
    InvalidWritePlan,
    MissingTemporaryFile,
    LinkedPath,
    NonRegularTemporaryFile,
    TemporaryFileChanged,
    ExistingFinalDestination,
    ExifToolFailure,
    TimedOut
}

public abstract record AmazonCaptureTimeMetadataWriteResult(
    AmazonCaptureTimeWriteReadyResult WritePlan,
    VerifiedMediaFileStagingSuccessResult StagingResult,
    ImageDataHashReadSuccessResult BaselineHashResult,
    string OutputRootPath,
    string ExifToolExecutablePath)
{
    public string TemporaryCopyPath => StagingResult.TemporaryCopyPath;

    public string IntendedFinalDestinationPath =>
        StagingResult.IntendedFinalDestinationPath;
}

public sealed record AmazonCaptureTimeMetadataWriteSuccessResult(
    AmazonCaptureTimeWriteReadyResult WritePlan,
    VerifiedMediaFileStagingSuccessResult StagingResult,
    ImageDataHashReadSuccessResult BaselineHashResult,
    string OutputRootPath,
    string ExifToolExecutablePath,
    AmazonCaptureTimeMetadataWriteStatus Status,
    string StandardOutput,
    string StandardError)
    : AmazonCaptureTimeMetadataWriteResult(
        WritePlan,
        StagingResult,
        BaselineHashResult,
        OutputRootPath,
        ExifToolExecutablePath);

public sealed record AmazonCaptureTimeMetadataWriteFailureResult(
    AmazonCaptureTimeWriteReadyResult WritePlan,
    VerifiedMediaFileStagingSuccessResult StagingResult,
    ImageDataHashReadSuccessResult BaselineHashResult,
    string OutputRootPath,
    string ExifToolExecutablePath,
    AmazonCaptureTimeMetadataWriteFailureKind FailureKind,
    string Message,
    int? ExitCode = null,
    TimeSpan? Timeout = null,
    string? TerminationError = null,
    long? ActualByteCount = null,
    string? ActualWholeFileSha256 = null,
    string StandardOutput = "",
    string StandardError = "")
    : AmazonCaptureTimeMetadataWriteResult(
        WritePlan,
        StagingResult,
        BaselineHashResult,
        OutputRootPath,
        ExifToolExecutablePath);
