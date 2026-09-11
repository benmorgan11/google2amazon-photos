namespace PhotoMigration.Core;

public static class AmazonCaptureTimeMetadataWriter
{
    public static readonly TimeSpan DefaultWriteTimeout = TimeSpan.FromMinutes(10);

    public static AmazonCaptureTimeMetadataWriteResult Execute(
        AmazonCaptureTimeWriteReadyResult writePlan,
        VerifiedMediaFileStagingSuccessResult stagingResult,
        ImageDataHashReadSuccessResult baselineHashResult,
        string outputRootPath,
        string exifToolExecutablePath,
        TimeSpan? timeout = null)
        => ExecuteCore(
            writePlan,
            stagingResult,
            baselineHashResult,
            outputRootPath,
            exifToolExecutablePath,
            [],
            timeout);

    internal static AmazonCaptureTimeMetadataWriteResult ExecuteCombined(
        AmazonCaptureTimeWriteReadyResult writePlan,
        JpegMetadataWriteReadyResult gpsWritePlan,
        VerifiedMediaFileStagingSuccessResult stagingResult,
        ImageDataHashReadSuccessResult baselineHashResult,
        string outputRootPath,
        string exifToolExecutablePath,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(writePlan);
        ArgumentNullException.ThrowIfNull(gpsWritePlan);
        ArgumentNullException.ThrowIfNull(stagingResult);
        ArgumentNullException.ThrowIfNull(baselineHashResult);

        if (!ReferenceEquals(gpsWritePlan.StagingResult, stagingResult)
            || !ReferenceEquals(gpsWritePlan.BaselineHashResult, baselineHashResult)
            || !ReferenceEquals(
                gpsWritePlan.TakeoutPlanningItem.MediaEntry,
                writePlan.PlanningItem.MediaEntry)
            || JpegGpsMetadataWriter.ValidatePlanJoin(gpsWritePlan) is not null
            || JpegGpsMetadataWriter.ValidateAssignments(gpsWritePlan) is not null)
        {
            return Failure(
                writePlan,
                stagingResult,
                baselineHashResult,
                outputRootPath,
                exifToolExecutablePath,
                AmazonCaptureTimeMetadataWriteFailureKind.InvalidWritePlan,
                "The combined GPS and capture-time plans do not retain one " +
                "valid staged JPEG pipeline.");
        }

        return ExecuteCore(
            writePlan,
            stagingResult,
            baselineHashResult,
            outputRootPath,
            exifToolExecutablePath,
            JpegGpsMetadataWriter.CreateAssignmentArguments(gpsWritePlan),
            timeout);
    }

    private static AmazonCaptureTimeMetadataWriteResult ExecuteCore(
        AmazonCaptureTimeWriteReadyResult writePlan,
        VerifiedMediaFileStagingSuccessResult stagingResult,
        ImageDataHashReadSuccessResult baselineHashResult,
        string outputRootPath,
        string exifToolExecutablePath,
        IReadOnlyList<string> leadingAssignmentArguments,
        TimeSpan? timeout)
    {
        ArgumentNullException.ThrowIfNull(writePlan);
        ArgumentNullException.ThrowIfNull(stagingResult);
        ArgumentNullException.ThrowIfNull(baselineHashResult);

        var writeTimeout = timeout ?? DefaultWriteTimeout;
        if (writeTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                "The Amazon capture-time metadata write timeout must be greater than zero.");
        }

        if (!VerifiedTemporaryExifToolWriter.TryNormalizePath(
                outputRootPath,
                requireAbsolute: false,
                out var outputRoot))
        {
            return Failure(
                writePlan,
                stagingResult,
                baselineHashResult,
                outputRootPath,
                exifToolExecutablePath,
                AmazonCaptureTimeMetadataWriteFailureKind.InvalidPath,
                "The output root path is invalid.");
        }

        if (!VerifiedTemporaryExifToolWriter.TryNormalizePath(
                exifToolExecutablePath,
                requireAbsolute: true,
                out var exifToolPath))
        {
            return Failure(
                writePlan,
                stagingResult,
                baselineHashResult,
                outputRoot,
                exifToolExecutablePath,
                AmazonCaptureTimeMetadataWriteFailureKind.InvalidPath,
                "The ExifTool executable path must be a valid absolute path.");
        }

        var planFailure = ValidatePlanJoin(
            writePlan,
            stagingResult,
            baselineHashResult,
            outputRoot,
            exifToolPath);
        if (planFailure is not null)
        {
            return planFailure;
        }

        var stagingFailure = VerifiedTemporaryExifToolWriter.ValidateStaging(
            outputRoot,
            stagingResult);
        if (stagingFailure is not null)
        {
            return MapFailure(
                writePlan,
                stagingResult,
                baselineHashResult,
                outputRoot,
                exifToolPath,
                stagingFailure);
        }

        planFailure = ValidateTypedPlan(
            writePlan,
            stagingResult,
            baselineHashResult,
            outputRoot,
            exifToolPath);
        if (planFailure is not null)
        {
            return planFailure;
        }

