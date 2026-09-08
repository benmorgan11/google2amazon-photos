using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace PhotoMigration.Core;

public static class JpegGpsMetadataWriter
{
    private const int BufferSize = 81_920;
    private const int CleanupWaitMilliseconds = 1_000;

    private static readonly JpegMetadataAssignmentTag[] RequiredCoordinateTags =
    [
        JpegMetadataAssignmentTag.GpsLatitude,
        JpegMetadataAssignmentTag.GpsLatitudeReference,
        JpegMetadataAssignmentTag.GpsLongitude,
        JpegMetadataAssignmentTag.GpsLongitudeReference
    ];

    public static readonly TimeSpan DefaultWriteTimeout = TimeSpan.FromSeconds(30);

    public static JpegGpsMetadataWriteResult Execute(
        string outputRootPath,
        JpegMetadataWriteReadyResult writePlan,
        string exifToolExecutablePath,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(writePlan);

        var writeTimeout = timeout ?? DefaultWriteTimeout;
        if (writeTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                "The JPEG GPS metadata write timeout must be greater than zero.");
        }

        if (!TryNormalizePath(outputRootPath, requireAbsolute: false, out var outputRoot))
        {
            return Failure(writePlan, JpegGpsMetadataWriteFailureKind.InvalidPath,
                "The output root path is invalid.");
        }

        if (!TryNormalizePath(exifToolExecutablePath, requireAbsolute: true,
                out var exifToolPath))
        {
            return Failure(writePlan, JpegGpsMetadataWriteFailureKind.InvalidPath,
                "The ExifTool executable path must be a valid absolute path.");
        }

        var planFailure = ValidatePlan(writePlan, outputRoot);
        if (planFailure is not null)
        {
            return planFailure;
        }

        var pathFailure = ValidatePaths(writePlan, outputRoot);
        if (pathFailure is not null)
        {
            return pathFailure;
        }

        var digestFailure = VerifyTemporaryCopy(writePlan, exifToolPath);
        if (digestFailure is not null)
        {
            return digestFailure;
        }

        // Repeat the link and collision checks immediately before handing the path
        // to ExifTool. Portable path APIs cannot make this sequence atomic.
        pathFailure = ValidatePaths(writePlan, outputRoot);
        if (pathFailure is not null)
        {
            return pathFailure;
        }

