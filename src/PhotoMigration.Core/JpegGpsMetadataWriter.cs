namespace PhotoMigration.Core;

public static class JpegGpsMetadataWriter
{
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

        if (!VerifiedTemporaryExifToolWriter.TryNormalizePath(
                outputRootPath,
                requireAbsolute: false,
                out var outputRoot))
        {
            return Failure(
                writePlan,
                JpegGpsMetadataWriteFailureKind.InvalidPath,
                "The output root path is invalid.");
        }

        if (!VerifiedTemporaryExifToolWriter.TryNormalizePath(
                exifToolExecutablePath,
                requireAbsolute: true,
                out var exifToolPath))
        {
            return Failure(
                writePlan,
                JpegGpsMetadataWriteFailureKind.InvalidPath,
                "The ExifTool executable path must be a valid absolute path.");
        }

        var planFailure = ValidatePlanJoin(writePlan);
        if (planFailure is not null)
        {
            return planFailure;
        }

        var stagingFailure = VerifiedTemporaryExifToolWriter.ValidateStaging(
            outputRoot,
            writePlan.StagingResult);
        if (stagingFailure is not null)
        {
            return MapFailure(writePlan, stagingFailure, exifToolPath: null);
        }

        planFailure = ValidateAssignments(writePlan);
        if (planFailure is not null)
        {
            return planFailure;
        }

