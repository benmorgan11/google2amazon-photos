namespace PhotoMigration.Core;

public sealed record TakeoutSidecarMetadata(
    string? Title,
    string? Description,
    DateTimeOffset? CreationTime,
    DateTimeOffset? PhotoTakenTime,
    double? Latitude,
    double? Longitude,
    double? Altitude,
    string? Url);
