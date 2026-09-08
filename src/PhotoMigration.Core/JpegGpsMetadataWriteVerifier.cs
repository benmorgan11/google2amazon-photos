using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace PhotoMigration.Core;

public static class JpegGpsMetadataWriteVerifier
{
    private const int CleanupWaitMilliseconds = 1_000;
    private const string ImageDataHashProperty = "File:ImageDataHash";

    private static readonly string[] RequestedArguments =
    [
        "-json",
        "-G1",
        "-s",
        "-api",
        "ImageHashType=SHA256",
        "-EXIF:GPSLatitude#",
        "-EXIF:GPSLatitudeRef#",
        "-EXIF:GPSLongitude#",
        "-EXIF:GPSLongitudeRef#",
        "-EXIF:GPSAltitude#",
        "-EXIF:GPSAltitudeRef#",
        "-ImageDataHash"
    ];

    private static readonly IReadOnlyDictionary<string, EmbeddedMetadataField> GpsFields =
        new Dictionary<string, EmbeddedMetadataField>(StringComparer.Ordinal)
        {
            ["GPS:GPSLatitude"] = EmbeddedMetadataField.GpsLatitude,
            ["GPS:GPSLatitudeRef"] = EmbeddedMetadataField.GpsLatitudeReference,
            ["GPS:GPSLongitude"] = EmbeddedMetadataField.GpsLongitude,
            ["GPS:GPSLongitudeRef"] = EmbeddedMetadataField.GpsLongitudeReference,
            ["GPS:GPSAltitude"] = EmbeddedMetadataField.GpsAltitude,
            ["GPS:GPSAltitudeRef"] = EmbeddedMetadataField.GpsAltitudeReference
        };

    public static readonly TimeSpan DefaultVerificationTimeout = TimeSpan.FromMinutes(10);

    public static JpegGpsMetadataWriteVerificationResult Verify(
        JpegGpsMetadataWriteSuccessResult writeResult,
        string exifToolExecutablePath,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(writeResult);

        var verificationTimeout = timeout ?? DefaultVerificationTimeout;
        if (verificationTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                "The JPEG GPS verification timeout must be greater than zero.");
        }

        var temporaryPath = writeResult.TemporaryCopyPath;
        var finalPath = writeResult.IntendedFinalDestinationPath;
        var baselineHash = writeResult.WritePlan.BaselineHashResult.ImageDataHashSha256;
        JpegGpsMetadataVerificationValues? expectedGps = null;

        if (!TryNormalizeAbsolutePath(exifToolExecutablePath, out var exifToolPath)
            || !TryBuildExpectedGps(writeResult, out expectedGps)
            || writeResult.Status != JpegGpsMetadataWriteStatus.PendingVerification
            || !StringComparer.Ordinal.Equals(
                temporaryPath, writeResult.WritePlan.StagingResult.TemporaryCopyPath)
            || !StringComparer.Ordinal.Equals(
                finalPath,
                writeResult.WritePlan.StagingResult.IntendedFinalDestinationPath)
            || !TryNormalizeSha256(baselineHash, out baselineHash))
        {
            return Failure(
                writeResult,
                JpegGpsMetadataWriteVerificationFailureKind.InvalidInput,
                "The write result, approved GPS assignments, paths, baseline hash, " +
                "or ExifTool path is invalid.",
                expectedGps,
                exifToolExecutablePath: exifToolExecutablePath);
        }

        var pathFailure = ValidatePaths(writeResult, baselineHash, expectedGps);
        if (pathFailure is not null)
        {
            return pathFailure;
        }

