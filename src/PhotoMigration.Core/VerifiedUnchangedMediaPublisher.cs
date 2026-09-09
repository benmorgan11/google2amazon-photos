using System.Security.Cryptography;

namespace PhotoMigration.Core;

public static class VerifiedUnchangedMediaPublisher
{
    private const int BufferSize = 81_920;

    public static VerifiedUnchangedMediaPublicationResult Publish(
        string outputRootPath,
        VerifiedMediaFileStagingSuccessResult stagingResult) =>
        Publish(
            outputRootPath,
            stagingResult,
            (temporaryPath, finalPath) =>
                File.Move(temporaryPath, finalPath, overwrite: false));

    internal static VerifiedUnchangedMediaPublicationResult Publish(
        string outputRootPath,
        VerifiedMediaFileStagingSuccessResult stagingResult,
        Action<string, string> moveFile)
    {
        ArgumentNullException.ThrowIfNull(stagingResult);
        ArgumentNullException.ThrowIfNull(moveFile);

        if (!TryNormalizeAbsolutePath(outputRootPath, out var outputRoot))
        {
            return Failure(
                stagingResult,
                VerifiedUnchangedMediaPublicationFailureKind.InvalidOutputRoot,
                "The output root must be a valid absolute path.");
        }

        var temporaryPath = stagingResult.TemporaryCopyPath;
        var finalPath = stagingResult.IntendedFinalDestinationPath;
        var pathFailure = ValidatePaths(
            stagingResult,
            outputRoot,
            temporaryPath,
            finalPath);
        if (pathFailure is not null)
        {
            return pathFailure;
        }

        var fileSystemFailure = ValidateFileSystem(
            stagingResult,
            outputRoot,
            temporaryPath,
            finalPath);
        if (fileSystemFailure is not null)
        {
            return fileSystemFailure;
        }

        FileDigest digest;
        try
        {
            digest = ComputeDigest(temporaryPath);
        }
        catch (Exception exception) when (IsFileSystemException(exception))
        {
            return Failure(
                stagingResult,
                exception is FileNotFoundException or DirectoryNotFoundException
                    ? VerifiedUnchangedMediaPublicationFailureKind.MissingTemporaryFile
                    : VerifiedUnchangedMediaPublicationFailureKind.ChangedTemporaryFile,
                $"The temporary file could not be verified: {exception.Message}");
        }

        var expectedHash = stagingResult.TemporaryCopySha256.ToUpperInvariant();
        if (digest.ByteCount != stagingResult.CopiedByteCount
            || !StringComparer.Ordinal.Equals(digest.Sha256, expectedHash))
        {
            return new VerifiedUnchangedMediaPublicationFailureResult(
                stagingResult,
                temporaryPath,
                finalPath,
                VerifiedUnchangedMediaPublicationFailureKind.ChangedTemporaryFile,
                "The temporary file no longer matches its verified staging result.",
                digest.ByteCount,
                digest.Sha256);
        }

        // Repeat the link and collision checks immediately before publication.
        // Portable filesystem APIs cannot make this sequence atomic.
        fileSystemFailure = ValidateFileSystem(
            stagingResult,
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
                stagingResult,
                VerifiedUnchangedMediaPublicationFailureKind.MoveFailure,
                $"The unchanged media file could not be published: {exception.Message}");
        }

