namespace PhotoMigration.Core;

public static class TakeoutMetadataPlanner
{
    private static readonly TakeoutSidecarMetadata EmptySidecarMetadata = new(
        Title: null,
        Description: null,
        CreationTime: null,
        PhotoTakenTime: null,
        Latitude: null,
        Longitude: null,
        Altitude: null,
        Url: null);

    public static TakeoutMetadataPlanningResult Plan(
        string rootPath,
        string exifToolExecutablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(exifToolExecutablePath);

        var analysisResult = TakeoutAnalyzer.Analyze(rootPath);
        var analyzedRootPath = Path.GetFullPath(rootPath);
        var absoluteExifToolPath = Path.GetFullPath(exifToolExecutablePath);
        var itemsByPath = new Dictionary<string, TakeoutMetadataPlanningItem>(
            StringComparer.Ordinal);

        foreach (var matched in analysisResult.MatchedMedia)
        {
            AddItem(
                itemsByPath,
                analyzedRootPath,
                absoluteExifToolPath,
                matched.MediaEntry,
                new MatchedTakeoutSidecarState(matched),
                matched.Metadata);
        }

        foreach (var unmatched in analysisResult.UnmatchedMedia)
        {
            AddItem(
                itemsByPath,
                analyzedRootPath,
                absoluteExifToolPath,
                unmatched.MediaEntry,
                new UnmatchedTakeoutSidecarState(unmatched),
                EmptySidecarMetadata);
        }

        foreach (var invalid in analysisResult.InvalidSidecars)
        {
            AddItem(
                itemsByPath,
                analyzedRootPath,
                absoluteExifToolPath,
                invalid.MediaEntry,
                new InvalidTakeoutSidecarState(invalid),
                EmptySidecarMetadata);
        }

        foreach (var ambiguous in analysisResult.AmbiguousMedia)
        {
            AddItem(
                itemsByPath,
                analyzedRootPath,
                absoluteExifToolPath,
                ambiguous.MediaEntry,
                new AmbiguousTakeoutSidecarState(ambiguous),
                EmptySidecarMetadata);
        }

        var items = itemsByPath.Values
            .OrderBy(item => item.MediaEntry.RelativePath, StringComparer.Ordinal)
            .ToList()
            .AsReadOnly();

        return new TakeoutMetadataPlanningResult(analysisResult, items);
    }

    private static void AddItem(
        IDictionary<string, TakeoutMetadataPlanningItem> itemsByPath,
        string analyzedRootPath,
        string exifToolExecutablePath,
        InventoryEntry mediaEntry,
        TakeoutPlanningSidecarState sidecarState,
        TakeoutSidecarMetadata sidecarMetadata)
    {
        var mediaPath = ResolveInventoryPath(analyzedRootPath, mediaEntry.RelativePath);
        var embeddedReadResult = ExifToolMetadataReader.Read(
            mediaPath,
            exifToolExecutablePath);
        var metadataPlan = MediaMetadataPlanBuilder.Build(
            embeddedReadResult,
            sidecarMetadata);
        var item = new TakeoutMetadataPlanningItem(
            mediaEntry,
            sidecarState,
            metadataPlan);

        if (!itemsByPath.TryAdd(mediaEntry.RelativePath, item))
        {
            throw new InvalidOperationException(
                $"Analysis produced duplicate media path '{mediaEntry.RelativePath}'.");
        }
    }

    private static string ResolveInventoryPath(
        string analyzedRootPath,
        string relativePath)
    {
        var platformRelativePath = relativePath
            .Replace('/', Path.DirectorySeparatorChar);

        if (Path.IsPathRooted(platformRelativePath))
        {
            throw EscapingPath(relativePath);
        }

        var absolutePath = Path.GetFullPath(
            Path.Combine(analyzedRootPath, platformRelativePath));
        var verifiedRelativePath = Path.GetRelativePath(
            analyzedRootPath,
            absolutePath);

        if (Path.IsPathRooted(verifiedRelativePath)
            || string.Equals(verifiedRelativePath, "..", StringComparison.Ordinal)
            || verifiedRelativePath.StartsWith(
                $"..{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal)
            || verifiedRelativePath.StartsWith(
                $"..{Path.AltDirectorySeparatorChar}",
                StringComparison.Ordinal))
        {
            throw EscapingPath(relativePath);
        }

        return absolutePath;
    }

    private static InvalidOperationException EscapingPath(string relativePath) =>
        new(
            $"Inventory path '{relativePath}' resolves outside the analyzed root.");
}
