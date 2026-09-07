namespace PhotoMigration.Core;

public enum EmbeddedCaptureTimeOffsetSource
{
    Unknown,
    IncludedInDateValue,
    OffsetTimeOriginal
}

public enum EmbeddedCaptureTimeParsingIssueKind
{
    InvalidDateValue,
    InvalidOffsetValue
}

public sealed record EmbeddedCaptureTimeCandidate(
    EmbeddedMetadataValue SourceValue,
    DateTime ParsedDateTime,
    DateTimeOffset? ParsedDateTimeOffset,
    EmbeddedCaptureTimeOffsetSource OffsetSource,
    EmbeddedMetadataValue? OffsetValue);

public sealed record EmbeddedCaptureTimeParsingIssue(
    EmbeddedCaptureTimeParsingIssueKind Kind,
    EmbeddedMetadataValue SourceValue,
    string Message);

public sealed record EmbeddedCaptureTimeParseResult(
    IReadOnlyList<EmbeddedCaptureTimeCandidate> Candidates,
    IReadOnlyList<EmbeddedCaptureTimeParsingIssue> Issues);
