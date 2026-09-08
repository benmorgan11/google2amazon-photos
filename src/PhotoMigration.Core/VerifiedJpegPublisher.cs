namespace PhotoMigration.Core;

public static class VerifiedJpegPublisher
{
    public static VerifiedJpegPublicationResult Publish(
        string outputRootPath,
        JpegGpsMetadataWriteVerifiedResult verifiedResult) =>
        Publish(
            outputRootPath,
            verifiedResult,
            (temporaryPath, finalPath) =>
                File.Move(temporaryPath, finalPath, overwrite: false));

    internal static VerifiedJpegPublicationResult Publish(
        string outputRootPath,
        JpegGpsMetadataWriteVerifiedResult verifiedResult,
        Action<string, string> moveFile)
    {
        ArgumentNullException.ThrowIfNull(verifiedResult);
        ArgumentNullException.ThrowIfNull(moveFile);

        if (!TryNormalizeAbsolutePath(outputRootPath, out var outputRoot))
        {
            return Failure(
                verifiedResult,
                VerifiedJpegPublicationFailureKind.InvalidOutputRoot,
                "The output root must be a valid absolute path.");
        }

        var temporaryPath = verifiedResult.TemporaryCopyPath;
        var finalPath = verifiedResult.IntendedFinalDestinationPath;
        var pathJoinFailure = ValidateRetainedPaths(
            verifiedResult,
            outputRoot,
            temporaryPath,
            finalPath);
        if (pathJoinFailure is not null)
        {
            return pathJoinFailure;
        }

        var fileSystemFailure = ValidateFileSystem(
            verifiedResult,
            outputRoot,
            temporaryPath,
            finalPath);
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
            return Failure(
                verifiedResult,
                exception is FileNotFoundException or DirectoryNotFoundException
                    ? VerifiedJpegPublicationFailureKind.MissingTemporaryFile
                    : VerifiedJpegPublicationFailureKind.MoveFailure,
                $"The verified temporary JPEG could not be inspected: {exception.Message}");
        }

