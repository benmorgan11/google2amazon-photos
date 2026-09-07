using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace PhotoMigration.Core;

public static class ExifToolMetadataReader
{
    private const int CleanupWaitMilliseconds = 1_000;

    private static readonly HashSet<string> SupportedExtensions = new(
        StringComparer.OrdinalIgnoreCase)
    {
        ".jpg",
        ".jpeg",
        ".heic",
        ".heif"
    };

    private static readonly IReadOnlyDictionary<string, EmbeddedMetadataField> FieldsByTag =
        new Dictionary<string, EmbeddedMetadataField>(StringComparer.Ordinal)
        {
            ["DateTimeOriginal"] = EmbeddedMetadataField.CaptureDateTime,
            ["CreateDate"] = EmbeddedMetadataField.CaptureDateTime,
            ["OffsetTimeOriginal"] = EmbeddedMetadataField.CaptureTimezoneOffset,
            ["GPSLatitude"] = EmbeddedMetadataField.GpsLatitude,
            ["GPSLatitudeRef"] = EmbeddedMetadataField.GpsLatitudeReference,
            ["GPSLongitude"] = EmbeddedMetadataField.GpsLongitude,
            ["GPSLongitudeRef"] = EmbeddedMetadataField.GpsLongitudeReference,
            ["GPSAltitude"] = EmbeddedMetadataField.GpsAltitude,
            ["GPSAltitudeRef"] = EmbeddedMetadataField.GpsAltitudeReference
        };

    private static readonly string[] RequestedArguments =
    [
        "-json",
        "-G1",
        "-s",
        "-EXIF:DateTimeOriginal",
        "-EXIF:CreateDate",
        "-XMP:DateTimeOriginal",
        "-XMP:CreateDate",
        "-EXIF:OffsetTimeOriginal",
        "-EXIF:GPSLatitude#",
        "-EXIF:GPSLatitudeRef#",
        "-EXIF:GPSLongitude#",
        "-EXIF:GPSLongitudeRef#",
        "-EXIF:GPSAltitude#",
        "-EXIF:GPSAltitudeRef#",
        "-XMP:GPSLatitude#",
        "-XMP:GPSLongitude#",
        "-XMP:GPSAltitude#"
    ];

    public static readonly TimeSpan DefaultReadTimeout = TimeSpan.FromSeconds(5);

    public static EmbeddedMetadataReadResult Read(
        string mediaPath,
        string exifToolExecutablePath,
        TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(exifToolExecutablePath);

        var readTimeout = timeout ?? DefaultReadTimeout;
        if (readTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                "The metadata read timeout must be greater than zero.");
        }

        var absoluteMediaPath = Path.GetFullPath(mediaPath);
        var extension = Path.GetExtension(absoluteMediaPath);
        if (!SupportedExtensions.Contains(extension))
        {
            return new EmbeddedMetadataUnsupportedMediaResult(
                absoluteMediaPath,
                extension);
        }

        if (!File.Exists(absoluteMediaPath))
        {
            return new EmbeddedMetadataMissingMediaResult(absoluteMediaPath);
        }

        var absoluteExifToolPath = Path.GetFullPath(exifToolExecutablePath);
        return RunExifTool(
            absoluteMediaPath,
            absoluteExifToolPath,
            readTimeout);
    }

    private static EmbeddedMetadataReadResult RunExifTool(
        string mediaPath,
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

        startInfo.ArgumentList.Add(mediaPath);

        using var process = new Process { StartInfo = startInfo };

        try
        {
            if (!process.Start())
            {
                return ExecutionFailure(
                    mediaPath,
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
                mediaPath,
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
                mediaPath,
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
                mediaPath,
                exifToolExecutablePath,
                captureError!,
                process.ExitCode);
        }

        if (process.ExitCode != 0)
        {
            return new EmbeddedMetadataExifToolFailureResult(
                mediaPath,
                exifToolExecutablePath,
                $"ExifTool metadata read exited with code {process.ExitCode}.",
                process.ExitCode,
                output,
                error);
        }

        return ParseOutput(
            mediaPath,
            exifToolExecutablePath,
            output,
            error);
    }

    private static EmbeddedMetadataReadResult ParseOutput(
        string mediaPath,
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
                    mediaPath,
                    exifToolExecutablePath,
                    "ExifTool JSON must be an array containing one object.",
                    standardOutput,
                    standardError);
            }

            var values = new List<EmbeddedMetadataValue>();
            var warnings = new List<string>();
            var errors = new List<string>();

            foreach (var property in root[0].EnumerateObject())
            {
                var separatorIndex = property.Name.IndexOf(':');
                var groupName = separatorIndex > 0
                    ? property.Name[..separatorIndex]
                    : string.Empty;
                var tagName = separatorIndex > 0
                    ? property.Name[(separatorIndex + 1)..]
                    : property.Name;

                if (string.Equals(tagName, "Warning", StringComparison.Ordinal))
                {
                    AddDiagnostic(property.Value, warnings);
                }
                else if (string.Equals(tagName, "Error", StringComparison.Ordinal))
                {
                    AddDiagnostic(property.Value, errors);
                }
                else if (separatorIndex > 0
                         && FieldsByTag.TryGetValue(tagName, out var field))
                {
                    values.Add(new EmbeddedMetadataValue(
                        field,
                        groupName,
                        tagName,
                        ReadRawValue(property.Value),
                        property.Value.ValueKind));
                }
            }

            values.Sort(CompareValues);
            warnings.Sort(StringComparer.Ordinal);
            errors.Sort(StringComparer.Ordinal);

            return new EmbeddedMetadataReadSuccessResult(
                mediaPath,
                exifToolExecutablePath,
                values.AsReadOnly(),
                warnings.AsReadOnly(),
                errors.AsReadOnly(),
                standardError);
        }
        catch (JsonException exception)
        {
            return MalformedJson(
                mediaPath,
                exifToolExecutablePath,
                $"ExifTool returned malformed JSON: {exception.Message}",
                standardOutput,
                standardError);
        }
    }

    private static string ReadRawValue(JsonElement value) =>
        value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : value.GetRawText();

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

    private static int CompareValues(
        EmbeddedMetadataValue left,
        EmbeddedMetadataValue right)
    {
        var groupComparison = StringComparer.Ordinal.Compare(left.GroupName, right.GroupName);
        return groupComparison != 0
            ? groupComparison
            : StringComparer.Ordinal.Compare(left.TagName, right.TagName);
    }

    private static EmbeddedMetadataReadTimedOutResult TimedOut(
        string mediaPath,
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

        return new EmbeddedMetadataReadTimedOutResult(
            mediaPath,
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

            return $"The timed-out ExifTool process could not be terminated: {exception.Message}";
        }
        catch (Exception exception) when (exception is NotSupportedException or Win32Exception)
        {
            return $"The timed-out ExifTool process could not be terminated: {exception.Message}";
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

    private static EmbeddedMetadataExifToolFailureResult ExecutionFailure(
        string mediaPath,
        string exifToolExecutablePath,
        string message,
        int? exitCode = null)
    {
        return new EmbeddedMetadataExifToolFailureResult(
            mediaPath,
            exifToolExecutablePath,
            message,
            exitCode,
            string.Empty,
            string.Empty);
    }

    private static EmbeddedMetadataMalformedJsonResult MalformedJson(
        string mediaPath,
        string exifToolExecutablePath,
        string message,
        string standardOutput,
        string standardError)
    {
        return new EmbeddedMetadataMalformedJsonResult(
            mediaPath,
            exifToolExecutablePath,
            message,
            standardOutput,
            standardError);
    }
}
