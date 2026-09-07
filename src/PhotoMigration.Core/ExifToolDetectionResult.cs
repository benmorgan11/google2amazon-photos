namespace PhotoMigration.Core;

public abstract record ExifToolDetectionResult(
    IReadOnlyList<string> CandidatePaths);

public sealed record ExifToolFoundResult(
    string ExecutablePath,
    string Version,
    IReadOnlyList<string> CandidatePaths)
    : ExifToolDetectionResult(CandidatePaths);

public sealed record ExifToolNotFoundResult(
    IReadOnlyList<string> CandidatePaths,
    string Message)
    : ExifToolDetectionResult(CandidatePaths);

public sealed record ExifToolUnableToRunResult(
    string ExecutablePath,
    string Message,
    int? ExitCode,
    string StandardOutput,
    string StandardError,
    IReadOnlyList<string> CandidatePaths)
    : ExifToolDetectionResult(CandidatePaths);

public sealed record ExifToolInvalidVersionResult(
    string ExecutablePath,
    string StandardOutput,
    string StandardError,
    IReadOnlyList<string> CandidatePaths)
    : ExifToolDetectionResult(CandidatePaths);

public sealed record ExifToolVersionCheckTimedOutResult(
    string ExecutablePath,
    TimeSpan Timeout,
    string? TerminationError,
    string StandardOutput,
    string StandardError,
    IReadOnlyList<string> CandidatePaths)
    : ExifToolDetectionResult(CandidatePaths);
