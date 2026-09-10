using System.Text.Json;
using PhotoMigration.Core;

namespace PhotoMigration.Core.Tests;

public sealed class AmazonCaptureTimeWritePlanBuilderTests
{
    [Fact]
    public void Build_FormatsOneInstantForEverySupportedFormatAndRetainsProvenance()
    {
        var sourceInstant = new DateTimeOffset(
            2020, 1, 2, 3, 4, 5, TimeSpan.FromHours(-8));
        var expectedUtc = new DateTimeOffset(
            2020, 1, 2, 11, 4, 5, TimeSpan.Zero);
        FormatCase[] cases =
        [
            ImageCase("photo.JpG", AmazonCaptureTimeMediaFormat.Jpeg),
            ImageCase("photo.jPeG", AmazonCaptureTimeMediaFormat.Jpeg),
            ImageCase("photo.HeIc", AmazonCaptureTimeMediaFormat.Heic),
            ImageCase("photo.hEiF", AmazonCaptureTimeMediaFormat.Heif),
            ImageCase("photo.PnG", AmazonCaptureTimeMediaFormat.Png),
            new(
                "video.MoV",
                AmazonCaptureTimeMediaFormat.Mov,
                [
                    new(
                        AmazonCaptureTimeAssignmentGroup.QuickTime,
                        AmazonCaptureTimeAssignmentTag.CreateDate,
                        "2020:01:02 11:04:05")
                ]),
            new(
                "video.mP4",
                AmazonCaptureTimeMediaFormat.Mp4,
                [
                    new(
                        AmazonCaptureTimeAssignmentGroup.Keys,
                        AmazonCaptureTimeAssignmentTag.CreationDate,
                        "2020:01:02 11:04:05+00:00")
                ])
        ];

        foreach (var value in cases)
        {
            var item = Item(
                value.RelativePath,
                SidecarStateKind.Matched,
                photoTakenTime: sourceInstant,
                matchRule: SidecarMatchRule.ExtensionOmitted);
            var matched = Assert.IsType<MatchedTakeoutSidecarState>(item.SidecarState);

            var result = Assert.IsType<AmazonCaptureTimeWriteReadyResult>(
                AmazonCaptureTimeWritePlanBuilder.Build(item));

            Assert.Equal(AmazonCaptureTimeWritePlanStatus.Ready, result.Status);
            Assert.Same(item, result.PlanningItem);
            Assert.Equal(expectedUtc, result.UtcCaptureInstant);
            Assert.Equal(TimeSpan.Zero, result.UtcCaptureInstant.Offset);
            Assert.Equal(value.MediaFormat, result.MediaFormat);
            Assert.Equal(value.Assignments, result.Assignments);
            Assert.Same(matched.AnalysisResult.SidecarEntry, result.MatchedSidecarEntry);
            Assert.Equal(SidecarMatchRule.ExtensionOmitted, result.SidecarMatchRule);
        }
    }

    [Fact]
    public void Build_MatchedPhotoTakenTimeOverridesConflictingEmbeddedTime()
    {
        var sidecarInstant = new DateTimeOffset(
            2024, 5, 6, 7, 8, 9, TimeSpan.FromHours(2));
        var item = Item(
            "photo.jpg",
            SidecarStateKind.Matched,
            photoTakenTime: sidecarInstant,
            embeddedValues:
            [
                CaptureValue("ExifIFD", "DateTimeOriginal", "2019:01:02 03:04:05Z")
            ]);
        var metadataPlan = Assert.IsType<SuccessfulMediaMetadataPlan>(item.MetadataPlan);
        Assert.Contains(
            MediaMetadataPlanReviewReason.CaptureTimeConflict,
            metadataPlan.ReviewReasons);

        var result = Assert.IsType<AmazonCaptureTimeWriteReadyResult>(
            AmazonCaptureTimeWritePlanBuilder.Build(item));

        Assert.Equal(
            new DateTimeOffset(2024, 5, 6, 5, 8, 9, TimeSpan.Zero),
            result.UtcCaptureInstant);
        Assert.Equal("2024:05:06 05:08:09", result.Assignments[0].Value);
        Assert.Equal("+00:00", result.Assignments[1].Value);
    }