        return RunExifTool(
            writeResult,
            exifToolPath,
            verificationTimeout,
            baselineHash,
            expectedGps!);
    }

    private static JpegGpsMetadataWriteVerificationResult RunExifTool(
        JpegGpsMetadataWriteSuccessResult writeResult,
        string exifToolPath,
        TimeSpan timeout,
        string baselineHash,
        JpegGpsMetadataVerificationValues expectedGps)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = exifToolPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var argument in RequestedArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.ArgumentList.Add(writeResult.TemporaryCopyPath);
        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return Failure(
                    writeResult,
                    JpegGpsMetadataWriteVerificationFailureKind.ExifToolFailure,
                    "ExifTool could not be started.",
                    expectedGps,
                    exifToolExecutablePath: exifToolPath);
            }
        }
        catch (Exception exception) when (exception is Win32Exception
                                          or InvalidOperationException
                                          or IOException
                                          or UnauthorizedAccessException
                                          or NotSupportedException)
        {
            return Failure(
                writeResult,
                JpegGpsMetadataWriteVerificationFailureKind.ExifToolFailure,
                $"ExifTool could not be started: {exception.Message}",
                expectedGps,
                exifToolExecutablePath: exifToolPath);
        }

        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        var timeoutMilliseconds = (int)Math.Min(
            Math.Ceiling(timeout.TotalMilliseconds), int.MaxValue);
        if (!process.WaitForExit(timeoutMilliseconds))
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

            return new JpegGpsMetadataWriteVerificationFailureResult(
                writeResult,
                writeResult.TemporaryCopyPath,
                writeResult.IntendedFinalDestinationPath,
                baselineHash,
                JpegGpsMetadataWriteVerificationFailureKind.TimedOut,
                $"ExifTool JPEG GPS verification timed out after {timeout}.",
                expectedGps,
                ExifToolExecutablePath: exifToolPath,
                Timeout: timeout,
                TerminationError: terminationError,
                StandardOutput: output,
                StandardError: error);
        }

        if (!TryCaptureOutput(
                standardOutput,
                standardError,
                out var capturedOutput,
                out var capturedError,
                out var captureError))
        {
            return Failure(
                writeResult,
                JpegGpsMetadataWriteVerificationFailureKind.ExifToolFailure,
                captureError!,
                expectedGps,
                exifToolExecutablePath: exifToolPath,
                exitCode: process.ExitCode);
        }

        if (process.ExitCode != 0)
        {
            return new JpegGpsMetadataWriteVerificationFailureResult(
                writeResult,
                writeResult.TemporaryCopyPath,
                writeResult.IntendedFinalDestinationPath,
                baselineHash,
                JpegGpsMetadataWriteVerificationFailureKind.ExifToolFailure,
                $"ExifTool JPEG GPS verification exited with code {process.ExitCode}.",
                expectedGps,
                ExifToolExecutablePath: exifToolPath,
                ExitCode: process.ExitCode,
                StandardOutput: capturedOutput,
                StandardError: capturedError);
        }

        var pathFailure = ValidatePaths(writeResult, baselineHash, expectedGps);
        return pathFailure ?? ParseAndVerify(
            writeResult,
            exifToolPath,
            baselineHash,
            expectedGps,
            capturedOutput,
            capturedError);
    }

    private static JpegGpsMetadataWriteVerificationResult ParseAndVerify(
        JpegGpsMetadataWriteSuccessResult writeResult,
        string exifToolPath,
        string baselineHash,
        JpegGpsMetadataVerificationValues expectedGps,
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
                return Failure(
                    writeResult,
                    JpegGpsMetadataWriteVerificationFailureKind.MalformedOutput,
                    "ExifTool JSON must be an array containing exactly one object.",
                    expectedGps,
                    exifToolExecutablePath: exifToolPath,
                    standardOutput: standardOutput,
                    standardError: standardError);
            }

            var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            var hashValues = new List<JsonElement>();
            var warnings = new List<string>();
            var errors = new List<string>();
            foreach (var property in root[0].EnumerateObject())
            {
                if (GpsFields.ContainsKey(property.Name))
                {
                    if (!values.TryAdd(property.Name, property.Value.Clone()))
                    {
                        return Failure(
                            writeResult,
                            JpegGpsMetadataWriteVerificationFailureKind.MalformedOutput,
                            $"ExifTool JSON contains duplicate '{property.Name}' values.",
                            expectedGps,
                            exifToolExecutablePath: exifToolPath,
                            standardOutput: standardOutput,
                            standardError: standardError);
                    }
                }
                else if (StringComparer.Ordinal.Equals(property.Name, ImageDataHashProperty))
                {
                    hashValues.Add(property.Value.Clone());
                }
                else if (IsDiagnostic(property.Name, "Warning"))
                {
                    AddDiagnostic(property.Value, warnings);
                }
                else if (IsDiagnostic(property.Name, "Error"))
                {
                    AddDiagnostic(property.Value, errors);
                }
            }

            warnings.Sort(StringComparer.Ordinal);
            errors.Sort(StringComparer.Ordinal);
            var readOnlyWarnings = warnings.AsReadOnly();
            var readOnlyErrors = errors.AsReadOnly();
            if (hashValues.Count == 0)
            {
                return Failure(
                    writeResult,
                    JpegGpsMetadataWriteVerificationFailureKind.MissingImageDataHash,
                    "ExifTool did not return File:ImageDataHash.",
                    expectedGps,
                    exifToolExecutablePath: exifToolPath,
                    warnings: readOnlyWarnings,
                    errors: readOnlyErrors,
                    standardOutput: standardOutput,
                    standardError: standardError);
            }

            if (hashValues.Count != 1)
            {
                return Failure(
                    writeResult,
                    JpegGpsMetadataWriteVerificationFailureKind.MalformedOutput,
                    "ExifTool JSON must contain exactly one File:ImageDataHash value.",
                    expectedGps,
                    exifToolExecutablePath: exifToolPath,
                    warnings: readOnlyWarnings,
                    errors: readOnlyErrors,
                    standardOutput: standardOutput,
                    standardError: standardError);
            }

            var rawHash = ReadRawValue(hashValues[0]);
            if (!TryNormalizeSha256(rawHash, out var actualHash))
            {
                return Failure(
                    writeResult,
                    JpegGpsMetadataWriteVerificationFailureKind.InvalidImageDataHash,
                    "ExifTool returned an invalid SHA-256 ImageDataHash.",
                    expectedGps,
                    exifToolExecutablePath: exifToolPath,
                    warnings: readOnlyWarnings,
                    errors: readOnlyErrors,
                    standardOutput: standardOutput,
                    standardError: standardError);
            }

            var gpsResult = ParseGps(values, expectedGps.Altitude is not null);
            if (gpsResult.FailureKind is { } gpsFailureKind)
            {
                return Failure(
                    writeResult,
                    gpsFailureKind,
                    gpsResult.Message!,
                    expectedGps,
                    actualImageDataHash: actualHash,
                    exifToolExecutablePath: exifToolPath,
                    warnings: readOnlyWarnings,
                    errors: readOnlyErrors,
                    standardOutput: standardOutput,
                    standardError: standardError);
            }

            var actualGps = gpsResult.Values!;
            if (!GpsMatches(expectedGps, actualGps))
            {
                return Failure(
                    writeResult,
                    JpegGpsMetadataWriteVerificationFailureKind.GpsMismatch,
                    "The written EXIF GPS values do not match the approved assignments.",
                    expectedGps,
                    actualGps,
                    actualHash,
                    exifToolPath,
                    warnings: readOnlyWarnings,
                    errors: readOnlyErrors,
                    standardOutput: standardOutput,
                    standardError: standardError);
            }

            if (!StringComparer.Ordinal.Equals(baselineHash, actualHash))
            {
                return Failure(
                    writeResult,
                    JpegGpsMetadataWriteVerificationFailureKind.MediaHashMismatch,
                    "The post-write ImageDataHash does not match the saved baseline.",
                    expectedGps,
                    actualGps,
                    actualHash,
                    exifToolPath,
                    warnings: readOnlyWarnings,
                    errors: readOnlyErrors,
                    standardOutput: standardOutput,
                    standardError: standardError);
            }

            return new JpegGpsMetadataWriteVerifiedResult(
                writeResult,
                writeResult.TemporaryCopyPath,
                writeResult.IntendedFinalDestinationPath,
                baselineHash,
                actualHash,
                expectedGps,
                actualGps,
                exifToolPath,
                readOnlyWarnings,
                readOnlyErrors,
                standardOutput,
                standardError);
        }
        catch (JsonException exception)
        {
            return Failure(
                writeResult,
                JpegGpsMetadataWriteVerificationFailureKind.MalformedOutput,
                $"ExifTool returned malformed JSON: {exception.Message}",
                expectedGps,
                exifToolExecutablePath: exifToolPath,
                standardOutput: standardOutput,
                standardError: standardError);
        }
    }

    private static GpsParseOutcome ParseGps(
        IReadOnlyDictionary<string, JsonElement> values,
        bool expectsAltitude)
    {
        var requiredProperties = new List<string>
        {
            "GPS:GPSLatitude",
            "GPS:GPSLatitudeRef",
            "GPS:GPSLongitude",
            "GPS:GPSLongitudeRef"
        };
        if (expectsAltitude)
        {
            requiredProperties.Add("GPS:GPSAltitude");
            requiredProperties.Add("GPS:GPSAltitudeRef");
        }

        if (requiredProperties.Any(property => !values.ContainsKey(property)))
        {
            return new GpsParseOutcome(
                null,
                JpegGpsMetadataWriteVerificationFailureKind.MissingGpsValues,
                "ExifTool did not return the complete approved EXIF GPS tag set.");
        }

        var hasAltitude = values.ContainsKey("GPS:GPSAltitude");
        var hasAltitudeReference = values.ContainsKey("GPS:GPSAltitudeRef");
        if (hasAltitude != hasAltitudeReference)
        {
            return new GpsParseOutcome(
                null,
                JpegGpsMetadataWriteVerificationFailureKind.InvalidGpsValues,
                "ExifTool returned an incomplete EXIF GPS altitude pair.");
        }

        var embeddedValues = values.Select(pair => new EmbeddedMetadataValue(
                GpsFields[pair.Key],
                "GPS",
                pair.Key[4..],
                ReadRawValue(pair.Value),
                pair.Value.ValueKind))
            .ToArray();
        var parsed = EmbeddedGpsParser.Parse(embeddedValues);
        var locations = EmbeddedLocationBuilder.Build(parsed.Candidates);
        if (parsed.Issues.Count > 0
            || locations.Issues.Count > 0
            || locations.Candidates.Count != 1)
        {
            return new GpsParseOutcome(
                null,
                JpegGpsMetadataWriteVerificationFailureKind.InvalidGpsValues,
                "ExifTool returned malformed or conflicting EXIF GPS values.");
        }

        var location = locations.Candidates[0];
        return new GpsParseOutcome(
            new JpegGpsMetadataVerificationValues(
                location.Latitude.ParsedValue,
                location.Latitude.ReferenceValue!.RawValue,
                location.Longitude.ParsedValue,
                location.Longitude.ReferenceValue!.RawValue,
                location.Altitude?.ParsedValue,
                location.Altitude?.ReferenceValue!.RawValue),
            null,
            null);
    }

    private static bool TryBuildExpectedGps(
        JpegGpsMetadataWriteSuccessResult writeResult,
        out JpegGpsMetadataVerificationValues? values)
    {
        values = null;
        var assignments = writeResult.WritePlan.Assignments;
        if (!TryGetAssignment(assignments, JpegMetadataAssignmentTag.GpsLatitude,
                out var latitudeText)
            || !TryGetAssignment(assignments,
                JpegMetadataAssignmentTag.GpsLatitudeReference,
                out var latitudeReference)
            || !TryGetAssignment(assignments, JpegMetadataAssignmentTag.GpsLongitude,
                out var longitudeText)
            || !TryGetAssignment(assignments,
                JpegMetadataAssignmentTag.GpsLongitudeReference,
                out var longitudeReference)
            || !TryParseFinite(latitudeText, out var latitude)
            || !TryParseFinite(longitudeText, out var longitude)
            || latitude is < 0 or > 90
            || longitude is < 0 or > 180
            || latitudeReference is not ("N" or "S")
            || longitudeReference is not ("E" or "W"))
        {
            return false;
        }

        var hasAltitude = TryGetAssignment(
            assignments, JpegMetadataAssignmentTag.GpsAltitude, out var altitudeText);
        var hasAltitudeReference = TryGetAssignment(
            assignments,
            JpegMetadataAssignmentTag.GpsAltitudeReference,
            out var altitudeReference);
        if (hasAltitude != hasAltitudeReference
            || hasAltitude && (!TryParseFinite(altitudeText, out _)
                || altitudeReference is not ("0" or "1")))
        {
            return false;
        }

        double? altitude = null;
        if (hasAltitude)
        {
            TryParseFinite(altitudeText, out var altitudeMagnitude);
            altitude = altitudeReference == "1" ? -altitudeMagnitude : altitudeMagnitude;
        }

        values = new JpegGpsMetadataVerificationValues(
            latitudeReference == "S" ? -latitude : latitude,
            latitudeReference,
            longitudeReference == "W" ? -longitude : longitude,
            longitudeReference,
            altitude,
            hasAltitude ? altitudeReference : null);
        return true;
    }

    private static bool TryGetAssignment(
        IReadOnlyList<JpegMetadataAssignment> assignments,
        JpegMetadataAssignmentTag tag,
        out string value)
    {
        value = string.Empty;
        var matches = assignments.Where(assignment => assignment.Tag == tag).ToList();
        if (matches.Count != 1)
        {
            return false;
        }

        value = matches[0].Value;
        return true;
    }

    private static bool GpsMatches(
        JpegGpsMetadataVerificationValues expected,
        JpegGpsMetadataVerificationValues actual) =>
        StringComparer.Ordinal.Equals(
            expected.LatitudeReference, actual.LatitudeReference)
        && StringComparer.Ordinal.Equals(
            expected.LongitudeReference, actual.LongitudeReference)
        && StringComparer.Ordinal.Equals(
            expected.AltitudeReference, actual.AltitudeReference)
        && Math.Abs(expected.Latitude - actual.Latitude)
            <= LocationDecisionMaker.CoordinateToleranceDegrees
        && Math.Abs(expected.Longitude - actual.Longitude)
            <= LocationDecisionMaker.CoordinateToleranceDegrees
        && (expected.Altitude is null && actual.Altitude is null
            || expected.Altitude is not null
            && actual.Altitude is not null
            && Math.Abs(expected.Altitude.Value - actual.Altitude.Value)
                <= LocationDecisionMaker.AltitudeToleranceMeters);

    private static JpegGpsMetadataWriteVerificationFailureResult? ValidatePaths(
        JpegGpsMetadataWriteSuccessResult writeResult,
        string baselineHash,
        JpegGpsMetadataVerificationValues? expectedGps)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(writeResult.TemporaryCopyPath);
        }
        catch (Exception exception) when (exception is FileNotFoundException
                                          or DirectoryNotFoundException)
        {
            return Failure(
                writeResult,
                JpegGpsMetadataWriteVerificationFailureKind.MissingTemporaryFile,
                "The staged temporary JPEG is missing.",
                expectedGps,
                baselineHash: baselineHash);
        }
        catch (Exception exception) when (IsFileSystemException(exception))
        {
            return Failure(
                writeResult,
                JpegGpsMetadataWriteVerificationFailureKind.NonRegularTemporaryFile,
                $"The staged temporary JPEG could not be inspected: {exception.Message}",
                expectedGps,
                baselineHash: baselineHash);
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            return Failure(
                writeResult,
                JpegGpsMetadataWriteVerificationFailureKind.LinkedTemporaryFile,
                "The staged temporary JPEG is a symbolic link or reparse point.",
                expectedGps,
                baselineHash: baselineHash);
        }

        if ((attributes & (FileAttributes.Directory | FileAttributes.Device)) != 0)
        {
            return Failure(
                writeResult,
                JpegGpsMetadataWriteVerificationFailureKind.NonRegularTemporaryFile,
                "The staged temporary JPEG is not a regular file.",
                expectedGps,
                baselineHash: baselineHash);
        }

        try
        {
            _ = File.GetAttributes(writeResult.IntendedFinalDestinationPath);
            return Failure(
                writeResult,
                JpegGpsMetadataWriteVerificationFailureKind.ExistingFinalDestination,
                "The intended final destination already exists.",
                expectedGps,
                baselineHash: baselineHash);
        }
        catch (Exception exception) when (exception is FileNotFoundException
                                          or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception exception) when (IsFileSystemException(exception))
        {
            return Failure(
                writeResult,
                JpegGpsMetadataWriteVerificationFailureKind.ExistingFinalDestination,
                $"The intended final destination could not be confirmed absent: {exception.Message}",
                expectedGps,
                baselineHash: baselineHash);
        }
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

    private static bool TryNormalizeAbsolutePath(string? path, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            return false;
        }

        try
        {
            normalized = Path.GetFullPath(path);
            return StringComparer.Ordinal.Equals(path, normalized);
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

    private static bool TryParseFinite(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
        && double.IsFinite(value);

    private static string ReadRawValue(JsonElement value) =>
        value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : value.GetRawText();

    private static bool IsDiagnostic(string propertyName, string diagnosticName) =>
        StringComparer.Ordinal.Equals(propertyName, diagnosticName)
        || propertyName.EndsWith(':' + diagnosticName, StringComparison.Ordinal);

    private static void AddDiagnostic(JsonElement value, ICollection<string> diagnostics) =>
        diagnostics.Add(ReadRawValue(value));

    private static bool IsFileSystemException(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or System.Security.SecurityException;

    private static JpegGpsMetadataWriteVerificationFailureResult Failure(
        JpegGpsMetadataWriteSuccessResult writeResult,
        JpegGpsMetadataWriteVerificationFailureKind kind,
        string message,
        JpegGpsMetadataVerificationValues? expectedGps = null,
        JpegGpsMetadataVerificationValues? actualGps = null,
        string? actualImageDataHash = null,
        string? exifToolExecutablePath = null,
        int? exitCode = null,
        string? baselineHash = null,
        IReadOnlyList<string>? warnings = null,
        IReadOnlyList<string>? errors = null,
        string standardOutput = "",
        string standardError = "") =>
        new(
            writeResult,
            writeResult.TemporaryCopyPath,
            writeResult.IntendedFinalDestinationPath,
            baselineHash ?? writeResult.WritePlan.BaselineHashResult.ImageDataHashSha256,
            kind,
            message,
            expectedGps,
            actualGps,
            actualImageDataHash,
            exifToolExecutablePath,
            exitCode,
            Warnings: warnings,
            Errors: errors,
            StandardOutput: standardOutput,
            StandardError: standardError);

    private sealed record GpsParseOutcome(
        JpegGpsMetadataVerificationValues? Values,
        JpegGpsMetadataWriteVerificationFailureKind? FailureKind,
        string? Message);
}
