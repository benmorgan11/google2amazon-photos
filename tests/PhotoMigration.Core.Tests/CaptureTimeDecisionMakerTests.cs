using System.Text.Json;

namespace PhotoMigration.Core.Tests;

public sealed class CaptureTimeDecisionMakerTests
{
    [Fact]
    public void Decide_OneUnzonedEmbeddedDateIsKept()
    {
        var candidate = Unzoned("ExifIFD", "DateTimeOriginal", "2020:01:02 03:04:05");
        var sidecar = Sidecar(
            creationTime: DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));

        var decision = Assert.IsType<KeepEmbeddedCaptureTimeDecision>(
            CaptureTimeDecisionMaker.Decide(ParseResult([candidate]), sidecar));

        Assert.Same(candidate, Assert.Single(decision.EmbeddedCandidates));
        Assert.Empty(decision.EmbeddedIssues);
        var comparison = Assert.Single(decision.SidecarComparisons);
        Assert.Equal(
            EmbeddedSidecarCaptureTimeComparisonKind.NotComparedNoSidecarPhotoTakenTime,
            comparison.Kind);
        Assert.Same(candidate, comparison.EmbeddedCandidate);
    }

    [Fact]
    public void Decide_ValidEmbeddedCandidateIsKeptAlongsideParsingIssues()
    {
        var candidate = Unzoned("ExifIFD", "DateTimeOriginal", "2020:01:02 03:04:05");
        var issue = Issue("invalid alternate value", "XMP-exif");
        var sidecar = Sidecar(
            photoTakenTime: DateTimeOffset.FromUnixTimeSeconds(1_600_000_000));

        var decision = Assert.IsType<KeepEmbeddedCaptureTimeDecision>(
            CaptureTimeDecisionMaker.Decide(
                ParseResult([candidate], [issue]),
                sidecar));

        Assert.Same(candidate, Assert.Single(decision.EmbeddedCandidates));
        Assert.Same(issue, Assert.Single(decision.EmbeddedIssues));
        Assert.Equal(
            EmbeddedSidecarCaptureTimeComparisonKind.CannotCompareUnknownEmbeddedTimezone,
            Assert.Single(decision.SidecarComparisons).Kind);
    }

    [Fact]
    public void Decide_OneZonedEmbeddedDateIsKept()
    {
        var instant = new DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.FromHours(-7));
        var candidate = Zoned("ExifIFD", "DateTimeOriginal", instant);

        var decision = Assert.IsType<KeepEmbeddedCaptureTimeDecision>(
            CaptureTimeDecisionMaker.Decide(ParseResult([candidate]), Sidecar()));

        Assert.Same(candidate, Assert.Single(decision.EmbeddedCandidates));
        Assert.Equal(
            EmbeddedSidecarCaptureTimeComparisonKind.NotComparedNoSidecarPhotoTakenTime,
            Assert.Single(decision.SidecarComparisons).Kind);
    }

    [Fact]
    public void Decide_MatchingEmbeddedAndSidecarCaptureTimesAreReported()
    {
        var instant = new DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var candidate = Zoned("ExifIFD", "DateTimeOriginal", instant);
        var sidecar = Sidecar(photoTakenTime: instant);

        var decision = Assert.IsType<KeepEmbeddedCaptureTimeDecision>(
            CaptureTimeDecisionMaker.Decide(ParseResult([candidate]), sidecar));

        var comparison = Assert.Single(decision.SidecarComparisons);
        Assert.Equal(EmbeddedSidecarCaptureTimeComparisonKind.Match, comparison.Kind);
        Assert.Equal(instant, comparison.SidecarPhotoTakenTime);
        Assert.Same(sidecar, decision.SidecarMetadata);
    }

    [Fact]
    public void Decide_ConflictingSidecarTimeIsReportedButEmbeddedTimeIsKept()
    {
        var embeddedInstant = new DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var candidate = Zoned("ExifIFD", "DateTimeOriginal", embeddedInstant);
        var sidecar = Sidecar(photoTakenTime: embeddedInstant.AddSeconds(1));

        var decision = Assert.IsType<KeepEmbeddedCaptureTimeDecision>(
            CaptureTimeDecisionMaker.Decide(ParseResult([candidate]), sidecar));

        Assert.Equal(
            EmbeddedSidecarCaptureTimeComparisonKind.Conflict,
            Assert.Single(decision.SidecarComparisons).Kind);
        Assert.Same(candidate, Assert.Single(decision.EmbeddedCandidates));
    }

    [Fact]
    public void Decide_UnzonedEmbeddedTimeCannotBeComparedWithSidecarInstant()
    {
        var candidate = Unzoned("ExifIFD", "DateTimeOriginal", "2020:01:02 03:04:05");
        var sidecar = Sidecar(
            photoTakenTime: new DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.Zero));

        var decision = Assert.IsType<KeepEmbeddedCaptureTimeDecision>(
            CaptureTimeDecisionMaker.Decide(ParseResult([candidate]), sidecar));

        Assert.Equal(
            EmbeddedSidecarCaptureTimeComparisonKind.CannotCompareUnknownEmbeddedTimezone,
            Assert.Single(decision.SidecarComparisons).Kind);
        Assert.Null(candidate.ParsedDateTimeOffset);
        Assert.Equal(DateTimeKind.Unspecified, candidate.ParsedDateTime.Kind);
    }

    [Fact]
    public void Decide_ZonedCandidatesForTheSameInstantAreEquivalent()
    {
        var utc = new DateTimeOffset(2020, 1, 2, 10, 0, 0, TimeSpan.Zero);
        var first = Zoned("ExifIFD", "DateTimeOriginal", utc);
        var second = Zoned("XMP-exif", "DateTimeOriginal", utc.ToOffset(TimeSpan.FromHours(2)));

        var decision = Assert.IsType<KeepEmbeddedCaptureTimeDecision>(
            CaptureTimeDecisionMaker.Decide(ParseResult([second, first]), Sidecar()));

        Assert.Equal([first, second], decision.EmbeddedCandidates);
    }

    [Fact]
    public void Decide_IdenticalUnzonedWallClocksAreEquivalent()
    {
        var first = Unzoned("ExifIFD", "DateTimeOriginal", "2020:01:02 03:04:05");
        var second = Unzoned("XMP-exif", "DateTimeOriginal", "2020:01:02 03:04:05");

        var decision = Assert.IsType<KeepEmbeddedCaptureTimeDecision>(
            CaptureTimeDecisionMaker.Decide(ParseResult([second, first]), Sidecar()));

        Assert.Equal([first, second], decision.EmbeddedCandidates);
    }

    [Fact]
    public void Decide_DifferentZonedCandidatesRequireReview()
    {
        var first = Zoned(
            "ExifIFD",
            "DateTimeOriginal",
            new DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.Zero));
        var second = Zoned(
            "XMP-exif",
            "DateTimeOriginal",
            new DateTimeOffset(2020, 1, 2, 3, 4, 6, TimeSpan.Zero));

        var decision = Assert.IsType<ReviewRequiredCaptureTimeDecision>(
            CaptureTimeDecisionMaker.Decide(ParseResult([second, first]), Sidecar()));

        Assert.Equal(CaptureTimeReviewReason.ConflictingEmbeddedCandidates, decision.Reason);
        Assert.Equal([first, second], decision.EmbeddedCandidates);
    }

    [Fact]
    public void Decide_DifferentUnzonedCandidatesRequireReview()
    {
        var first = Unzoned("ExifIFD", "DateTimeOriginal", "2020:01:02 03:04:05");
        var second = Unzoned("XMP-exif", "DateTimeOriginal", "2020:01:02 03:04:06");

        var decision = Assert.IsType<ReviewRequiredCaptureTimeDecision>(
            CaptureTimeDecisionMaker.Decide(ParseResult([second, first]), Sidecar()));

        Assert.Equal(CaptureTimeReviewReason.ConflictingEmbeddedCandidates, decision.Reason);
    }

    [Fact]
    public void Decide_MixedZonedAndUnzonedCandidatesRequireReview()
    {
        var zoned = Zoned(
            "ExifIFD",
            "DateTimeOriginal",
            new DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.Zero));
        var unzoned = Unzoned("XMP-exif", "DateTimeOriginal", "2020:01:02 03:04:05");

        var decision = Assert.IsType<ReviewRequiredCaptureTimeDecision>(
            CaptureTimeDecisionMaker.Decide(ParseResult([unzoned, zoned]), Sidecar()));

        Assert.Equal(CaptureTimeReviewReason.MixedEmbeddedTimezoneKnowledge, decision.Reason);
    }

    [Fact]
    public void Decide_SidecarPhotoTakenTimeIsProposedWhenEmbeddedTimeIsAbsent()
    {
        var instant = DateTimeOffset.FromUnixTimeSeconds(1_600_000_000);
        var sidecar = Sidecar(
            photoTakenTime: instant,
            creationTime: instant.AddDays(1));

        var decision = Assert.IsType<ProposeSidecarPhotoTakenTimeDecision>(
            CaptureTimeDecisionMaker.Decide(ParseResult(), sidecar));

        AssertProposal(
            decision.Proposal,
            instant,
            SidecarCaptureTimeSource.PhotoTakenTime,
            sidecar);
    }

    [Fact]
    public void Decide_SidecarCreationTimeIsALowConfidenceFallback()
    {
        var instant = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var sidecar = Sidecar(creationTime: instant);

        var decision = Assert.IsType<ProposeLowConfidenceSidecarCreationTimeDecision>(
            CaptureTimeDecisionMaker.Decide(ParseResult(), sidecar));

        AssertProposal(
            decision.Proposal,
            instant,
            SidecarCaptureTimeSource.CreationTime,
            sidecar);
    }

    [Fact]
    public void Decide_InvalidEmbeddedMetadataPreventsAutomaticSidecarFallback()
    {
        var issue = Issue("invalid capture time");
        var sidecar = Sidecar(
            photoTakenTime: DateTimeOffset.FromUnixTimeSeconds(1_600_000_000));

        var decision = Assert.IsType<ReviewRequiredCaptureTimeDecision>(
            CaptureTimeDecisionMaker.Decide(ParseResult(issues: [issue]), sidecar));

        Assert.Equal(
            CaptureTimeReviewReason.EmbeddedParsingIssuesWithoutValidCandidate,
            decision.Reason);
        Assert.Same(issue, Assert.Single(decision.EmbeddedIssues));
        Assert.Empty(decision.EmbeddedCandidates);
        Assert.Empty(decision.SidecarComparisons);
    }

    [Fact]
    public void Decide_CompletelyMissingTimeReturnsMissingResult()
    {
        var decision = Assert.IsType<CaptureTimeMissingDecision>(
            CaptureTimeDecisionMaker.Decide(ParseResult(), Sidecar()));

        Assert.Empty(decision.EmbeddedCandidates);
        Assert.Empty(decision.EmbeddedIssues);
        Assert.Empty(decision.SidecarComparisons);
    }

    [Fact]
    public void Decide_FractionalEmbeddedSecondsMatchWholeSecondSidecarTimestamp()
    {
        var sidecarInstant = new DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var embeddedInstant = sidecarInstant.AddMilliseconds(999);
        var candidate = Zoned("ExifIFD", "DateTimeOriginal", embeddedInstant);

        var decision = Assert.IsType<KeepEmbeddedCaptureTimeDecision>(
            CaptureTimeDecisionMaker.Decide(
                ParseResult([candidate]),
                Sidecar(photoTakenTime: sidecarInstant)));

        Assert.Equal(
            EmbeddedSidecarCaptureTimeComparisonKind.Match,
            Assert.Single(decision.SidecarComparisons).Kind);
    }

    [Fact]
    public void Decide_ShuffledInputProducesDeterministicCandidatesIssuesAndComparisons()
    {
        var later = Zoned(
            "XMP-exif",
            "DateTimeOriginal",
            new DateTimeOffset(2020, 1, 2, 3, 4, 6, TimeSpan.Zero));
        var earlier = Zoned(
            "ExifIFD",
            "DateTimeOriginal",
            new DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.Zero));
        var laterIssue = Issue("z issue", "XMP-exif");
        var earlierIssue = Issue("a issue", "ExifIFD");
        var sidecar = Sidecar(
            photoTakenTime: new DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.Zero));

        var first = Assert.IsType<ReviewRequiredCaptureTimeDecision>(
            CaptureTimeDecisionMaker.Decide(
                ParseResult([later, earlier], [laterIssue, earlierIssue]),
                sidecar));
        var second = Assert.IsType<ReviewRequiredCaptureTimeDecision>(
            CaptureTimeDecisionMaker.Decide(
                ParseResult([earlier, later], [earlierIssue, laterIssue]),
                sidecar));

        Assert.Equal(first.Reason, second.Reason);
        Assert.Equal(first.EmbeddedCandidates, second.EmbeddedCandidates);
        Assert.Equal(first.EmbeddedIssues, second.EmbeddedIssues);
        Assert.Equal(first.SidecarComparisons, second.SidecarComparisons);
        Assert.Equal([earlier, later], first.EmbeddedCandidates);
        Assert.Equal([earlierIssue, laterIssue], first.EmbeddedIssues);
        Assert.Collection(
            first.SidecarComparisons,
            comparison => Assert.Equal(
                EmbeddedSidecarCaptureTimeComparisonKind.Match,
                comparison.Kind),
            comparison => Assert.Equal(
                EmbeddedSidecarCaptureTimeComparisonKind.Conflict,
                comparison.Kind));
    }

    private static void AssertProposal(
        SidecarCaptureTimeProposal proposal,
        DateTimeOffset expectedInstant,
        SidecarCaptureTimeSource expectedSource,
        TakeoutSidecarMetadata expectedMetadata)
    {
        Assert.Equal(expectedInstant, proposal.Instant);
        Assert.Equal(expectedSource, proposal.Source);
        Assert.Equal(
            SidecarTimestampTimezoneStatus.OriginalLocalTimezoneUnknown,
            proposal.TimezoneStatus);
        Assert.Same(expectedMetadata, proposal.SourceMetadata);
    }

    private static EmbeddedCaptureTimeCandidate Unzoned(
        string groupName,
        string tagName,
        string rawValue)
    {
        var parsed = DateTime.ParseExact(
            rawValue,
            "yyyy:MM:dd HH:mm:ss",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None);
        var source = new EmbeddedMetadataValue(
            EmbeddedMetadataField.CaptureDateTime,
            groupName,
            tagName,
            rawValue,
            JsonValueKind.String);

        return new EmbeddedCaptureTimeCandidate(
            source,
            parsed,
            null,
            EmbeddedCaptureTimeOffsetSource.Unknown,
            null);
    }

    private static EmbeddedCaptureTimeCandidate Zoned(
        string groupName,
        string tagName,
        DateTimeOffset instant)
    {
        var source = new EmbeddedMetadataValue(
            EmbeddedMetadataField.CaptureDateTime,
            groupName,
            tagName,
            instant.ToString(
                "yyyy:MM:dd HH:mm:ss.FFFFFFFzzz",
                System.Globalization.CultureInfo.InvariantCulture),
            JsonValueKind.String);

        return new EmbeddedCaptureTimeCandidate(
            source,
            DateTime.SpecifyKind(instant.DateTime, DateTimeKind.Unspecified),
            instant,
            EmbeddedCaptureTimeOffsetSource.IncludedInDateValue,
            null);
    }

    private static EmbeddedCaptureTimeParsingIssue Issue(
        string rawValue,
        string groupName = "ExifIFD") =>
        new(
            EmbeddedCaptureTimeParsingIssueKind.InvalidDateValue,
            new EmbeddedMetadataValue(
                EmbeddedMetadataField.CaptureDateTime,
                groupName,
                "DateTimeOriginal",
                rawValue,
                JsonValueKind.String),
            "Synthetic invalid capture time.");

    private static EmbeddedCaptureTimeParseResult ParseResult(
        IReadOnlyList<EmbeddedCaptureTimeCandidate>? candidates = null,
        IReadOnlyList<EmbeddedCaptureTimeParsingIssue>? issues = null) =>
        new(
            candidates ?? Array.Empty<EmbeddedCaptureTimeCandidate>(),
            issues ?? Array.Empty<EmbeddedCaptureTimeParsingIssue>());

    private static TakeoutSidecarMetadata Sidecar(
        DateTimeOffset? photoTakenTime = null,
        DateTimeOffset? creationTime = null) =>
        new(
            Title: null,
            Description: null,
            CreationTime: creationTime,
            PhotoTakenTime: photoTakenTime,
            Latitude: null,
            Longitude: null,
            Altitude: null,
            Url: null);
}