    [Fact]
    public void Build_RetainsEmbeddedTimeWithoutMatchedPhotoTakenTimeAndNeverUsesCreationTime()
    {
        var embedded = CaptureValue(
            "XMP-exif",
            "DateTimeOriginal",
            "2021:02:03 04:05:06-07:00");
        var unmatched = Item(
            "photo.jpg",
            SidecarStateKind.Unmatched,
            embeddedValues: [embedded]);
        var matchedCreationOnly = Item(
            "photo.jpg",
            SidecarStateKind.Matched,
            creationTime: DateTimeOffset.FromUnixTimeSeconds(1_700_000_000),
            embeddedValues: [embedded]);

        foreach (var item in new[] { unmatched, matchedCreationOnly })
        {
            var result = Assert.IsType<AmazonCaptureTimeNoChangeRequiredResult>(
                AmazonCaptureTimeWritePlanBuilder.Build(item));
            Assert.Equal(
                AmazonCaptureTimeWritePlanStatus.NoChangeRequired,
                result.Status);
            Assert.Same(item, result.PlanningItem);
            Assert.Same(
                embedded,
                Assert.Single(result.RetainedEmbeddedCaptureTimes).SourceValue);
        }

        var creationOnly = Item(
            "photo.jpg",
            SidecarStateKind.Matched,
            creationTime: DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));
        var attention = Assert.IsType<AmazonCaptureTimeAttentionRequiredResult>(
            AmazonCaptureTimeWritePlanBuilder.Build(creationOnly));
        Assert.Equal(
            AmazonCaptureTimeWritePlanAttentionReason.NoUsableCaptureTime,
            attention.Reason);
    }

    [Fact]
    public void Build_InvalidAmbiguousUnmatchedAndMissingTimestampCasesNeedAttention()
    {
        var embedded = CaptureValue(
            "ExifIFD",
            "DateTimeOriginal",
            "2020:01:02 03:04:05Z");
        var cases = new[]
        {
            (Item("photo.jpg", SidecarStateKind.Invalid, embeddedValues: [embedded]),
                AmazonCaptureTimeWritePlanAttentionReason.InvalidSidecar),
            (Item("photo.jpg", SidecarStateKind.Ambiguous, embeddedValues: [embedded]),
                AmazonCaptureTimeWritePlanAttentionReason.AmbiguousSidecar),
            (Item("photo.jpg", SidecarStateKind.Unmatched),
                AmazonCaptureTimeWritePlanAttentionReason.NoUsableCaptureTime),
            (Item("photo.jpg", SidecarStateKind.Matched),
                AmazonCaptureTimeWritePlanAttentionReason.NoUsableCaptureTime)
        };

        foreach (var (item, expectedReason) in cases)
        {
            var result = Assert.IsType<AmazonCaptureTimeAttentionRequiredResult>(
                AmazonCaptureTimeWritePlanBuilder.Build(item));
            Assert.Equal(AmazonCaptureTimeWritePlanStatus.AttentionRequired, result.Status);
            Assert.Same(item, result.PlanningItem);
            Assert.Equal(expectedReason, result.Reason);
        }
    }

    [Fact]
    public void Build_UnsupportedExtensionReturnsFormatPlanningResult()
    {
        var item = Item(
            "animation.GiF",
            SidecarStateKind.Matched,
            photoTakenTime: DateTimeOffset.FromUnixTimeSeconds(1_600_000_000));

        var result = Assert.IsType<AmazonCaptureTimeUnsupportedFormatResult>(
            AmazonCaptureTimeWritePlanBuilder.Build(item));

        Assert.Equal(AmazonCaptureTimeWritePlanStatus.UnsupportedFormat, result.Status);
        Assert.Same(item, result.PlanningItem);
        Assert.Equal(".GiF", result.Extension);
    }

    private static FormatCase ImageCase(
        string relativePath,
        AmazonCaptureTimeMediaFormat mediaFormat) =>
        new(
            relativePath,
            mediaFormat,
            [
                new(
                    AmazonCaptureTimeAssignmentGroup.Exif,
                    AmazonCaptureTimeAssignmentTag.DateTimeOriginal,
                    "2020:01:02 11:04:05"),
                new(
                    AmazonCaptureTimeAssignmentGroup.Exif,
                    AmazonCaptureTimeAssignmentTag.OffsetTimeOriginal,
                    "+00:00")
            ]);

    private static TakeoutMetadataPlanningItem Item(
        string relativePath,
        SidecarStateKind sidecarState,
        DateTimeOffset? photoTakenTime = null,
        DateTimeOffset? creationTime = null,
        IEnumerable<EmbeddedMetadataValue>? embeddedValues = null,
        SidecarMatchRule matchRule = SidecarMatchRule.LegacyJson)
    {
        var mediaEntry = new InventoryEntry(relativePath, 4);
        var sidecarEntry = new InventoryEntry($"{relativePath}.json", 128);
        var parsedMetadata = Sidecar(creationTime, photoTakenTime);
        var metadataForPlan = sidecarState == SidecarStateKind.Matched
            ? parsedMetadata
            : Sidecar();
        var read = new EmbeddedMetadataReadSuccessResult(
            $"/synthetic/{relativePath}",
            "/synthetic/exiftool",
            (embeddedValues ?? []).ToList().AsReadOnly(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            string.Empty);
        var metadataPlan = MediaMetadataPlanBuilder.Build(read, metadataForPlan);
        TakeoutPlanningSidecarState state = sidecarState switch
        {
            SidecarStateKind.Matched => new MatchedTakeoutSidecarState(
                new ParsedMediaResult(
                    mediaEntry,
                    sidecarEntry,
                    matchRule,
                    parsedMetadata)),
            SidecarStateKind.Unmatched => new UnmatchedTakeoutSidecarState(
                new UnmatchedMediaResult(mediaEntry)),
            SidecarStateKind.Invalid => new InvalidTakeoutSidecarState(
                new InvalidSidecarResult(
                    mediaEntry,
                    sidecarEntry,
                    matchRule,
                    new TakeoutSidecarParseException(
                        sidecarEntry.RelativePath,
                        "synthetic invalid sidecar"))),
            SidecarStateKind.Ambiguous => new AmbiguousTakeoutSidecarState(
                new AmbiguousMediaSidecarResult(
                    mediaEntry,
                    [
                        new SidecarMatchCandidate(sidecarEntry, matchRule),
                        new SidecarMatchCandidate(
                            new InventoryEntry(
                                $"{relativePath}.supplemental-metadata.json",
                                128),
                            SidecarMatchRule.SupplementalMetadataJson)
                    ])),
            _ => throw new ArgumentOutOfRangeException(nameof(sidecarState))
        };

        return new TakeoutMetadataPlanningItem(mediaEntry, state, metadataPlan);
    }

    private static EmbeddedMetadataValue CaptureValue(
        string group,
        string tag,
        string value) =>
        new(
            EmbeddedMetadataField.CaptureDateTime,
            group,
            tag,
            value,
            JsonValueKind.String);

    private static TakeoutSidecarMetadata Sidecar(
        DateTimeOffset? creationTime = null,
        DateTimeOffset? photoTakenTime = null) =>
        new(
            Title: "synthetic",
            Description: null,
            CreationTime: creationTime,
            PhotoTakenTime: photoTakenTime,
            Latitude: null,
            Longitude: null,
            Altitude: null,
            Url: null);

    private enum SidecarStateKind
    {
        Matched,
        Unmatched,
        Invalid,
        Ambiguous
    }

    private sealed record FormatCase(
        string RelativePath,
        AmazonCaptureTimeMediaFormat MediaFormat,
        IReadOnlyList<AmazonCaptureTimeAssignment> Assignments);
}
