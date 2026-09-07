namespace PhotoMigration.Core;

public sealed record DestinationPathPlanningItem(
    InventoryEntry MediaEntry,
    string AbsoluteSourcePath,
    string AbsoluteDestinationPath);

public enum DestinationPathPlanningIssueKind
{
    InvalidSourceRoot,
    InvalidOutputRoot,
    OverlappingRoots,
    EmptyLogicalPath,
    RootedLogicalPath,
    EmptyPathSegment,
    TraversalSegment,
    InvalidLogicalPath,
    SourcePathOutsideRoot,
    DestinationPathOutsideRoot,
    DuplicateLogicalPath,
    DestinationCollision,
    FileDirectoryConflict
}

public sealed record DestinationPathPlanningIssue(
    DestinationPathPlanningIssueKind Kind,
    InventoryEntry? MediaEntry,
    InventoryEntry? ConflictingMediaEntry,
    string Message);

public abstract record DestinationPathPlanningResult;

public sealed record DestinationPathPlanningSuccessResult(
    IReadOnlyList<DestinationPathPlanningItem> Items)
    : DestinationPathPlanningResult;

public sealed record DestinationPathPlanningFailureResult(
    IReadOnlyList<DestinationPathPlanningIssue> Issues)
    : DestinationPathPlanningResult;
