using System.Globalization;
using System.Text.Json;
using PhotoMigration.Core;

namespace PhotoMigration.Core.Tests;

public sealed class JpegMetadataWritePlanBuilderTests
{
    private const string FullFileHash =
        "1111111111111111111111111111111111111111111111111111111111111111";
    private const string MediaDataHash =
        "2222222222222222222222222222222222222222222222222222222222222222";

    [Fact]
    public void Build_SafeGpsIsOrderedAndRetainsMatchedSidecarProvenance()
    {
        GpsCase[] cases =
        [
            new(34.25, 118.5, 25.125, ["34.25", "N", "118.5", "E", "25.125", "0"]),
            new(-34.25, -118.5, -25.125, ["34.25", "S", "118.5", "W", "25.125", "1"]),
            new(12.345678901234567, -98.76543210987654, null,
                [
                    Math.Abs(12.345678901234567).ToString("R", CultureInfo.InvariantCulture),
                    "N",
                    Math.Abs(-98.76543210987654).ToString("R", CultureInfo.InvariantCulture),
                    "W"
                ])
        ];

        foreach (var value in cases)
        {
            const string sidecarPath = "Album/photo.jpg.supplemental-metadata.json";
            var sidecar = Sidecar(value.Latitude, value.Longitude, value.Altitude);
            var context = Context(
                sidecar,
                relativePath: "Album/PHOTO.JPEG",
                sidecarRelativePath: sidecarPath,
                matchRule: SidecarMatchRule.SupplementalMetadataJson);
            var matched = Assert.IsType<MatchedTakeoutSidecarState>(
                context.PlanningItem.SidecarState);

            var result = Assert.IsType<JpegMetadataWriteReadyResult>(
                JpegMetadataWritePlanBuilder.Build(
                    context.PlanningItem,
                    context.BaselineHash));

            Assert.Same(context.PlanningItem, result.TakeoutPlanningItem);
            Assert.Same(context.MetadataPlan, result.MetadataPlan);
            Assert.Same(context.BaselineHash.StagingResult, result.StagingResult);
            Assert.Same(context.BaselineHash, result.BaselineHashResult);
            Assert.Equal(
                Enum.GetValues<JpegMetadataAssignmentTag>().Take(value.Expected.Length),
                result.Assignments.Select(assignment => assignment.Tag));
            Assert.Equal(value.Expected, result.Assignments.Select(a => a.Value));
            Assert.All(result.Assignments, assignment =>
            {
                Assert.Equal(JpegMetadataAssignmentGroup.Exif, assignment.Group);
                Assert.Equal(SidecarLocationSource.GeoData, assignment.Source.Source);
                Assert.Same(sidecar, assignment.Source.SidecarMetadata);
                Assert.Same(matched.AnalysisResult.SidecarEntry, assignment.Source.SidecarEntry);
                Assert.Equal(sidecarPath, assignment.Source.SidecarEntry.RelativePath);
                Assert.Equal(SidecarMatchRule.SupplementalMetadataJson, assignment.Source.MatchRule);
            });
        }
    }

    [Fact]
    public void Build_ExistingEmbeddedMetadataRequiresNoChanges()
    {
        EmbeddedMetadataValue[] values =
        [
            CaptureValue("ExifIFD", "DateTimeOriginal", "2020:01:02 03:04:05Z"),
            GpsValue(EmbeddedMetadataField.GpsLatitude, "GPSLatitude", "34.25"),
            GpsValue(EmbeddedMetadataField.GpsLatitudeReference, "GPSLatitudeRef", "N",
                JsonValueKind.String),
            GpsValue(EmbeddedMetadataField.GpsLongitude, "GPSLongitude", "118.5"),
            GpsValue(EmbeddedMetadataField.GpsLongitudeReference, "GPSLongitudeRef", "W",
                JsonValueKind.String)
        ];
        var context = Context(Sidecar(), values);

        var result = Assert.IsType<JpegMetadataWriteNoChangesResult>(
            JpegMetadataWritePlanBuilder.Build(context.PlanningItem, context.BaselineHash));

        Assert.Same(context.PlanningItem, result.TakeoutPlanningItem);
        Assert.IsType<KeepEmbeddedCaptureTimeDecision>(context.MetadataPlan.CaptureTimeDecision);
        Assert.IsType<KeepEmbeddedLocationDecision>(context.MetadataPlan.LocationDecision);
    }

