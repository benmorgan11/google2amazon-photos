using System.Text.Json;

namespace PhotoMigration.Core.Tests;

public sealed class MediaMetadataPlanBuilderTests
{
    [Fact]
    public void Build_ValidEmbeddedMetadataProducesNoChangePlan()
    {
        var read = Success(
            ZonedTime("ExifIFD", "DateTimeOriginal", "2020:01:02 03:04:05Z")
                .Concat(ExifLocation(34.25, -118.5, 25)));
        var sidecar = Sidecar();

        var plan = Assert.IsType<SuccessfulMediaMetadataPlan>(
            MediaMetadataPlanBuilder.Build(read, sidecar));

        Assert.Equal(MediaMetadataPlanStatus.NoMetadataChangesProposed, plan.Status);
        Assert.Same(read, plan.SuccessfulRead);
        Assert.Same(read, plan.EmbeddedReadResult);
        Assert.Same(sidecar, plan.SidecarMetadata);
        Assert.IsType<KeepEmbeddedCaptureTimeDecision>(plan.CaptureTimeDecision);
        Assert.IsType<KeepEmbeddedLocationDecision>(plan.LocationDecision);
        Assert.Single(plan.CaptureTimeParseResult.Candidates);
        Assert.Single(plan.LocationBuildResult.Candidates);
        Assert.Same(plan.LocationBuildResult, plan.LocationDecision.EmbeddedLocationResult);
        Assert.Empty(plan.ReviewReasons);
    }

    [Fact]
    public void Build_SidecarPhotoTakenTimeProducesSafeChangePlan()
    {
        var photoTakenTime = DateTimeOffset.FromUnixTimeSeconds(1_600_000_000);

        var plan = Assert.IsType<SuccessfulMediaMetadataPlan>(
            MediaMetadataPlanBuilder.Build(
                Success([]),
                Sidecar(photoTakenTime: photoTakenTime)));

        Assert.Equal(MediaMetadataPlanStatus.SafeMetadataChangesProposed, plan.Status);
        var decision = Assert.IsType<ProposeSidecarPhotoTakenTimeDecision>(
            plan.CaptureTimeDecision);
        Assert.Equal(photoTakenTime, decision.Proposal.Instant);
        Assert.IsType<LocationMissingDecision>(plan.LocationDecision);
        Assert.Empty(plan.ReviewReasons);
    }

    [Fact]
    public void Build_SidecarLocationProducesSafeChangePlan()
    {
        var sidecar = Sidecar(latitude: 34.25, longitude: -118.5);

        var plan = Assert.IsType<SuccessfulMediaMetadataPlan>(
            MediaMetadataPlanBuilder.Build(Success([]), sidecar));

        Assert.Equal(MediaMetadataPlanStatus.SafeMetadataChangesProposed, plan.Status);
        var decision = Assert.IsType<ProposeSidecarLocationDecision>(plan.LocationDecision);
        Assert.Equal(34.25, decision.Proposal.Latitude);
        Assert.Equal(-118.5, decision.Proposal.Longitude);
        Assert.Null(decision.Proposal.Altitude);
        Assert.Equal(SidecarLocationSource.GeoData, decision.Proposal.Source);
        Assert.Same(sidecar, decision.Proposal.SourceMetadata);
    }

    [Fact]
    public void Build_CaptureTimeAndLocationProposalsProduceSafeChangePlan()
    {
        var sidecar = Sidecar(
            photoTakenTime: DateTimeOffset.FromUnixTimeSeconds(1_600_000_000),
            latitude: 34.25,
            longitude: -118.5,
            altitude: 25);

        var plan = Assert.IsType<SuccessfulMediaMetadataPlan>(
            MediaMetadataPlanBuilder.Build(Success([]), sidecar));

        Assert.Equal(MediaMetadataPlanStatus.SafeMetadataChangesProposed, plan.Status);
        Assert.IsType<ProposeSidecarPhotoTakenTimeDecision>(plan.CaptureTimeDecision);
        Assert.IsType<ProposeSidecarLocationDecision>(plan.LocationDecision);
        Assert.Empty(plan.ReviewReasons);
    }

