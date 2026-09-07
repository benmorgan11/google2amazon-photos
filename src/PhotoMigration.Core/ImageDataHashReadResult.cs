namespace PhotoMigration.Core;

public abstract record ImageDataHashReadResult(
    VerifiedMediaFileStagingSuccessResult StagingResult);

public sealed record ImageDataHashReadSuccessResult(
    VerifiedMediaFileStagingSuccessResult StagingResult,
    string TemporaryCopyPath,
    string ExifToolExecutablePath,
    string AlgorithmName,
    string RawImageDataHash,
    string ImageDataHashSha256,
    IReadOnlyList<string> Warnings,
    string StandardError)
    : ImageDataHashReadResult(StagingResult);

public sealed record ImageDataHashInvalidStagingResult(
    VerifiedMediaFileStagingSuccessResult StagingResult,
    string Message)
    : ImageDataHashReadResult(StagingResult);

public sealed record ImageDataHashMissingTemporaryFileResult(
    VerifiedMediaFileStagingSuccessResult StagingResult,
    string TemporaryCopyPath)
    : ImageDataHashReadResult(StagingResult);

public sealed record ImageDataHashLinkedTemporaryFileResult(
    VerifiedMediaFileStagingSuccessResult StagingResult,
    string TemporaryCopyPath)
    : ImageDataHashReadResult(StagingResult);

public sealed record ImageDataHashNonRegularTemporaryFileResult(
    VerifiedMediaFileStagingSuccessResult StagingResult,
    string TemporaryCopyPath)
    : ImageDataHashReadResult(StagingResult);

public sealed record ImageDataHashUnsupportedMediaResult(
    VerifiedMediaFileStagingSuccessResult StagingResult,
    string TemporaryCopyPath,
    string Extension)
    : ImageDataHashReadResult(StagingResult);

public sealed record ImageDataHashTemporaryVerificationFailureResult(
    VerifiedMediaFileStagingSuccessResult StagingResult,
    string TemporaryCopyPath,
    string ExifToolExecutablePath,
    string Message,
    long ExpectedByteCount,
    long? ActualByteCount,
    string ExpectedWholeFileSha256,
    string? ActualWholeFileSha256,
    string StandardOutput,
    string StandardError)
    : ImageDataHashReadResult(StagingResult);

public sealed record ImageDataHashExifToolFailureResult(
    VerifiedMediaFileStagingSuccessResult StagingResult,
    string TemporaryCopyPath,
    string ExifToolExecutablePath,
    string Message,
    int? ExitCode,
    string StandardOutput,
    string StandardError)
    : ImageDataHashReadResult(StagingResult);

public sealed record ImageDataHashReadTimedOutResult(
    VerifiedMediaFileStagingSuccessResult StagingResult,
    string TemporaryCopyPath,
    string ExifToolExecutablePath,
    TimeSpan Timeout,
    string? TerminationError,
    string StandardOutput,
    string StandardError)
    : ImageDataHashReadResult(StagingResult);

public sealed record ImageDataHashMalformedJsonResult(
    VerifiedMediaFileStagingSuccessResult StagingResult,
    string TemporaryCopyPath,
    string ExifToolExecutablePath,
    string Message,
    string StandardOutput,
    string StandardError)
    : ImageDataHashReadResult(StagingResult);

public sealed record ImageDataHashMissingValueResult(
    VerifiedMediaFileStagingSuccessResult StagingResult,
    string TemporaryCopyPath,
    string ExifToolExecutablePath,
    IReadOnlyList<string> Warnings,
    string StandardOutput,
    string StandardError)
    : ImageDataHashReadResult(StagingResult);

public sealed record ImageDataHashInvalidValueResult(
    VerifiedMediaFileStagingSuccessResult StagingResult,
    string TemporaryCopyPath,
    string ExifToolExecutablePath,
    string RawImageDataHash,
    IReadOnlyList<string> Warnings,
    string StandardOutput,
    string StandardError)
    : ImageDataHashReadResult(StagingResult);
