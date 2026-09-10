using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PhotoMigration.Core;

public static class ExifToolImageDataHashReader
{
    private const int CleanupWaitMilliseconds = 1_000;
    private const string AlgorithmName = "SHA256";
    private const string GroupedHashTagName = "File:ImageDataHash";

    private static readonly HashSet<string> SupportedExtensions = new(
        StringComparer.OrdinalIgnoreCase)
    {
        ".jpg",
        ".jpeg",
        ".heic",
        ".heif",
        ".png",
        ".mov",
        ".mp4"
    };

    private static readonly string[] RequestedArguments =
    [
        "-json",
        "-G1",
        "-s",
        "-api",
        "ImageHashType=SHA256",
        "-ImageDataHash"
    ];

    public static readonly TimeSpan DefaultHashTimeout = TimeSpan.FromMinutes(10);

    public static ImageDataHashReadResult Read(
        VerifiedMediaFileStagingSuccessResult stagingResult,
        string exifToolExecutablePath,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(stagingResult);
        ArgumentException.ThrowIfNullOrWhiteSpace(exifToolExecutablePath);

        var hashTimeout = timeout ?? DefaultHashTimeout;
        if (hashTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                "The ImageDataHash read timeout must be greater than zero.");
        }

        if (!Path.IsPathFullyQualified(exifToolExecutablePath))
        {
            throw new ArgumentException(
                "The ExifTool executable path must be absolute.",
                nameof(exifToolExecutablePath));
        }

        var invalidStagingResult = ValidateStagingResult(stagingResult);
        if (invalidStagingResult is not null)
        {
            return invalidStagingResult;
        }

        var temporaryPath = stagingResult.TemporaryCopyPath;
        var extension = Path.GetExtension(stagingResult.IntendedFinalDestinationPath);
        if (!SupportedExtensions.Contains(extension))
        {
            return new ImageDataHashUnsupportedMediaResult(
                stagingResult,
                temporaryPath,
                extension);
        }

        var temporaryFileFailure = ValidateTemporaryFile(stagingResult, temporaryPath);
        if (temporaryFileFailure is not null)
        {
            return temporaryFileFailure;
        }

        string absoluteExifToolPath;
        try
        {
            absoluteExifToolPath = Path.GetFullPath(exifToolExecutablePath);
        }
        catch (Exception exception) when (exception is ArgumentException
                                          or NotSupportedException
                                          or PathTooLongException)
        {
            return ExecutionFailure(
                stagingResult,
                temporaryPath,
                exifToolExecutablePath,
                $"The ExifTool executable path is invalid: {exception.Message}");
        }