        return RunExifTool(writePlan, exifToolPath, writeTimeout);
    }

    private static JpegGpsMetadataWriteFailureResult? ValidatePlan(
        JpegMetadataWriteReadyResult writePlan,
        string outputRoot)
    {
        var staging = writePlan.StagingResult;
        var baseline = writePlan.BaselineHashResult;
        if (!ReferenceEquals(staging, baseline.StagingResult)
            || !StringComparer.Ordinal.Equals(
                baseline.TemporaryCopyPath, staging.TemporaryCopyPath)
            || !StringComparer.Ordinal.Equals(
                staging.IntendedFinalDestinationPath,
                staging.PlanningItem.AbsoluteDestinationPath))
        {
            return Failure(writePlan, JpegGpsMetadataWriteFailureKind.InvalidWritePlan,
                "The write plan no longer has consistent staging and baseline joins.");
        }

        if (staging.CopiedByteCount < 0
            || !TryNormalizeSha256(staging.TemporaryCopySha256, out _)
            || !TryNormalizePath(staging.TemporaryCopyPath, true, out var temporaryPath)
            || !TryNormalizePath(staging.IntendedFinalDestinationPath, true,
                out var finalPath)
            || !StringComparer.Ordinal.Equals(temporaryPath, staging.TemporaryCopyPath)
            || !StringComparer.Ordinal.Equals(finalPath,
                staging.IntendedFinalDestinationPath)
            || !IsStrictlyBelow(outputRoot, temporaryPath)
            || !IsStrictlyBelow(outputRoot, finalPath)
            || !StringComparer.Ordinal.Equals(
                Path.GetDirectoryName(temporaryPath), Path.GetDirectoryName(finalPath))
            || PathsEqualConservatively(temporaryPath, finalPath))
        {
            return Failure(writePlan, JpegGpsMetadataWriteFailureKind.InvalidPath,
                "The temporary and final paths must be distinct normalized absolute " +
                "paths in one directory below the output root.");
        }

        var assignments = writePlan.Assignments;
        if (assignments is null
            || assignments.Any(assignment => assignment is null
                || assignment.Group != JpegMetadataAssignmentGroup.Exif
                || string.IsNullOrEmpty(assignment.Value))
            || assignments.GroupBy(assignment => assignment.Tag).Any(group => group.Count() != 1)
            || RequiredCoordinateTags.Any(tag => assignments.All(a => a.Tag != tag)))
        {
            return Failure(writePlan, JpegGpsMetadataWriteFailureKind.InvalidWritePlan,
                "The write plan must contain one complete EXIF GPS coordinate assignment set.");
        }

        var hasAltitude = assignments.Any(a => a.Tag == JpegMetadataAssignmentTag.GpsAltitude);
        var hasAltitudeReference = assignments.Any(
            a => a.Tag == JpegMetadataAssignmentTag.GpsAltitudeReference);
        if (hasAltitude != hasAltitudeReference
            || assignments.Count != RequiredCoordinateTags.Length + (hasAltitude ? 2 : 0))
        {
            return Failure(writePlan, JpegGpsMetadataWriteFailureKind.InvalidWritePlan,
                "EXIF GPS altitude and altitude reference must either both be present or both be absent.");
        }

        return null;
    }

    private static JpegGpsMetadataWriteFailureResult? ValidatePaths(
        JpegMetadataWriteReadyResult writePlan,
        string outputRoot)
    {
        if (!TryGetAttributes(outputRoot, out var rootAttributes))
        {
            return Failure(writePlan, JpegGpsMetadataWriteFailureKind.InvalidOutputRoot,
                $"The output root does not exist: '{outputRoot}'.");
        }

        if ((rootAttributes & FileAttributes.ReparsePoint) != 0)
        {
            return Failure(writePlan, JpegGpsMetadataWriteFailureKind.LinkedPath,
                $"The output root must not be a symbolic link or reparse point: '{outputRoot}'.");
        }

        if ((rootAttributes & FileAttributes.Directory) == 0)
        {
            return Failure(writePlan, JpegGpsMetadataWriteFailureKind.InvalidOutputRoot,
                $"The output root is not a directory: '{outputRoot}'.");
        }

        var temporaryPath = writePlan.StagingResult.TemporaryCopyPath;
        var finalPath = writePlan.StagingResult.IntendedFinalDestinationPath;
        foreach (var component in ComponentsBelowRoot(outputRoot, temporaryPath))
        {
            if (!TryGetAttributes(component, out var attributes))
            {
                return Failure(writePlan,
                    component == temporaryPath
                        ? JpegGpsMetadataWriteFailureKind.MissingTemporaryFile
                        : JpegGpsMetadataWriteFailureKind.InvalidPath,
                    $"An output path component does not exist: '{component}'.");
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                return Failure(writePlan, JpegGpsMetadataWriteFailureKind.LinkedPath,
                    $"The temporary path contains a symbolic link or reparse point: '{component}'.");
            }

            if (component != temporaryPath && (attributes & FileAttributes.Directory) == 0)
            {
                return Failure(writePlan, JpegGpsMetadataWriteFailureKind.InvalidPath,
                    $"An output path component is not a directory: '{component}'.");
            }

            if (component == temporaryPath
                && (attributes & (FileAttributes.Directory | FileAttributes.Device)) != 0)
            {
                return Failure(writePlan,
                    JpegGpsMetadataWriteFailureKind.NonRegularTemporaryFile,
                    $"The temporary copy is not a regular file: '{temporaryPath}'.");
            }
        }

        var finalDirectory = Path.GetDirectoryName(finalPath)!;
        foreach (var component in ComponentsBelowRoot(outputRoot, finalDirectory))
        {
            if (!TryGetAttributes(component, out var attributes)
                || (attributes & FileAttributes.Directory) == 0)
            {
                return Failure(writePlan, JpegGpsMetadataWriteFailureKind.InvalidPath,
                    $"The final destination directory is unavailable: '{component}'.");
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                return Failure(writePlan, JpegGpsMetadataWriteFailureKind.LinkedPath,
                    $"The final destination path contains a symbolic link or reparse point: '{component}'.");
            }
        }

        if (TryGetAttributes(finalPath, out _))
        {
            return Failure(writePlan,
                JpegGpsMetadataWriteFailureKind.ExistingFinalDestination,
                $"The final destination already exists: '{finalPath}'.");
        }

        return null;
    }

    private static JpegGpsMetadataWriteFailureResult? VerifyTemporaryCopy(
        JpegMetadataWriteReadyResult writePlan,
        string exifToolPath)
    {
        long byteCount = 0;
        string hash;
        try
        {
            using var stream = new FileStream(
                writePlan.StagingResult.TemporaryCopyPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                BufferSize,
                FileOptions.SequentialScan);
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[BufferSize];
            int bytesRead;
            while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                hasher.AppendData(buffer, 0, bytesRead);
                byteCount += bytesRead;
            }

            hash = Convert.ToHexString(hasher.GetHashAndReset());
        }
        catch (Exception exception) when (IsFileSystemException(exception))
        {
            return Failure(writePlan,
                exception is FileNotFoundException or DirectoryNotFoundException
                    ? JpegGpsMetadataWriteFailureKind.MissingTemporaryFile
                    : JpegGpsMetadataWriteFailureKind.TemporaryFileChanged,
                $"The temporary copy could not be verified: {exception.Message}",
                exifToolPath);
        }

        if (byteCount != writePlan.StagingResult.CopiedByteCount
            || !StringComparer.Ordinal.Equals(
                hash, writePlan.StagingResult.TemporaryCopySha256.ToUpperInvariant()))
        {
            return new JpegGpsMetadataWriteFailureResult(
                writePlan,
                JpegGpsMetadataWriteFailureKind.TemporaryFileChanged,
                "The temporary copy no longer matches its verified staging result.",
                exifToolPath,
                ActualByteCount: byteCount,
                ActualWholeFileSha256: hash);
        }

        return null;
    }

    private static JpegGpsMetadataWriteResult RunExifTool(
        JpegMetadataWriteReadyResult writePlan,
        string exifToolPath,
        TimeSpan timeout)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = exifToolPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("-overwrite_original");
        AddAssignment(startInfo, writePlan, JpegMetadataAssignmentTag.GpsLatitude,
            "-EXIF:GPSLatitude=");
        AddAssignment(startInfo, writePlan, JpegMetadataAssignmentTag.GpsLatitudeReference,
            "-EXIF:GPSLatitudeRef=");
        AddAssignment(startInfo, writePlan, JpegMetadataAssignmentTag.GpsLongitude,
            "-EXIF:GPSLongitude=");
        AddAssignment(startInfo, writePlan, JpegMetadataAssignmentTag.GpsLongitudeReference,
            "-EXIF:GPSLongitudeRef=");
        if (writePlan.Assignments.Any(a => a.Tag == JpegMetadataAssignmentTag.GpsAltitude))
        {
            AddAssignment(startInfo, writePlan, JpegMetadataAssignmentTag.GpsAltitude,
                "-EXIF:GPSAltitude=");
            AddAssignment(startInfo, writePlan,
                JpegMetadataAssignmentTag.GpsAltitudeReference,
                "-EXIF:GPSAltitudeRef#=");
        }

        startInfo.ArgumentList.Add(writePlan.StagingResult.TemporaryCopyPath);
        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return Failure(writePlan, JpegGpsMetadataWriteFailureKind.ExifToolFailure,
                    "ExifTool could not be started.", exifToolPath);
            }
        }
        catch (Exception exception) when (exception is Win32Exception
                                          or InvalidOperationException
                                          or IOException
                                          or UnauthorizedAccessException
                                          or NotSupportedException)
        {
            return Failure(writePlan, JpegGpsMetadataWriteFailureKind.ExifToolFailure,
                $"ExifTool could not be started: {exception.Message}", exifToolPath);
        }

        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        var timeoutMilliseconds = (int)Math.Min(
            Math.Ceiling(timeout.TotalMilliseconds), int.MaxValue);
        if (!process.WaitForExit(timeoutMilliseconds))
        {
            var terminationError = Terminate(process);
            TryCaptureOutput(standardOutput, standardError, out var timedOutOutput,
                out var timedOutError, ref terminationError);
            return new JpegGpsMetadataWriteFailureResult(
                writePlan,
                JpegGpsMetadataWriteFailureKind.TimedOut,
                $"ExifTool GPS metadata write timed out after {timeout}.",
                exifToolPath,
                Timeout: timeout,
                TerminationError: terminationError,
                StandardOutput: timedOutOutput,
                StandardError: timedOutError);
        }

        string? captureError = null;
        if (!TryCaptureOutput(standardOutput, standardError, out var output, out var error,
                ref captureError))
        {
            return Failure(writePlan, JpegGpsMetadataWriteFailureKind.ExifToolFailure,
                captureError!, exifToolPath, process.ExitCode);
        }

        if (process.ExitCode != 0)
        {
            return new JpegGpsMetadataWriteFailureResult(
                writePlan,
                JpegGpsMetadataWriteFailureKind.ExifToolFailure,
                $"ExifTool GPS metadata write exited with code {process.ExitCode}.",
                exifToolPath,
                process.ExitCode,
                StandardOutput: output,
                StandardError: error);
        }

        return new JpegGpsMetadataWriteSuccessResult(
            writePlan,
            writePlan.StagingResult.TemporaryCopyPath,
            writePlan.StagingResult.IntendedFinalDestinationPath,
            exifToolPath,
            JpegGpsMetadataWriteStatus.PendingVerification,
            output,
            error);
    }

    private static void AddAssignment(
        ProcessStartInfo startInfo,
        JpegMetadataWriteReadyResult writePlan,
        JpegMetadataAssignmentTag tag,
        string prefix) =>
        startInfo.ArgumentList.Add(prefix + writePlan.Assignments.Single(a => a.Tag == tag).Value);

    private static bool TryCaptureOutput(
        Task<string> standardOutput,
        Task<string> standardError,
        out string output,
        out string error,
        ref string? captureError)
    {
        output = string.Empty;
        error = string.Empty;
        if (captureError is not null)
        {
            return false;
        }

        try
        {
            var redirectedOutput = Task.WhenAll(standardOutput, standardError);
            if (!redirectedOutput.Wait(CleanupWaitMilliseconds))
            {
                captureError = "ExifTool output streams did not close within one second.";
                return false;
            }

            var captured = redirectedOutput.GetAwaiter().GetResult();
            output = captured[0];
            error = captured[1];
            return true;
        }
        catch (Exception exception) when (exception is AggregateException
                                          or IOException
                                          or InvalidOperationException)
        {
            captureError = $"ExifTool output could not be read: {exception.Message}";
            return false;
        }
    }

    private static string? Terminate(Process process)
    {
        try
        {
            if (process.HasExited)
            {
                return null;
            }

            process.Kill(entireProcessTree: true);
            return process.WaitForExit(CleanupWaitMilliseconds)
                ? null
                : "The timed-out ExifTool process did not exit within one second after termination was requested.";
        }
        catch (InvalidOperationException exception)
        {
            return HasExited(process)
                ? null
                : $"The timed-out ExifTool process could not be terminated: {exception.Message}";
        }
        catch (Exception exception) when (exception is NotSupportedException or Win32Exception)
        {
            return $"The timed-out ExifTool process could not be terminated: {exception.Message}";
        }
    }

    private static bool HasExited(Process process)
    {
        try { return process.HasExited; }
        catch (InvalidOperationException) { return false; }
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

    private static bool TryNormalizePath(
        string? path,
        bool requireAbsolute,
        out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(path)
            || requireAbsolute && !Path.IsPathFullyQualified(path))
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

    private static bool TryNormalizeSha256(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (value is null || value.Length != 64 || value.Any(character =>
                character is not (>= '0' and <= '9')
                and not (>= 'A' and <= 'F')
                and not (>= 'a' and <= 'f')))
        {
            return false;
        }

        normalized = value.ToUpperInvariant();
        return true;
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
        catch (Exception exception) when (IsFileSystemException(exception))
        {
            attributes = default;
            return false;
        }
    }

    private static bool PathsEqualConservatively(string left, string right) =>
        StringComparer.OrdinalIgnoreCase.Equals(
            left.Normalize(NormalizationForm.FormC),
            right.Normalize(NormalizationForm.FormC));

    private static bool IsFileSystemException(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or System.Security.SecurityException;

    private static JpegGpsMetadataWriteFailureResult Failure(
        JpegMetadataWriteReadyResult writePlan,
        JpegGpsMetadataWriteFailureKind kind,
        string message,
        string? exifToolPath = null,
        int? exitCode = null) =>
        new(writePlan, kind, message, exifToolPath, exitCode);
}
