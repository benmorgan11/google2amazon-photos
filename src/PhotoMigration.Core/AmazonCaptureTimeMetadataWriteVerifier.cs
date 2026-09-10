using System.Text.Json;

namespace PhotoMigration.Core;

public static class AmazonCaptureTimeMetadataWriteVerifier
{
    private const string ImageDataHashProperty = "File:ImageDataHash";

    public static readonly TimeSpan DefaultVerificationTimeout = TimeSpan.FromMinutes(10);

    public static AmazonCaptureTimeMetadataWriteVerificationResult Verify(
        AmazonCaptureTimeMetadataWriteSuccessResult writeResult,
        string exifToolExecutablePath,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(writeResult);

        var verificationTimeout = timeout ?? DefaultVerificationTimeout;
        if (verificationTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                "The Amazon capture-time verification timeout must be greater than zero.");
        }

        var rawExpected = writeResult.WritePlan.Assignments?.ToArray()
                          ?? Array.Empty<AmazonCaptureTimeAssignment>();
        var expectedAssignments = Array.AsReadOnly(rawExpected);
        var baselineHash = writeResult.BaselineHashResult.ImageDataHashSha256;

        if (!TryValidateInput(
                writeResult,
                exifToolExecutablePath,
                out var exifToolPath,
                out var outputRoot,
                out var specs,
                out baselineHash))
        {
            return Failure(
                writeResult,
                expectedAssignments,
                baselineHash,
                AmazonCaptureTimeMetadataWriteVerificationFailureKind.InvalidInput,
                "The write result, approved capture-time assignments, paths, " +
                "baseline hash, or ExifTool path is invalid.",
                exifToolExecutablePath: exifToolExecutablePath);
        }

        expectedAssignments = Array.AsReadOnly(
            specs.Select(spec => spec.Assignment).ToArray());
        var pathFailure = VerifiedTemporaryExifToolWriter.ValidatePaths(
            outputRoot,
            writeResult.StagingResult);
        if (pathFailure is not null)
        {
            return MapPathFailure(
                writeResult,
                expectedAssignments,
                baselineHash,
                exifToolPath,
                pathFailure);
        }

        var processOutcome = ExifToolProcessRunner.Run(
            exifToolPath,
            CreateExifToolArguments(writeResult.TemporaryCopyPath, specs),
            verificationTimeout,
            "Amazon capture-time verification");

        pathFailure = VerifiedTemporaryExifToolWriter.ValidatePaths(
            outputRoot,
            writeResult.StagingResult);
        if (pathFailure is not null)
        {
            return MapPathFailure(
                writeResult,
                expectedAssignments,
                baselineHash,
                exifToolPath,
                pathFailure,
                processOutcome);
        }

        if (!processOutcome.Succeeded)
        {
            return MapProcessFailure(
                writeResult,
                expectedAssignments,
                baselineHash,
                exifToolPath,
                processOutcome);
        }