        var outcome = VerifiedTemporaryExifToolWriter.Execute(
            outputRoot,
            stagingResult,
            exifToolPath,
            leadingAssignmentArguments
                .Concat(CreateAssignmentArguments(writePlan))
                .ToArray(),
            writeTimeout,
            "capture-time metadata write");
        return MapOutcome(
            writePlan,
            stagingResult,
            baselineHashResult,
            outputRoot,
            exifToolPath,
            outcome);
    }

    private static AmazonCaptureTimeMetadataWriteFailureResult? ValidatePlanJoin(
        AmazonCaptureTimeWriteReadyResult writePlan,
        VerifiedMediaFileStagingSuccessResult stagingResult,
        ImageDataHashReadSuccessResult baselineHashResult,
        string outputRoot,
        string exifToolPath)
    {
        return !ReferenceEquals(
                   writePlan.PlanningItem.MediaEntry,
                   stagingResult.PlanningItem.MediaEntry)
               || !ReferenceEquals(stagingResult, baselineHashResult.StagingResult)
               || !StringComparer.Ordinal.Equals(
                   baselineHashResult.TemporaryCopyPath,
                   stagingResult.TemporaryCopyPath)
               || !StringComparer.Ordinal.Equals(
                   stagingResult.IntendedFinalDestinationPath,
                   stagingResult.PlanningItem.AbsoluteDestinationPath)
            ? Failure(
                writePlan,
                stagingResult,
                baselineHashResult,
                outputRoot,
                exifToolPath,
                AmazonCaptureTimeMetadataWriteFailureKind.InvalidWritePlan,
                "The write plan, staging result, and baseline hash must retain " +
                "the same media and paths.")
            : null;
    }

    private static AmazonCaptureTimeMetadataWriteFailureResult? ValidateTypedPlan(
        AmazonCaptureTimeWriteReadyResult writePlan,
        VerifiedMediaFileStagingSuccessResult stagingResult,
        ImageDataHashReadSuccessResult baselineHashResult,
        string outputRoot,
        string exifToolPath)
    {
        return !MatchesMediaFormat(
                   writePlan.MediaFormat,
                   writePlan.PlanningItem.MediaEntry.RelativePath)
               || !MatchesMediaFormat(
                   writePlan.MediaFormat,
                   stagingResult.TemporaryCopyPath)
               || !MatchesMediaFormat(
                   writePlan.MediaFormat,
                   stagingResult.IntendedFinalDestinationPath)
               || writePlan.UtcCaptureInstant.Offset != TimeSpan.Zero
               || !HasExpectedAssignments(writePlan)
            ? Failure(
                writePlan,
                stagingResult,
                baselineHashResult,
                outputRoot,
                exifToolPath,
                AmazonCaptureTimeMetadataWriteFailureKind.InvalidWritePlan,
                "The write plan does not contain the expected ordered assignments " +
                "for its UTC media format.")
            : null;
    }

    private static bool HasExpectedAssignments(
        AmazonCaptureTimeWriteReadyResult writePlan)
    {
        if (writePlan.Assignments is null
            || writePlan.Assignments.Any(
                assignment => assignment is null || string.IsNullOrEmpty(assignment.Value)))
        {
            return false;
        }

        (AmazonCaptureTimeAssignmentGroup Group, AmazonCaptureTimeAssignmentTag Tag)[]
            expected = writePlan.MediaFormat switch
            {
                AmazonCaptureTimeMediaFormat.Jpeg
                    or AmazonCaptureTimeMediaFormat.Heic
                    or AmazonCaptureTimeMediaFormat.Heif
                    or AmazonCaptureTimeMediaFormat.Png =>
                [
                    (AmazonCaptureTimeAssignmentGroup.Exif,
                        AmazonCaptureTimeAssignmentTag.DateTimeOriginal),
                    (AmazonCaptureTimeAssignmentGroup.Exif,
                        AmazonCaptureTimeAssignmentTag.OffsetTimeOriginal)
                ],
                AmazonCaptureTimeMediaFormat.Mov =>
                [
                    (AmazonCaptureTimeAssignmentGroup.QuickTime,
                        AmazonCaptureTimeAssignmentTag.CreateDate)
                ],
                AmazonCaptureTimeMediaFormat.Mp4 =>
                [
                    (AmazonCaptureTimeAssignmentGroup.Keys,
                        AmazonCaptureTimeAssignmentTag.CreationDate)
                ],
                _ => []
            };

        return writePlan.Assignments.Count == expected.Length
               && expected.Select((value, index) =>
                       writePlan.Assignments[index].Group == value.Group
                       && writePlan.Assignments[index].Tag == value.Tag)
                   .All(matches => matches);
    }

    private static bool MatchesMediaFormat(
        AmazonCaptureTimeMediaFormat mediaFormat,
        string path)
    {
        var extension = Path.GetExtension(path);
        return mediaFormat switch
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

    private static IReadOnlyList<string> CreateAssignmentArguments(
        AmazonCaptureTimeWriteReadyResult writePlan) =>
        writePlan.Assignments
            .Select(assignment => AssignmentPrefix(assignment) + assignment.Value)
            .ToArray();

    private static string AssignmentPrefix(AmazonCaptureTimeAssignment assignment) =>
        (assignment.Group, assignment.Tag) switch
        {
            (AmazonCaptureTimeAssignmentGroup.Exif,
                AmazonCaptureTimeAssignmentTag.DateTimeOriginal) =>
                "-EXIF:DateTimeOriginal=",
            (AmazonCaptureTimeAssignmentGroup.Exif,
                AmazonCaptureTimeAssignmentTag.OffsetTimeOriginal) =>
                "-EXIF:OffsetTimeOriginal=",
            (AmazonCaptureTimeAssignmentGroup.QuickTime,
                AmazonCaptureTimeAssignmentTag.CreateDate) =>
                "-QuickTime:CreateDate=",
            (AmazonCaptureTimeAssignmentGroup.Keys,
                AmazonCaptureTimeAssignmentTag.CreationDate) =>
                "-Keys:CreationDate=",
            _ => throw new ArgumentOutOfRangeException(nameof(assignment))
        };

    private static AmazonCaptureTimeMetadataWriteResult MapOutcome(
        AmazonCaptureTimeWriteReadyResult writePlan,
        VerifiedMediaFileStagingSuccessResult stagingResult,
        ImageDataHashReadSuccessResult baselineHashResult,
        string outputRoot,
        string exifToolPath,
        VerifiedTemporaryExifToolWriteOutcome outcome)
    {
        if (!outcome.Succeeded)
        {
            return MapFailure(
                writePlan,
                stagingResult,
                baselineHashResult,
                outputRoot,
                exifToolPath,
                outcome);
        }

        return new AmazonCaptureTimeMetadataWriteSuccessResult(
            writePlan,
            stagingResult,
            baselineHashResult,
            outputRoot,
            exifToolPath,
            AmazonCaptureTimeMetadataWriteStatus.PendingVerification,
            outcome.StandardOutput,
            outcome.StandardError);
    }

    private static AmazonCaptureTimeMetadataWriteFailureResult MapFailure(
        AmazonCaptureTimeWriteReadyResult writePlan,
        VerifiedMediaFileStagingSuccessResult stagingResult,
        ImageDataHashReadSuccessResult baselineHashResult,
        string outputRoot,
        string exifToolPath,
        VerifiedTemporaryExifToolWriteOutcome outcome) =>
        new(
            writePlan,
            stagingResult,
            baselineHashResult,
            outputRoot,
            exifToolPath,
            MapFailureKind(outcome.FailureKind!.Value),
            outcome.Message,
            outcome.ExitCode,
            outcome.Timeout,
            outcome.TerminationError,
            outcome.ActualByteCount,
            outcome.ActualWholeFileSha256,
            outcome.StandardOutput,
            outcome.StandardError);

    private static AmazonCaptureTimeMetadataWriteFailureKind MapFailureKind(
        VerifiedTemporaryExifToolWriteFailureKind kind) => kind switch
        {
            VerifiedTemporaryExifToolWriteFailureKind.InvalidPath =>
                AmazonCaptureTimeMetadataWriteFailureKind.InvalidPath,
            VerifiedTemporaryExifToolWriteFailureKind.InvalidOutputRoot =>
                AmazonCaptureTimeMetadataWriteFailureKind.InvalidOutputRoot,
            VerifiedTemporaryExifToolWriteFailureKind.MissingTemporaryFile =>
                AmazonCaptureTimeMetadataWriteFailureKind.MissingTemporaryFile,
            VerifiedTemporaryExifToolWriteFailureKind.LinkedPath =>
                AmazonCaptureTimeMetadataWriteFailureKind.LinkedPath,
            VerifiedTemporaryExifToolWriteFailureKind.NonRegularTemporaryFile =>
                AmazonCaptureTimeMetadataWriteFailureKind.NonRegularTemporaryFile,
            VerifiedTemporaryExifToolWriteFailureKind.TemporaryFileChanged =>
                AmazonCaptureTimeMetadataWriteFailureKind.TemporaryFileChanged,
            VerifiedTemporaryExifToolWriteFailureKind.ExistingFinalDestination =>
                AmazonCaptureTimeMetadataWriteFailureKind.ExistingFinalDestination,
            VerifiedTemporaryExifToolWriteFailureKind.ExifToolFailure =>
                AmazonCaptureTimeMetadataWriteFailureKind.ExifToolFailure,
            VerifiedTemporaryExifToolWriteFailureKind.TimedOut =>
                AmazonCaptureTimeMetadataWriteFailureKind.TimedOut,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

    private static AmazonCaptureTimeMetadataWriteFailureResult Failure(
        AmazonCaptureTimeWriteReadyResult writePlan,
        VerifiedMediaFileStagingSuccessResult stagingResult,
        ImageDataHashReadSuccessResult baselineHashResult,
        string outputRoot,
        string exifToolPath,
        AmazonCaptureTimeMetadataWriteFailureKind kind,
        string message) =>
        new(
            writePlan,
            stagingResult,
            baselineHashResult,
            outputRoot,
            exifToolPath,
            kind,
            message);
}
