namespace PhotoMigration.Core;

public enum JpegGpsMetadataWriteStatus
{
    PendingVerification
}

public enum JpegGpsMetadataWriteFailureKind
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

public abstract record JpegGpsMetadataWriteResult(
    JpegMetadataWriteReadyResult WritePlan);

public sealed record JpegGpsMetadataWriteSuccessResult(
    JpegMetadataWriteReadyResult WritePlan,
    string TemporaryCopyPath,
    string IntendedFinalDestinationPath,
    string ExifToolExecutablePath,
    JpegGpsMetadataWriteStatus Status,
    string StandardOutput,
    string StandardError)
    : JpegGpsMetadataWriteResult(WritePlan);

public sealed record JpegGpsMetadataWriteFailureResult(
    JpegMetadataWriteReadyResult WritePlan,
    JpegGpsMetadataWriteFailureKind FailureKind,
    string Message,
    string? ExifToolExecutablePath = null,
    int? ExitCode = null,
    TimeSpan? Timeout = null,
    string? TerminationError = null,
    long? ActualByteCount = null,
    string? ActualWholeFileSha256 = null,
    string StandardOutput = "",
    string StandardError = "")
    : JpegGpsMetadataWriteResult(WritePlan);
