using System.Text.Json;

namespace PhotoMigration.Core;

public enum EmbeddedMetadataField
{
    CaptureDateTime,
    CaptureTimezoneOffset,
    GpsLatitude,
    GpsLatitudeReference,
    GpsLongitude,
    GpsLongitudeReference,
    GpsAltitude,
    GpsAltitudeReference
}

public sealed record EmbeddedMetadataValue(
    EmbeddedMetadataField Field,
    string GroupName,
    string TagName,
    string RawValue,
    JsonValueKind ValueKind);

public abstract record EmbeddedMetadataReadResult(string MediaPath);

public sealed record EmbeddedMetadataReadSuccessResult(
    string MediaPath,
    string ExifToolExecutablePath,
    IReadOnlyList<EmbeddedMetadataValue> Values,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors,
    string StandardError)
    : EmbeddedMetadataReadResult(MediaPath);

public sealed record EmbeddedMetadataUnsupportedMediaResult(
    string MediaPath,
    string Extension)
    : EmbeddedMetadataReadResult(MediaPath);

public sealed record EmbeddedMetadataMissingMediaResult(string MediaPath)
    : EmbeddedMetadataReadResult(MediaPath);

public sealed record EmbeddedMetadataExifToolFailureResult(
    string MediaPath,
    string ExifToolExecutablePath,
    string Message,
    int? ExitCode,
    string StandardOutput,
    string StandardError)
    : EmbeddedMetadataReadResult(MediaPath);

public sealed record EmbeddedMetadataReadTimedOutResult(
    string MediaPath,
    string ExifToolExecutablePath,
    TimeSpan Timeout,
    string? TerminationError,
    string StandardOutput,
    string StandardError)
    : EmbeddedMetadataReadResult(MediaPath);

public sealed record EmbeddedMetadataMalformedJsonResult(
    string MediaPath,
    string ExifToolExecutablePath,
    string Message,
    string StandardOutput,
    string StandardError)
    : EmbeddedMetadataReadResult(MediaPath);
