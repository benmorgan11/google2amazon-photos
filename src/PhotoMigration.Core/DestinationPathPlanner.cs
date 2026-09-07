using System.Text;

namespace PhotoMigration.Core;

public static class DestinationPathPlanner
{
    public static DestinationPathPlanningResult Plan(
        string sourceRootPath,
        string outputRootPath,
        IEnumerable<InventoryEntry> mediaEntries)
    {
        ArgumentNullException.ThrowIfNull(mediaEntries);

        var issues = new List<DestinationPathPlanningIssue>();
        var sourceRoot = NormalizeRoot(
            sourceRootPath,
            DestinationPathPlanningIssueKind.InvalidSourceRoot,
            "Source root",
            issues);
        var outputRoot = NormalizeRoot(
            outputRootPath,
            DestinationPathPlanningIssueKind.InvalidOutputRoot,
            "Output root",
            issues);

        if (sourceRoot is null || outputRoot is null)
        {
            return Failure(issues);
        }

        if (IsSameOrDescendant(sourceRoot, outputRoot)
            || IsSameOrDescendant(outputRoot, sourceRoot))
        {
            issues.Add(new DestinationPathPlanningIssue(
                DestinationPathPlanningIssueKind.OverlappingRoots,
                null,
                null,
                $"Source root '{sourceRoot}' and output root '{outputRoot}' must not overlap."));
            return Failure(issues);
        }

        var validatedEntries = new List<ValidatedEntry>();
        foreach (var entry in mediaEntries)
        {
            if (entry is null)
            {
                issues.Add(new DestinationPathPlanningIssue(
                    DestinationPathPlanningIssueKind.InvalidLogicalPath,
                    null,
                    null,
                    "The media entry collection contains a null entry."));
                continue;
            }

            ValidateEntry(entry, sourceRoot, outputRoot, validatedEntries, issues);
        }

        validatedEntries.Sort(static (left, right) =>
        {
            var pathComparison = StringComparer.Ordinal.Compare(
                left.MediaEntry.RelativePath,
                right.MediaEntry.RelativePath);
            return pathComparison != 0
                ? pathComparison
                : left.MediaEntry.SizeInBytes.CompareTo(right.MediaEntry.SizeInBytes);
        });

        AddDuplicateLogicalPathIssues(validatedEntries, issues);
        AddDestinationCollisionIssues(validatedEntries, issues);
        AddFileDirectoryConflictIssues(validatedEntries, outputRoot, issues);

        if (issues.Count > 0)
        {
            return Failure(issues);
        }

        return new DestinationPathPlanningSuccessResult(
            validatedEntries
                .OrderBy(entry => entry.MediaEntry.RelativePath, StringComparer.Ordinal)
                .Select(entry => new DestinationPathPlanningItem(
                    entry.MediaEntry,
                    entry.SourcePath,
                    entry.DestinationPath))
                .ToList()
                .AsReadOnly());
    }

    private static string? NormalizeRoot(
        string? rootPath,
        DestinationPathPlanningIssueKind issueKind,
        string description,
        ICollection<DestinationPathPlanningIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || !Path.IsPathFullyQualified(rootPath))
        {
            issues.Add(new DestinationPathPlanningIssue(
                issueKind,
                null,
                null,
                $"{description} must be an absolute path."));
            return null;
        }

