namespace PhotoMigration.Core;

public sealed record JpegGpsMetadataVerificationValues(
    double Latitude,
    string LatitudeReference,
    double Longitude,
    string LongitudeReference,
    double? Altitude,
    string? AltitudeReference);

public enum JpegGpsMetadataWriteVerificationFailureKind
{
    InvalidInput,
    MissingTemporaryFile,
    LinkedTemporaryFile,
    NonRegularTemporaryFile,
    ExistingFinalDestination,
    ExifToolFailure,
    TimedOut,
    MalformedOutput,
    MissingGpsValues,
    InvalidGpsValues,
    GpsMismatch,
    MissingImageDataHash,
    InvalidImageDataHash,
    MediaHashMismatch
}

public abstract record JpegGpsMetadataWriteVerificationResult(
    JpegGpsMetadataWriteSuccessResult WriteResult,
    string TemporaryCopyPath,
    string IntendedFinalDestinationPath,
    string BaselineImageDataHashSha256);

public sealed record JpegGpsMetadataWriteVerifiedResult(
    JpegGpsMetadataWriteSuccessResult WriteResult,
    string TemporaryCopyPath,
    string IntendedFinalDestinationPath,
    string BaselineImageDataHashSha256,
    string ActualImageDataHashSha256,
    JpegGpsMetadataVerificationValues ExpectedGps,
    JpegGpsMetadataVerificationValues ActualGps,
    string ExifToolExecutablePath,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors,
    string StandardOutput,
    string StandardError)
    : JpegGpsMetadataWriteVerificationResult(
        WriteResult,
        TemporaryCopyPath,
        IntendedFinalDestinationPath,
        BaselineImageDataHashSha256);

public sealed record JpegGpsMetadataWriteVerificationFailureResult(
    JpegGpsMetadataWriteSuccessResult WriteResult,
    string TemporaryCopyPath,
    string IntendedFinalDestinationPath,
    string BaselineImageDataHashSha256,
    JpegGpsMetadataWriteVerificationFailureKind FailureKind,
    string Message,
    JpegGpsMetadataVerificationValues? ExpectedGps = null,
    JpegGpsMetadataVerificationValues? ActualGps = null,
    string? ActualImageDataHashSha256 = null,
    string? ExifToolExecutablePath = null,
    int? ExitCode = null,
    TimeSpan? Timeout = null,
    string? TerminationError = null,
    IReadOnlyList<string>? Warnings = null,
    IReadOnlyList<string>? Errors = null,
    string StandardOutput = "",
    string StandardError = "")
    : JpegGpsMetadataWriteVerificationResult(
        WriteResult,
        TemporaryCopyPath,
        IntendedFinalDestinationPath,
        BaselineImageDataHashSha256);
