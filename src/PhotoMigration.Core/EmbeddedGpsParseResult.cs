namespace PhotoMigration.Core;

public enum EmbeddedGpsParsingIssueKind
{
    MalformedNumber,
    NonFiniteNumber,
    OutOfRange,
    MissingReference,
    InvalidReference,
    ConflictingSignAndReference
}

public sealed record EmbeddedGpsCandidate(
    EmbeddedMetadataValue SourceValue,
    double ParsedValue,
    EmbeddedMetadataValue? ReferenceValue);

public sealed record EmbeddedGpsParsingIssue(
    EmbeddedGpsParsingIssueKind Kind,
    EmbeddedMetadataValue SourceValue,
    EmbeddedMetadataValue? ReferenceValue,
    string Message);

public sealed record EmbeddedGpsParseResult(
    IReadOnlyList<EmbeddedGpsCandidate> Candidates,
    IReadOnlyList<EmbeddedGpsParsingIssue> Issues);
