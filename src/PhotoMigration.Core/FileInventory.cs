namespace PhotoMigration.Core;

public static class FileInventory
{
    public static InventoryResult Create(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        var root = new DirectoryInfo(Path.GetFullPath(rootPath));
        ValidateRoot(root);

        var entries = new List<InventoryEntry>();
        var pendingDirectories = new Stack<DirectoryInfo>();
        pendingDirectories.Push(root);

        while (pendingDirectories.TryPop(out var directory))
        {
            foreach (var item in GetEntries(directory, root))
            {
                if ((GetAttributes(item, root) & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                if (item is DirectoryInfo childDirectory)
                {
                    pendingDirectories.Push(childDirectory);
                    continue;
                }

                if (item is FileInfo file)
                {
                    entries.Add(CreateEntry(file, root));
                }
            }
        }

        entries.Sort(static (left, right) =>
            StringComparer.Ordinal.Compare(left.RelativePath, right.RelativePath));

        return new InventoryResult(entries.AsReadOnly());
    }

    private static void ValidateRoot(DirectoryInfo root)
    {
        FileAttributes attributes;

        try
        {
            attributes = File.GetAttributes(root.FullName);
        }
        catch (DirectoryNotFoundException)
        {
            throw new DirectoryNotFoundException($"Input folder does not exist: '{root.FullName}'.");
        }
        catch (FileNotFoundException)
        {
            throw new DirectoryNotFoundException($"Input folder does not exist: '{root.FullName}'.");
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            throw new InventoryTraversalException(".", exception);
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new ArgumentException(
                $"Input folder must not be a symbolic link or reparse point: '{root.FullName}'.",
                "rootPath");
        }

        if ((attributes & FileAttributes.Directory) == 0)
        {
            throw new ArgumentException($"Input path is not a folder: '{root.FullName}'.", "rootPath");
        }
    }

    private static FileSystemInfo[] GetEntries(DirectoryInfo directory, DirectoryInfo root)
    {
        try
        {
            // Materialize each directory before continuing so enumeration errors cannot
            // produce a result that appears complete.
            return directory.GetFileSystemInfos();
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            throw new InventoryTraversalException(ToDisplayPath(root, directory.FullName), exception);
        }
    }

    private static FileAttributes GetAttributes(FileSystemInfo item, DirectoryInfo root)
    {
        try
        {
            return File.GetAttributes(item.FullName);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            throw new InventoryTraversalException(ToDisplayPath(root, item.FullName), exception);
        }
    }

    private static InventoryEntry CreateEntry(FileInfo file, DirectoryInfo root)
    {
        try
        {
            return new InventoryEntry(ToDisplayPath(root, file.FullName), file.Length);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            throw new InventoryTraversalException(ToDisplayPath(root, file.FullName), exception);
        }
    }

    private static string ToDisplayPath(DirectoryInfo root, string path)
    {
        var relativePath = Path.GetRelativePath(root.FullName, path);
        return relativePath
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');
    }
}