        return RunExifTool(
            stagingResult,
            temporaryPath,
            absoluteExifToolPath,
            hashTimeout);
    }

    private static ImageDataHashInvalidStagingResult? ValidateStagingResult(
        VerifiedMediaFileStagingSuccessResult stagingResult)
    {
        if (stagingResult.CopiedByteCount < 0
            || stagingResult.CopiedByteCount
            != stagingResult.PlanningItem.MediaEntry.SizeInBytes)
        {
            return InvalidStaging(
                stagingResult,
                "The staging byte count does not match the inventory entry.");
        }

        if (!TryNormalizeSha256(stagingResult.SourceSha256, out var sourceHash)
            || !TryNormalizeSha256(
                stagingResult.TemporaryCopySha256,
                out var temporaryHash)
            || !StringComparer.Ordinal.Equals(sourceHash, temporaryHash))
        {
            return InvalidStaging(
                stagingResult,
                "The staging result does not contain matching valid SHA-256 values.");
        }

        if (!TryNormalizeAbsolutePath(
                stagingResult.TemporaryCopyPath,
                out var temporaryPath)
            || !TryNormalizeAbsolutePath(
                stagingResult.IntendedFinalDestinationPath,
                out var intendedFinalPath)
            || !TryNormalizeAbsolutePath(
                stagingResult.PlanningItem.AbsoluteDestinationPath,
                out var plannedFinalPath))
        {
            return InvalidStaging(
                stagingResult,
                "The staging result contains an invalid or non-absolute path.");
        }

        if (!StringComparer.Ordinal.Equals(
                stagingResult.TemporaryCopyPath,
                temporaryPath)
            || !StringComparer.Ordinal.Equals(
                stagingResult.IntendedFinalDestinationPath,
                intendedFinalPath)
            || !StringComparer.Ordinal.Equals(
                stagingResult.PlanningItem.AbsoluteDestinationPath,
                plannedFinalPath))
        {
            return InvalidStaging(
                stagingResult,
                "The staging paths are not normalized absolute paths.");
        }

        if (!StringComparer.Ordinal.Equals(intendedFinalPath, plannedFinalPath))
        {
            return InvalidStaging(
                stagingResult,
                "The intended final destination does not match the planning item.");
        }

        var temporaryDirectory = Path.GetDirectoryName(temporaryPath);
        var finalDirectory = Path.GetDirectoryName(intendedFinalPath);
        if (temporaryDirectory is null
            || finalDirectory is null
            || !StringComparer.Ordinal.Equals(temporaryDirectory, finalDirectory))
        {
            return InvalidStaging(
                stagingResult,
                "The temporary copy is not beside its intended final destination.");
        }

        if (PathsEqualConservatively(temporaryPath, intendedFinalPath))
        {
            return InvalidStaging(
                stagingResult,
                "The temporary copy path must differ from the final destination.");
        }

        if (!StringComparer.OrdinalIgnoreCase.Equals(
                Path.GetExtension(temporaryPath),
                Path.GetExtension(intendedFinalPath)))
        {
            return InvalidStaging(
                stagingResult,
                "The temporary copy and final destination extensions do not match.");
        }

        return null;
    }

    private static ImageDataHashReadResult RunExifTool(
        VerifiedMediaFileStagingSuccessResult stagingResult,
        string temporaryPath,
        string exifToolExecutablePath,
        TimeSpan timeout)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = exifToolExecutablePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var argument in RequestedArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.ArgumentList.Add(temporaryPath);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return ExecutionFailure(
                    stagingResult,
                    temporaryPath,
                    exifToolExecutablePath,
                    "ExifTool could not be started.");
            }
        }
        catch (Exception exception) when (exception is Win32Exception
                                          or InvalidOperationException
                                          or IOException
                                          or UnauthorizedAccessException
                                          or NotSupportedException)
        {
            return ExecutionFailure(
                stagingResult,
                temporaryPath,
                exifToolExecutablePath,
                $"ExifTool could not be started: {exception.Message}");
        }

        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        var timeoutMilliseconds = (int)Math.Min(
            Math.Ceiling(timeout.TotalMilliseconds),
            int.MaxValue);

        if (!process.WaitForExit(timeoutMilliseconds))
        {
            return TimedOut(
                stagingResult,
                temporaryPath,
                exifToolExecutablePath,
                timeout,
                process,
                standardOutput,
                standardError);
        }

        if (!TryCaptureOutput(
                standardOutput,
                standardError,
                out var output,
                out var error,
                out var captureError))
        {
            return ExecutionFailure(
                stagingResult,
                temporaryPath,
                exifToolExecutablePath,
                captureError!,
                process.ExitCode);
        }

        if (process.ExitCode != 0)
        {
            return new ImageDataHashExifToolFailureResult(
                stagingResult,
                temporaryPath,
                exifToolExecutablePath,
                $"ExifTool ImageDataHash read exited with code {process.ExitCode}.",
                process.ExitCode,
                output,
                error);
        }

        var verificationResult = VerifyTemporaryCopy(
            stagingResult,
            temporaryPath,
            exifToolExecutablePath,
            output,
            error);
        if (verificationResult is not null)
        {
            return verificationResult;
        }

        return ParseOutput(
            stagingResult,
            temporaryPath,
            exifToolExecutablePath,
            output,
            error);
    }

    private static ImageDataHashReadResult? VerifyTemporaryCopy(
        VerifiedMediaFileStagingSuccessResult stagingResult,
        string temporaryPath,
        string exifToolExecutablePath,
        string standardOutput,
        string standardError)
    {
        var temporaryFileFailure = ValidateTemporaryFile(stagingResult, temporaryPath);
        if (temporaryFileFailure is not null)
        {
            return temporaryFileFailure;
        }

        long actualByteCount;
        string actualHash;
        try
        {
            using var stream = new FileStream(
                temporaryPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 81_920,
                FileOptions.SequentialScan);
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81_920];
            actualByteCount = 0;

            int bytesRead;
            while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                hasher.AppendData(buffer, 0, bytesRead);
                actualByteCount += bytesRead;
            }

            actualHash = Convert.ToHexString(hasher.GetHashAndReset());
        }
        catch (Exception exception) when (exception is FileNotFoundException
                                          or DirectoryNotFoundException)
        {
            return new ImageDataHashMissingTemporaryFileResult(
                stagingResult,
                temporaryPath);
        }
        catch (Exception exception) when (IsFileSystemException(exception))
        {
            return VerificationFailure(
                stagingResult,
                temporaryPath,
                exifToolExecutablePath,
                $"The temporary copy could not be verified: {exception.Message}",
                actualByteCount: null,
                actualHash: null,
                standardOutput,
                standardError);
        }

        temporaryFileFailure = ValidateTemporaryFile(stagingResult, temporaryPath);
        if (temporaryFileFailure is not null)
        {
            return temporaryFileFailure;
        }

        var expectedHash = stagingResult.TemporaryCopySha256.ToUpperInvariant();
        if (actualByteCount != stagingResult.CopiedByteCount
            || !StringComparer.Ordinal.Equals(actualHash, expectedHash))
        {
            return VerificationFailure(
                stagingResult,
                temporaryPath,
                exifToolExecutablePath,
                "The temporary copy no longer matches its verified staging result.",
                actualByteCount,
                actualHash,
                standardOutput,
                standardError);
        }

        return null;
    }

    private static ImageDataHashReadResult ParseOutput(
        VerifiedMediaFileStagingSuccessResult stagingResult,
        string temporaryPath,
        string exifToolExecutablePath,
        string standardOutput,
        string standardError)
    {
        try
        {
            using var document = JsonDocument.Parse(standardOutput);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array
                || root.GetArrayLength() != 1
                || root[0].ValueKind != JsonValueKind.Object)
            {
                return MalformedJson(
                    stagingResult,
                    temporaryPath,
                    exifToolExecutablePath,
                    "ExifTool JSON must be an array containing exactly one object.",
                    standardOutput,
                    standardError);
            }

            var hashValues = new List<JsonElement>();
            var warnings = new List<string>();
            foreach (var property in root[0].EnumerateObject())
            {
                if (StringComparer.Ordinal.Equals(property.Name, GroupedHashTagName))
                {
                    hashValues.Add(property.Value.Clone());
                }
                else if (IsWarning(property.Name))
                {
                    AddDiagnostic(property.Value, warnings);
                }
            }

            warnings.Sort(StringComparer.Ordinal);
            var readOnlyWarnings = warnings.AsReadOnly();
            if (hashValues.Count == 0)
            {
                return new ImageDataHashMissingValueResult(
                    stagingResult,
                    temporaryPath,
                    exifToolExecutablePath,
                    readOnlyWarnings,
                    standardOutput,
                    standardError);
            }

            if (hashValues.Count != 1)
            {
                return MalformedJson(
                    stagingResult,
                    temporaryPath,
                    exifToolExecutablePath,
                    "ExifTool JSON must contain exactly one File:ImageDataHash value.",
                    standardOutput,
                    standardError);
            }

            var hashValue = hashValues[0];
            var rawHash = hashValue.ValueKind == JsonValueKind.String
                ? hashValue.GetString() ?? string.Empty
                : hashValue.GetRawText();
            if (!TryNormalizeSha256(rawHash, out var normalizedHash))
            {
                return new ImageDataHashInvalidValueResult(
                    stagingResult,
                    temporaryPath,
                    exifToolExecutablePath,
                    rawHash,
                    readOnlyWarnings,
                    standardOutput,
                    standardError);
            }

            return new ImageDataHashReadSuccessResult(
                stagingResult,
                temporaryPath,
                exifToolExecutablePath,
                AlgorithmName,
                rawHash,
                normalizedHash,
                readOnlyWarnings,
                standardError);
        }
        catch (JsonException exception)
        {
            return MalformedJson(
                stagingResult,
                temporaryPath,
                exifToolExecutablePath,
                $"ExifTool returned malformed JSON: {exception.Message}",
                standardOutput,
                standardError);
        }
    }

    private static ImageDataHashReadTimedOutResult TimedOut(
        VerifiedMediaFileStagingSuccessResult stagingResult,
        string temporaryPath,
        string exifToolExecutablePath,
        TimeSpan timeout,
        Process process,
        Task<string> standardOutput,
        Task<string> standardError)
    {
        var terminationError = Terminate(process);
        var output = string.Empty;
        var error = string.Empty;

        if (terminationError is null
            && !TryCaptureOutput(
                standardOutput,
                standardError,
                out output,
                out error,
                out terminationError))
        {
            output = string.Empty;
            error = string.Empty;
        }

        return new ImageDataHashReadTimedOutResult(
            stagingResult,
            temporaryPath,
            exifToolExecutablePath,
            timeout,
            terminationError,
            output,
            error);
    }

    private static bool TryCaptureOutput(
        Task<string> standardOutput,
        Task<string> standardError,
        out string output,
        out string error,
        out string? captureError)
    {
        output = string.Empty;
        error = string.Empty;
        captureError = null;

        try
        {
            var redirectedOutput = Task.WhenAll(standardOutput, standardError);
            if (!redirectedOutput.Wait(CleanupWaitMilliseconds))
            {
                captureError = "ExifTool output streams did not close within one second.";
                return false;
            }

            var capturedOutput = redirectedOutput.GetAwaiter().GetResult();
            output = capturedOutput[0];
            error = capturedOutput[1];
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
            if (HasExited(process))
            {
                return null;
            }

            return $"The timed-out ExifTool process could not be terminated: " +
                   exception.Message;
        }
        catch (Exception exception) when (exception is NotSupportedException or Win32Exception)
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

    private static bool TryNormalizeAbsolutePath(string path, out string normalizedPath)
    {
        normalizedPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            return false;
        }

        try
        {
            normalizedPath = Path.GetFullPath(path);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException
                                          or NotSupportedException
                                          or PathTooLongException)
        {
            return false;
        }
    }

    private static bool TryNormalizeSha256(string? value, out string normalizedHash)
    {
        normalizedHash = string.Empty;
        if (value is null || value.Length != 64 || value.Any(character => !IsHex(character)))
        {
            return false;
        }

        normalizedHash = value.ToUpperInvariant();
        return true;
    }

    private static bool IsHex(char value) =>
        value is >= '0' and <= '9'
            or >= 'A' and <= 'F'
            or >= 'a' and <= 'f';

    private static ImageDataHashReadResult? ValidateTemporaryFile(
        VerifiedMediaFileStagingSuccessResult stagingResult,
        string temporaryPath)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(temporaryPath);
        }
        catch (Exception exception) when (exception is FileNotFoundException
                                          or DirectoryNotFoundException)
        {
            return new ImageDataHashMissingTemporaryFileResult(
                stagingResult,
                temporaryPath);
        }
        catch (Exception exception) when (IsFileSystemException(exception))
        {
            return InvalidStaging(
                stagingResult,
                $"The temporary copy could not be inspected: {exception.Message}");
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            return new ImageDataHashLinkedTemporaryFileResult(
                stagingResult,
                temporaryPath);
        }

        return (attributes & (FileAttributes.Directory | FileAttributes.Device)) != 0
            ? new ImageDataHashNonRegularTemporaryFileResult(stagingResult, temporaryPath)
            : null;
    }

    private static bool IsWarning(string propertyName)
    {
        var separatorIndex = propertyName.IndexOf(':');
        var tagName = separatorIndex >= 0
            ? propertyName[(separatorIndex + 1)..]
            : propertyName;
        return StringComparer.Ordinal.Equals(tagName, "Warning");
    }

    private static void AddDiagnostic(JsonElement value, ICollection<string> diagnostics)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            diagnostics.Add(value.GetString() ?? string.Empty);
        }
        else
        {
            diagnostics.Add(value.GetRawText());
        }
    }

    private static ImageDataHashInvalidStagingResult InvalidStaging(
        VerifiedMediaFileStagingSuccessResult stagingResult,
        string message) =>
        new(stagingResult, message);

    private static ImageDataHashExifToolFailureResult ExecutionFailure(
        VerifiedMediaFileStagingSuccessResult stagingResult,
        string temporaryPath,
        string exifToolExecutablePath,
        string message,
        int? exitCode = null) =>
        new(
            stagingResult,
            temporaryPath,
            exifToolExecutablePath,
            message,
            exitCode,
            string.Empty,
            string.Empty);

    private static ImageDataHashMalformedJsonResult MalformedJson(
        VerifiedMediaFileStagingSuccessResult stagingResult,
        string temporaryPath,
        string exifToolExecutablePath,
        string message,
        string standardOutput,
        string standardError) =>
        new(
            stagingResult,
            temporaryPath,
            exifToolExecutablePath,
            message,
            standardOutput,
            standardError);

    private static ImageDataHashTemporaryVerificationFailureResult VerificationFailure(
        VerifiedMediaFileStagingSuccessResult stagingResult,
        string temporaryPath,
        string exifToolExecutablePath,
        string message,
        long? actualByteCount,
        string? actualHash,
        string standardOutput,
        string standardError) =>
        new(
            stagingResult,
            temporaryPath,
            exifToolExecutablePath,
            message,
            stagingResult.CopiedByteCount,
            actualByteCount,
            stagingResult.TemporaryCopySha256.ToUpperInvariant(),
            actualHash,
            standardOutput,
            standardError);

    private static bool PathsEqualConservatively(string left, string right) =>
        StringComparer.OrdinalIgnoreCase.Equals(
            left.Normalize(NormalizationForm.FormC),
            right.Normalize(NormalizationForm.FormC));

    private static bool IsFileSystemException(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or System.Security.SecurityException;
}
