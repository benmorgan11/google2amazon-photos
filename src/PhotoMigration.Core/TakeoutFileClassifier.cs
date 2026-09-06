namespace PhotoMigration.Core;

public static class TakeoutFileClassifier
{
    private const string JsonExtension = ".json";

    private static readonly HashSet<string> PhotoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg",
        ".jpeg",
        ".png",
        ".gif",
        ".webp",
        ".heic",
        ".heif",
        ".tif",
        ".tiff",
        ".dng"
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4",
        ".mov",
        ".m4v",
        ".avi",
        ".mpg",
        ".mpeg",
        ".3gp",
        ".3g2",
        ".mkv",
        ".webm",
        ".mts",
        ".m2ts"
    };

    public static IReadOnlyList<TakeoutFileClassification> Classify(
        IEnumerable<InventoryEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var sortedEntries = entries
            .OrderBy(entry => entry.RelativePath, StringComparer.Ordinal)
            .ToList();

        RejectDuplicatePaths(sortedEntries, nameof(entries));

        return sortedEntries
            .Select(entry => new TakeoutFileClassification(
                entry,
                DetermineCategory(entry.RelativePath)))
            .ToList()
            .AsReadOnly();
    }

    private static void RejectDuplicatePaths(
        IReadOnlyList<InventoryEntry> entries,
        string parameterName)
    {
        for (var index = 1; index < entries.Count; index++)
        {
            if (StringComparer.Ordinal.Equals(
                    entries[index - 1].RelativePath,
                    entries[index].RelativePath))
            {
                throw new ArgumentException(
                    $"Entries must have unique logical paths. Duplicate: '{entries[index].RelativePath}'.",
                    parameterName);
            }
        }
    }

    private static TakeoutFileCategory DetermineCategory(string relativePath)
    {
        var extension = GetFinalExtension(relativePath);

        if (StringComparer.OrdinalIgnoreCase.Equals(extension, JsonExtension))
        {
            return TakeoutFileCategory.JsonCandidate;
        }

        if (PhotoExtensions.Contains(extension))
        {
            return TakeoutFileCategory.PhotoCandidate;
        }

        if (VideoExtensions.Contains(extension))
        {
            return TakeoutFileCategory.VideoCandidate;
        }

        return TakeoutFileCategory.Other;
    }

    private static string GetFinalExtension(string relativePath)
    {
        var fileNameStart = relativePath.LastIndexOf('/') + 1;
        var extensionStart = relativePath.LastIndexOf('.');

        if (extensionStart <= fileNameStart || extensionStart == relativePath.Length - 1)
        {
            return string.Empty;
        }

        return relativePath[extensionStart..];
    }
}
