namespace PhotoMigration.Core;

public static class VerifiedAmazonCaptureTimePublisher
{
    public static VerifiedAmazonCaptureTimePublicationResult Publish(
        string outputRootPath,
        AmazonCaptureTimeMetadataWriteVerifiedResult verifiedResult)
    {
        ArgumentNullException.ThrowIfNull(verifiedResult);

        if (!RetainedPathsMatch(verifiedResult))
        {
            return Failure(
                verifiedResult,
                VerifiedAmazonCaptureTimePublicationFailureKind.MismatchedPaths,
                "The verification, write, staging, and destination paths do not match.");
        }

        var outcome = VerifiedMetadataFilePublisher.Publish(
            outputRootPath,
            verifiedResult.TemporaryCopyPath,
            verifiedResult.IntendedFinalDestinationPath,
            (temporaryPath, finalPath) =>
                File.Move(temporaryPath, finalPath, overwrite: false));
        return outcome.Succeeded
            ? new VerifiedAmazonCaptureTimePublicationSuccessResult(
                verifiedResult,
                outcome.TemporaryPath,
                outcome.FinalPath,
                outcome.PublishedByteCount!.Value)
            : Failure(
                verifiedResult,
                MapFailureKind(outcome.FailureKind!.Value),
                outcome.Message);
    }

    private static bool RetainedPathsMatch(
        AmazonCaptureTimeMetadataWriteVerifiedResult verifiedResult)
    {
        var writeResult = verifiedResult.WriteResult;
        var stagingResult = writeResult.StagingResult;
        var baselineResult = writeResult.BaselineHashResult;
        return ReferenceEquals(baselineResult.StagingResult, stagingResult)
               && StringComparer.Ordinal.Equals(
                   verifiedResult.TemporaryCopyPath,
                   writeResult.TemporaryCopyPath)
               && StringComparer.Ordinal.Equals(
                   verifiedResult.TemporaryCopyPath,
                   stagingResult.TemporaryCopyPath)
               && StringComparer.Ordinal.Equals(
                   verifiedResult.TemporaryCopyPath,
                   baselineResult.TemporaryCopyPath)
               && StringComparer.Ordinal.Equals(
                   verifiedResult.IntendedFinalDestinationPath,
                   writeResult.IntendedFinalDestinationPath)
               && StringComparer.Ordinal.Equals(
                   verifiedResult.IntendedFinalDestinationPath,
                   stagingResult.IntendedFinalDestinationPath)
               && StringComparer.Ordinal.Equals(
                   verifiedResult.IntendedFinalDestinationPath,
                   stagingResult.PlanningItem.AbsoluteDestinationPath);
    }

    private static VerifiedAmazonCaptureTimePublicationFailureKind MapFailureKind(
        VerifiedMetadataFilePublicationFailureKind kind) => kind switch
        {
            VerifiedMetadataFilePublicationFailureKind.InvalidOutputRoot =>
                VerifiedAmazonCaptureTimePublicationFailureKind.InvalidOutputRoot,
            VerifiedMetadataFilePublicationFailureKind.InvalidPath =>
                VerifiedAmazonCaptureTimePublicationFailureKind.InvalidPath,
            VerifiedMetadataFilePublicationFailureKind.MissingTemporaryFile =>
                VerifiedAmazonCaptureTimePublicationFailureKind.MissingTemporaryFile,
            VerifiedMetadataFilePublicationFailureKind.LinkedPath =>
                VerifiedAmazonCaptureTimePublicationFailureKind.LinkedPath,
            VerifiedMetadataFilePublicationFailureKind.NonRegularTemporaryFile =>
                VerifiedAmazonCaptureTimePublicationFailureKind.NonRegularTemporaryFile,
            VerifiedMetadataFilePublicationFailureKind.ExistingDestination =>
                VerifiedAmazonCaptureTimePublicationFailureKind.ExistingDestination,
            VerifiedMetadataFilePublicationFailureKind.MoveFailure =>
                VerifiedAmazonCaptureTimePublicationFailureKind.MoveFailure,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

    private static VerifiedAmazonCaptureTimePublicationFailureResult Failure(
        AmazonCaptureTimeMetadataWriteVerifiedResult verifiedResult,
        VerifiedAmazonCaptureTimePublicationFailureKind kind,
        string message) =>
        new(
            verifiedResult,
            verifiedResult.TemporaryCopyPath,
            verifiedResult.IntendedFinalDestinationPath,
            kind,
            message);
}
