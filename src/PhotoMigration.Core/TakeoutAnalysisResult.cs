namespace PhotoMigration.Core;

public sealed record ParsedMediaResult(
    InventoryEntry MediaEntry,
    InventoryEntry SidecarEntry,
    SidecarMatchRule MatchRule,
    TakeoutSidecarMetadata Metadata);

public sealed record InvalidSidecarResult(
    InventoryEntry MediaEntry,
    InventoryEntry SidecarEntry,
    SidecarMatchRule MatchRule,
    TakeoutSidecarParseException Error);

public sealed class TakeoutAnalysisResult
{
    public TakeoutAnalysisResult(
        IReadOnlyList<ParsedMediaResult> matchedMedia,
        IReadOnlyList<InvalidSidecarResult> invalidSidecars,
        IReadOnlyList<UnmatchedMediaResult> unmatchedMedia,
        IReadOnlyList<AmbiguousMediaSidecarResult> ambiguousMedia,
        IReadOnlyList<InventoryEntry> unusedJsonCandidates,
        IReadOnlyList<InventoryEntry> otherFiles)
    {
        MatchedMedia = matchedMedia;
        InvalidSidecars = invalidSidecars;
        UnmatchedMedia = unmatchedMedia;
        AmbiguousMedia = ambiguousMedia;
        UnusedJsonCandidates = unusedJsonCandidates;
        OtherFiles = otherFiles;
    }

    public IReadOnlyList<ParsedMediaResult> MatchedMedia { get; }

    public IReadOnlyList<InvalidSidecarResult> InvalidSidecars { get; }

    public IReadOnlyList<UnmatchedMediaResult> UnmatchedMedia { get; }

    public IReadOnlyList<AmbiguousMediaSidecarResult> AmbiguousMedia { get; }

    public IReadOnlyList<InventoryEntry> UnusedJsonCandidates { get; }

    public IReadOnlyList<InventoryEntry> OtherFiles { get; }
}
