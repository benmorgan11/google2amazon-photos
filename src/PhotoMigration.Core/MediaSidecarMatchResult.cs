namespace PhotoMigration.Core;

public abstract record MediaSidecarMatchResult(InventoryEntry MediaEntry);

public sealed record MatchedMediaSidecarResult(
    InventoryEntry MediaEntry,
    InventoryEntry SidecarEntry,
    SidecarMatchRule Rule)
    : MediaSidecarMatchResult(MediaEntry);

public sealed record AmbiguousMediaSidecarResult(
    InventoryEntry MediaEntry,
    IReadOnlyList<SidecarMatchCandidate> Candidates)
    : MediaSidecarMatchResult(MediaEntry);

public sealed record UnmatchedMediaResult(InventoryEntry MediaEntry)
    : MediaSidecarMatchResult(MediaEntry);
