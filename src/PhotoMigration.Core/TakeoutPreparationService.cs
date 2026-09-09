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
        foreach (var item in orderedItems)
        {
            var destination = destinationsByPath[item.MediaEntry.RelativePath];
            outcomes.Add(ProcessItem(
                sourceRootPath,
                outputRootPath,
                absoluteExifToolPath,
                item,
                destination));
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
        DestinationPathPlanningItem destination)
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
                TakeoutPreparationOutcomeKind.PublishedJpegGps,
                "The verified JPEG GPS file was published.",
                success)
            : Failed(
                item,
                destination,
                TakeoutPreparationFailureStage.Publication,
                "The verified JPEG GPS file could not be published.",
                publicationResult);
    }

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