    [Fact]
    public void Build_LowConfidenceCreationTimeRequiresReview()
    {
        var creationTime = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

        var plan = Assert.IsType<SuccessfulMediaMetadataPlan>(
            MediaMetadataPlanBuilder.Build(
                Success([]),
                Sidecar(creationTime: creationTime)));

        Assert.Equal(MediaMetadataPlanStatus.ReviewRequired, plan.Status);
        Assert.IsType<ProposeLowConfidenceSidecarCreationTimeDecision>(
            plan.CaptureTimeDecision);
        Assert.Equal(
            [MediaMetadataPlanReviewReason.LowConfidenceCreationTimeFallback],
            plan.ReviewReasons);
    }

    [Fact]
    public void Build_CaptureTimeConflictRequiresReviewWithoutReplacingEmbeddedValue()
    {
        var embedded = ZonedTime(
            "ExifIFD",
            "DateTimeOriginal",
            "2020:01:02 03:04:05Z");
        var sidecar = Sidecar(
            photoTakenTime: new DateTimeOffset(2020, 1, 2, 3, 4, 6, TimeSpan.Zero));

        var plan = Assert.IsType<SuccessfulMediaMetadataPlan>(
            MediaMetadataPlanBuilder.Build(Success(embedded), sidecar));

        Assert.Equal(MediaMetadataPlanStatus.ReviewRequired, plan.Status);
        Assert.Contains(MediaMetadataPlanReviewReason.CaptureTimeConflict, plan.ReviewReasons);
        Assert.IsType<KeepEmbeddedCaptureTimeDecision>(plan.CaptureTimeDecision);
    }

    [Fact]
    public void Build_LocationConflictRequiresReviewWithoutReplacingEmbeddedValue()
    {
        var read = Success(ExifLocation(34.25, -118.5, 25));
        var sidecar = Sidecar(latitude: 35, longitude: -118.5, altitude: 25);

        var plan = Assert.IsType<SuccessfulMediaMetadataPlan>(
            MediaMetadataPlanBuilder.Build(read, sidecar));

        Assert.Equal(MediaMetadataPlanStatus.ReviewRequired, plan.Status);
        Assert.Contains(MediaMetadataPlanReviewReason.LocationConflict, plan.ReviewReasons);
        Assert.IsType<KeepEmbeddedLocationDecision>(plan.LocationDecision);
    }

    [Fact]
    public void Build_ParserIssuesRequireReviewDespiteOtherValidEmbeddedValues()
    {
        var values = ZonedTime(
                "ExifIFD",
                "DateTimeOriginal",
                "2020:01:02 03:04:05Z")
            .Concat(
                [Value(
                    EmbeddedMetadataField.CaptureDateTime,
                    "XMP-exif",
                    "DateTimeOriginal",
                    "invalid date")])
            .Concat(ExifLocation(34.25, -118.5))
            .Concat(
                [Value(
                    EmbeddedMetadataField.GpsLatitude,
                    "XMP-exif",
                    "GPSLatitude",
                    "invalid latitude")]);

        var plan = Assert.IsType<SuccessfulMediaMetadataPlan>(
            MediaMetadataPlanBuilder.Build(Success(values), Sidecar()));

        Assert.Equal(MediaMetadataPlanStatus.ReviewRequired, plan.Status);
        Assert.Contains(
            MediaMetadataPlanReviewReason.EmbeddedCaptureTimeParsingIssues,
            plan.ReviewReasons);
        Assert.Contains(
            MediaMetadataPlanReviewReason.EmbeddedGpsParsingIssues,
            plan.ReviewReasons);
        Assert.IsType<KeepEmbeddedCaptureTimeDecision>(plan.CaptureTimeDecision);
        Assert.IsType<KeepEmbeddedLocationDecision>(plan.LocationDecision);
    }

    [Theory]
    [InlineData("warning", "", "")]
    [InlineData("", "json error", "")]
    [InlineData("", "", "standard error")]
    public void Build_ExifToolDiagnosticsRequireReview(
        string warning,
        string error,
        string standardError)
    {
        var read = Success(
            [],
            warnings: TextList(warning),
            errors: TextList(error),
            standardError: standardError);

        var plan = Assert.IsType<SuccessfulMediaMetadataPlan>(
            MediaMetadataPlanBuilder.Build(read, Sidecar()));

        Assert.Equal(MediaMetadataPlanStatus.ReviewRequired, plan.Status);
        Assert.Equal(
            [MediaMetadataPlanReviewReason.ExifToolDiagnostics],
            plan.ReviewReasons);
    }

