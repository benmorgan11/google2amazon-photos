namespace PhotoMigration.Core;

public static class LocationDecisionMaker
{
    public const double CoordinateToleranceDegrees = 0.000001;
    public const double AltitudeToleranceMeters = 0.1;

    public static LocationDecision Decide(
        EmbeddedGpsParseResult embeddedGpsResult,
        TakeoutSidecarMetadata sidecarMetadata)
    {
        ArgumentNullException.ThrowIfNull(embeddedGpsResult);
        ArgumentNullException.ThrowIfNull(sidecarMetadata);

        var normalizedGpsResult = Normalize(embeddedGpsResult);
        var embeddedLocationResult = EmbeddedLocationBuilder.Build(
            normalizedGpsResult.Candidates);
        var sidecarLocation = AssessSidecar(sidecarMetadata);
        var comparisons = embeddedLocationResult.Candidates
            .Select(candidate => Compare(candidate, sidecarLocation))
            .ToList()
            .AsReadOnly();

        if (embeddedLocationResult.Candidates.Count > 0)
        {
            var reasons = new List<LocationReviewReason>();
            if (!AreEquivalent(embeddedLocationResult.Candidates))
            {
                reasons.Add(LocationReviewReason.ConflictingEmbeddedCandidates);
            }

            if (sidecarLocation.Status == SidecarLocationStatus.Incomplete)
            {
                reasons.Add(LocationReviewReason.IncompleteSidecarLocation);
            }

            if (reasons.Count > 0)
            {
                return new ReviewRequiredLocationDecision(
                    normalizedGpsResult,
                    embeddedLocationResult,
                    sidecarMetadata,
                    sidecarLocation,
                    comparisons,
                    reasons.AsReadOnly());
            }

            return new KeepEmbeddedLocationDecision(
                normalizedGpsResult,
                embeddedLocationResult,
                sidecarMetadata,
                sidecarLocation,
                comparisons);
        }

        var reviewReasons = ReviewReasonsFor(
            normalizedGpsResult,
            embeddedLocationResult,
            sidecarLocation);
        if (reviewReasons.Count > 0)
        {
            return new ReviewRequiredLocationDecision(
                normalizedGpsResult,
                embeddedLocationResult,
                sidecarMetadata,
                sidecarLocation,
                comparisons,
                reviewReasons);
        }

        if (sidecarLocation.Status == SidecarLocationStatus.Complete)
        {
            return new ProposeSidecarLocationDecision(
                normalizedGpsResult,
                embeddedLocationResult,
                sidecarMetadata,
                sidecarLocation,
                comparisons,
                new SidecarLocationProposal(
                    sidecarLocation.Latitude!.Value,
                    sidecarLocation.Longitude!.Value,
                    sidecarLocation.Altitude,
                    SidecarLocationSource.GeoData,
                    sidecarMetadata));
        }

        return new LocationMissingDecision(
            normalizedGpsResult,
            embeddedLocationResult,
            sidecarMetadata,
            sidecarLocation,
            comparisons);
    }

    private static EmbeddedGpsParseResult Normalize(EmbeddedGpsParseResult result) =>
        new(
            result.Candidates
                .OrderBy(candidate => candidate.SourceValue.GroupName, StringComparer.Ordinal)
                .ThenBy(candidate => candidate.SourceValue.TagName, StringComparer.Ordinal)
                .ThenBy(candidate => candidate.SourceValue.RawValue, StringComparer.Ordinal)
                .ThenBy(candidate => candidate.ReferenceValue?.GroupName, StringComparer.Ordinal)
                .ThenBy(candidate => candidate.ReferenceValue?.TagName, StringComparer.Ordinal)
                .ThenBy(candidate => candidate.ReferenceValue?.RawValue, StringComparer.Ordinal)
                .ToList()
                .AsReadOnly(),
            result.Issues
                .OrderBy(issue => issue.SourceValue.GroupName, StringComparer.Ordinal)
                .ThenBy(issue => issue.SourceValue.TagName, StringComparer.Ordinal)
                .ThenBy(issue => issue.SourceValue.RawValue, StringComparer.Ordinal)
                .ThenBy(issue => issue.Kind)
                .ThenBy(issue => issue.ReferenceValue?.GroupName, StringComparer.Ordinal)
                .ThenBy(issue => issue.ReferenceValue?.TagName, StringComparer.Ordinal)
                .ThenBy(issue => issue.ReferenceValue?.RawValue, StringComparer.Ordinal)
                .ToList()
                .AsReadOnly());

