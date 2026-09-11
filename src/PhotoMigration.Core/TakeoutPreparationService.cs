namespace PhotoMigration.Core;

public static class TakeoutPreparationService
{
    public static TakeoutPreparationResult Prepare(
        string sourceRootPath,
        string outputRootPath,
        string exifToolExecutablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exifToolExecutablePath);
        var absoluteExifToolPath = Path.GetFullPath(exifToolExecutablePath);
        var metadataPlanningResult = TakeoutMetadataPlanner.Plan(
            sourceRootPath,
            absoluteExifToolPath);
        var orderedItems = metadataPlanningResult.Items
            .OrderBy(item => item.MediaEntry.RelativePath, StringComparer.Ordinal)
            .ToList();
        var captureTimePlans = orderedItems
            .Select(AmazonCaptureTimeWritePlanBuilder.Build)
            .ToList();
        var destinationPlanningResult = DestinationPathPlanner.Plan(
            sourceRootPath,
            outputRootPath,
            orderedItems.Select(item => item.MediaEntry));

        if (destinationPlanningResult is DestinationPathPlanningFailureResult failure)
        {
            var message = failure.Issues.Count == 0
                ? "Destination planning failed."
                : $"Destination planning failed: {failure.Issues[0].Message}";
            return new TakeoutPreparationResult(
                metadataPlanningResult,
                destinationPlanningResult,
                orderedItems.Select(item => Failed(
                        item,
                        null,
                        TakeoutPreparationFailureStage.DestinationPlanning,
                        message,
                        failure))
                    .ToList()
                    .AsReadOnly());
        }

        var destinationPlan = (DestinationPathPlanningSuccessResult)
            destinationPlanningResult;
        var destinationsByPath = destinationPlan.Items.ToDictionary(
            item => item.MediaEntry.RelativePath,
            StringComparer.Ordinal);
        var outcomes = new List<TakeoutPreparationItemOutcome>(orderedItems.Count);
        for (var index = 0; index < orderedItems.Count; index++)
        {
            var item = orderedItems[index];
            var destination = destinationsByPath[item.MediaEntry.RelativePath];
            outcomes.Add(ProcessItem(
                sourceRootPath,
                outputRootPath,
                absoluteExifToolPath,
                item,
                destination,
                captureTimePlans[index]));
        }