    [Fact]
    public void Build_AllBlockedCaptureTimeCasesRequireReview()
    {
        foreach (var captureCase in Enum.GetValues<BlockedCaptureCase>())
        {
            var context = BlockedCaptureContext(captureCase);
            var result = Assert.IsType<JpegMetadataWriteReviewRequiredResult>(
                JpegMetadataWritePlanBuilder.Build(context.PlanningItem, context.BaselineHash));

            Assert.Empty(result.ProposedAssignments);
            if (captureCase == BlockedCaptureCase.PhotoTakenTime)
            {
                Assert.Single(result.ReviewReasons
                    .OfType<SidecarPhotoTakenTimeTimezoneReviewReason>());
            }
            else
            {
                Assert.NotEmpty(result.ReviewReasons
                    .OfType<ExistingMediaMetadataPlanReviewReason>());
            }
        }
    }

    [Fact]
    public void Build_UpstreamReviewReasonsBlockOtherwiseSafeGps()
    {
        var context = Context(Sidecar(1, 2));
        var allReasons = Enum.GetValues<MediaMetadataPlanReviewReason>();
        var reviewedPlan = context.MetadataPlan with
        {
            Status = MediaMetadataPlanStatus.ReviewRequired,
            ReviewReasons = allReasons.Reverse().Concat(allReasons).ToArray()
        };

        var result = Assert.IsType<JpegMetadataWriteReviewRequiredResult>(
            JpegMetadataWritePlanBuilder.Build(
                context.PlanningItem with { MetadataPlan = reviewedPlan },
                context.BaselineHash));

        Assert.Equal(4, result.ProposedAssignments.Count);
        Assert.Equal(allReasons, result.ReviewReasons
            .OfType<ExistingMediaMetadataPlanReviewReason>()
            .Select(reason => reason.Reason));
    }

    [Fact]
    public void Build_InvalidMetadataPlanAndUnsupportedFormatReturnTypedResults()
    {
        var jpeg = Context(Sidecar());
        var unsupportedRead = new EmbeddedMetadataUnsupportedMediaResult(
            jpeg.BaselineHash.StagingResult.PlanningItem.AbsoluteSourcePath,
            ".jpg");
        var invalidItem = jpeg.PlanningItem with
        {
            MetadataPlan = new UnsupportedEmbeddedMetadataFormatPlan(
                unsupportedRead,
                jpeg.MetadataPlan.SidecarMetadata)
        };
        var invalid = Assert.IsType<JpegMetadataWriteInvalidInputResult>(
            JpegMetadataWritePlanBuilder.Build(invalidItem, jpeg.BaselineHash));
        Assert.Equal(JpegMetadataWritePlanInputIssueKind.InvalidMetadataPlan,
            Assert.Single(invalid.Issues).Kind);

        var heic = Context(Sidecar(), relativePath: "Album/photo.heic");
        var unsupported = Assert.IsType<JpegMetadataWriteUnsupportedFormatResult>(
            JpegMetadataWritePlanBuilder.Build(heic.PlanningItem, heic.BaselineHash));
        Assert.Equal(".heic", unsupported.Extension);
    }

    [Fact]
    public void Build_MismatchedPipelineJoinsAreInvalid()
    {
        var context = Context(Sidecar(1, 2));
        var staging = context.BaselineHash.StagingResult;
        var plan = staging.PlanningItem;
        var otherSource = Path.Combine(Path.GetDirectoryName(plan.AbsoluteSourcePath)!, "other.jpg");
        var otherTemporary = Path.Combine(Path.GetDirectoryName(staging.TemporaryCopyPath)!, ".other.jpg");
        var otherFinal = Path.Combine(Path.GetDirectoryName(staging.IntendedFinalDestinationPath)!, "other.jpg");
        var cases = new[]
        {
            (context.PlanningItem,
                context.BaselineHash with
                {
                    StagingResult = staging with
                    {
                        PlanningItem = plan with
                        {
                            MediaEntry = new InventoryEntry("Album/other.jpg", 4)
                        }
                    }
                },
                JpegMetadataWritePlanInputIssueKind.MismatchedMediaEntry),
            (context.PlanningItem with
                {
                    MetadataPlan = context.MetadataPlan with
                    {
                        SuccessfulRead = context.MetadataPlan.SuccessfulRead with
                        {
                            MediaPath = otherSource
                        }
                    }
                }, context.BaselineHash,
                JpegMetadataWritePlanInputIssueKind.MismatchedSourcePath),
            (context.PlanningItem,
                context.BaselineHash with { TemporaryCopyPath = otherTemporary },
                JpegMetadataWritePlanInputIssueKind.MismatchedTemporaryPath),
            (context.PlanningItem,
                context.BaselineHash with
                {
                    StagingResult = staging with { IntendedFinalDestinationPath = otherFinal }
                },
                JpegMetadataWritePlanInputIssueKind.MismatchedFinalDestination)
        };

        foreach (var (item, baseline, expected) in cases)
        {
            var result = Assert.IsType<JpegMetadataWriteInvalidInputResult>(
                JpegMetadataWritePlanBuilder.Build(item, baseline));
            Assert.Contains(result.Issues, issue => issue.Kind == expected);
        }
    }