        // Recheck links and the destination immediately before the move. Portable
        // filesystem APIs cannot make validation and publication one atomic step.
        fileSystemFailure = ValidateFileSystem(
            verifiedResult,
            outputRoot,
            temporaryPath,
            finalPath);
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
            return Failure(
                verifiedResult,
                VerifiedJpegPublicationFailureKind.MoveFailure,
                $"The verified JPEG could not be published: {exception.Message}");
        }

        return new VerifiedJpegPublicationSuccessResult(
            verifiedResult,
            temporaryPath,
            finalPath,
            byteCount);
    }

    private static VerifiedJpegPublicationFailureResult? ValidateRetainedPaths(
        JpegGpsMetadataWriteVerifiedResult verifiedResult,
        string outputRoot,
        string temporaryPath,
        string finalPath)
    {
        var writeResult = verifiedResult.WriteResult;
        var stagingResult = writeResult.WritePlan.StagingResult;
        if (!StringComparer.Ordinal.Equals(
                temporaryPath, writeResult.TemporaryCopyPath)
            || !StringComparer.Ordinal.Equals(
                temporaryPath, stagingResult.TemporaryCopyPath)
            || !StringComparer.Ordinal.Equals(
                temporaryPath,
                writeResult.WritePlan.BaselineHashResult.TemporaryCopyPath)
            || !StringComparer.Ordinal.Equals(
                finalPath, writeResult.IntendedFinalDestinationPath)
            || !StringComparer.Ordinal.Equals(
                finalPath, stagingResult.IntendedFinalDestinationPath)
            || !StringComparer.Ordinal.Equals(
                finalPath, stagingResult.PlanningItem.AbsoluteDestinationPath))
        {
            return Failure(
                verifiedResult,
                VerifiedJpegPublicationFailureKind.MismatchedPaths,
                "The verification, write, staging, and destination paths do not match.");
        }

        if (!TryNormalizeAbsolutePath(temporaryPath, out var normalizedTemporary)
            || !TryNormalizeAbsolutePath(finalPath, out var normalizedFinal)
            || !StringComparer.Ordinal.Equals(temporaryPath, normalizedTemporary)
            || !StringComparer.Ordinal.Equals(finalPath, normalizedFinal)
            || !IsStrictlyBelow(outputRoot, temporaryPath)
            || !IsStrictlyBelow(outputRoot, finalPath)
            || !StringComparer.Ordinal.Equals(
                Path.GetDirectoryName(temporaryPath),
                Path.GetDirectoryName(finalPath))
            || StringComparer.Ordinal.Equals(temporaryPath, finalPath))
        {
            return Failure(
                verifiedResult,
                VerifiedJpegPublicationFailureKind.InvalidPath,
                "The temporary and final paths must be distinct normalized absolute " +
                "paths in one directory below the output root.");
        }

        return null;
    }

    private static VerifiedJpegPublicationFailureResult? ValidateFileSystem(
        JpegGpsMetadataWriteVerifiedResult verifiedResult,
        string outputRoot,
        string temporaryPath,
        string finalPath)
    {
        if (!TryGetAttributes(outputRoot, out var rootAttributes, out var rootError))
        {
            return Failure(
                verifiedResult,
                VerifiedJpegPublicationFailureKind.InvalidOutputRoot,
                rootError is null
                    ? $"The output root does not exist: '{outputRoot}'."
                    : $"The output root could not be inspected: {rootError}");
        }

        if ((rootAttributes & FileAttributes.ReparsePoint) != 0)
        {
            return Failure(
                verifiedResult,
                VerifiedJpegPublicationFailureKind.LinkedPath,
                $"The output root must not be a symbolic link or reparse point: '{outputRoot}'.");
        }

        if ((rootAttributes & FileAttributes.Directory) == 0)
        {
            return Failure(
                verifiedResult,
                VerifiedJpegPublicationFailureKind.InvalidOutputRoot,
                $"The output root is not a directory: '{outputRoot}'.");
        }

        foreach (var component in ComponentsBelowRoot(outputRoot, temporaryPath))
        {
            if (!TryGetAttributes(component, out var attributes, out var error))
            {
                return Failure(
                    verifiedResult,
                    component == temporaryPath
                        ? VerifiedJpegPublicationFailureKind.MissingTemporaryFile
                        : VerifiedJpegPublicationFailureKind.InvalidPath,
                    error is null
                        ? $"An output path component does not exist: '{component}'."
                        : $"An output path component could not be inspected: {error}");
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                return Failure(
                    verifiedResult,
                    VerifiedJpegPublicationFailureKind.LinkedPath,
                    $"The temporary path contains a symbolic link or reparse point: '{component}'.");
            }

            if (!StringComparer.Ordinal.Equals(component, temporaryPath)
                && (attributes & FileAttributes.Directory) == 0)
            {
                return Failure(
                    verifiedResult,
                    VerifiedJpegPublicationFailureKind.InvalidPath,
                    $"An output path component is not a directory: '{component}'.");
            }

            if (StringComparer.Ordinal.Equals(component, temporaryPath)
                && (attributes & (FileAttributes.Directory | FileAttributes.Device)) != 0)
            {
                return Failure(
                    verifiedResult,
                    VerifiedJpegPublicationFailureKind.NonRegularTemporaryFile,
                    $"The verified temporary path is not a regular file: '{temporaryPath}'.");
            }
        }

        foreach (var component in ComponentsBelowRoot(
                     outputRoot, Path.GetDirectoryName(finalPath)!))
        {
            if (!TryGetAttributes(component, out var attributes, out var error)
                || (attributes & FileAttributes.Directory) == 0)
            {
                return Failure(
                    verifiedResult,
                    VerifiedJpegPublicationFailureKind.InvalidPath,
                    error is null
                        ? $"The final destination directory is unavailable: '{component}'."
                        : $"The final destination directory could not be inspected: {error}");
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                return Failure(
                    verifiedResult,
                    VerifiedJpegPublicationFailureKind.LinkedPath,
                    $"The final path contains a symbolic link or reparse point: '{component}'.");
            }
        }

        if (TryGetAttributes(finalPath, out _, out var finalError))
        {
            return Failure(
                verifiedResult,
                VerifiedJpegPublicationFailureKind.ExistingDestination,
                $"The final destination already exists: '{finalPath}'.");
        }

        if (finalError is not null)
        {
            return Failure(
                verifiedResult,
                VerifiedJpegPublicationFailureKind.ExistingDestination,
                $"The final destination could not be confirmed absent: {finalError}");
        }

        return null;
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
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar,
                StringComparison.Ordinal)
            && !relative.StartsWith(".." + Path.AltDirectorySeparatorChar,
                StringComparison.Ordinal);
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

    private static VerifiedJpegPublicationFailureResult Failure(
        JpegGpsMetadataWriteVerifiedResult verifiedResult,
        VerifiedJpegPublicationFailureKind kind,
        string message) =>
        new(
            verifiedResult,
            verifiedResult.TemporaryCopyPath,
            verifiedResult.IntendedFinalDestinationPath,
            kind,
            message);
}