        return new VerifiedUnchangedMediaPublicationSuccessResult(
            stagingResult,
            temporaryPath,
            finalPath,
            digest.ByteCount,
            digest.Sha256);
    }

    private static VerifiedUnchangedMediaPublicationFailureResult? ValidatePaths(
        VerifiedMediaFileStagingSuccessResult stagingResult,
        string outputRoot,
        string temporaryPath,
        string finalPath)
    {
        if (!StringComparer.Ordinal.Equals(
                finalPath, stagingResult.PlanningItem.AbsoluteDestinationPath)
            || !TryNormalizeAbsolutePath(temporaryPath, out var normalizedTemporary)
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
                stagingResult,
                VerifiedUnchangedMediaPublicationFailureKind.InvalidPath,
                "The staging paths must be distinct normalized absolute paths in " +
                "one directory below the output root and retain the planned destination.");
        }

        return null;
    }

    private static VerifiedUnchangedMediaPublicationFailureResult? ValidateFileSystem(
        VerifiedMediaFileStagingSuccessResult stagingResult,
        string outputRoot,
        string temporaryPath,
        string finalPath)
    {
        if (!TryGetAttributes(outputRoot, out var rootAttributes, out var rootError))
        {
            return Failure(
                stagingResult,
                VerifiedUnchangedMediaPublicationFailureKind.InvalidOutputRoot,
                rootError is null
                    ? $"The output root does not exist: '{outputRoot}'."
                    : $"The output root could not be inspected: {rootError}");
        }

        if ((rootAttributes & FileAttributes.ReparsePoint) != 0)
        {
            return Failure(
                stagingResult,
                VerifiedUnchangedMediaPublicationFailureKind.LinkedPath,
                $"The output root must not be a symbolic link or reparse point: '{outputRoot}'.");
        }

        if ((rootAttributes & FileAttributes.Directory) == 0)
        {
            return Failure(
                stagingResult,
                VerifiedUnchangedMediaPublicationFailureKind.InvalidOutputRoot,
                $"The output root is not a directory: '{outputRoot}'.");
        }

        foreach (var component in ComponentsBelowRoot(outputRoot, temporaryPath))
        {
            if (!TryGetAttributes(component, out var attributes, out var error))
            {
                return Failure(
                    stagingResult,
                    StringComparer.Ordinal.Equals(component, temporaryPath)
                        ? VerifiedUnchangedMediaPublicationFailureKind.MissingTemporaryFile
                        : VerifiedUnchangedMediaPublicationFailureKind.InvalidPath,
                    error is null
                        ? $"An output path component does not exist: '{component}'."
                        : $"An output path component could not be inspected: {error}");
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                return Failure(
                    stagingResult,
                    VerifiedUnchangedMediaPublicationFailureKind.LinkedPath,
                    $"The temporary path contains a symbolic link or reparse point: '{component}'.");
            }

            if (!StringComparer.Ordinal.Equals(component, temporaryPath)
                && (attributes & FileAttributes.Directory) == 0)
            {
                return Failure(
                    stagingResult,
                    VerifiedUnchangedMediaPublicationFailureKind.InvalidPath,
                    $"An output path component is not a directory: '{component}'.");
            }

            if (StringComparer.Ordinal.Equals(component, temporaryPath)
                && (attributes & (FileAttributes.Directory | FileAttributes.Device)) != 0)
            {
                return Failure(
                    stagingResult,
                    VerifiedUnchangedMediaPublicationFailureKind.NonRegularTemporaryFile,
                    $"The temporary path is not a regular file: '{temporaryPath}'.");
            }
        }

        foreach (var component in ComponentsBelowRoot(
                     outputRoot, Path.GetDirectoryName(finalPath)!))
        {
            if (!TryGetAttributes(component, out var attributes, out var error)
                || (attributes & FileAttributes.Directory) == 0)
            {
                return Failure(
                    stagingResult,
                    VerifiedUnchangedMediaPublicationFailureKind.InvalidPath,
                    error is null
                        ? $"The final destination directory is unavailable: '{component}'."
                        : $"The final destination directory could not be inspected: {error}");
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                return Failure(
                    stagingResult,
                    VerifiedUnchangedMediaPublicationFailureKind.LinkedPath,
                    $"The final path contains a symbolic link or reparse point: '{component}'.");
            }
        }

        if (TryGetAttributes(finalPath, out _, out var finalError))
        {
            return Failure(
                stagingResult,
                VerifiedUnchangedMediaPublicationFailureKind.ExistingDestination,
                $"The final destination already exists: '{finalPath}'.");
        }

        if (finalError is not null)
        {
            return Failure(
                stagingResult,
                VerifiedUnchangedMediaPublicationFailureKind.ExistingDestination,
                $"The final destination could not be confirmed absent: {finalError}");
        }

        return null;
    }

    private static FileDigest ComputeDigest(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[BufferSize];
        long byteCount = 0;
        int bytesRead;
        while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            hash.AppendData(buffer, 0, bytesRead);
            byteCount += bytesRead;
        }

        return new FileDigest(
            byteCount,
            Convert.ToHexString(hash.GetHashAndReset()));
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

    private static VerifiedUnchangedMediaPublicationFailureResult Failure(
        VerifiedMediaFileStagingSuccessResult stagingResult,
        VerifiedUnchangedMediaPublicationFailureKind kind,
        string message) =>
        new(
            stagingResult,
            stagingResult.TemporaryCopyPath,
            stagingResult.IntendedFinalDestinationPath,
            kind,
            message);

    private sealed record FileDigest(long ByteCount, string Sha256);
}
