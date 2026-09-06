namespace PhotoMigration.Core;

public static class ExactSidecarMatcher
{
    private const string SupplementalSuffix = ".supplemental-metadata.json";
    private const string LegacySuffix = ".json";

    public static IReadOnlyList<MediaSidecarMatchResult> Match(
        IEnumerable<InventoryEntry> mediaCandidates,
        IEnumerable<InventoryEntry> jsonCandidates)
    {
        ArgumentNullException.ThrowIfNull(mediaCandidates);
        ArgumentNullException.ThrowIfNull(jsonCandidates);

        var media = MaterializeUniqueEntries(mediaCandidates, nameof(mediaCandidates));
        var sidecars = MaterializeUniqueEntries(jsonCandidates, nameof(jsonCandidates));
        var sidecarsByPath = sidecars.ToDictionary(
            entry => entry.RelativePath,
            StringComparer.Ordinal);

        var candidatesByMediaPath = new Dictionary<string, List<SidecarMatchCandidate>>(
            StringComparer.Ordinal);
        var claimCountsBySidecarPath = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var mediaEntry in media)
        {
            var candidates = FindCandidates(mediaEntry, sidecarsByPath);
            candidatesByMediaPath.Add(mediaEntry.RelativePath, candidates);

            foreach (var candidate in candidates)
            {
                claimCountsBySidecarPath.TryGetValue(
                    candidate.SidecarEntry.RelativePath,
                    out var claimCount);
                claimCountsBySidecarPath[candidate.SidecarEntry.RelativePath] = claimCount + 1;
            }
        }

        var results = new List<MediaSidecarMatchResult>(media.Count);

        foreach (var mediaEntry in media)
        {
            var candidates = candidatesByMediaPath[mediaEntry.RelativePath];

            if (candidates.Count == 0)
            {
                results.Add(new UnmatchedMediaResult(mediaEntry));
                continue;
            }

            var hasSharedCandidate = candidates.Any(candidate =>
                claimCountsBySidecarPath[candidate.SidecarEntry.RelativePath] > 1);

            if (candidates.Count > 1 || hasSharedCandidate)
            {
                results.Add(new AmbiguousMediaSidecarResult(mediaEntry, candidates.AsReadOnly()));
                continue;
            }

            var match = candidates[0];
            results.Add(new MatchedMediaSidecarResult(
                mediaEntry,
                match.SidecarEntry,
                match.Rule));
        }

        return results.AsReadOnly();
    }

    private static List<InventoryEntry> MaterializeUniqueEntries(
        IEnumerable<InventoryEntry> entries,
        string parameterName)
    {
        var sortedEntries = entries
            .OrderBy(entry => entry.RelativePath, StringComparer.Ordinal)
            .ToList();

        for (var index = 1; index < sortedEntries.Count; index++)
        {
            if (StringComparer.Ordinal.Equals(
                    sortedEntries[index - 1].RelativePath,
                    sortedEntries[index].RelativePath))
            {
                throw new ArgumentException(
                    $"Entries must have unique logical paths. Duplicate: '{sortedEntries[index].RelativePath}'.",
                    parameterName);
            }
        }

        return sortedEntries;
    }

    private static List<SidecarMatchCandidate> FindCandidates(
        InventoryEntry mediaEntry,
        IReadOnlyDictionary<string, InventoryEntry> sidecarsByPath)
    {
        var candidates = new List<SidecarMatchCandidate>(capacity: 2);

        AddCandidate(
            mediaEntry.RelativePath + LegacySuffix,
            ExactSidecarMatchRule.LegacyJson,
            sidecarsByPath,
            candidates);
        AddCandidate(
            mediaEntry.RelativePath + SupplementalSuffix,
            ExactSidecarMatchRule.SupplementalMetadataJson,
            sidecarsByPath,
            candidates);

        candidates.Sort(static (left, right) =>
            StringComparer.Ordinal.Compare(
                left.SidecarEntry.RelativePath,
                right.SidecarEntry.RelativePath));

        return candidates;
    }

    private static void AddCandidate(
        string expectedPath,
        ExactSidecarMatchRule rule,
        IReadOnlyDictionary<string, InventoryEntry> sidecarsByPath,
        ICollection<SidecarMatchCandidate> candidates)
    {
        if (sidecarsByPath.TryGetValue(expectedPath, out var sidecarEntry))
        {
            candidates.Add(new SidecarMatchCandidate(sidecarEntry, rule));
        }
    }
}
