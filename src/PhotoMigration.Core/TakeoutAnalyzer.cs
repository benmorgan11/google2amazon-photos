namespace PhotoMigration.Core;

public static class TakeoutAnalyzer
{
    public static TakeoutAnalysisResult Analyze(string rootPath)
    {
        var inventory = FileInventory.Create(rootPath);
        var classifications = TakeoutFileClassifier.Classify(inventory.Entries);

        var mediaCandidates = classifications
            .Where(classification => classification.Category is
                TakeoutFileCategory.PhotoCandidate or TakeoutFileCategory.VideoCandidate)
            .Select(classification => classification.Entry)
            .ToList();
        var jsonCandidates = classifications
            .Where(classification => classification.Category == TakeoutFileCategory.JsonCandidate)
            .Select(classification => classification.Entry)
            .ToList();
        var otherFiles = classifications
            .Where(classification => classification.Category == TakeoutFileCategory.Other)
            .Select(classification => classification.Entry)
            .ToList();

        var matches = TakeoutSidecarMatcher.Match(mediaCandidates, jsonCandidates);
        var analyzedRootPath = Path.GetFullPath(rootPath);
        var matchedMedia = new List<ParsedMediaResult>();
        var invalidSidecars = new List<InvalidSidecarResult>();
        var unmatchedMedia = new List<UnmatchedMediaResult>();
        var ambiguousMedia = new List<AmbiguousMediaSidecarResult>();
        var acceptedSidecarPaths = new HashSet<string>(StringComparer.Ordinal);

        foreach (var match in matches)
        {
            switch (match)
            {
                case MatchedMediaSidecarResult matched:
                    acceptedSidecarPaths.Add(matched.SidecarEntry.RelativePath);
                    ParseMatchedSidecar(
                        analyzedRootPath,
                        matched,
                        matchedMedia,
                        invalidSidecars);
                    break;
                case UnmatchedMediaResult unmatched:
                    unmatchedMedia.Add(unmatched);
                    break;
                case AmbiguousMediaSidecarResult ambiguous:
                    ambiguousMedia.Add(ambiguous);
                    break;
            }
        }

        var unusedJsonCandidates = jsonCandidates
            .Where(entry => !acceptedSidecarPaths.Contains(entry.RelativePath))
            .ToList();

        return new TakeoutAnalysisResult(
            matchedMedia.AsReadOnly(),
            invalidSidecars.AsReadOnly(),
            unmatchedMedia.AsReadOnly(),
            ambiguousMedia.AsReadOnly(),
            unusedJsonCandidates.AsReadOnly(),
            otherFiles.AsReadOnly());
    }

    private static void ParseMatchedSidecar(
        string analyzedRootPath,
        MatchedMediaSidecarResult matched,
        ICollection<ParsedMediaResult> matchedMedia,
        ICollection<InvalidSidecarResult> invalidSidecars)
    {
        var platformRelativePath = matched.SidecarEntry.RelativePath.Replace(
            '/',
            Path.DirectorySeparatorChar);
        var sidecarPath = Path.Combine(analyzedRootPath, platformRelativePath);

        try
        {
            var metadata = TakeoutSidecarParser.Parse(sidecarPath);
            matchedMedia.Add(new ParsedMediaResult(
                matched.MediaEntry,
                matched.SidecarEntry,
                matched.Rule,
                metadata));
        }
        catch (TakeoutSidecarParseException exception)
        {
            invalidSidecars.Add(new InvalidSidecarResult(
                matched.MediaEntry,
                matched.SidecarEntry,
                matched.Rule,
                exception));
        }
    }
}