    [Fact]
    public void Build_OnlyValidMatchedSidecarStateCanAuthorizeGps()
    {
        var context = Context(Sidecar(1, 2));
        var matched = Assert.IsType<MatchedTakeoutSidecarState>(
            context.PlanningItem.SidecarState);
        var media = context.PlanningItem.MediaEntry;
        var sidecar = matched.AnalysisResult.SidecarEntry;
        TakeoutPlanningSidecarState[] states =
        [
            new MatchedTakeoutSidecarState(matched.AnalysisResult with
            {
                MediaEntry = new InventoryEntry("Album/other.jpg", 4)
            }),
            new MatchedTakeoutSidecarState(matched.AnalysisResult with
            {
                Metadata = Sidecar(3, 4)
            }),
            new UnmatchedTakeoutSidecarState(new UnmatchedMediaResult(media)),
            new InvalidTakeoutSidecarState(new InvalidSidecarResult(
                media, sidecar, matched.AnalysisResult.MatchRule,
                new TakeoutSidecarParseException(sidecar.RelativePath, "synthetic failure"))),
            new AmbiguousTakeoutSidecarState(new AmbiguousMediaSidecarResult(
                media,
                [
                    new SidecarMatchCandidate(sidecar, matched.AnalysisResult.MatchRule),
                    new SidecarMatchCandidate(
                        new InventoryEntry("Album/photo.jpg.supplemental-metadata.json", 128),
                        SidecarMatchRule.SupplementalMetadataJson)
                ]))
        ];

        foreach (var state in states)
        {
            var result = Assert.IsType<JpegMetadataWriteInvalidInputResult>(
                JpegMetadataWritePlanBuilder.Build(
                    context.PlanningItem with { SidecarState = state },
                    context.BaselineHash));
            Assert.Contains(result.Issues, issue =>
                issue.Kind == JpegMetadataWritePlanInputIssueKind.InvalidSidecarProvenance);
        }
    }

    [Fact]
    public void Build_NonFiniteOrOutOfRangeGpsIsInvalid()
    {
        var context = Context(Sidecar(1, 2));
        var location = Assert.IsType<ProposeSidecarLocationDecision>(
            context.MetadataPlan.LocationDecision);
        (double Latitude, double Longitude)[] values =
        [
            (double.NaN, 2),
            (91, 2),
            (1, double.PositiveInfinity),
            (1, -181)
        ];

        foreach (var value in values)
        {
            var forgedPlan = context.MetadataPlan with
            {
                LocationDecision = location with
                {
                    Proposal = location.Proposal with
                    {
                        Latitude = value.Latitude,
                        Longitude = value.Longitude
                    }
                }
            };
            var result = Assert.IsType<JpegMetadataWriteInvalidInputResult>(
                JpegMetadataWritePlanBuilder.Build(
                    context.PlanningItem with { MetadataPlan = forgedPlan },
                    context.BaselineHash));
            Assert.Contains(result.Issues, issue =>
                issue.Kind == JpegMetadataWritePlanInputIssueKind.InvalidLocationProposal);
        }
    }