        return new TakeoutPreparationResult(
            metadataPlanningResult,
            destinationPlanningResult,
            outcomes.AsReadOnly());
    }

    private static TakeoutPreparationItemOutcome ProcessItem(
        string sourceRootPath,
        string outputRootPath,
        string exifToolExecutablePath,
        TakeoutMetadataPlanningItem item,
        DestinationPathPlanningItem destination,
        AmazonCaptureTimeWritePlanResult captureTimePlan)
    {
        if (item.SidecarState is InvalidTakeoutSidecarState)
        {
            return Attention(
                item,
                destination,
                TakeoutPreparationAttentionReason.InvalidSidecar,
                "The matched sidecar is invalid.",
                item.SidecarState);
        }

        if (item.SidecarState is AmbiguousTakeoutSidecarState)
        {
            return Attention(
                item,
                destination,
                TakeoutPreparationAttentionReason.AmbiguousSidecar,
                "More than one sidecar candidate matches this media file.",
                item.SidecarState);
        }

        if (captureTimePlan is AmazonCaptureTimeUnsupportedFormatResult unsupported)
        {
            return Attention(
                item,
                destination,
                TakeoutPreparationAttentionReason.WriteFormatNotSupported,
                "Capture-time writing is not implemented for this format.",
                unsupported);
        }

        if (captureTimePlan is AmazonCaptureTimeAttentionRequiredResult attention)
        {
            return Attention(
                item,
                destination,
                attention.Reason == AmazonCaptureTimeWritePlanAttentionReason
                    .EmbeddedMetadataUnavailable
                    ? TakeoutPreparationAttentionReason.EmbeddedMetadataUnavailable
                    : TakeoutPreparationAttentionReason.CaptureTimeAttentionRequired,
                "The capture-time plan requires attention.",
                attention);
        }

        if (captureTimePlan is AmazonCaptureTimeWriteReadyResult captureReady)
        {
            var metadataAttention = CaptureWriteMetadataAttention(
                item,
                destination,
                captureReady);
            return metadataAttention ?? PublishCaptureTime(
                sourceRootPath,
                outputRootPath,
                exifToolExecutablePath,
                item,
                destination,
                captureReady);
        }

        return item.MetadataPlan.Status switch
        {
            MediaMetadataPlanStatus.NoMetadataChangesProposed =>
                PublishUnchanged(
                    sourceRootPath,
                    outputRootPath,
                    item,
                    destination),
            MediaMetadataPlanStatus.SafeMetadataChangesProposed =>
                PublishJpegGps(
                    sourceRootPath,
                    outputRootPath,
                    exifToolExecutablePath,
                    item,
                    destination),
            MediaMetadataPlanStatus.ReviewRequired => Attention(
                item,
                destination,
                TakeoutPreparationAttentionReason.MetadataReviewRequired,
                "The metadata plan requires review.",
                item.MetadataPlan),
            MediaMetadataPlanStatus.EmbeddedMetadataUnavailable => Attention(
                item,
                destination,
                TakeoutPreparationAttentionReason.EmbeddedMetadataUnavailable,
                "Embedded metadata could not be read safely.",
                item.MetadataPlan),
            MediaMetadataPlanStatus.EmbeddedMetadataFormatNotSupportedYet => Attention(
                item,
                destination,
                TakeoutPreparationAttentionReason.EmbeddedFormatNotSupported,
                "Embedded metadata reading is not implemented for this format.",
                item.MetadataPlan),
            _ => throw new ArgumentOutOfRangeException(nameof(item))
        };
    }

    private static TakeoutPreparationItemOutcome? CaptureWriteMetadataAttention(
        TakeoutMetadataPlanningItem item,
        DestinationPathPlanningItem destination,
        AmazonCaptureTimeWriteReadyResult captureReady)
    {
        if (item.MetadataPlan is UnsupportedEmbeddedMetadataFormatPlan
            && captureReady.MediaFormat == AmazonCaptureTimeMediaFormat.Png)
        {
            return SidecarLocationRequiresAttention(
                    item.MetadataPlan.SidecarMetadata)
                ? Attention(
                    item,
                    destination,
                    TakeoutPreparationAttentionReason.WriteFormatNotSupported,
                    "The PNG has sidecar location data, but its location writer " +
                    "is not implemented.",
                    item.MetadataPlan)
                : null;
        }

        if (item.MetadataPlan is not SuccessfulMediaMetadataPlan metadataPlan)
        {
            return Attention(
                item,
                destination,
                TakeoutPreparationAttentionReason.EmbeddedMetadataUnavailable,
                "Embedded metadata could not be read safely before writing capture time.",
                item.MetadataPlan);
        }

        if (metadataPlan.LocationDecision is ProposeSidecarLocationDecision
            && captureReady.MediaFormat != AmazonCaptureTimeMediaFormat.Jpeg)
        {
            return Attention(
                item,
                destination,
                TakeoutPreparationAttentionReason.WriteFormatNotSupported,
                "Safe location metadata was proposed, but this format's location " +
                "writer is not implemented.",
                metadataPlan.LocationDecision);
        }

        var unresolvedReasons = metadataPlan.ReviewReasons
            .Where(reason => !IsResolvedByAuthoritativeCaptureTime(reason))
            .ToArray();
        return unresolvedReasons.Length == 0
            ? null
            : Attention(
                item,
                destination,
                TakeoutPreparationAttentionReason.MetadataReviewRequired,
                "Metadata review remains after applying the authoritative UTC " +
                "capture-time policy.",
                metadataPlan);
    }

    private static bool SidecarLocationRequiresAttention(
        TakeoutSidecarMetadata sidecar)
    {
        var hasLatitude = sidecar.Latitude is not null;
        var hasLongitude = sidecar.Longitude is not null;
        if (hasLatitude && hasLongitude)
        {
            return sidecar.Latitude!.Value != 0
                   || sidecar.Longitude!.Value != 0;
        }

        return hasLatitude || hasLongitude;
    }

    private static bool IsResolvedByAuthoritativeCaptureTime(
        MediaMetadataPlanReviewReason reason) => reason is
        MediaMetadataPlanReviewReason.LowConfidenceCreationTimeFallback
        or MediaMetadataPlanReviewReason.CaptureTimeReviewRequired
        or MediaMetadataPlanReviewReason.CaptureTimeConflict
        or MediaMetadataPlanReviewReason.EmbeddedCaptureTimeParsingIssues;

    private static TakeoutPreparationItemOutcome PublishUnchanged(
        string sourceRootPath,
        string outputRootPath,
        TakeoutMetadataPlanningItem item,
        DestinationPathPlanningItem destination)
    {
        var stagingResult = VerifiedMediaFileStager.Stage(
            sourceRootPath,
            outputRootPath,
            destination);
        if (stagingResult is not VerifiedMediaFileStagingSuccessResult stagingSuccess)
        {
            return Failed(
                item,
                destination,
                TakeoutPreparationFailureStage.Staging,
                "The media file could not be staged.",
                stagingResult);
        }

        var publicationResult = VerifiedUnchangedMediaPublisher.Publish(
            outputRootPath,
            stagingSuccess);
        return publicationResult is VerifiedUnchangedMediaPublicationSuccessResult success
            ? Published(
                item,
                destination,
                TakeoutPreparationOutcomeKind.PublishedUnchanged,
                "The unchanged media file was published.",
                success)
            : Failed(
                item,
                destination,
                TakeoutPreparationFailureStage.Publication,
                "The unchanged media file could not be published.",
                publicationResult);
    }

    private static TakeoutPreparationItemOutcome PublishJpegGps(
        string sourceRootPath,
        string outputRootPath,
        string exifToolExecutablePath,
        TakeoutMetadataPlanningItem item,
        DestinationPathPlanningItem destination)
    {
        var stagingResult = VerifiedMediaFileStager.Stage(
            sourceRootPath,
            outputRootPath,
            destination);
        if (stagingResult is not VerifiedMediaFileStagingSuccessResult stagingSuccess)
        {
            return Failed(
                item,
                destination,
                TakeoutPreparationFailureStage.Staging,
                "The media file could not be staged for a metadata change.",
                stagingResult);
        }

        var hashResult = ExifToolImageDataHashReader.Read(
            stagingSuccess,
            exifToolExecutablePath);
        if (hashResult is not ImageDataHashReadSuccessResult hashSuccess)
        {
            return Failed(
                item,
                destination,
                TakeoutPreparationFailureStage.ImageDataHashReading,
                "The baseline media-data hash could not be read.",
                hashResult);
        }

        var writePlan = JpegMetadataWritePlanBuilder.Build(item, hashSuccess);
        if (writePlan is JpegMetadataWriteUnsupportedFormatResult)
        {
            return Attention(
                item,
                destination,
                TakeoutPreparationAttentionReason.WriteFormatNotSupported,
                "Metadata writing is not implemented for this format.",
                writePlan);
        }

        if (writePlan is JpegMetadataWriteReviewRequiredResult)
        {
            return Attention(
                item,
                destination,
                TakeoutPreparationAttentionReason.JpegWriteReviewRequired,
                "The JPEG metadata write plan requires review.",
                writePlan);
        }

        if (writePlan is not JpegMetadataWriteReadyResult writeReady)
        {
            return Failed(
                item,
                destination,
                TakeoutPreparationFailureStage.JpegWritePlanning,
                "An executable JPEG GPS write plan could not be built.",
                writePlan);
        }

        var writeResult = JpegGpsMetadataWriter.Execute(
            outputRootPath,
            writeReady,
            exifToolExecutablePath);
        if (writeResult is not JpegGpsMetadataWriteSuccessResult writeSuccess)
        {
            return Failed(
                item,
                destination,
                TakeoutPreparationFailureStage.JpegWriting,
                "JPEG GPS metadata could not be written.",
                writeResult);
        }

        var verificationResult = JpegGpsMetadataWriteVerifier.Verify(
            writeSuccess,
            exifToolExecutablePath);
        if (verificationResult is not JpegGpsMetadataWriteVerifiedResult verified)
        {
            return Failed(
                item,
                destination,
                TakeoutPreparationFailureStage.JpegVerification,
                "The JPEG GPS metadata write could not be verified.",
                verificationResult);
        }

        var publicationResult = VerifiedJpegPublisher.Publish(
            outputRootPath,
            verified);
        return publicationResult is VerifiedJpegPublicationSuccessResult success
            ? Published(
                item,
                destination,
                TakeoutPreparationOutcomeKind.PublishedWithGps,
                "The verified JPEG GPS file was published.",
                success)
            : Failed(
                item,
                destination,
                TakeoutPreparationFailureStage.Publication,
                "The verified JPEG GPS file could not be published.",
                publicationResult);
    }

    private static TakeoutPreparationItemOutcome PublishCaptureTime(
        string sourceRootPath,
        string outputRootPath,
        string exifToolExecutablePath,
        TakeoutMetadataPlanningItem item,
        DestinationPathPlanningItem destination,
        AmazonCaptureTimeWriteReadyResult captureTimePlan)
    {
        var stagingResult = VerifiedMediaFileStager.Stage(
            sourceRootPath,
            outputRootPath,
            destination);
        if (stagingResult is not VerifiedMediaFileStagingSuccessResult stagingSuccess)
        {
            return Failed(
                item,
                destination,
                TakeoutPreparationFailureStage.Staging,
                "The media file could not be staged for a capture-time change.",
                stagingResult);
        }

        var hashResult = ExifToolImageDataHashReader.Read(
            stagingSuccess,
            exifToolExecutablePath);
        if (hashResult is not ImageDataHashReadSuccessResult hashSuccess)
        {
            return Failed(
                item,
                destination,
                TakeoutPreparationFailureStage.ImageDataHashReading,
                "The baseline media-data hash could not be read.",
                hashResult);
        }

        JpegMetadataWriteReadyResult? gpsWritePlan = null;
        if (captureTimePlan.MediaFormat == AmazonCaptureTimeMediaFormat.Jpeg)
        {
            var gpsPlan = JpegMetadataWritePlanBuilder.Build(item, hashSuccess);
            switch (gpsPlan)
            {
                case JpegMetadataWriteReadyResult ready:
                    gpsWritePlan = ready;
                    break;
                case JpegMetadataWriteNoChangesResult:
                    break;
                case JpegMetadataWriteReviewRequiredResult review
                    when review.ReviewReasons.All(IsResolvedGpsReviewReason):
                    if (review.ProposedAssignments.Count > 0)
                    {
                        gpsWritePlan = new JpegMetadataWriteReadyResult(
                            item,
                            stagingSuccess,
                            hashSuccess,
                            review.ProposedAssignments);
                    }

                    break;
                case JpegMetadataWriteReviewRequiredResult review:
                    return Attention(
                        item,
                        destination,
                        TakeoutPreparationAttentionReason.JpegWriteReviewRequired,
                        "The JPEG metadata write plan still requires review.",
                        review);
                case JpegMetadataWriteUnsupportedFormatResult unsupported:
                    return Attention(
                        item,
                        destination,
                        TakeoutPreparationAttentionReason.WriteFormatNotSupported,
                        "JPEG location metadata writing is not implemented for this format.",
                        unsupported);
                default:
                    return Failed(
                        item,
                        destination,
                        TakeoutPreparationFailureStage.JpegWritePlanning,
                        "A safe JPEG metadata write plan could not be built.",
                        gpsPlan);
            }
        }

        var writeResult = gpsWritePlan is null
            ? AmazonCaptureTimeMetadataWriter.Execute(
                captureTimePlan,
                stagingSuccess,
                hashSuccess,
                outputRootPath,
                exifToolExecutablePath)
            : AmazonCaptureTimeMetadataWriter.ExecuteCombined(
                captureTimePlan,
                gpsWritePlan,
                stagingSuccess,
                hashSuccess,
                outputRootPath,
                exifToolExecutablePath);
        if (writeResult is not AmazonCaptureTimeMetadataWriteSuccessResult writeSuccess)
        {
            return Failed(
                item,
                destination,
                TakeoutPreparationFailureStage.CaptureTimeWriting,
                "Capture-time metadata could not be written.",
                writeResult);
        }

        var captureVerification = AmazonCaptureTimeMetadataWriteVerifier.Verify(
            writeSuccess,
            exifToolExecutablePath);
        if (captureVerification
            is not AmazonCaptureTimeMetadataWriteVerifiedResult captureVerified)
        {
            return Failed(
                item,
                destination,
                TakeoutPreparationFailureStage.CaptureTimeVerification,
                "The capture-time metadata write could not be verified.",
                captureVerification);
        }

        JpegGpsMetadataWriteVerifiedResult? gpsVerified = null;
        if (gpsWritePlan is not null)
        {
            var gpsWriteResult = new JpegGpsMetadataWriteSuccessResult(
                gpsWritePlan,
                stagingSuccess.TemporaryCopyPath,
                stagingSuccess.IntendedFinalDestinationPath,
                exifToolExecutablePath,
                JpegGpsMetadataWriteStatus.PendingVerification,
                writeSuccess.StandardOutput,
                writeSuccess.StandardError);
            var gpsVerification = JpegGpsMetadataWriteVerifier.Verify(
                gpsWriteResult,
                exifToolExecutablePath);
            if (gpsVerification is not JpegGpsMetadataWriteVerifiedResult verified)
            {
                return Failed(
                    item,
                    destination,
                    TakeoutPreparationFailureStage.JpegVerification,
                    "The combined JPEG GPS metadata write could not be verified.",
                    gpsVerification);
            }

            gpsVerified = verified;
        }

        var publicationResult = VerifiedAmazonCaptureTimePublisher.Publish(
            outputRootPath,
            captureVerified);
        if (publicationResult
            is not VerifiedAmazonCaptureTimePublicationSuccessResult publicationSuccess)
        {
            return Failed(
                item,
                destination,
                TakeoutPreparationFailureStage.Publication,
                "The verified capture-time file could not be published.",
                publicationResult);
        }

        return gpsVerified is null
            ? Published(
                item,
                destination,
                TakeoutPreparationOutcomeKind.PublishedWithCaptureTime,
                "The verified UTC capture-time file was published.",
                publicationSuccess)
            : Published(
                item,
                destination,
                TakeoutPreparationOutcomeKind.PublishedWithGpsAndCaptureTime,
                "The verified JPEG GPS and UTC capture-time file was published.",
                new VerifiedJpegGpsAndCaptureTimePublicationResult(
                    publicationSuccess,
                    gpsVerified));
    }

    private static bool IsResolvedGpsReviewReason(
        JpegMetadataWritePlanReviewReason reason) => reason switch
        {
            SidecarPhotoTakenTimeTimezoneReviewReason => true,
            ExistingMediaMetadataPlanReviewReason existing =>
                IsResolvedByAuthoritativeCaptureTime(existing.Reason),
            _ => false
        };

    private static TakeoutPreparationItemOutcome Published(
        TakeoutMetadataPlanningItem item,
        DestinationPathPlanningItem destination,
        TakeoutPreparationOutcomeKind kind,
        string message,
        object result) =>
        new(item, destination, kind, null, null, message, result);

    private static TakeoutPreparationItemOutcome Attention(
        TakeoutMetadataPlanningItem item,
        DestinationPathPlanningItem destination,
        TakeoutPreparationAttentionReason reason,
        string message,
        object result) =>
        new(
            item,
            destination,
            TakeoutPreparationOutcomeKind.AttentionRequired,
            reason,
            null,
            message,
            result);

    private static TakeoutPreparationItemOutcome Failed(
        TakeoutMetadataPlanningItem item,
        DestinationPathPlanningItem? destination,
        TakeoutPreparationFailureStage stage,
        string message,
        object result) =>
        new(
            item,
            destination,
            TakeoutPreparationOutcomeKind.Failed,
            null,
            stage,
            message,
            result);
}
