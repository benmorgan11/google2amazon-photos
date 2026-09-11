namespace PhotoMigration.Core;

internal enum VerifiedMetadataFilePublicationFailureKind
{
    InvalidOutputRoot,
    InvalidPath,
    MissingTemporaryFile,
    LinkedPath,
    NonRegularTemporaryFile,
    ExistingDestination,
    MoveFailure
}

internal sealed record VerifiedMetadataFilePublicationOutcome(
    bool Succeeded,
    string TemporaryPath,
    string FinalPath,
    long? PublishedByteCount = null,
    VerifiedMetadataFilePublicationFailureKind? FailureKind = null,
    string Message = "");

internal static class VerifiedMetadataFilePublisher
{
    internal static VerifiedMetadataFilePublicationOutcome Publish(
        string outputRootPath,
        string temporaryPath,
        string finalPath,
        Action<string, string> moveFile)
    {
        ArgumentNullException.ThrowIfNull(moveFile);

        if (!TryNormalizeAbsolutePath(outputRootPath, out var outputRoot))
        {
            return Failure(temporaryPath, finalPath,
                VerifiedMetadataFilePublicationFailureKind.InvalidOutputRoot,
                "The output root must be a valid absolute path.");
        }

        if (!TryNormalizeAbsolutePath(temporaryPath, out var normalizedTemporary)
            || !TryNormalizeAbsolutePath(finalPath, out var normalizedFinal)
            || !StringComparer.Ordinal.Equals(temporaryPath, normalizedTemporary)
            || !StringComparer.Ordinal.Equals(finalPath, normalizedFinal)
            || !IsStrictlyBelow(outputRoot, temporaryPath)
            || !IsStrictlyBelow(outputRoot, finalPath)
            || !StringComparer.Ordinal.Equals(
                Path.GetDirectoryName(temporaryPath), Path.GetDirectoryName(finalPath))
            || StringComparer.Ordinal.Equals(temporaryPath, finalPath))
        {
            return Failure(temporaryPath, finalPath,
                VerifiedMetadataFilePublicationFailureKind.InvalidPath,
                "The temporary and final paths must be distinct normalized absolute " +
                "paths in one directory below the output root.");
        }

        var fileSystemFailure = ValidateFileSystem(
            outputRoot, temporaryPath, finalPath);
        if (fileSystemFailure is not null)
        {
            return fileSystemFailure;
        }

        long byteCount;
        try
        {
            byteCount = new FileInfo(temporaryPath).Length;
        }
        catch (Exception exception) when (IsFileSystemException(exception))
        {
            return Failure(temporaryPath, finalPath,
                exception is FileNotFoundException or DirectoryNotFoundException
                    ? VerifiedMetadataFilePublicationFailureKind.MissingTemporaryFile
                    : VerifiedMetadataFilePublicationFailureKind.MoveFailure,
                $"The verified temporary file could not be inspected: {exception.Message}");
        }

        // Portable filesystem APIs cannot make validation and publication one
        // atomic step, so repeat the checks immediately before the move.
        fileSystemFailure = ValidateFileSystem(outputRoot, temporaryPath, finalPath);
        if (fileSystemFailure is not null)
        {
            return fileSystemFailure;
        }

        try
        {
            moveFile(temporaryPath, finalPath);
        }
        catch (Exception exception) when (IsFileSystemException(exception))
        {
            return Failure(temporaryPath, finalPath,
                VerifiedMetadataFilePublicationFailureKind.MoveFailure,
                $"The verified file could not be published: {exception.Message}");
        }

        return new VerifiedMetadataFilePublicationOutcome(
            true, temporaryPath, finalPath, byteCount);
    }

