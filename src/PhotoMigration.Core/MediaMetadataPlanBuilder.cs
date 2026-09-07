namespace PhotoMigration.Core;

public static class MediaMetadataPlanBuilder
{
    public static MediaMetadataPlan Build(
        EmbeddedMetadataReadResult embeddedReadResult,
        TakeoutSidecarMetadata sidecarMetadata)
    {
        ArgumentNullException.ThrowIfNull(embeddedReadResult);
        ArgumentNullException.ThrowIfNull(sidecarMetadata);

        return embeddedReadResult switch
        {
            EmbeddedMetadataReadSuccessResult success => BuildSuccess(
                success,
                sidecarMetadata),
            EmbeddedMetadataUnsupportedMediaResult unsupported =>
                new UnsupportedEmbeddedMetadataFormatPlan(
                    unsupported,
                    sidecarMetadata),
            EmbeddedMetadataMissingMediaResult missing =>
                new MissingMediaMetadataPlan(missing, sidecarMetadata),
            EmbeddedMetadataExifToolFailureResult failure =>
                new ExifToolFailureMetadataPlan(failure, sidecarMetadata),
            EmbeddedMetadataReadTimedOutResult timeout =>
                new EmbeddedMetadataTimeoutPlan(timeout, sidecarMetadata),
            EmbeddedMetadataMalformedJsonResult malformed =>
                new MalformedEmbeddedMetadataJsonPlan(malformed, sidecarMetadata),
            _ => throw new ArgumentException(
                "The embedded metadata read result type is not supported.",
                nameof(embeddedReadResult))
        };
    }

    private static SuccessfulMediaMetadataPlan BuildSuccess(
        EmbeddedMetadataReadSuccessResult success,
        TakeoutSidecarMetadata sidecarMetadata)
    {
        var captureTimeParseResult = EmbeddedCaptureTimeParser.Parse(success.Values);
        var gpsParseResult = EmbeddedGpsParser.Parse(success.Values);
        var captureTimeDecision = CaptureTimeDecisionMaker.Decide(
            captureTimeParseResult,
            sidecarMetadata);
        var locationDecision = LocationDecisionMaker.Decide(
            gpsParseResult,
            sidecarMetadata);
        var locationBuildResult = locationDecision.EmbeddedLocationResult;
        var reviewReasons = CollectReviewReasons(
            success,
            captureTimeParseResult,
            gpsParseResult,
            locationBuildResult,
            captureTimeDecision,
            locationDecision);
        var status = DetermineStatus(
            reviewReasons,
            captureTimeDecision,
            locationDecision);

        return new SuccessfulMediaMetadataPlan(
            status,
            success,
            sidecarMetadata,
            captureTimeParseResult,
            gpsParseResult,
            locationBuildResult,
            captureTimeDecision,
            locationDecision,
            reviewReasons);
    }

    private static IReadOnlyList<MediaMetadataPlanReviewReason> CollectReviewReasons(
        EmbeddedMetadataReadSuccessResult success,
        EmbeddedCaptureTimeParseResult captureTimeParseResult,
        EmbeddedGpsParseResult gpsParseResult,
        EmbeddedLocationBuildResult locationBuildResult,
        CaptureTimeDecision captureTimeDecision,
        LocationDecision locationDecision)
    {
        var reasons = new SortedSet<MediaMetadataPlanReviewReason>();

        if (captureTimeDecision is ProposeLowConfidenceSidecarCreationTimeDecision)
        {
            reasons.Add(MediaMetadataPlanReviewReason.LowConfidenceCreationTimeFallback);
        }

        if (captureTimeDecision is ReviewRequiredCaptureTimeDecision)
        {
            reasons.Add(MediaMetadataPlanReviewReason.CaptureTimeReviewRequired);
        }

        if (captureTimeDecision.SidecarComparisons.Any(
                comparison => comparison.Kind
                    == EmbeddedSidecarCaptureTimeComparisonKind.Conflict))
        {
            reasons.Add(MediaMetadataPlanReviewReason.CaptureTimeConflict);
        }

        if (captureTimeParseResult.Issues.Count > 0)
        {
            reasons.Add(MediaMetadataPlanReviewReason.EmbeddedCaptureTimeParsingIssues);
        }

        if (locationDecision is ReviewRequiredLocationDecision)
        {
            reasons.Add(MediaMetadataPlanReviewReason.LocationReviewRequired);
        }

        if (locationDecision.SidecarComparisons.Any(
                comparison => comparison.Kind is
                    EmbeddedSidecarLocationComparisonKind.CoordinateConflict
                    or EmbeddedSidecarLocationComparisonKind.AltitudeConflict))
        {
            reasons.Add(MediaMetadataPlanReviewReason.LocationConflict);
        }

        if (gpsParseResult.Issues.Count > 0)
        {
            reasons.Add(MediaMetadataPlanReviewReason.EmbeddedGpsParsingIssues);
        }

        if (locationBuildResult.Issues.Count > 0)
        {
            reasons.Add(MediaMetadataPlanReviewReason.EmbeddedLocationBuildingIssues);
        }

        if (success.Warnings.Count > 0
            || success.Errors.Count > 0
            || !string.IsNullOrEmpty(success.StandardError))
        {
            reasons.Add(MediaMetadataPlanReviewReason.ExifToolDiagnostics);
        }

        return reasons.ToList().AsReadOnly();
    }

    private static MediaMetadataPlanStatus DetermineStatus(
        IReadOnlyList<MediaMetadataPlanReviewReason> reviewReasons,
        CaptureTimeDecision captureTimeDecision,
        LocationDecision locationDecision)
    {
        if (reviewReasons.Count > 0)
        {
            return MediaMetadataPlanStatus.ReviewRequired;
        }

        if (captureTimeDecision is ProposeSidecarPhotoTakenTimeDecision
            || locationDecision is ProposeSidecarLocationDecision)
        {
            return MediaMetadataPlanStatus.SafeMetadataChangesProposed;
        }

        return MediaMetadataPlanStatus.NoMetadataChangesProposed;
    }
}
