using System.Globalization;

namespace PhotoMigration.Core;

public static class JpegMetadataWritePlanBuilder
{
    private static readonly HashSet<string> SupportedExtensions = new(
        StringComparer.OrdinalIgnoreCase)
    {
        ".jpg",
        ".jpeg"
    };

    public static JpegMetadataWritePlanResult Build(
        TakeoutMetadataPlanningItem takeoutPlanningItem,
        ImageDataHashReadSuccessResult baselineHashResult)
    {
        ArgumentNullException.ThrowIfNull(takeoutPlanningItem);
        ArgumentNullException.ThrowIfNull(baselineHashResult);

        var stagingResult = baselineHashResult.StagingResult;
        if (takeoutPlanningItem.MetadataPlan
            is not SuccessfulMediaMetadataPlan metadataPlan)
        {
            return Invalid(
                takeoutPlanningItem,
                stagingResult,
                baselineHashResult,
                JpegMetadataWritePlanInputIssueKind.InvalidMetadataPlan,
                "JPEG write planning requires a successful metadata plan.");
        }

        var issues = ValidatePipelineJoin(
            takeoutPlanningItem,
            metadataPlan,
            baselineHashResult);
        if (issues.Count > 0)
        {
            return new JpegMetadataWriteInvalidInputResult(
                takeoutPlanningItem,
                stagingResult,
                baselineHashResult,
                issues);
        }

        var extension = Path.GetExtension(takeoutPlanningItem.MediaEntry.RelativePath);
        if (!SupportedExtensions.Contains(extension))
        {
            return new JpegMetadataWriteUnsupportedFormatResult(
                takeoutPlanningItem,
                stagingResult,
                baselineHashResult,
                extension);
        }

        var assignmentIssues = new List<JpegMetadataWritePlanInputIssue>();
        var assignments = CreateAssignments(
            takeoutPlanningItem,
            metadataPlan,
            assignmentIssues);
        if (assignmentIssues.Count > 0)
        {
            return new JpegMetadataWriteInvalidInputResult(
                takeoutPlanningItem,
                stagingResult,
                baselineHashResult,
                assignmentIssues.AsReadOnly());
        }

        var reviewReasons = CreateReviewReasons(metadataPlan);
        if (reviewReasons.Count > 0)
        {
            return new JpegMetadataWriteReviewRequiredResult(
                takeoutPlanningItem,
                stagingResult,
                baselineHashResult,
                assignments,
                reviewReasons);
        }

        return assignments.Count == 0
            ? new JpegMetadataWriteNoChangesResult(
                takeoutPlanningItem,
                stagingResult,
                baselineHashResult)
            : new JpegMetadataWriteReadyResult(
                takeoutPlanningItem,
                stagingResult,
                baselineHashResult,
                assignments);
    }

    private static IReadOnlyList<JpegMetadataWritePlanInputIssue> ValidatePipelineJoin(
        TakeoutMetadataPlanningItem takeoutPlanningItem,
        SuccessfulMediaMetadataPlan metadataPlan,
        ImageDataHashReadSuccessResult baselineHashResult)
    {
        var issues = new List<JpegMetadataWritePlanInputIssue>();
        var staging = baselineHashResult.StagingResult;

        if (!ReferenceEquals(
                takeoutPlanningItem.MediaEntry,
                staging.PlanningItem.MediaEntry)
            || !ReferenceEquals(
                takeoutPlanningItem.SidecarState.MediaEntry,
                takeoutPlanningItem.MediaEntry))
        {
            AddIssue(
                issues,
                JpegMetadataWritePlanInputIssueKind.MismatchedMediaEntry,
                "The Takeout item, sidecar state, and staging result must retain " +
                "the same media entry.");
        }

        if (!StringComparer.Ordinal.Equals(
                metadataPlan.SuccessfulRead.MediaPath,
                staging.PlanningItem.AbsoluteSourcePath))
        {
            AddIssue(
                issues,
                JpegMetadataWritePlanInputIssueKind.MismatchedSourcePath,
                "The embedded metadata source path does not match the staging source.");
        }

        if (!StringComparer.Ordinal.Equals(
                baselineHashResult.TemporaryCopyPath,
                staging.TemporaryCopyPath))
        {
            AddIssue(
                issues,
                JpegMetadataWritePlanInputIssueKind.MismatchedTemporaryPath,
                "The baseline and staging temporary paths do not match.");
        }

        if (!StringComparer.Ordinal.Equals(
                staging.IntendedFinalDestinationPath,
                staging.PlanningItem.AbsoluteDestinationPath))
        {
            AddIssue(
                issues,
                JpegMetadataWritePlanInputIssueKind.MismatchedFinalDestination,
                "The staging and planned final destinations do not match.");
        }

        ValidateSidecarProvenance(takeoutPlanningItem, metadataPlan, issues);
        return issues.AsReadOnly();
    }

    private static void ValidateSidecarProvenance(
        TakeoutMetadataPlanningItem takeoutPlanningItem,
        SuccessfulMediaMetadataPlan metadataPlan,
        ICollection<JpegMetadataWritePlanInputIssue> issues)
    {
        if (takeoutPlanningItem.SidecarState is MatchedTakeoutSidecarState matched)
        {
            var parsed = matched.AnalysisResult;
            if (!ReferenceEquals(parsed.MediaEntry, takeoutPlanningItem.MediaEntry)
                || !ReferenceEquals(parsed.Metadata, metadataPlan.SidecarMetadata)
                || parsed.SidecarEntry is null
                || !Enum.IsDefined(parsed.MatchRule))
            {
                AddIssue(
                    issues,
                    JpegMetadataWritePlanInputIssueKind.InvalidSidecarProvenance,
                    "The matched state must retain the media entry, parsed metadata, " +
                    "sidecar entry, and match rule used by the plan.");
            }

            return;
        }

        if (HasSidecarProposal(metadataPlan))
        {
            AddIssue(
                issues,
                JpegMetadataWritePlanInputIssueKind.InvalidSidecarProvenance,
                "Sidecar-derived proposals require one matched parsed sidecar.");
        }
    }