    private static VerifiedMetadataFilePublicationOutcome? ValidateFileSystem(
        string outputRoot,
        string temporaryPath,
        string finalPath)
    {
        if (!TryGetAttributes(outputRoot, out var rootAttributes, out var rootError))
        {
            return Failure(temporaryPath, finalPath,
                VerifiedMetadataFilePublicationFailureKind.InvalidOutputRoot,
                rootError is null
                    ? $"The output root does not exist: '{outputRoot}'."
                    : $"The output root could not be inspected: {rootError}");
        }

        if ((rootAttributes & FileAttributes.ReparsePoint) != 0)
        {
            return Failure(temporaryPath, finalPath,
                VerifiedMetadataFilePublicationFailureKind.LinkedPath,
                $"The output root must not be a symbolic link or reparse point: '{outputRoot}'.");
        }

        if ((rootAttributes & FileAttributes.Directory) == 0)
        {
            return Failure(temporaryPath, finalPath,
                VerifiedMetadataFilePublicationFailureKind.InvalidOutputRoot,
                $"The output root is not a directory: '{outputRoot}'.");
        }

        foreach (var component in ComponentsBelowRoot(outputRoot, temporaryPath))
        {
            if (!TryGetAttributes(component, out var attributes, out var error))
            {
                return Failure(temporaryPath, finalPath,
                    component == temporaryPath
                        ? VerifiedMetadataFilePublicationFailureKind.MissingTemporaryFile
                        : VerifiedMetadataFilePublicationFailureKind.InvalidPath,
                    error is null
                        ? $"An output path component does not exist: '{component}'."
                        : $"An output path component could not be inspected: {error}");
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                return Failure(temporaryPath, finalPath,
                    VerifiedMetadataFilePublicationFailureKind.LinkedPath,
                    $"The temporary path contains a symbolic link or reparse point: '{component}'.");
            }

            if (!StringComparer.Ordinal.Equals(component, temporaryPath)
                && (attributes & FileAttributes.Directory) == 0)
            {
                return Failure(temporaryPath, finalPath,
                    VerifiedMetadataFilePublicationFailureKind.InvalidPath,
                    $"An output path component is not a directory: '{component}'.");
            }

            if (StringComparer.Ordinal.Equals(component, temporaryPath)
                && (attributes & (FileAttributes.Directory | FileAttributes.Device)) != 0)
            {
                return Failure(temporaryPath, finalPath,
                    VerifiedMetadataFilePublicationFailureKind.NonRegularTemporaryFile,
                    $"The verified temporary path is not a regular file: '{temporaryPath}'.");
            }
        }

        foreach (var component in ComponentsBelowRoot(
                     outputRoot, Path.GetDirectoryName(finalPath)!))
        {
            if (!TryGetAttributes(component, out var attributes, out var error)
                || (attributes & FileAttributes.Directory) == 0)
            {
                return Failure(temporaryPath, finalPath,
                    VerifiedMetadataFilePublicationFailureKind.InvalidPath,
                    error is null
                        ? $"The final destination directory is unavailable: '{component}'."
                        : $"The final destination directory could not be inspected: {error}");
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                return Failure(temporaryPath, finalPath,
                    VerifiedMetadataFilePublicationFailureKind.LinkedPath,
                    $"The final path contains a symbolic link or reparse point: '{component}'.");
            }
        }

        if (TryGetAttributes(finalPath, out _, out var finalError))
        {
            return Failure(temporaryPath, finalPath,
                VerifiedMetadataFilePublicationFailureKind.ExistingDestination,
                $"The final destination already exists: '{finalPath}'.");
        }

        return finalError is null
            ? null
            : Failure(temporaryPath, finalPath,
                VerifiedMetadataFilePublicationFailureKind.ExistingDestination,
                $"The final destination could not be confirmed absent: {finalError}");
    }

    private static bool TryNormalizeAbsolutePath(string? path, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            return false;
        }

        try
        {
            normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException
                                          or NotSupportedException
                                          or PathTooLongException)
        {
            return false;
        }
    }

    private static bool IsStrictlyBelow(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative)
               && relative != "."
               && relative != ".."
               && !relative.StartsWith(
                   ".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
               && !relative.StartsWith(
                   ".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static IReadOnlyList<string> ComponentsBelowRoot(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        var components = new List<string>();
        var current = root;
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            components.Add(current);
        }

        return components.AsReadOnly();
    }

    private static bool TryGetAttributes(
        string path,
        out FileAttributes attributes,
        out string? error)
    {
        error = null;
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            attributes = default;
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            attributes = default;
            return false;
        }
        catch (Exception exception) when (IsFileSystemException(exception))
        {
            attributes = default;
            error = exception.Message;
            return false;
        }
    }

    private static bool IsFileSystemException(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or System.Security.SecurityException;

    private static VerifiedMetadataFilePublicationOutcome Failure(
        string temporaryPath,
        string finalPath,
        VerifiedMetadataFilePublicationFailureKind kind,
        string message) =>
        new(false, temporaryPath, finalPath, FailureKind: kind, Message: message);
}