    private static WritePlanContext BlockedCaptureContext(BlockedCaptureCase value)
    {
        var time = new DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.Zero);
        return value switch
        {
            BlockedCaptureCase.PhotoTakenTime => Context(Sidecar(photoTakenTime: time)),
            BlockedCaptureCase.CreationTime => Context(Sidecar(creationTime: time)),
            BlockedCaptureCase.Conflict => Context(
                Sidecar(photoTakenTime: time.AddSeconds(1)),
                [CaptureValue("ExifIFD", "DateTimeOriginal", "2020:01:02 03:04:05Z")]),
            BlockedCaptureCase.MixedTimezoneKnowledge => Context(Sidecar(),
                [
                    CaptureValue("ExifIFD", "DateTimeOriginal", "2020:01:02 03:04:05Z"),
                    CaptureValue("XMP-exif", "DateTimeOriginal", "2020:01:02 03:04:05")
                ]),
            BlockedCaptureCase.ParsingIssue => Context(
                Sidecar(photoTakenTime: time),
                [CaptureValue("ExifIFD", "DateTimeOriginal", "invalid")]),
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };
    }

    private static WritePlanContext Context(
        TakeoutSidecarMetadata sidecar,
        IEnumerable<EmbeddedMetadataValue>? values = null,
        string relativePath = "Album/photo.jpg",
        string? sidecarRelativePath = null,
        SidecarMatchRule matchRule = SidecarMatchRule.LegacyJson)
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(),
            "PhotoMigration-JpegWritePlanTests"));
        var source = Combine(Path.Combine(root, "source"), relativePath.Split('/'));
        var destination = Combine(Path.Combine(root, "output"), relativePath.Split('/'));
        var temporary = Path.Combine(Path.GetDirectoryName(destination)!,
            $".staged-for-write{Path.GetExtension(relativePath)}");
        var entry = new InventoryEntry(relativePath, 4);
        var destinationPlan = new DestinationPathPlanningItem(entry, source, destination);
        var staging = new VerifiedMediaFileStagingSuccessResult(
            destinationPlan, temporary, destination, 4, FullFileHash, FullFileHash);
        var baseline = new ImageDataHashReadSuccessResult(
            staging, temporary, Path.Combine(root, "tools", "exiftool"),
            "SHA256", MediaDataHash.ToLowerInvariant(), MediaDataHash,
            Array.Empty<string>(), string.Empty);
        var read = new EmbeddedMetadataReadSuccessResult(
            source, baseline.ExifToolExecutablePath,
            (values ?? []).ToList().AsReadOnly(),
            Array.Empty<string>(), Array.Empty<string>(), string.Empty);
        var metadataPlan = Assert.IsType<SuccessfulMediaMetadataPlan>(
            MediaMetadataPlanBuilder.Build(read, sidecar));
        var parsed = new ParsedMediaResult(
            entry,
            new InventoryEntry(sidecarRelativePath ?? $"{relativePath}.json", 128),
            matchRule,
            sidecar);
        return new WritePlanContext(
            new TakeoutMetadataPlanningItem(
                entry, new MatchedTakeoutSidecarState(parsed), metadataPlan),
            baseline);
    }

    private static EmbeddedMetadataValue CaptureValue(
        string group, string tag, string value) =>
        new(EmbeddedMetadataField.CaptureDateTime, group, tag, value,
            JsonValueKind.String);

    private static EmbeddedMetadataValue GpsValue(
        EmbeddedMetadataField field,
        string tag,
        string value,
        JsonValueKind kind = JsonValueKind.Number) =>
        new(field, "GPS", tag, value, kind);

    private static TakeoutSidecarMetadata Sidecar(
        double? latitude = null,
        double? longitude = null,
        double? altitude = null,
        DateTimeOffset? creationTime = null,
        DateTimeOffset? photoTakenTime = null) =>
        new("synthetic.jpg", "Synthetic description", creationTime,
            photoTakenTime, latitude, longitude, altitude, null);

    private static string Combine(string root, IEnumerable<string> segments)
    {
        foreach (var segment in segments)
        {
            root = Path.Combine(root, segment);
        }

        return Path.GetFullPath(root);
    }

    private enum BlockedCaptureCase
    {
        PhotoTakenTime,
        CreationTime,
        Conflict,
        MixedTimezoneKnowledge,
        ParsingIssue
    }

    private sealed record GpsCase(
        double Latitude,
        double Longitude,
        double? Altitude,
        string[] Expected);

    private sealed record WritePlanContext(
        TakeoutMetadataPlanningItem PlanningItem,
        ImageDataHashReadSuccessResult BaselineHash)
    {
        public SuccessfulMediaMetadataPlan MetadataPlan =>
            (SuccessfulMediaMetadataPlan)PlanningItem.MetadataPlan;
    }
}