    private static SidecarLocationAssessment AssessSidecar(
        TakeoutSidecarMetadata metadata)
    {
        SidecarLocationStatus status;

        if (metadata.Latitude is not null && metadata.Longitude is not null)
        {
            status = metadata.Latitude.Value == 0 && metadata.Longitude.Value == 0
                ? SidecarLocationStatus.ZeroZeroPlaceholder
                : SidecarLocationStatus.Complete;
        }
        else if (metadata.Latitude is not null || metadata.Longitude is not null)
        {
            status = SidecarLocationStatus.Incomplete;
        }
        else
        {
            status = SidecarLocationStatus.Missing;
        }

        return new SidecarLocationAssessment(
            status,
            metadata.Latitude,
            metadata.Longitude,
            metadata.Altitude,
            SidecarLocationSource.GeoData,
            metadata);
    }

    private static IReadOnlyList<LocationReviewReason> ReviewReasonsFor(
        EmbeddedGpsParseResult gpsResult,
        EmbeddedLocationBuildResult locationResult,
        SidecarLocationAssessment sidecarLocation)
    {
        var reasons = new List<LocationReviewReason>();

        if (gpsResult.Issues.Count > 0)
        {
            reasons.Add(
                LocationReviewReason.EmbeddedGpsParsingIssuesWithoutCompleteLocation);
        }

        if (locationResult.Issues.Count > 0)
        {
            reasons.Add(
                LocationReviewReason.EmbeddedLocationBuildingIssuesWithoutCompleteLocation);
        }

        if (sidecarLocation.Status == SidecarLocationStatus.Incomplete)
        {
            reasons.Add(LocationReviewReason.IncompleteSidecarLocation);
        }

        return reasons.AsReadOnly();
    }

    private static bool AreEquivalent(
        IReadOnlyList<EmbeddedLocationCandidate> candidates)
    {
        for (var leftIndex = 0; leftIndex < candidates.Count; leftIndex++)
        {
            for (var rightIndex = leftIndex + 1;
                 rightIndex < candidates.Count;
                 rightIndex++)
            {
                if (!AreEquivalent(candidates[leftIndex], candidates[rightIndex]))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool AreEquivalent(
        EmbeddedLocationCandidate left,
        EmbeddedLocationCandidate right)
    {
        if (!WithinCoordinateTolerance(
                left.Latitude.ParsedValue,
                right.Latitude.ParsedValue)
            || !WithinCoordinateTolerance(
                left.Longitude.ParsedValue,
                right.Longitude.ParsedValue))
        {
            return false;
        }

        return left.Altitude is null
               || right.Altitude is null
               || WithinAltitudeTolerance(
                   left.Altitude.ParsedValue,
                   right.Altitude.ParsedValue);
    }

    private static EmbeddedSidecarLocationComparison Compare(
        EmbeddedLocationCandidate embedded,
        SidecarLocationAssessment sidecar)
    {
        var kind = sidecar.Status switch
        {
            SidecarLocationStatus.Missing =>
                EmbeddedSidecarLocationComparisonKind.NoUsableSidecarLocation,
            SidecarLocationStatus.ZeroZeroPlaceholder =>
                EmbeddedSidecarLocationComparisonKind.SidecarZeroZeroPlaceholder,
            SidecarLocationStatus.Incomplete =>
                EmbeddedSidecarLocationComparisonKind.IncompleteSidecarLocation,
            SidecarLocationStatus.Complete => CompareComplete(embedded, sidecar),
            _ => throw new ArgumentOutOfRangeException(nameof(sidecar))
        };

        return new EmbeddedSidecarLocationComparison(embedded, sidecar, kind);
    }

    private static EmbeddedSidecarLocationComparisonKind CompareComplete(
        EmbeddedLocationCandidate embedded,
        SidecarLocationAssessment sidecar)
    {
        if (!WithinCoordinateTolerance(
                embedded.Latitude.ParsedValue,
                sidecar.Latitude!.Value)
            || !WithinCoordinateTolerance(
                embedded.Longitude.ParsedValue,
                sidecar.Longitude!.Value))
        {
            return EmbeddedSidecarLocationComparisonKind.CoordinateConflict;
        }

        if (embedded.Altitude is not null
            && sidecar.Altitude is not null
            && !WithinAltitudeTolerance(
                embedded.Altitude.ParsedValue,
                sidecar.Altitude.Value))
        {
            return EmbeddedSidecarLocationComparisonKind.AltitudeConflict;
        }

        return EmbeddedSidecarLocationComparisonKind.Match;
    }

    private static bool WithinCoordinateTolerance(double left, double right) =>
        Math.Abs(left - right) <= CoordinateToleranceDegrees;

    private static bool WithinAltitudeTolerance(double left, double right) =>
        Math.Abs(left - right) <= AltitudeToleranceMeters;
}
