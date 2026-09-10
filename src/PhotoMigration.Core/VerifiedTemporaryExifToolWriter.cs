using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace PhotoMigration.Core;

internal enum VerifiedTemporaryExifToolWriteFailureKind
{
    InvalidPath,
    InvalidOutputRoot,
    MissingTemporaryFile,
    LinkedPath,
    NonRegularTemporaryFile,
    TemporaryFileChanged,
    ExistingFinalDestination,
    ExifToolFailure,
    TimedOut
}

internal sealed record VerifiedTemporaryExifToolWriteOutcome(
    bool Succeeded,
    VerifiedTemporaryExifToolWriteFailureKind? FailureKind = null,
    string Message = "",
    int? ExitCode = null,
    TimeSpan? Timeout = null,
    string? TerminationError = null,
    long? ActualByteCount = null,
    string? ActualWholeFileSha256 = null,
    string StandardOutput = "",
    string StandardError = "",
    bool RetainExifToolPath = false);

internal static class VerifiedTemporaryExifToolWriter
{
    private const int BufferSize = 81_920;
    private const int CleanupWaitMilliseconds = 1_000;

    internal static bool TryNormalizePath(
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

    internal static VerifiedTemporaryExifToolWriteOutcome? ValidateStaging(
        string outputRoot,
        VerifiedMediaFileStagingSuccessResult staging)
    {
        if (staging.CopiedByteCount < 0
            || !IsSha256(staging.TemporaryCopySha256)
            || !TryNormalizePath(staging.TemporaryCopyPath, true, out var temporaryPath)
            || !TryNormalizePath(
                staging.IntendedFinalDestinationPath,
                true,
                out var finalPath)
            || !StringComparer.Ordinal.Equals(temporaryPath, staging.TemporaryCopyPath)
            || !StringComparer.Ordinal.Equals(
                finalPath,
                staging.IntendedFinalDestinationPath)
            || !IsStrictlyBelow(outputRoot, temporaryPath)
            || !IsStrictlyBelow(outputRoot, finalPath)
            || !StringComparer.Ordinal.Equals(
                Path.GetDirectoryName(temporaryPath),
                Path.GetDirectoryName(finalPath))
            || PathsEqualConservatively(temporaryPath, finalPath))
        {
            return Failure(
                VerifiedTemporaryExifToolWriteFailureKind.InvalidPath,
                "The temporary and final paths must be distinct normalized absolute " +
                "paths in one directory below the output root.");
        }

        return null;
    }

    internal static VerifiedTemporaryExifToolWriteOutcome Execute(
        string outputRoot,
        VerifiedMediaFileStagingSuccessResult staging,
        string exifToolPath,
        IReadOnlyList<string> assignmentArguments,
        TimeSpan timeout,
        string operationDescription)
    {
        var failure = ValidatePaths(outputRoot, staging);
        if (failure is not null)
        {
            return failure;
        }

        failure = VerifyTemporaryCopy(staging);
        if (failure is not null)
        {
            return failure;
        }

        // Portable path checks and process access cannot be atomic.
        // Repeat link and collision checks immediately before ExifTool starts.
        failure = ValidatePaths(outputRoot, staging);
        if (failure is not null)
        {
            return failure;
        }

        return RunExifTool(
            staging.TemporaryCopyPath,
            exifToolPath,
            assignmentArguments,
            timeout,
            operationDescription);
    }

    private static VerifiedTemporaryExifToolWriteOutcome? ValidatePaths(
        string outputRoot,
        VerifiedMediaFileStagingSuccessResult staging)
    {
        if (!TryGetAttributes(outputRoot, out var rootAttributes))
        {
            return Failure(
                VerifiedTemporaryExifToolWriteFailureKind.InvalidOutputRoot,
                $"The output root does not exist: '{outputRoot}'.");
        }

        if ((rootAttributes & FileAttributes.ReparsePoint) != 0)
        {
            return Failure(
                VerifiedTemporaryExifToolWriteFailureKind.LinkedPath,
                $"The output root must not be a symbolic link or reparse point: " +
                $"'{outputRoot}'.");
        }

        if ((rootAttributes & FileAttributes.Directory) == 0)
        {
            return Failure(
                VerifiedTemporaryExifToolWriteFailureKind.InvalidOutputRoot,
                $"The output root is not a directory: '{outputRoot}'.");
        }

        var temporaryPath = staging.TemporaryCopyPath;
        foreach (var component in ComponentsBelowRoot(outputRoot, temporaryPath))
        {
            if (!TryGetAttributes(component, out var attributes))
            {
                return Failure(
                    component == temporaryPath
                        ? VerifiedTemporaryExifToolWriteFailureKind.MissingTemporaryFile
                        : VerifiedTemporaryExifToolWriteFailureKind.InvalidPath,
                    $"An output path component does not exist: '{component}'.");
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                return Failure(
                    VerifiedTemporaryExifToolWriteFailureKind.LinkedPath,
                    $"The temporary path contains a symbolic link or reparse point: " +
                    $"'{component}'.");
            }

            if (!StringComparer.Ordinal.Equals(component, temporaryPath)
                && (attributes & FileAttributes.Directory) == 0)
            {
                return Failure(
                    VerifiedTemporaryExifToolWriteFailureKind.InvalidPath,
                    $"An output path component is not a directory: '{component}'.");
            }

            if (StringComparer.Ordinal.Equals(component, temporaryPath)
                && (attributes & (FileAttributes.Directory | FileAttributes.Device)) != 0)
            {
                return Failure(
                    VerifiedTemporaryExifToolWriteFailureKind.NonRegularTemporaryFile,
                    $"The temporary copy is not a regular file: '{temporaryPath}'.");
            }
        }

        var finalPath = staging.IntendedFinalDestinationPath;
        var finalDirectory = Path.GetDirectoryName(finalPath)!;
        foreach (var component in ComponentsBelowRoot(outputRoot, finalDirectory))
        {
            if (!TryGetAttributes(component, out var attributes)
                || (attributes & FileAttributes.Directory) == 0)
            {
                return Failure(
                    VerifiedTemporaryExifToolWriteFailureKind.InvalidPath,
                    $"The final destination directory is unavailable: '{component}'.");
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                return Failure(
                    VerifiedTemporaryExifToolWriteFailureKind.LinkedPath,
                    $"The final destination path contains a symbolic link or reparse " +
                    $"point: '{component}'.");
            }
        }

        return TryGetAttributes(finalPath, out _)
            ? Failure(
                VerifiedTemporaryExifToolWriteFailureKind.ExistingFinalDestination,
                $"The final destination already exists: '{finalPath}'.")
            : null;
    }

    private static VerifiedTemporaryExifToolWriteOutcome? VerifyTemporaryCopy(
        VerifiedMediaFileStagingSuccessResult staging)
    {
        long byteCount = 0;
        string hash;
        try
        {
            using var stream = new FileStream(
                staging.TemporaryCopyPath,
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
            return Failure(
                exception is FileNotFoundException or DirectoryNotFoundException
                    ? VerifiedTemporaryExifToolWriteFailureKind.MissingTemporaryFile
                    : VerifiedTemporaryExifToolWriteFailureKind.TemporaryFileChanged,
                $"The temporary copy could not be verified: {exception.Message}",
                retainExifToolPath: true);
        }

        if (byteCount == staging.CopiedByteCount
            && StringComparer.Ordinal.Equals(
                hash,
                staging.TemporaryCopySha256.ToUpperInvariant()))
        {
            return null;
        }

        return new VerifiedTemporaryExifToolWriteOutcome(
            false,
            VerifiedTemporaryExifToolWriteFailureKind.TemporaryFileChanged,
            "The temporary copy no longer matches its verified staging result.",
            ActualByteCount: byteCount,
            ActualWholeFileSha256: hash,
            RetainExifToolPath: true);
    }

    private static VerifiedTemporaryExifToolWriteOutcome RunExifTool(
        string temporaryPath,
        string exifToolPath,
        IReadOnlyList<string> assignmentArguments,
        TimeSpan timeout,
        string operationDescription)
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
        foreach (var argument in assignmentArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        startInfo.ArgumentList.Add(temporaryPath);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return Failure(
                    VerifiedTemporaryExifToolWriteFailureKind.ExifToolFailure,
                    "ExifTool could not be started.",
                    retainExifToolPath: true);
            }
        }
        catch (Exception exception) when (exception is Win32Exception
                                          or InvalidOperationException
                                          or IOException
                                          or UnauthorizedAccessException
                                          or NotSupportedException)
        {
            return Failure(
                VerifiedTemporaryExifToolWriteFailureKind.ExifToolFailure,
                $"ExifTool could not be started: {exception.Message}",
                retainExifToolPath: true);
        }

        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        var timeoutMilliseconds = (int)Math.Min(
            Math.Ceiling(timeout.TotalMilliseconds),
            int.MaxValue);
        if (!process.WaitForExit(timeoutMilliseconds))
        {
            var terminationError = Terminate(process);
            TryCaptureOutput(
                standardOutput,
                standardError,
                out var timedOutOutput,
                out var timedOutError,
                ref terminationError);
            return new VerifiedTemporaryExifToolWriteOutcome(
                false,
                VerifiedTemporaryExifToolWriteFailureKind.TimedOut,
                $"ExifTool {operationDescription} timed out after {timeout}.",
                Timeout: timeout,
                TerminationError: terminationError,
                StandardOutput: timedOutOutput,
                StandardError: timedOutError,
                RetainExifToolPath: true);
        }

        string? captureError = null;
        if (!TryCaptureOutput(
                standardOutput,
                standardError,
                out var output,
                out var error,
                ref captureError))
        {
            return new VerifiedTemporaryExifToolWriteOutcome(
                false,
                VerifiedTemporaryExifToolWriteFailureKind.ExifToolFailure,
                captureError!,
                process.ExitCode,
                StandardOutput: output,
                StandardError: error,
                RetainExifToolPath: true);
        }

        return process.ExitCode == 0
            ? new VerifiedTemporaryExifToolWriteOutcome(
                true,
                StandardOutput: output,
                StandardError: error,
                RetainExifToolPath: true)
            : new VerifiedTemporaryExifToolWriteOutcome(
                false,
                VerifiedTemporaryExifToolWriteFailureKind.ExifToolFailure,
                $"ExifTool {operationDescription} exited with code {process.ExitCode}.",
                process.ExitCode,
                StandardOutput: output,
                StandardError: error,
                RetainExifToolPath: true);
    }

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
                : "The timed-out ExifTool process did not exit within one second " +
                  "after termination was requested.";
        }
        catch (InvalidOperationException exception)
        {
            return HasExited(process)
                ? null
                : $"The timed-out ExifTool process could not be terminated: " +
                  exception.Message;
        }
        catch (Exception exception) when (exception is NotSupportedException
                                          or Win32Exception)
        {
            return $"The timed-out ExifTool process could not be terminated: " +
                   exception.Message;
        }
    }

    private static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
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
               && !relative.StartsWith(
                   ".." + Path.DirectorySeparatorChar,
                   StringComparison.Ordinal)
               && !relative.StartsWith(
                   ".." + Path.AltDirectorySeparatorChar,
                   StringComparison.Ordinal);
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 }
        && value.All(character =>
            character is >= '0' and <= '9'
                or >= 'A' and <= 'F'
                or >= 'a' and <= 'f');

    private static bool TryGetAttributes(string path, out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (Exception exception) when (exception is FileNotFoundException
                                          or DirectoryNotFoundException
                                          || IsFileSystemException(exception))
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

    private static VerifiedTemporaryExifToolWriteOutcome Failure(
        VerifiedTemporaryExifToolWriteFailureKind kind,
        string message,
        bool retainExifToolPath = false) =>
        new(false, kind, message, RetainExifToolPath: retainExifToolPath);
}