    private static bool HasSidecarProposal(SuccessfulMediaMetadataPlan metadataPlan) =>
        metadataPlan.CaptureTimeDecision is ProposeSidecarPhotoTakenTimeDecision
            or ProposeLowConfidenceSidecarCreationTimeDecision
        || metadataPlan.LocationDecision is ProposeSidecarLocationDecision;

    private static IReadOnlyList<JpegMetadataAssignment> CreateAssignments(
        TakeoutMetadataPlanningItem takeoutPlanningItem,
        SuccessfulMediaMetadataPlan metadataPlan,
        ICollection<JpegMetadataWritePlanInputIssue> issues)
    {
        if (metadataPlan.LocationDecision
            is not ProposeSidecarLocationDecision location)
        {
            return Array.Empty<JpegMetadataAssignment>();
        }

        if (takeoutPlanningItem.SidecarState
            is not MatchedTakeoutSidecarState matchedSidecar)
        {
            AddIssue(
                issues,
                JpegMetadataWritePlanInputIssueKind.InvalidSidecarProvenance,
                "Sidecar GPS assignments require one matched parsed sidecar.");
            return Array.Empty<JpegMetadataAssignment>();
        }

        var proposal = location.Proposal;
        if (!IsCoordinate(proposal.Latitude, -90, 90)
            || !IsCoordinate(proposal.Longitude, -180, 180)
            || proposal.Altitude is { } altitude && !double.IsFinite(altitude))
        {
            AddIssue(
                issues,
                JpegMetadataWritePlanInputIssueKind.InvalidLocationProposal,
                "The sidecar location proposal contains an invalid coordinate.");
            return Array.Empty<JpegMetadataAssignment>();
        }

        var source = new JpegMetadataAssignmentSource(
            proposal.Source,
            proposal.SourceMetadata,
            matchedSidecar.AnalysisResult.SidecarEntry,
            matchedSidecar.AnalysisResult.MatchRule);
        var assignments = new List<JpegMetadataAssignment>
        {
            Assignment(
                JpegMetadataAssignmentTag.GpsLatitude,
                FormatMagnitude(proposal.Latitude),
                source),
            Assignment(
                JpegMetadataAssignmentTag.GpsLatitudeReference,
                proposal.Latitude < 0 ? "S" : "N",
                source),
            Assignment(
                JpegMetadataAssignmentTag.GpsLongitude,
                FormatMagnitude(proposal.Longitude),
                source),
            Assignment(
                JpegMetadataAssignmentTag.GpsLongitudeReference,
                proposal.Longitude < 0 ? "W" : "E",
                source)
        };

        if (proposal.Altitude is { } proposedAltitude)
        {
            assignments.Add(Assignment(
                JpegMetadataAssignmentTag.GpsAltitude,
                FormatMagnitude(proposedAltitude),
                source));
            assignments.Add(Assignment(
                JpegMetadataAssignmentTag.GpsAltitudeReference,
                proposedAltitude < 0 ? "1" : "0",
                source));
        }

        return assignments.AsReadOnly();
    }

    private static IReadOnlyList<JpegMetadataWritePlanReviewReason> CreateReviewReasons(
        SuccessfulMediaMetadataPlan metadataPlan)
    {
        var reasons = metadataPlan.ReviewReasons
            .Distinct()
            .OrderBy(reason => reason)
            .Select(reason =>
                (JpegMetadataWritePlanReviewReason)
                new ExistingMediaMetadataPlanReviewReason(reason))
            .ToList();

        if (metadataPlan.CaptureTimeDecision
            is ProposeSidecarPhotoTakenTimeDecision photoTakenTime)
        {
            reasons.Add(new SidecarPhotoTakenTimeTimezoneReviewReason(
                photoTakenTime.Proposal));
        }

        if (metadataPlan.Status == MediaMetadataPlanStatus.ReviewRequired
            && reasons.Count == 0)
        {
            reasons.Add(new MetadataPlanReviewRequiredReason());
        }

        return reasons.AsReadOnly();
    }

    private static JpegMetadataAssignment Assignment(
        JpegMetadataAssignmentTag tag,
        string value,
        JpegMetadataAssignmentSource source) =>
        new(JpegMetadataAssignmentGroup.Exif, tag, value, source);

    private static bool IsCoordinate(double value, double minimum, double maximum) =>
        double.IsFinite(value) && value >= minimum && value <= maximum;

    private static string FormatMagnitude(double value) =>
        Math.Abs(value).ToString("R", CultureInfo.InvariantCulture);

    private static JpegMetadataWriteInvalidInputResult Invalid(
        TakeoutMetadataPlanningItem takeoutPlanningItem,
        VerifiedMediaFileStagingSuccessResult stagingResult,
        ImageDataHashReadSuccessResult baselineHashResult,
        JpegMetadataWritePlanInputIssueKind kind,
        string message) =>
        new(
            takeoutPlanningItem,
            stagingResult,
            baselineHashResult,
            [new JpegMetadataWritePlanInputIssue(kind, message)]);

    private static void AddIssue(
        ICollection<JpegMetadataWritePlanInputIssue> issues,
        JpegMetadataWritePlanInputIssueKind kind,
        string message)
    {
        if (!issues.Any(issue => issue.Kind == kind))
        {
            issues.Add(new JpegMetadataWritePlanInputIssue(kind, message));
        }
    }
}
