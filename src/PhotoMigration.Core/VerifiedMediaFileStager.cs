using System.Security.Cryptography;
using System.Text;

namespace PhotoMigration.Core;

public static class VerifiedMediaFileStager
{
    private const int BufferSize = 81_920;
    private const int MaximumTemporaryNameAttempts = 16;

    public static VerifiedMediaFileStagingResult Stage(
        string sourceRootPath,
        string outputRootPath,
        DestinationPathPlanningItem planningItem) =>
        Stage(
            sourceRootPath,
            outputRootPath,
            planningItem,
            CreateTemporaryFileName,
            _ => { },
            File.Delete);

    internal static VerifiedMediaFileStagingResult Stage(
        string sourceRootPath,
        string outputRootPath,
        DestinationPathPlanningItem planningItem,
        Func<string, string> temporaryFileNameFactory,
        Action<string> afterCopy,
        Action<string> deleteTemporaryFile)
    {
        ArgumentNullException.ThrowIfNull(planningItem);
        ArgumentNullException.ThrowIfNull(temporaryFileNameFactory);
        ArgumentNullException.ThrowIfNull(afterCopy);
        ArgumentNullException.ThrowIfNull(deleteTemporaryFile);

        var replanned = DestinationPathPlanner.Plan(
            sourceRootPath,
            outputRootPath,
            [planningItem.MediaEntry]);
        if (replanned is DestinationPathPlanningFailureResult planningFailure)
        {
            var failureKind = planningFailure.Issues.Any(
                issue => issue.Kind == DestinationPathPlanningIssueKind.OverlappingRoots)
                ? VerifiedMediaFileStagingFailureKind.OverlappingRoots
                : VerifiedMediaFileStagingFailureKind.InvalidPlan;
            return Failure(
                planningItem,
                failureKind,
                $"The destination plan is not valid: {planningFailure.Issues[0].Message}");
        }

        var expectedItem = AssertSinglePlannedItem(
            (DestinationPathPlanningSuccessResult)replanned);
        if (!StringComparer.Ordinal.Equals(
                planningItem.AbsoluteSourcePath,
                expectedItem.AbsoluteSourcePath)
            || !StringComparer.Ordinal.Equals(
                planningItem.AbsoluteDestinationPath,
                expectedItem.AbsoluteDestinationPath))
        {
            return Failure(
                planningItem,
                VerifiedMediaFileStagingFailureKind.InvalidPlan,
                "The supplied source or destination path does not match the destination plan.");
        }

        var sourceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceRootPath));
        var outputRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputRootPath));

        var sourceRootFailure = ValidateRoot(sourceRoot, "Source", planningItem);
        if (sourceRootFailure is not null)
        {
            return sourceRootFailure;
        }

        var outputRootFailure = ValidateRoot(outputRoot, "Output", planningItem);
        if (outputRootFailure is not null)
        {
            return outputRootFailure;
        }

        var sourceFailure = ValidateSourcePath(
            sourceRoot,
            planningItem.AbsoluteSourcePath,
            planningItem);
        if (sourceFailure is not null)
        {
            return sourceFailure;
        }

        var destinationDirectory = Path.GetDirectoryName(
            planningItem.AbsoluteDestinationPath)!;
        FileStream sourceStream;
        try
        {
            sourceStream = new FileStream(
                planningItem.AbsoluteSourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                BufferSize,
                FileOptions.SequentialScan);
        }
        catch (FileNotFoundException exception)
        {
            return Failure(
                planningItem,
                VerifiedMediaFileStagingFailureKind.MissingSource,
                $"Source file is missing: {exception.Message}");
        }
        catch (DirectoryNotFoundException exception)
        {
            return Failure(
                planningItem,
                VerifiedMediaFileStagingFailureKind.MissingSource,
                $"Source path is missing: {exception.Message}");
        }
        catch (Exception exception) when (IsFileSystemException(exception))
        {
            return Failure(
                planningItem,
                VerifiedMediaFileStagingFailureKind.CopyFailure,
                $"Source file could not be opened for reading: {exception.Message}");
        }

        using (sourceStream)
        {
            long sourceLength;
            try
            {
                sourceLength = sourceStream.Length;
            }
            catch (Exception exception) when (IsFileSystemException(exception))
            {
                return Failure(
                    planningItem,
                    VerifiedMediaFileStagingFailureKind.InvalidSourceFile,
                    $"Source length could not be read: {exception.Message}");
            }

            if (sourceLength != planningItem.MediaEntry.SizeInBytes)
            {
                return Failure(
                    planningItem,
                    VerifiedMediaFileStagingFailureKind.SourceChanged,
                    $"Source size is {sourceLength} bytes but the inventory recorded " +
                    $"{planningItem.MediaEntry.SizeInBytes} bytes.");
            }

            var directoryFailure = EnsureDestinationDirectory(
                outputRoot,
                destinationDirectory,
                planningItem);
            if (directoryFailure is not null)
            {
                return directoryFailure;
            }

            var destinationFailure = RejectExistingDestination(
                planningItem.AbsoluteDestinationPath,
                planningItem);
            if (destinationFailure is not null)
            {
                return destinationFailure;
            }

            var temporaryCreation = CreateTemporaryFile(
                destinationDirectory,
                planningItem.AbsoluteDestinationPath,
                temporaryFileNameFactory);
            if (temporaryCreation.Stream is null)
            {
                return Failure(
                    planningItem,
                    VerifiedMediaFileStagingFailureKind.TemporaryFileCreationFailure,
                    temporaryCreation.ErrorMessage!);
            }

            var temporaryPath = temporaryCreation.Path!;
            long copiedByteCount;
            byte[] sourceHash;
            try
            {
                using (temporaryCreation.Stream)
                using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                {
                    copiedByteCount = CopyAndHash(sourceStream, temporaryCreation.Stream, hash);
                    temporaryCreation.Stream.Flush(flushToDisk: true);
                    sourceHash = hash.GetHashAndReset();
                }
            }
            catch (Exception exception) when (
                IsFileSystemException(exception) || exception is CryptographicException)
            {
                return FailureAfterTemporaryCreation(
                    planningItem,
                    VerifiedMediaFileStagingFailureKind.CopyFailure,
                    $"Source bytes could not be copied: {exception.Message}",
                    temporaryPath,
                    deleteTemporaryFile);
            }

            if (copiedByteCount != planningItem.MediaEntry.SizeInBytes)
            {
                return FailureAfterTemporaryCreation(
                    planningItem,
                    VerifiedMediaFileStagingFailureKind.SourceChanged,
                    $"Copied {copiedByteCount} bytes but the inventory recorded " +
                    $"{planningItem.MediaEntry.SizeInBytes} bytes.",
                    temporaryPath,
                    deleteTemporaryFile);
            }

            try
            {
                afterCopy(temporaryPath);
            }
            catch (Exception exception)
            {
                return FailureAfterTemporaryCreation(
                    planningItem,
                    VerifiedMediaFileStagingFailureKind.VerificationFailure,
                    $"The temporary copy could not be prepared for verification: " +
                    exception.Message,
                    temporaryPath,
                    deleteTemporaryFile);
            }

            var temporaryValidationError = ValidateTemporaryCopy(temporaryPath);
            if (temporaryValidationError is not null)
            {
                return FailureAfterTemporaryCreation(
                    planningItem,
                    VerifiedMediaFileStagingFailureKind.VerificationFailure,
                    temporaryValidationError,
                    temporaryPath,
                    deleteTemporaryFile);
            }

            FileDigest temporaryDigest;
            try
            {
                temporaryDigest = ComputeDigest(temporaryPath);
            }
            catch (Exception exception) when (
                IsFileSystemException(exception) || exception is CryptographicException)
            {
                return FailureAfterTemporaryCreation(
                    planningItem,
                    VerifiedMediaFileStagingFailureKind.VerificationFailure,
                    $"The temporary copy could not be verified: {exception.Message}",
                    temporaryPath,
                    deleteTemporaryFile);
            }

            temporaryValidationError = ValidateTemporaryCopy(temporaryPath);
            if (temporaryValidationError is not null)
            {
                return FailureAfterTemporaryCreation(
                    planningItem,
                    VerifiedMediaFileStagingFailureKind.VerificationFailure,
                    temporaryValidationError,
                    temporaryPath,
                    deleteTemporaryFile);
            }

            if (temporaryDigest.ByteCount != copiedByteCount
                || !CryptographicOperations.FixedTimeEquals(
                    sourceHash,
                    temporaryDigest.Hash))
            {
                return FailureAfterTemporaryCreation(
                    planningItem,
                    VerifiedMediaFileStagingFailureKind.VerificationFailure,
                    "The temporary copy's byte count or SHA-256 hash does not match the source.",
                    temporaryPath,
                    deleteTemporaryFile);
            }

            var finalSourceFailure = ValidateSourcePath(
                sourceRoot,
                planningItem.AbsoluteSourcePath,
                planningItem);
            if (finalSourceFailure is not null)
            {
                return FailureAfterTemporaryCreation(
                    planningItem,
                    finalSourceFailure.FailureKind,
                    finalSourceFailure.Message,
                    temporaryPath,
                    deleteTemporaryFile);
            }

            try
            {
                if (sourceStream.Length != planningItem.MediaEntry.SizeInBytes)
                {
                    return FailureAfterTemporaryCreation(
                        planningItem,
                        VerifiedMediaFileStagingFailureKind.SourceChanged,
                        "The source size changed while its temporary copy was staged.",
                        temporaryPath,
                        deleteTemporaryFile);
                }
            }
            catch (Exception exception) when (IsFileSystemException(exception))
            {
                return FailureAfterTemporaryCreation(
                    planningItem,
                    VerifiedMediaFileStagingFailureKind.CopyFailure,
                    $"The source could not be revalidated after copying: {exception.Message}",
                    temporaryPath,
                    deleteTemporaryFile);
            }

            var finalDirectoryFailure = ValidateExistingDestinationDirectory(
                outputRoot,
                destinationDirectory,
                planningItem);
            if (finalDirectoryFailure is not null)
            {
                return FailureAfterTemporaryCreation(
                    planningItem,
                    finalDirectoryFailure.FailureKind,
                    finalDirectoryFailure.Message,
                    temporaryPath,
                    deleteTemporaryFile);
            }

            destinationFailure = RejectExistingDestination(
                planningItem.AbsoluteDestinationPath,
                planningItem);
            if (destinationFailure is not null)
            {
                return FailureAfterTemporaryCreation(
                    planningItem,
                    destinationFailure.FailureKind,
                    destinationFailure.Message,
                    temporaryPath,
                    deleteTemporaryFile);
            }

            return new VerifiedMediaFileStagingSuccessResult(
                planningItem,
                temporaryPath,
                planningItem.AbsoluteDestinationPath,
                copiedByteCount,
                Convert.ToHexString(sourceHash),
                Convert.ToHexString(temporaryDigest.Hash));
        }
    }

    private static DestinationPathPlanningItem AssertSinglePlannedItem(
        DestinationPathPlanningSuccessResult result) =>
        result.Items.Count == 1
            ? result.Items[0]
            : throw new InvalidOperationException(
                "Revalidating one inventory entry did not produce one planned item.");

    private static VerifiedMediaFileStagingFailureResult? ValidateRoot(
        string path,
        string description,
        DestinationPathPlanningItem planningItem)
    {
        FileAttributes attributes;
        try
        {
            if (!TryGetAttributes(path, out attributes))
            {
                return Failure(
                    planningItem,
                    VerifiedMediaFileStagingFailureKind.InvalidRoot,
                    $"{description} root does not exist: '{path}'.");
            }
        }
        catch (Exception exception) when (IsFileSystemException(exception))
        {
            return Failure(
                planningItem,
                VerifiedMediaFileStagingFailureKind.InvalidRoot,
                $"{description} root could not be inspected: {exception.Message}");
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            return Failure(
                planningItem,
                VerifiedMediaFileStagingFailureKind.LinkedPath,
                $"{description} root must not be a symbolic link or reparse point: '{path}'.");
        }

        return (attributes & FileAttributes.Directory) != 0
            ? null
            : Failure(
                planningItem,
                VerifiedMediaFileStagingFailureKind.InvalidRoot,
                $"{description} root is not a directory: '{path}'.");
    }

    private static VerifiedMediaFileStagingFailureResult? ValidateSourcePath(
        string sourceRoot,
        string sourcePath,
        DestinationPathPlanningItem planningItem)
    {
        var components = ComponentsBelowRoot(sourceRoot, sourcePath);
        for (var index = 0; index < components.Count; index++)
        {
            var component = components[index];
            FileAttributes attributes;
            try
            {
                if (!TryGetAttributes(component, out attributes))
                {
                    return Failure(
                        planningItem,
                        VerifiedMediaFileStagingFailureKind.MissingSource,
                        $"Source path component does not exist: '{component}'.");
                }
            }
            catch (Exception exception) when (IsFileSystemException(exception))
            {
                return Failure(
                    planningItem,
                    VerifiedMediaFileStagingFailureKind.CopyFailure,
                    $"Source path component could not be inspected: {exception.Message}");
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                return Failure(
                    planningItem,
                    VerifiedMediaFileStagingFailureKind.LinkedPath,
                    $"Source path contains a symbolic link or reparse point: '{component}'.");
            }

            var isFinalComponent = index == components.Count - 1;
            if (!isFinalComponent && (attributes & FileAttributes.Directory) == 0)
            {
                return Failure(
                    planningItem,
                    VerifiedMediaFileStagingFailureKind.InvalidSourceFile,
                    $"Source path component is not a directory: '{component}'.");
            }

            if (isFinalComponent
                && ((attributes & FileAttributes.Directory) != 0
                    || (attributes & FileAttributes.Device) != 0))
            {
                return Failure(
                    planningItem,
                    VerifiedMediaFileStagingFailureKind.InvalidSourceFile,
                    $"Source path is not a regular file: '{sourcePath}'.");
            }
        }

        return null;
    }

    private static VerifiedMediaFileStagingFailureResult? EnsureDestinationDirectory(
        string outputRoot,
        string destinationDirectory,
        DestinationPathPlanningItem planningItem)
    {
        foreach (var component in ComponentsBelowRoot(outputRoot, destinationDirectory))
        {
            FileAttributes attributes;
            try
            {
                if (!TryGetAttributes(component, out attributes))
                {
                    Directory.CreateDirectory(component);
                    if (!TryGetAttributes(component, out attributes))
                    {
                        return Failure(
                            planningItem,
                            VerifiedMediaFileStagingFailureKind.DirectoryCreationFailure,
                            $"Destination directory was not created: '{component}'.");
                    }
                }
            }
            catch (Exception exception) when (IsFileSystemException(exception))
            {
                return Failure(
                    planningItem,
                    VerifiedMediaFileStagingFailureKind.DirectoryCreationFailure,
                    $"Destination directory could not be created or inspected: " +
                    exception.Message);
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                return Failure(
                    planningItem,
                    VerifiedMediaFileStagingFailureKind.LinkedPath,
                    $"Destination path contains a symbolic link or reparse point: '{component}'.");
            }

            if ((attributes & FileAttributes.Directory) == 0)
            {
                return Failure(
                    planningItem,
                    VerifiedMediaFileStagingFailureKind.DirectoryCreationFailure,
                    $"Destination path component is not a directory: '{component}'.");
            }
        }

        return null;
    }

    private static VerifiedMediaFileStagingFailureResult? ValidateExistingDestinationDirectory(
        string outputRoot,
        string destinationDirectory,
        DestinationPathPlanningItem planningItem)
    {
        foreach (var component in ComponentsBelowRoot(outputRoot, destinationDirectory))
        {
            FileAttributes attributes;
            try
            {
                if (!TryGetAttributes(component, out attributes))
                {
                    return Failure(
                        planningItem,
                        VerifiedMediaFileStagingFailureKind.DirectoryCreationFailure,
                        $"Destination directory disappeared during staging: '{component}'.");
                }
            }
            catch (Exception exception) when (IsFileSystemException(exception))
            {
                return Failure(
                    planningItem,
                    VerifiedMediaFileStagingFailureKind.DirectoryCreationFailure,
                    $"Destination directory could not be revalidated: {exception.Message}");
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                return Failure(
                    planningItem,
                    VerifiedMediaFileStagingFailureKind.LinkedPath,
                    $"Destination path became a symbolic link or reparse point: '{component}'.");
            }

            if ((attributes & FileAttributes.Directory) == 0)
            {
                return Failure(
                    planningItem,
                    VerifiedMediaFileStagingFailureKind.DirectoryCreationFailure,
                    $"Destination path component is no longer a directory: '{component}'.");
            }
        }

        return null;
    }

    private static VerifiedMediaFileStagingFailureResult? RejectExistingDestination(
        string destinationPath,
        DestinationPathPlanningItem planningItem)
    {
        try
        {
            return TryGetAttributes(destinationPath, out _)
                ? Failure(
                    planningItem,
                    VerifiedMediaFileStagingFailureKind.ExistingDestination,
                    $"Final destination already exists: '{destinationPath}'.")
                : null;
        }
        catch (Exception exception) when (IsFileSystemException(exception))
        {
            return Failure(
                planningItem,
                VerifiedMediaFileStagingFailureKind.ExistingDestination,
                $"The final destination could not be confirmed unused: {exception.Message}");
        }
    }

    private static TemporaryFileCreation CreateTemporaryFile(
        string destinationDirectory,
        string finalDestinationPath,
        Func<string, string> temporaryFileNameFactory)
    {
        string? lastCollisionPath = null;
        for (var attempt = 0; attempt < MaximumTemporaryNameAttempts; attempt++)
        {
            string temporaryFileName;
            try
            {
                temporaryFileName = temporaryFileNameFactory(finalDestinationPath);
            }
            catch (Exception exception)
            {
                return new TemporaryFileCreation(
                    null,
                    null,
                    $"A temporary filename could not be generated: {exception.Message}");
            }

            if (!IsSimpleFileName(temporaryFileName))
            {
                return new TemporaryFileCreation(
                    null,
                    null,
                    "The generated temporary name is not a single filename.");
            }

            var temporaryPath = Path.Combine(destinationDirectory, temporaryFileName);
            if (PathsEqualConservatively(temporaryPath, finalDestinationPath))
            {
                return new TemporaryFileCreation(
                    null,
                    null,
                    "The generated temporary path matches the final destination.");
            }

            try
            {
                var stream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    BufferSize,
                    FileOptions.SequentialScan);
                return new TemporaryFileCreation(temporaryPath, stream, null);
            }
            catch (IOException exception)
            {
                try
                {
                    if (TryGetAttributes(temporaryPath, out _))
                    {
                        lastCollisionPath = temporaryPath;
                        continue;
                    }
                }
                catch (Exception inspectionException) when (
                    IsFileSystemException(inspectionException))
                {
                    return new TemporaryFileCreation(
                        null,
                        null,
                        $"A temporary path collision could not be inspected: " +
                        inspectionException.Message);
                }

                return new TemporaryFileCreation(
                    null,
                    null,
                    $"A temporary file could not be created: {exception.Message}");
            }
            catch (Exception exception) when (IsFileSystemException(exception))
            {
                return new TemporaryFileCreation(
                    null,
                    null,
                    $"A temporary file could not be created: {exception.Message}");
            }
        }

        return new TemporaryFileCreation(
            null,
            null,
            $"No unused temporary filename was found after " +
            $"{MaximumTemporaryNameAttempts} attempts. Last collision: " +
            $"'{lastCollisionPath}'.");
    }

    private static long CopyAndHash(
        Stream source,
        Stream destination,
        IncrementalHash hash)
    {
        var buffer = new byte[BufferSize];
        long byteCount = 0;
        int bytesRead;
        while ((bytesRead = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            destination.Write(buffer, 0, bytesRead);
            hash.AppendData(buffer, 0, bytesRead);
            byteCount += bytesRead;
        }

        return byteCount;
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

        return new FileDigest(byteCount, hash.GetHashAndReset());
    }

    private static string? ValidateTemporaryCopy(string temporaryPath)
    {
        try
        {
            if (!TryGetAttributes(temporaryPath, out var attributes))
            {
                return $"The temporary copy is missing: '{temporaryPath}'.";
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                return $"The temporary copy became a symbolic link or reparse point: " +
                       $"'{temporaryPath}'.";
            }

            if ((attributes & (FileAttributes.Directory | FileAttributes.Device)) != 0)
            {
                return $"The temporary copy is not a regular file: '{temporaryPath}'.";
            }

            return null;
        }
        catch (Exception exception) when (IsFileSystemException(exception))
        {
            return $"The temporary copy could not be inspected: {exception.Message}";
        }
    }

    private static VerifiedMediaFileStagingFailureResult FailureAfterTemporaryCreation(
        DestinationPathPlanningItem planningItem,
        VerifiedMediaFileStagingFailureKind failureKind,
        string message,
        string temporaryPath,
        Action<string> deleteTemporaryFile)
    {
        try
        {
            deleteTemporaryFile(temporaryPath);
            return Failure(planningItem, failureKind, message);
        }
        catch (Exception cleanupException)
        {
            return new VerifiedMediaFileStagingFailureResult(
                planningItem,
                VerifiedMediaFileStagingFailureKind.CleanupFailure,
                $"{message} Cleanup of '{temporaryPath}' failed: " + cleanupException.Message,
                temporaryPath,
                failureKind);
        }
    }

    private static VerifiedMediaFileStagingFailureResult Failure(
        DestinationPathPlanningItem planningItem,
        VerifiedMediaFileStagingFailureKind failureKind,
        string message) =>
        new(planningItem, failureKind, message);

    private static IReadOnlyList<string> ComponentsBelowRoot(string root, string path)
    {
        var components = new Stack<string>();
        var current = path;
        while (!StringComparer.Ordinal.Equals(current, root))
        {
            components.Push(current);
            current = Path.GetDirectoryName(current)
                ?? throw new InvalidOperationException(
                    $"Path '{path}' is not beneath root '{root}'.");
        }

        return components.ToList().AsReadOnly();
    }

    private static bool TryGetAttributes(string path, out FileAttributes attributes)
    {
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
    }

    private static string CreateTemporaryFileName(string finalDestinationPath) =>
        $".photomigration-{Guid.NewGuid():N}.staging" +
        Path.GetExtension(finalDestinationPath);

    private static bool IsSimpleFileName(string path) =>
        !string.IsNullOrWhiteSpace(path)
        && !Path.IsPathRooted(path)
        && StringComparer.Ordinal.Equals(path, Path.GetFileName(path))
        && !path.Contains(Path.DirectorySeparatorChar)
        && !path.Contains(Path.AltDirectorySeparatorChar);

    private static bool PathsEqualConservatively(string left, string right) =>
        StringComparer.OrdinalIgnoreCase.Equals(
            left.Normalize(NormalizationForm.FormC),
            right.Normalize(NormalizationForm.FormC));

    private static bool IsFileSystemException(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or System.Security.SecurityException;

    private sealed record TemporaryFileCreation(
        string? Path,
        FileStream? Stream,
        string? ErrorMessage);

    private sealed record FileDigest(long ByteCount, byte[] Hash);
}
