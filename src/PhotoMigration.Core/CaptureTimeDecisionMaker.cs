namespace PhotoMigration.Core;

public static class CaptureTimeDecisionMaker
{
    public static CaptureTimeDecision Decide(
        EmbeddedCaptureTimeParseResult embeddedResult,
        TakeoutSidecarMetadata sidecarMetadata)
    {
        ArgumentNullException.ThrowIfNull(embeddedResult);
        ArgumentNullException.ThrowIfNull(sidecarMetadata);

        var candidates = embeddedResult.Candidates
            .OrderBy(candidate => candidate.SourceValue.GroupName, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.SourceValue.TagName, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.SourceValue.RawValue, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.OffsetSource)
            .ThenBy(candidate => candidate.OffsetValue?.GroupName, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.OffsetValue?.TagName, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.OffsetValue?.RawValue, StringComparer.Ordinal)
            .ToList()
            .AsReadOnly();
        var issues = embeddedResult.Issues
            .OrderBy(issue => issue.SourceValue.GroupName, StringComparer.Ordinal)
            .ThenBy(issue => issue.SourceValue.TagName, StringComparer.Ordinal)
            .ThenBy(issue => issue.SourceValue.RawValue, StringComparer.Ordinal)
            .ThenBy(issue => issue.Kind)
            .ToList()
            .AsReadOnly();
        var comparisons = candidates
            .Select(candidate => CompareWithSidecar(candidate, sidecarMetadata.PhotoTakenTime))
            .ToList()
            .AsReadOnly();

        if (candidates.Count > 0)
        {
            var reviewReason = ReviewReasonFor(candidates);
            if (reviewReason is not null)
            {
                return new ReviewRequiredCaptureTimeDecision(
                    candidates,
                    issues,
                    comparisons,
                    sidecarMetadata,
                    reviewReason.Value);
            }

            return new KeepEmbeddedCaptureTimeDecision(
                candidates,
                issues,
                comparisons,
                sidecarMetadata);
        }

        if (issues.Count > 0)
        {
            return new ReviewRequiredCaptureTimeDecision(
                candidates,
                issues,
                comparisons,
                sidecarMetadata,
                CaptureTimeReviewReason.EmbeddedParsingIssuesWithoutValidCandidate);
        }

        if (sidecarMetadata.PhotoTakenTime is not null)
        {
            return new ProposeSidecarPhotoTakenTimeDecision(
                candidates,
                issues,
                comparisons,
                sidecarMetadata,
                Proposal(
                    sidecarMetadata.PhotoTakenTime.Value,
                    SidecarCaptureTimeSource.PhotoTakenTime,
                    sidecarMetadata));
        }

        if (sidecarMetadata.CreationTime is not null)
        {
            return new ProposeLowConfidenceSidecarCreationTimeDecision(
                candidates,
                issues,
                comparisons,
                sidecarMetadata,
                Proposal(
                    sidecarMetadata.CreationTime.Value,
                    SidecarCaptureTimeSource.CreationTime,
                    sidecarMetadata));
        }

        return new CaptureTimeMissingDecision(
            candidates,
            issues,
            comparisons,
            sidecarMetadata);
    }

    private static CaptureTimeReviewReason? ReviewReasonFor(
        IReadOnlyList<EmbeddedCaptureTimeCandidate> candidates)
    {
        if (candidates.Count < 2)
        {
            return null;
        }

        var zonedCount = candidates.Count(candidate => candidate.ParsedDateTimeOffset is not null);
        if (zonedCount > 0 && zonedCount < candidates.Count)
        {
            return CaptureTimeReviewReason.MixedEmbeddedTimezoneKnowledge;
        }

        if (zonedCount == candidates.Count)
        {
            var firstInstant = candidates[0].ParsedDateTimeOffset!.Value;
            return candidates.All(
                candidate => candidate.ParsedDateTimeOffset!.Value == firstInstant)
                ? null
                : CaptureTimeReviewReason.ConflictingEmbeddedCandidates;
        }

        var firstWallClock = candidates[0].ParsedDateTime;
        return candidates.All(candidate => candidate.ParsedDateTime == firstWallClock)
            ? null
            : CaptureTimeReviewReason.ConflictingEmbeddedCandidates;
    }

    private static EmbeddedSidecarCaptureTimeComparison CompareWithSidecar(
        EmbeddedCaptureTimeCandidate candidate,
        DateTimeOffset? sidecarPhotoTakenTime)
    {
        if (sidecarPhotoTakenTime is null)
        {
            return new EmbeddedSidecarCaptureTimeComparison(
                candidate,
                null,
                EmbeddedSidecarCaptureTimeComparisonKind
                    .NotComparedNoSidecarPhotoTakenTime);
        }

        if (candidate.ParsedDateTimeOffset is null)
        {
            return new EmbeddedSidecarCaptureTimeComparison(
                candidate,
                sidecarPhotoTakenTime,
                EmbeddedSidecarCaptureTimeComparisonKind
                    .CannotCompareUnknownEmbeddedTimezone);
        }

        var kind = candidate.ParsedDateTimeOffset.Value.ToUnixTimeSeconds()
                   == sidecarPhotoTakenTime.Value.ToUnixTimeSeconds()
            ? EmbeddedSidecarCaptureTimeComparisonKind.Match
            : EmbeddedSidecarCaptureTimeComparisonKind.Conflict;

        return new EmbeddedSidecarCaptureTimeComparison(
            candidate,
            sidecarPhotoTakenTime,
            kind);
    }

    private static SidecarCaptureTimeProposal Proposal(
        DateTimeOffset instant,
        SidecarCaptureTimeSource source,
        TakeoutSidecarMetadata sourceMetadata) =>
        new(
            instant,
            source,
            SidecarTimestampTimezoneStatus.OriginalLocalTimezoneUnknown,
            sourceMetadata);
}