        try
        {
            var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
            _ = CollisionKey(normalized);
            return normalized;
        }
        catch (Exception exception) when (IsPathException(exception))
        {
            issues.Add(new DestinationPathPlanningIssue(
                issueKind,
                null,
                null,
                $"{description} is invalid: {exception.Message}"));
            return null;
        }
    }

    private static void ValidateEntry(
        InventoryEntry entry,
        string sourceRoot,
        string outputRoot,
        ICollection<ValidatedEntry> validatedEntries,
        ICollection<DestinationPathPlanningIssue> issues)
    {
        var relativePath = entry.RelativePath;
        if (string.IsNullOrEmpty(relativePath))
        {
            AddEntryIssue(
                issues,
                DestinationPathPlanningIssueKind.EmptyLogicalPath,
                entry,
                "The media entry has an empty logical path.");
            return;
        }

        if (IsRootedLogicalPath(relativePath))
        {
            AddEntryIssue(
                issues,
                DestinationPathPlanningIssueKind.RootedLogicalPath,
                entry,
                $"Inventory path '{relativePath}' must be relative.");
            return;
        }

        var segments = OperatingSystem.IsWindows()
            ? relativePath.Split(['/', '\\'], StringSplitOptions.None)
            : relativePath.Split('/', StringSplitOptions.None);
        if (segments.Any(segment => segment.Length == 0))
        {
            AddEntryIssue(
                issues,
                DestinationPathPlanningIssueKind.EmptyPathSegment,
                entry,
                $"Inventory path '{relativePath}' contains an empty path segment.");
            return;
        }

        if (segments.Any(segment => segment is "." or ".."))
        {
            AddEntryIssue(
                issues,
                DestinationPathPlanningIssueKind.TraversalSegment,
                entry,
                $"Inventory path '{relativePath}' contains a '.' or '..' traversal segment.");
            return;
        }

        string sourcePath;
        string destinationPath;
        string destinationCollisionKey;
        try
        {
            sourcePath = Path.GetFullPath(Combine(sourceRoot, segments));
            destinationPath = Path.GetFullPath(Combine(outputRoot, segments));
            destinationCollisionKey = CollisionKey(destinationPath);
        }
        catch (Exception exception) when (IsPathException(exception))
        {
            AddEntryIssue(
                issues,
                DestinationPathPlanningIssueKind.InvalidLogicalPath,
                entry,
                $"Inventory path '{relativePath}' is invalid: {exception.Message}");
            return;
        }

        if (!IsStrictDescendant(sourcePath, sourceRoot))
        {
            AddEntryIssue(
                issues,
                DestinationPathPlanningIssueKind.SourcePathOutsideRoot,
                entry,
                $"Inventory path '{relativePath}' resolves outside the source root.");
            return;
        }

        if (!IsStrictDescendant(destinationPath, outputRoot))
        {
            AddEntryIssue(
                issues,
                DestinationPathPlanningIssueKind.DestinationPathOutsideRoot,
                entry,
                $"Inventory path '{relativePath}' resolves outside the output root.");
            return;
        }

        validatedEntries.Add(new ValidatedEntry(
            entry,
            string.Join('/', segments),
            sourcePath,
            destinationPath,
            destinationCollisionKey));
    }

    private static void AddDuplicateLogicalPathIssues(
        IReadOnlyList<ValidatedEntry> entries,
        ICollection<DestinationPathPlanningIssue> issues)
    {
        foreach (var group in entries
                     .GroupBy(entry => entry.LogicalPath, StringComparer.Ordinal)
                     .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var duplicates = group.ToList();
            for (var index = 1; index < duplicates.Count; index++)
            {
                AddPairIssue(
                    issues,
                    DestinationPathPlanningIssueKind.DuplicateLogicalPath,
                    duplicates[0].MediaEntry,
                    duplicates[index].MediaEntry,
                    $"Inventory path '{group.Key}' was supplied more than once.");
            }
        }
    }

    private static void AddDestinationCollisionIssues(
        IReadOnlyList<ValidatedEntry> entries,
        ICollection<DestinationPathPlanningIssue> issues)
    {
        foreach (var group in entries
                     .GroupBy(entry => entry.DestinationCollisionKey, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            var distinctLogicalPaths = group
                .GroupBy(entry => entry.LogicalPath, StringComparer.Ordinal)
                .Select(logicalGroup => logicalGroup.First())
                .OrderBy(entry => entry.MediaEntry.RelativePath, StringComparer.Ordinal)
                .ToList();

            for (var index = 1; index < distinctLogicalPaths.Count; index++)
            {
                var first = distinctLogicalPaths[0].MediaEntry;
                var conflicting = distinctLogicalPaths[index].MediaEntry;
                AddPairIssue(
                    issues,
                    DestinationPathPlanningIssueKind.DestinationCollision,
                    first,
                    conflicting,
                    $"Inventory paths '{first.RelativePath}' and " +
                    $"'{conflicting.RelativePath}' resolve to the same destination name.");
            }
        }
    }

    private static void AddFileDirectoryConflictIssues(
        IReadOnlyList<ValidatedEntry> entries,
        string outputRoot,
        ICollection<DestinationPathPlanningIssue> issues)
    {
        var representatives = entries
            .GroupBy(entry => entry.LogicalPath, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
        var entriesByDestination = representatives
            .GroupBy(entry => entry.DestinationCollisionKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderBy(entry => entry.MediaEntry.RelativePath, StringComparer.Ordinal)
                    .ToList(),
                StringComparer.OrdinalIgnoreCase);

        foreach (var descendant in representatives
                     .OrderBy(entry => entry.MediaEntry.RelativePath, StringComparer.Ordinal))
        {
            var parentPath = Path.GetDirectoryName(descendant.DestinationPath);
            while (parentPath is not null && !PathsEqual(parentPath, outputRoot))
            {
                if (entriesByDestination.TryGetValue(CollisionKey(parentPath), out var ancestors))
                {
                    foreach (var ancestor in ancestors)
                    {
                        AddPairIssue(
                            issues,
                            DestinationPathPlanningIssueKind.FileDirectoryConflict,
                            ancestor.MediaEntry,
                            descendant.MediaEntry,
                            $"Destination '{ancestor.MediaEntry.RelativePath}' would be a file, " +
                            $"but '{descendant.MediaEntry.RelativePath}' requires it as a directory.");
                    }
                }

                parentPath = Path.GetDirectoryName(parentPath);
            }
        }
    }

    private static DestinationPathPlanningFailureResult Failure(
        IEnumerable<DestinationPathPlanningIssue> issues) =>
        new(
            issues
                .OrderBy(issue => issue.MediaEntry?.RelativePath, StringComparer.Ordinal)
                .ThenBy(issue => issue.ConflictingMediaEntry?.RelativePath, StringComparer.Ordinal)
                .ThenBy(issue => issue.Kind)
                .ThenBy(issue => issue.Message, StringComparer.Ordinal)
                .ToList()
                .AsReadOnly());

    private static void AddEntryIssue(
        ICollection<DestinationPathPlanningIssue> issues,
        DestinationPathPlanningIssueKind kind,
        InventoryEntry entry,
        string message) =>
        issues.Add(new DestinationPathPlanningIssue(kind, entry, null, message));

    private static void AddPairIssue(
        ICollection<DestinationPathPlanningIssue> issues,
        DestinationPathPlanningIssueKind kind,
        InventoryEntry first,
        InventoryEntry second,
        string message) =>
        issues.Add(new DestinationPathPlanningIssue(kind, first, second, message));

    private static bool IsRootedLogicalPath(string path) =>
        Path.IsPathRooted(path)
        || path[0] is '/' or '\\'
        || (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':');

    private static string Combine(string root, IEnumerable<string> segments)
    {
        var result = root;
        foreach (var segment in segments)
        {
            result = Path.Combine(result, segment);
        }

        return result;
    }

    private static bool IsStrictDescendant(string path, string root) =>
        !PathsEqual(path, root) && IsSameOrDescendant(path, root);

    private static bool IsSameOrDescendant(string path, string root)
    {
        var normalizedPath = CollisionKey(path);
        var normalizedRoot = CollisionKey(root);
        if (StringComparer.OrdinalIgnoreCase.Equals(normalizedPath, normalizedRoot))
        {
            return true;
        }

        if (!normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return EndsWithDirectorySeparator(normalizedRoot)
               || (normalizedPath.Length > normalizedRoot.Length
                   && IsDirectorySeparator(normalizedPath[normalizedRoot.Length]));
    }

    private static bool PathsEqual(string left, string right) =>
        StringComparer.OrdinalIgnoreCase.Equals(CollisionKey(left), CollisionKey(right));

    private static string CollisionKey(string path) =>
        path.Normalize(NormalizationForm.FormC);

    private static bool EndsWithDirectorySeparator(string path) =>
        path.Length > 0 && IsDirectorySeparator(path[^1]);

    private static bool IsDirectorySeparator(char value) =>
        value == Path.DirectorySeparatorChar || value == Path.AltDirectorySeparatorChar;

    private static bool IsPathException(Exception exception) =>
        exception is ArgumentException or NotSupportedException or PathTooLongException;

    private sealed record ValidatedEntry(
        InventoryEntry MediaEntry,
        string LogicalPath,
        string SourcePath,
        string DestinationPath,
        string DestinationCollisionKey);
}