        return ParseAndVerify(
            writeResult,
            expectedAssignments,
            baselineHash,
            exifToolPath,
            specs,
            processOutcome.StandardOutput,
            processOutcome.StandardError);
    }

    private static bool TryValidateInput(
        AmazonCaptureTimeMetadataWriteSuccessResult writeResult,
        string exifToolExecutablePath,
        out string exifToolPath,
        out string outputRoot,
        out IReadOnlyList<VerificationSpec> specs,
        out string baselineHash)
    {
        exifToolPath = string.Empty;
        outputRoot = string.Empty;
        specs = Array.Empty<VerificationSpec>();
        baselineHash = writeResult.BaselineHashResult.ImageDataHashSha256;
        var staging = writeResult.StagingResult;
        var writePlan = writeResult.WritePlan;

        if (writeResult.Status
                != AmazonCaptureTimeMetadataWriteStatus.PendingVerification
            || !ReferenceEquals(
                writePlan.PlanningItem.MediaEntry,
                staging.PlanningItem.MediaEntry)
            || !ReferenceEquals(staging, writeResult.BaselineHashResult.StagingResult)
            || !StringComparer.Ordinal.Equals(
                writeResult.BaselineHashResult.TemporaryCopyPath,
                staging.TemporaryCopyPath)
            || !StringComparer.Ordinal.Equals(
                staging.IntendedFinalDestinationPath,
                staging.PlanningItem.AbsoluteDestinationPath)
            || !VerifiedTemporaryExifToolWriter.TryNormalizePath(
                writeResult.OutputRootPath,
                requireAbsolute: true,
                out outputRoot)
            || !StringComparer.Ordinal.Equals(outputRoot, writeResult.OutputRootPath)
            || !VerifiedTemporaryExifToolWriter.TryNormalizePath(
                writeResult.ExifToolExecutablePath,
                requireAbsolute: true,
                out var writeExifToolPath)
            || !StringComparer.Ordinal.Equals(
                writeExifToolPath,
                writeResult.ExifToolExecutablePath)
            || !VerifiedTemporaryExifToolWriter.TryNormalizePath(
                exifToolExecutablePath,
                requireAbsolute: true,
                out exifToolPath)
            || !TryNormalizeSha256(baselineHash, out baselineHash)
            || VerifiedTemporaryExifToolWriter.ValidateStaging(outputRoot, staging)
                is not null
            || !TryBuildSpecs(writePlan, staging, out specs))
        {
            return false;
        }

        return true;
    }

    private static bool TryBuildSpecs(
        AmazonCaptureTimeWriteReadyResult writePlan,
        VerifiedMediaFileStagingSuccessResult staging,
        out IReadOnlyList<VerificationSpec> specs)
    {
        if (writePlan.Assignments is null)
        {
            specs = Array.Empty<VerificationSpec>();
            return false;
        }

        specs = writePlan.MediaFormat switch
        {
            AmazonCaptureTimeMediaFormat.Jpeg
                or AmazonCaptureTimeMediaFormat.Heic
                or AmazonCaptureTimeMediaFormat.Heif
                or AmazonCaptureTimeMediaFormat.Png =>
            BuildSpecs(
                writePlan,
                [
                    (AmazonCaptureTimeAssignmentGroup.Exif,
                        AmazonCaptureTimeAssignmentTag.DateTimeOriginal,
                        "-EXIF:DateTimeOriginal",
                        "ExifIFD:DateTimeOriginal"),
                    (AmazonCaptureTimeAssignmentGroup.Exif,
                        AmazonCaptureTimeAssignmentTag.OffsetTimeOriginal,
                        "-EXIF:OffsetTimeOriginal",
                        "ExifIFD:OffsetTimeOriginal")
                ]),
            AmazonCaptureTimeMediaFormat.Mov =>
            BuildSpecs(
                writePlan,
                [
                    (AmazonCaptureTimeAssignmentGroup.QuickTime,
                        AmazonCaptureTimeAssignmentTag.CreateDate,
                        "-QuickTime:CreateDate",
                        "QuickTime:CreateDate")
                ]),
            AmazonCaptureTimeMediaFormat.Mp4 =>
            BuildSpecs(
                writePlan,
                [
                    (AmazonCaptureTimeAssignmentGroup.Keys,
                        AmazonCaptureTimeAssignmentTag.CreationDate,
                        "-Keys:CreationDate",
                        "Keys:CreationDate")
                ]),
            _ => Array.Empty<VerificationSpec>()
        };

        return specs.Count > 0
               && MatchesMediaFormat(
                   writePlan.MediaFormat,
                   writePlan.PlanningItem.MediaEntry.RelativePath)
               && MatchesMediaFormat(writePlan.MediaFormat, staging.TemporaryCopyPath)
               && MatchesMediaFormat(
                   writePlan.MediaFormat,
                   staging.IntendedFinalDestinationPath);
    }

    private static IReadOnlyList<VerificationSpec> BuildSpecs(
        AmazonCaptureTimeWriteReadyResult writePlan,
        IReadOnlyList<(AmazonCaptureTimeAssignmentGroup Group,
            AmazonCaptureTimeAssignmentTag Tag,
            string RequestArgument,
            string JsonProperty)> expected)
    {
        if (writePlan.Assignments.Count != expected.Count)
        {
            return Array.Empty<VerificationSpec>();
        }

        var specs = new List<VerificationSpec>(expected.Count);
        for (var index = 0; index < expected.Count; index++)
        {
            var assignment = writePlan.Assignments[index];
            var value = expected[index];
            if (assignment is null
                || assignment.Group != value.Group
                || assignment.Tag != value.Tag
                || string.IsNullOrEmpty(assignment.Value))
            {
                return Array.Empty<VerificationSpec>();
            }

            specs.Add(new VerificationSpec(
                assignment,
                value.RequestArgument,
                value.JsonProperty));
        }

        return specs.AsReadOnly();
    }

    private static bool MatchesMediaFormat(
        AmazonCaptureTimeMediaFormat format,
        string path)
    {
        var extension = Path.GetExtension(path);
        return format switch
        {
            AmazonCaptureTimeMediaFormat.Jpeg =>
                extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase),
            AmazonCaptureTimeMediaFormat.Heic =>
                extension.Equals(".heic", StringComparison.OrdinalIgnoreCase),
            AmazonCaptureTimeMediaFormat.Heif =>
                extension.Equals(".heif", StringComparison.OrdinalIgnoreCase),
            AmazonCaptureTimeMediaFormat.Png =>
                extension.Equals(".png", StringComparison.OrdinalIgnoreCase),
            AmazonCaptureTimeMediaFormat.Mov =>
                extension.Equals(".mov", StringComparison.OrdinalIgnoreCase),
            AmazonCaptureTimeMediaFormat.Mp4 =>
                extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private static IReadOnlyList<string> CreateExifToolArguments(
        string temporaryPath,
        IReadOnlyList<VerificationSpec> specs)
    {
        var arguments = new List<string>
        {
            "-json",
            "-G1",
            "-s",
            "-api",
            "ImageHashType=SHA256"
        };
        arguments.AddRange(specs.Select(spec => spec.RequestArgument));
        arguments.Add("-ImageDataHash");
        arguments.Add(temporaryPath);
        return arguments.AsReadOnly();
    }

    private static AmazonCaptureTimeMetadataWriteVerificationResult ParseAndVerify(
        AmazonCaptureTimeMetadataWriteSuccessResult writeResult,
        IReadOnlyList<AmazonCaptureTimeAssignment> expectedAssignments,
        string baselineHash,
        string exifToolPath,
        IReadOnlyList<VerificationSpec> specs,
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
                    expectedAssignments,
                    baselineHash,
                    AmazonCaptureTimeMetadataWriteVerificationFailureKind.MalformedOutput,
                    "ExifTool JSON must be an array containing exactly one object.",
                    exifToolExecutablePath: exifToolPath,
                    standardOutput: standardOutput,
                    standardError: standardError);
            }

            var expectedProperties = specs.ToDictionary(
                spec => spec.JsonProperty,
                StringComparer.Ordinal);
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            var hashValues = new List<string>();
            var warnings = new List<string>();
            var errors = new List<string>();
            foreach (var property in root[0].EnumerateObject())
            {
                if (expectedProperties.ContainsKey(property.Name))
                {
                    if (property.Value.ValueKind != JsonValueKind.String
                        || !values.TryAdd(
                            property.Name,
                            property.Value.GetString() ?? string.Empty))
                    {
                        return Failure(
                            writeResult,
                            expectedAssignments,
                            baselineHash,
                            AmazonCaptureTimeMetadataWriteVerificationFailureKind
                                .MalformedOutput,
                            $"ExifTool JSON contains an invalid or duplicate " +
                            $"'{property.Name}' value.",
                            exifToolExecutablePath: exifToolPath,
                            standardOutput: standardOutput,
                            standardError: standardError);
                    }
                }
                else if (StringComparer.Ordinal.Equals(
                             property.Name,
                             ImageDataHashProperty))
                {
                    hashValues.Add(ReadRawValue(property.Value));
                }
                else if (IsDiagnostic(property.Name, "Warning"))
                {
                    warnings.Add(ReadRawValue(property.Value));
                }
                else if (IsDiagnostic(property.Name, "Error"))
                {
                    errors.Add(ReadRawValue(property.Value));
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
                    expectedAssignments,
                    baselineHash,
                    AmazonCaptureTimeMetadataWriteVerificationFailureKind
                        .MissingImageDataHash,
                    "ExifTool did not return File:ImageDataHash.",
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
                    expectedAssignments,
                    baselineHash,
                    AmazonCaptureTimeMetadataWriteVerificationFailureKind.MalformedOutput,
                    "ExifTool JSON must contain exactly one File:ImageDataHash value.",
                    exifToolExecutablePath: exifToolPath,
                    warnings: readOnlyWarnings,
                    errors: readOnlyErrors,
                    standardOutput: standardOutput,
                    standardError: standardError);
            }

            if (!TryNormalizeSha256(hashValues[0], out var actualHash))
            {
                return Failure(
                    writeResult,
                    expectedAssignments,
                    baselineHash,
                    AmazonCaptureTimeMetadataWriteVerificationFailureKind
                        .InvalidImageDataHash,
                    "ExifTool returned an invalid SHA-256 ImageDataHash.",
                    exifToolExecutablePath: exifToolPath,
                    warnings: readOnlyWarnings,
                    errors: readOnlyErrors,
                    standardOutput: standardOutput,
                    standardError: standardError);
            }

            var actualAssignments = Array.AsReadOnly(specs
                .Where(spec => values.ContainsKey(spec.JsonProperty))
                .Select(spec => spec.Assignment with
                {
                    Value = values[spec.JsonProperty]
                })
                .ToArray());
            if (actualAssignments.Count != specs.Count)
            {
                return Failure(
                    writeResult,
                    expectedAssignments,
                    baselineHash,
                    AmazonCaptureTimeMetadataWriteVerificationFailureKind
                        .MissingCaptureTimeValues,
                    "ExifTool did not return every approved capture-time field.",
                    actualAssignments,
                    actualHash,
                    exifToolPath,
                    warnings: readOnlyWarnings,
                    errors: readOnlyErrors,
                    standardOutput: standardOutput,
                    standardError: standardError);
            }

            if (!expectedAssignments.SequenceEqual(actualAssignments))
            {
                return Failure(
                    writeResult,
                    expectedAssignments,
                    baselineHash,
                    AmazonCaptureTimeMetadataWriteVerificationFailureKind
                        .CaptureTimeMismatch,
                    "The written capture-time values do not match the approved " +
                    "assignments.",
                    actualAssignments,
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
                    expectedAssignments,
                    baselineHash,
                    AmazonCaptureTimeMetadataWriteVerificationFailureKind
                        .MediaHashMismatch,
                    "The post-write ImageDataHash does not match the saved baseline.",
                    actualAssignments,
                    actualHash,
                    exifToolPath,
                    warnings: readOnlyWarnings,
                    errors: readOnlyErrors,
                    standardOutput: standardOutput,
                    standardError: standardError);
            }

            return new AmazonCaptureTimeMetadataWriteVerifiedResult(
                writeResult,
                writeResult.TemporaryCopyPath,
                writeResult.IntendedFinalDestinationPath,
                expectedAssignments,
                baselineHash,
                actualAssignments,
                actualHash,
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
                expectedAssignments,
                baselineHash,
                AmazonCaptureTimeMetadataWriteVerificationFailureKind.MalformedOutput,
                $"ExifTool returned malformed JSON: {exception.Message}",
                exifToolExecutablePath: exifToolPath,
                standardOutput: standardOutput,
                standardError: standardError);
        }
    }

    private static AmazonCaptureTimeMetadataWriteVerificationFailureResult
        MapProcessFailure(
            AmazonCaptureTimeMetadataWriteSuccessResult writeResult,
            IReadOnlyList<AmazonCaptureTimeAssignment> expectedAssignments,
            string baselineHash,
            string exifToolPath,
            ExifToolProcessOutcome outcome) =>
        new(
            writeResult,
            writeResult.TemporaryCopyPath,
            writeResult.IntendedFinalDestinationPath,
            expectedAssignments,
            baselineHash,
            outcome.FailureKind == ExifToolProcessFailureKind.TimedOut
                ? AmazonCaptureTimeMetadataWriteVerificationFailureKind.TimedOut
                : AmazonCaptureTimeMetadataWriteVerificationFailureKind.ExifToolFailure,
            outcome.Message,
            ExifToolExecutablePath: exifToolPath,
            ExitCode: outcome.ExitCode,
            Timeout: outcome.Timeout,
            TerminationError: outcome.TerminationError,
            StandardOutput: outcome.StandardOutput,
            StandardError: outcome.StandardError);

    private static AmazonCaptureTimeMetadataWriteVerificationFailureResult
        MapPathFailure(
            AmazonCaptureTimeMetadataWriteSuccessResult writeResult,
            IReadOnlyList<AmazonCaptureTimeAssignment> expectedAssignments,
            string baselineHash,
            string exifToolPath,
            VerifiedTemporaryExifToolWriteOutcome outcome,
            ExifToolProcessOutcome? processOutcome = null) =>
        new(
            writeResult,
            writeResult.TemporaryCopyPath,
            writeResult.IntendedFinalDestinationPath,
            expectedAssignments,
            baselineHash,
            outcome.FailureKind switch
            {
                VerifiedTemporaryExifToolWriteFailureKind.MissingTemporaryFile =>
                    AmazonCaptureTimeMetadataWriteVerificationFailureKind
                        .MissingTemporaryFile,
                VerifiedTemporaryExifToolWriteFailureKind.LinkedPath =>
                    AmazonCaptureTimeMetadataWriteVerificationFailureKind
                        .LinkedTemporaryFile,
                VerifiedTemporaryExifToolWriteFailureKind.NonRegularTemporaryFile =>
                    AmazonCaptureTimeMetadataWriteVerificationFailureKind
                        .NonRegularTemporaryFile,
                VerifiedTemporaryExifToolWriteFailureKind.ExistingFinalDestination =>
                    AmazonCaptureTimeMetadataWriteVerificationFailureKind
                        .ExistingFinalDestination,
                _ => AmazonCaptureTimeMetadataWriteVerificationFailureKind.InvalidInput
            },
            outcome.Message,
            ExifToolExecutablePath: exifToolPath,
            ExitCode: processOutcome?.ExitCode,
            Timeout: processOutcome?.Timeout,
            TerminationError: processOutcome?.TerminationError,
            StandardOutput: processOutcome?.StandardOutput ?? string.Empty,
            StandardError: processOutcome?.StandardError ?? string.Empty);

    private static bool TryNormalizeSha256(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (value is null
            || value.Length != 64
            || value.Any(character =>
                character is not (>= '0' and <= '9')
                and not (>= 'A' and <= 'F')
                and not (>= 'a' and <= 'f')))
        {
            return false;
        }

        normalized = value.ToUpperInvariant();
        return true;
    }

    private static string ReadRawValue(JsonElement value) =>
        value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : value.GetRawText();

    private static bool IsDiagnostic(string propertyName, string diagnosticName) =>
        StringComparer.Ordinal.Equals(propertyName, diagnosticName)
        || propertyName.EndsWith(':' + diagnosticName, StringComparison.Ordinal);

    private static AmazonCaptureTimeMetadataWriteVerificationFailureResult Failure(
        AmazonCaptureTimeMetadataWriteSuccessResult writeResult,
        IReadOnlyList<AmazonCaptureTimeAssignment> expectedAssignments,
        string baselineHash,
        AmazonCaptureTimeMetadataWriteVerificationFailureKind kind,
        string message,
        IReadOnlyList<AmazonCaptureTimeAssignment>? actualAssignments = null,
        string? actualImageDataHash = null,
        string? exifToolExecutablePath = null,
        IReadOnlyList<string>? warnings = null,
        IReadOnlyList<string>? errors = null,
        string standardOutput = "",
        string standardError = "") =>
        new(
            writeResult,
            writeResult.TemporaryCopyPath,
            writeResult.IntendedFinalDestinationPath,
            expectedAssignments,
            baselineHash,
            kind,
            message,
            actualAssignments,
            actualImageDataHash,
            exifToolExecutablePath,
            Warnings: warnings,
            Errors: errors,
            StandardOutput: standardOutput,
            StandardError: standardError);

    private sealed record VerificationSpec(
        AmazonCaptureTimeAssignment Assignment,
        string RequestArgument,
        string JsonProperty);
}