    [Fact]
    public void Build_UnknownEmbeddedTimezoneRemainsReportableWithoutRequiringReview()
    {
        var embedded = Value(
            EmbeddedMetadataField.CaptureDateTime,
            "ExifIFD",
            "DateTimeOriginal",
            "2020:01:02 03:04:05");
        var sidecar = Sidecar(
            photoTakenTime: new DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.Zero));

        var plan = Assert.IsType<SuccessfulMediaMetadataPlan>(
            MediaMetadataPlanBuilder.Build(Success([embedded]), sidecar));

        Assert.Equal(MediaMetadataPlanStatus.NoMetadataChangesProposed, plan.Status);
        Assert.Empty(plan.ReviewReasons);
        Assert.Equal(
            EmbeddedSidecarCaptureTimeComparisonKind.CannotCompareUnknownEmbeddedTimezone,
            Assert.Single(plan.CaptureTimeDecision.SidecarComparisons).Kind);
    }

    [Fact]
    public void Build_CompletelyMissingMetadataProducesNoChangePlan()
    {
        var plan = Assert.IsType<SuccessfulMediaMetadataPlan>(
            MediaMetadataPlanBuilder.Build(Success([]), Sidecar()));

        Assert.Equal(MediaMetadataPlanStatus.NoMetadataChangesProposed, plan.Status);
        Assert.IsType<CaptureTimeMissingDecision>(plan.CaptureTimeDecision);
        Assert.IsType<LocationMissingDecision>(plan.LocationDecision);
        Assert.Empty(plan.ReviewReasons);
    }

    [Fact]
    public void Build_UnsupportedReaderFormatHasDedicatedPlanStatus()
    {
        var read = new EmbeddedMetadataUnsupportedMediaResult("/media/photo.heic", ".heic");
        var sidecar = Sidecar(
            photoTakenTime: DateTimeOffset.FromUnixTimeSeconds(1_600_000_000));

        var plan = Assert.IsType<UnsupportedEmbeddedMetadataFormatPlan>(
            MediaMetadataPlanBuilder.Build(read, sidecar));

        Assert.Equal(
            MediaMetadataPlanStatus.EmbeddedMetadataFormatNotSupportedYet,
            plan.Status);
        Assert.Same(read, plan.UnsupportedRead);
        Assert.Same(read, plan.EmbeddedReadResult);
        Assert.Same(sidecar, plan.SidecarMetadata);
    }

    [Fact]
    public void Build_MissingMediaPreservesUnavailableReadOutcome()
    {
        var read = new EmbeddedMetadataMissingMediaResult("/media/missing.jpg");

        var plan = Assert.IsType<MissingMediaMetadataPlan>(
            MediaMetadataPlanBuilder.Build(read, Sidecar(latitude: 1, longitude: 2)));

        AssertUnavailable(plan, read);
        Assert.Same(read, plan.MissingRead);
    }

    [Fact]
    public void Build_ExifToolFailurePreservesUnavailableReadOutcome()
    {
        var read = new EmbeddedMetadataExifToolFailureResult(
            "/media/photo.jpg",
            "/tools/exiftool",
            "synthetic failure",
            9,
            "output",
            "error");

        var plan = Assert.IsType<ExifToolFailureMetadataPlan>(
            MediaMetadataPlanBuilder.Build(read, Sidecar(latitude: 1, longitude: 2)));

        AssertUnavailable(plan, read);
        Assert.Same(read, plan.FailureRead);
    }

    [Fact]
    public void Build_TimeoutPreservesUnavailableReadOutcome()
    {
        var read = new EmbeddedMetadataReadTimedOutResult(
            "/media/photo.jpg",
            "/tools/exiftool",
            TimeSpan.FromSeconds(5),
            "termination detail",
            "output",
            "error");

        var plan = Assert.IsType<EmbeddedMetadataTimeoutPlan>(
            MediaMetadataPlanBuilder.Build(read, Sidecar(latitude: 1, longitude: 2)));

        AssertUnavailable(plan, read);
        Assert.Same(read, plan.TimeoutRead);
    }

    [Fact]
    public void Build_MalformedJsonPreservesUnavailableReadOutcome()
    {
        var read = new EmbeddedMetadataMalformedJsonResult(
            "/media/photo.jpg",
            "/tools/exiftool",
            "synthetic malformed JSON",
            "not-json",
            "error");

        var plan = Assert.IsType<MalformedEmbeddedMetadataJsonPlan>(
            MediaMetadataPlanBuilder.Build(read, Sidecar(latitude: 1, longitude: 2)));

        AssertUnavailable(plan, read);
        Assert.Same(read, plan.MalformedRead);
    }

    [Fact]
    public void Build_ReviewReasonsAreUniqueAndDeterministicallyEnumOrdered()
    {
        var values = new List<EmbeddedMetadataValue>();
        values.AddRange(ZonedTime(
            "XMP-exif",
            "DateTimeOriginal",
            "2020:01:02 03:04:06Z"));
        values.AddRange(ZonedTime(
            "ExifIFD",
            "DateTimeOriginal",
            "2020:01:02 03:04:05Z"));
        values.Add(Value(
            EmbeddedMetadataField.CaptureDateTime,
            "XMP-xmp",
            "CreateDate",
            "invalid date"));
        values.AddRange(ExifLocation(34.25, -118.5));
        values.AddRange(XmpLocation(35, -118.5));
        values.Add(Value(
            EmbeddedMetadataField.GpsLatitude,
            "XMP-other",
            "GPSLatitude",
            "10"));
        values.Add(Value(
            EmbeddedMetadataField.GpsLongitude,
            "XMP-invalid",
            "GPSLongitude",
            "invalid longitude"));
        var sidecar = Sidecar(
            photoTakenTime: new DateTimeOffset(2020, 1, 2, 3, 4, 7, TimeSpan.Zero),
            latitude: 36,
            longitude: -118.5);

        var first = Assert.IsType<SuccessfulMediaMetadataPlan>(
            MediaMetadataPlanBuilder.Build(
                Success(values, warnings: ["warning"]),
                sidecar));
        values.Reverse();
        var second = Assert.IsType<SuccessfulMediaMetadataPlan>(
            MediaMetadataPlanBuilder.Build(
                Success(values, warnings: ["warning"]),
                sidecar));

        var expected = new[]
        {
            MediaMetadataPlanReviewReason.CaptureTimeReviewRequired,
            MediaMetadataPlanReviewReason.CaptureTimeConflict,
            MediaMetadataPlanReviewReason.EmbeddedCaptureTimeParsingIssues,
            MediaMetadataPlanReviewReason.LocationReviewRequired,
            MediaMetadataPlanReviewReason.LocationConflict,
            MediaMetadataPlanReviewReason.EmbeddedGpsParsingIssues,
            MediaMetadataPlanReviewReason.EmbeddedLocationBuildingIssues,
            MediaMetadataPlanReviewReason.ExifToolDiagnostics
        };
        Assert.Equal(expected, first.ReviewReasons);
        Assert.Equal(expected, second.ReviewReasons);
        Assert.Equal(first.ReviewReasons.Count, first.ReviewReasons.Distinct().Count());
    }

    [Fact]
    public void Build_SidecarTitleAndDescriptionAreRetainedWithoutProposedWrites()
    {
        var sidecar = new TakeoutSidecarMetadata(
            Title: "synthetic title.jpg",
            Description: "Synthetic description",
            CreationTime: null,
            PhotoTakenTime: null,
            Latitude: null,
            Longitude: null,
            Altitude: null,
            Url: null);

        var plan = Assert.IsType<SuccessfulMediaMetadataPlan>(
            MediaMetadataPlanBuilder.Build(Success([]), sidecar));

        Assert.Equal(MediaMetadataPlanStatus.NoMetadataChangesProposed, plan.Status);
        Assert.Same(sidecar, plan.SidecarMetadata);
        Assert.Equal("synthetic title.jpg", plan.SidecarMetadata.Title);
        Assert.Equal("Synthetic description", plan.SidecarMetadata.Description);
        Assert.IsType<CaptureTimeMissingDecision>(plan.CaptureTimeDecision);
        Assert.IsType<LocationMissingDecision>(plan.LocationDecision);
    }

    private static void AssertUnavailable(
        MediaMetadataPlan plan,
        EmbeddedMetadataReadResult read)
    {
        Assert.Equal(MediaMetadataPlanStatus.EmbeddedMetadataUnavailable, plan.Status);
        Assert.Same(read, plan.EmbeddedReadResult);
    }

    private static EmbeddedMetadataReadSuccessResult Success(
        IEnumerable<EmbeddedMetadataValue> values,
        IReadOnlyList<string>? warnings = null,
        IReadOnlyList<string>? errors = null,
        string standardError = "") =>
        new(
            "/media/photo.jpg",
            "/tools/exiftool",
            values.ToList().AsReadOnly(),
            warnings ?? Array.Empty<string>(),
            errors ?? Array.Empty<string>(),
            standardError);

    private static IEnumerable<EmbeddedMetadataValue> ZonedTime(
        string groupName,
        string tagName,
        string rawValue) =>
        [Value(
            EmbeddedMetadataField.CaptureDateTime,
            groupName,
            tagName,
            rawValue)];

    private static IEnumerable<EmbeddedMetadataValue> ExifLocation(
        double latitude,
        double longitude,
        double? altitude = null)
    {
        var values = new List<EmbeddedMetadataValue>
        {
            Value(EmbeddedMetadataField.GpsLatitude, "GPS", "GPSLatitude", Math.Abs(latitude)),
            Value(
                EmbeddedMetadataField.GpsLatitudeReference,
                "GPS",
                "GPSLatitudeRef",
                latitude < 0 ? "S" : "N"),
            Value(
                EmbeddedMetadataField.GpsLongitude,
                "GPS",
                "GPSLongitude",
                Math.Abs(longitude)),
            Value(
                EmbeddedMetadataField.GpsLongitudeReference,
                "GPS",
                "GPSLongitudeRef",
                longitude < 0 ? "W" : "E")
        };

        if (altitude is not null)
        {
            values.Add(Value(
                EmbeddedMetadataField.GpsAltitude,
                "GPS",
                "GPSAltitude",
                Math.Abs(altitude.Value)));
            values.Add(Value(
                EmbeddedMetadataField.GpsAltitudeReference,
                "GPS",
                "GPSAltitudeRef",
                altitude < 0 ? "1" : "0"));
        }

        return values;
    }

    private static IEnumerable<EmbeddedMetadataValue> XmpLocation(
        double latitude,
        double longitude,
        double? altitude = null)
    {
        var values = new List<EmbeddedMetadataValue>
        {
            Value(EmbeddedMetadataField.GpsLatitude, "XMP-exif", "GPSLatitude", latitude),
            Value(EmbeddedMetadataField.GpsLongitude, "XMP-exif", "GPSLongitude", longitude)
        };

        if (altitude is not null)
        {
            values.Add(Value(
                EmbeddedMetadataField.GpsAltitude,
                "XMP-exif",
                "GPSAltitude",
                altitude.Value));
        }

        return values;
    }

    private static EmbeddedMetadataValue Value(
        EmbeddedMetadataField field,
        string groupName,
        string tagName,
        double rawValue) =>
        Value(
            field,
            groupName,
            tagName,
            rawValue.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            JsonValueKind.Number);

    private static EmbeddedMetadataValue Value(
        EmbeddedMetadataField field,
        string groupName,
        string tagName,
        string rawValue,
        JsonValueKind valueKind = JsonValueKind.String) =>
        new(field, groupName, tagName, rawValue, valueKind);

    private static IReadOnlyList<string> TextList(string value) =>
        string.IsNullOrEmpty(value) ? Array.Empty<string>() : new[] { value };

    private static TakeoutSidecarMetadata Sidecar(
        DateTimeOffset? creationTime = null,
        DateTimeOffset? photoTakenTime = null,
        double? latitude = null,
        double? longitude = null,
        double? altitude = null) =>
        new(
            Title: null,
            Description: null,
            CreationTime: creationTime,
            PhotoTakenTime: photoTakenTime,
            Latitude: latitude,
            Longitude: longitude,
            Altitude: altitude,
            Url: null);
}
