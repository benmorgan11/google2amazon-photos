using System.Globalization;

namespace PhotoMigration.Core;

public static class AmazonCaptureTimeWritePlanBuilder
{
    private const string UtcDateTimeFormat = "yyyy:MM:dd HH:mm:ss";

    public static AmazonCaptureTimeWritePlanResult Build(
        TakeoutMetadataPlanningItem planningItem)
    {
        ArgumentNullException.ThrowIfNull(planningItem);

        var extension = Path.GetExtension(planningItem.MediaEntry.RelativePath);
        if (!TryGetMediaFormat(extension, out var mediaFormat))
        {
            return new AmazonCaptureTimeUnsupportedFormatResult(
                planningItem,
                extension);
        }

        if (planningItem.SidecarState is InvalidTakeoutSidecarState)
        {
            return Attention(
                planningItem,
                AmazonCaptureTimeWritePlanAttentionReason.InvalidSidecar);
        }

        if (planningItem.SidecarState is AmbiguousTakeoutSidecarState)
        {
            return Attention(
                planningItem,
                AmazonCaptureTimeWritePlanAttentionReason.AmbiguousSidecar);
        }

        if (planningItem.SidecarState is MatchedTakeoutSidecarState matched
            && matched.AnalysisResult.Metadata.PhotoTakenTime is { } photoTakenTime)
        {
            var utcCaptureInstant = ToWholeSecondUtc(photoTakenTime);
            return new AmazonCaptureTimeWriteReadyResult(
                planningItem,
                utcCaptureInstant,
                mediaFormat,
                CreateAssignments(mediaFormat, utcCaptureInstant),
                matched.AnalysisResult.SidecarEntry,
                matched.AnalysisResult.MatchRule);
        }

        if (TryGetTrustworthyEmbeddedCaptureTimes(
                planningItem.MetadataPlan,
                out var embeddedCaptureTimes))
        {
            return new AmazonCaptureTimeNoChangeRequiredResult(
                planningItem,
                embeddedCaptureTimes);
        }

        return Attention(planningItem, AttentionReasonFor(planningItem.MetadataPlan));
    }

    private static IReadOnlyList<AmazonCaptureTimeAssignment> CreateAssignments(
        AmazonCaptureTimeMediaFormat mediaFormat,
        DateTimeOffset utcCaptureInstant)
    {
        var dateTime = utcCaptureInstant.ToString(
            UtcDateTimeFormat,
            CultureInfo.InvariantCulture);

        return mediaFormat switch
        {
            AmazonCaptureTimeMediaFormat.Jpeg
                or AmazonCaptureTimeMediaFormat.Heic
                or AmazonCaptureTimeMediaFormat.Heif
                or AmazonCaptureTimeMediaFormat.Png =>
            Array.AsReadOnly<AmazonCaptureTimeAssignment>(
            [
                new AmazonCaptureTimeAssignment(
                    AmazonCaptureTimeAssignmentGroup.Exif,
                    AmazonCaptureTimeAssignmentTag.DateTimeOriginal,
                    dateTime),
                new AmazonCaptureTimeAssignment(
                    AmazonCaptureTimeAssignmentGroup.Exif,
                    AmazonCaptureTimeAssignmentTag.OffsetTimeOriginal,
                    "+00:00")
            ]),
            AmazonCaptureTimeMediaFormat.Mov =>
            Array.AsReadOnly<AmazonCaptureTimeAssignment>(
            [
                new AmazonCaptureTimeAssignment(
                    AmazonCaptureTimeAssignmentGroup.QuickTime,
                    AmazonCaptureTimeAssignmentTag.CreateDate,
                    dateTime)
            ]),
            AmazonCaptureTimeMediaFormat.Mp4 =>
            Array.AsReadOnly<AmazonCaptureTimeAssignment>(
            [
                new AmazonCaptureTimeAssignment(
                    AmazonCaptureTimeAssignmentGroup.Keys,
                    AmazonCaptureTimeAssignmentTag.CreationDate,
                    $"{dateTime}+00:00")
            ]),
            _ => throw new ArgumentOutOfRangeException(nameof(mediaFormat))
        };
    }

    private static bool TryGetTrustworthyEmbeddedCaptureTimes(
        MediaMetadataPlan metadataPlan,
        out IReadOnlyList<EmbeddedCaptureTimeCandidate> embeddedCaptureTimes)
    {
        embeddedCaptureTimes = Array.Empty<EmbeddedCaptureTimeCandidate>();

        if (metadataPlan is not SuccessfulMediaMetadataPlan successful
            || successful.CaptureTimeDecision is not KeepEmbeddedCaptureTimeDecision keep
            || keep.EmbeddedCandidates.Count == 0
            || successful.ReviewReasons.Any(IsCaptureTimeReviewReason))
        {
            return false;
        }

        embeddedCaptureTimes = keep.EmbeddedCandidates;
        return true;
    }

    private static bool IsCaptureTimeReviewReason(
        MediaMetadataPlanReviewReason reason) =>
        reason is MediaMetadataPlanReviewReason.LowConfidenceCreationTimeFallback
            or MediaMetadataPlanReviewReason.CaptureTimeReviewRequired
            or MediaMetadataPlanReviewReason.CaptureTimeConflict
            or MediaMetadataPlanReviewReason.EmbeddedCaptureTimeParsingIssues
            or MediaMetadataPlanReviewReason.ExifToolDiagnostics;

    private static AmazonCaptureTimeWritePlanAttentionReason AttentionReasonFor(
        MediaMetadataPlan metadataPlan)
    {
        if (metadataPlan is not SuccessfulMediaMetadataPlan successful)
        {
            return AmazonCaptureTimeWritePlanAttentionReason.EmbeddedMetadataUnavailable;
        }

        return successful.CaptureTimeDecision is CaptureTimeMissingDecision
            or ProposeLowConfidenceSidecarCreationTimeDecision
            or ProposeSidecarPhotoTakenTimeDecision
            ? AmazonCaptureTimeWritePlanAttentionReason.NoUsableCaptureTime
            : AmazonCaptureTimeWritePlanAttentionReason
                .EmbeddedCaptureTimeRequiresReview;
    }

    private static DateTimeOffset ToWholeSecondUtc(DateTimeOffset instant) =>
        DateTimeOffset.FromUnixTimeSeconds(instant.ToUnixTimeSeconds());

    private static bool TryGetMediaFormat(
        string extension,
        out AmazonCaptureTimeMediaFormat mediaFormat)
    {
        switch (extension.ToUpperInvariant())
        {
            case ".JPG":
            case ".JPEG":
                mediaFormat = AmazonCaptureTimeMediaFormat.Jpeg;
                return true;
            case ".HEIC":
                mediaFormat = AmazonCaptureTimeMediaFormat.Heic;
                return true;
            case ".HEIF":
                mediaFormat = AmazonCaptureTimeMediaFormat.Heif;
                return true;
            case ".PNG":
                mediaFormat = AmazonCaptureTimeMediaFormat.Png;
                return true;
            case ".MOV":
                mediaFormat = AmazonCaptureTimeMediaFormat.Mov;
                return true;
            case ".MP4":
                mediaFormat = AmazonCaptureTimeMediaFormat.Mp4;
                return true;
            default:
                mediaFormat = default;
                return false;
        }
    }

    private static AmazonCaptureTimeAttentionRequiredResult Attention(
        TakeoutMetadataPlanningItem planningItem,
        AmazonCaptureTimeWritePlanAttentionReason reason) =>
        new(planningItem, reason);
}
