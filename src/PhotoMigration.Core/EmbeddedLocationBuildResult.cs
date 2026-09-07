namespace PhotoMigration.Core;

public enum EmbeddedLocationIssueKind
{
    LatitudeWithoutLongitude,
    LongitudeWithoutLatitude,
    AltitudeWithoutCoordinatePair
}

public sealed record EmbeddedLocationCandidate(
    string GroupName,
    EmbeddedGpsCandidate Latitude,
    EmbeddedGpsCandidate Longitude,
    EmbeddedGpsCandidate? Altitude);

public sealed record EmbeddedLocationIssue(
    EmbeddedLocationIssueKind Kind,
    string GroupName,
    IReadOnlyList<EmbeddedGpsCandidate> Values,
    string Message);

public sealed record EmbeddedLocationBuildResult(
    IReadOnlyList<EmbeddedLocationCandidate> Candidates,
    IReadOnlyList<EmbeddedLocationIssue> Issues);