        var outcome = VerifiedTemporaryExifToolWriter.Execute(
            outputRoot,
            writePlan.StagingResult,
            exifToolPath,
            CreateAssignmentArguments(writePlan),
            writeTimeout,
            "GPS metadata write");
        return MapOutcome(writePlan, exifToolPath, outcome);
    }

    internal static JpegGpsMetadataWriteFailureResult? ValidatePlanJoin(
        JpegMetadataWriteReadyResult writePlan)
    {
        var staging = writePlan.StagingResult;
        var baseline = writePlan.BaselineHashResult;
        return !ReferenceEquals(staging, baseline.StagingResult)
               || !StringComparer.Ordinal.Equals(
                   baseline.TemporaryCopyPath,
                   staging.TemporaryCopyPath)
               || !StringComparer.Ordinal.Equals(
                   staging.IntendedFinalDestinationPath,
                   staging.PlanningItem.AbsoluteDestinationPath)
            ? Failure(
                writePlan,
                JpegGpsMetadataWriteFailureKind.InvalidWritePlan,
                "The write plan no longer has consistent staging and baseline joins.")
            : null;
    }

    internal static JpegGpsMetadataWriteFailureResult? ValidateAssignments(
        JpegMetadataWriteReadyResult writePlan)
    {
        var assignments = writePlan.Assignments;
        if (assignments is null
            || assignments.Any(assignment => assignment is null
                || assignment.Group != JpegMetadataAssignmentGroup.Exif
                || string.IsNullOrEmpty(assignment.Value))
            || assignments.GroupBy(assignment => assignment.Tag)
                .Any(group => group.Count() != 1)
            || RequiredCoordinateTags.Any(
                tag => assignments.All(assignment => assignment.Tag != tag)))
        {
            return Failure(
                writePlan,
                JpegGpsMetadataWriteFailureKind.InvalidWritePlan,
                "The write plan must contain one complete EXIF GPS coordinate " +
                "assignment set.");
        }

        var hasAltitude = assignments.Any(
            assignment => assignment.Tag == JpegMetadataAssignmentTag.GpsAltitude);
        var hasAltitudeReference = assignments.Any(
            assignment => assignment.Tag
                == JpegMetadataAssignmentTag.GpsAltitudeReference);
        return hasAltitude != hasAltitudeReference
               || assignments.Count
               != RequiredCoordinateTags.Length + (hasAltitude ? 2 : 0)
            ? Failure(
                writePlan,
                JpegGpsMetadataWriteFailureKind.InvalidWritePlan,
                "EXIF GPS altitude and altitude reference must either both be " +
                "present or both be absent.")
            : null;
    }

    internal static IReadOnlyList<string> CreateAssignmentArguments(
        JpegMetadataWriteReadyResult writePlan)
    {
        var arguments = new List<string>
        {
            Assignment(
                writePlan,
                JpegMetadataAssignmentTag.GpsLatitude,
                "-EXIF:GPSLatitude="),
            Assignment(
                writePlan,
                JpegMetadataAssignmentTag.GpsLatitudeReference,
                "-EXIF:GPSLatitudeRef="),
            Assignment(
                writePlan,
                JpegMetadataAssignmentTag.GpsLongitude,
                "-EXIF:GPSLongitude="),
            Assignment(
                writePlan,
                JpegMetadataAssignmentTag.GpsLongitudeReference,
                "-EXIF:GPSLongitudeRef=")
        };

        if (writePlan.Assignments.Any(
                assignment => assignment.Tag == JpegMetadataAssignmentTag.GpsAltitude))
        {
            arguments.Add(Assignment(
                writePlan,
                JpegMetadataAssignmentTag.GpsAltitude,
                "-EXIF:GPSAltitude="));
            arguments.Add(Assignment(
                writePlan,
                JpegMetadataAssignmentTag.GpsAltitudeReference,
                "-EXIF:GPSAltitudeRef#="));
        }

        return arguments.AsReadOnly();
    }

    private static string Assignment(
        JpegMetadataWriteReadyResult writePlan,
        JpegMetadataAssignmentTag tag,
        string prefix) =>
        prefix + writePlan.Assignments.Single(assignment => assignment.Tag == tag).Value;

    private static JpegGpsMetadataWriteResult MapOutcome(
        JpegMetadataWriteReadyResult writePlan,
        string exifToolPath,
        VerifiedTemporaryExifToolWriteOutcome outcome)
    {
        if (!outcome.Succeeded)
        {
            return MapFailure(
                writePlan,
                outcome,
                outcome.RetainExifToolPath ? exifToolPath : null);
        }

        return new JpegGpsMetadataWriteSuccessResult(
            writePlan,
            writePlan.StagingResult.TemporaryCopyPath,
            writePlan.StagingResult.IntendedFinalDestinationPath,
            exifToolPath,
            JpegGpsMetadataWriteStatus.PendingVerification,
            outcome.StandardOutput,
            outcome.StandardError);
    }

    private static JpegGpsMetadataWriteFailureResult MapFailure(
        JpegMetadataWriteReadyResult writePlan,
        VerifiedTemporaryExifToolWriteOutcome outcome,
        string? exifToolPath) =>
        new(
            writePlan,
            MapFailureKind(outcome.FailureKind!.Value),
            outcome.Message,
            exifToolPath,
            outcome.ExitCode,
            outcome.Timeout,
            outcome.TerminationError,
            outcome.ActualByteCount,
            outcome.ActualWholeFileSha256,
            outcome.StandardOutput,
            outcome.StandardError);

    private static JpegGpsMetadataWriteFailureKind MapFailureKind(
        VerifiedTemporaryExifToolWriteFailureKind kind) => kind switch
        {
            VerifiedTemporaryExifToolWriteFailureKind.InvalidPath =>
                JpegGpsMetadataWriteFailureKind.InvalidPath,
            VerifiedTemporaryExifToolWriteFailureKind.InvalidOutputRoot =>
                JpegGpsMetadataWriteFailureKind.InvalidOutputRoot,
            VerifiedTemporaryExifToolWriteFailureKind.MissingTemporaryFile =>
                JpegGpsMetadataWriteFailureKind.MissingTemporaryFile,
            VerifiedTemporaryExifToolWriteFailureKind.LinkedPath =>
                JpegGpsMetadataWriteFailureKind.LinkedPath,
            VerifiedTemporaryExifToolWriteFailureKind.NonRegularTemporaryFile =>
                JpegGpsMetadataWriteFailureKind.NonRegularTemporaryFile,
            VerifiedTemporaryExifToolWriteFailureKind.TemporaryFileChanged =>
                JpegGpsMetadataWriteFailureKind.TemporaryFileChanged,
            VerifiedTemporaryExifToolWriteFailureKind.ExistingFinalDestination =>
                JpegGpsMetadataWriteFailureKind.ExistingFinalDestination,
            VerifiedTemporaryExifToolWriteFailureKind.ExifToolFailure =>
                JpegGpsMetadataWriteFailureKind.ExifToolFailure,
            VerifiedTemporaryExifToolWriteFailureKind.TimedOut =>
                JpegGpsMetadataWriteFailureKind.TimedOut,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

    private static JpegGpsMetadataWriteFailureResult Failure(
        JpegMetadataWriteReadyResult writePlan,
        JpegGpsMetadataWriteFailureKind kind,
        string message) =>
        new(writePlan, kind, message);
}
