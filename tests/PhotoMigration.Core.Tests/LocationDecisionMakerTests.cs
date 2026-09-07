using System.Globalization;
using System.Text.Json;

namespace PhotoMigration.Core.Tests;

public sealed class LocationDecisionMakerTests
{
    [Fact]
    public void Decide_OneEmbeddedLocationIsKeptWithProvenance()
    {
        var latitude = Gps(EmbeddedMetadataField.GpsLatitude, "GPS", 34.25, "N");
        var longitude = Gps(EmbeddedMetadataField.GpsLongitude, "GPS", -118.5, "W");
        var altitude = Gps(EmbeddedMetadataField.GpsAltitude, "GPS", 25, "0");
        var gpsResult = ParseResult([longitude, latitude, altitude]);
        var sidecar = Sidecar();

        var decision = Assert.IsType<KeepEmbeddedLocationDecision>(
            LocationDecisionMaker.Decide(gpsResult, sidecar));

        var location = Assert.Single(decision.EmbeddedLocationResult.Candidates);
        Assert.Equal("GPS", location.GroupName);
        Assert.Same(latitude, location.Latitude);
        Assert.Same(longitude, location.Longitude);
        Assert.Same(altitude, location.Altitude);
        Assert.Same(latitude.ReferenceValue, location.Latitude.ReferenceValue);
        Assert.Same(sidecar, decision.SidecarMetadata);
        Assert.Same(sidecar, decision.SidecarLocation.SourceMetadata);
        Assert.Equal(
            EmbeddedSidecarLocationComparisonKind.NoUsableSidecarLocation,
            Assert.Single(decision.SidecarComparisons).Kind);
    }

    [Fact]
    public void Decide_EquivalentExifAndXmpLocationsAreBothKept()
    {
        var candidates = CompleteLocations(
            exifLatitude: 34.25,
            exifLongitude: -118.5,
            exifAltitude: 25,
            xmpLatitude: 34.2500005,
            xmpLongitude: -118.5000005,
            xmpAltitude: 25.05);

        var decision = Assert.IsType<KeepEmbeddedLocationDecision>(
            LocationDecisionMaker.Decide(ParseResult(candidates), Sidecar()));

        Assert.Equal(2, decision.EmbeddedLocationResult.Candidates.Count);
        Assert.Equal(
            ["GPS", "XMP-exif"],
            decision.EmbeddedLocationResult.Candidates.Select(location => location.GroupName));
    }

    [Fact]
    public void Decide_ConflictingEmbeddedLocationsRequireReview()
    {
        var candidates = CompleteLocations(
            exifLatitude: 34.25,
            exifLongitude: -118.5,
            xmpLatitude: 34.26,
            xmpLongitude: -118.5);

        var decision = Assert.IsType<ReviewRequiredLocationDecision>(
            LocationDecisionMaker.Decide(ParseResult(candidates), Sidecar()));

        Assert.Equal(
            [LocationReviewReason.ConflictingEmbeddedCandidates],
            decision.Reasons);
        Assert.Equal(2, decision.EmbeddedLocationResult.Candidates.Count);
    }

    [Fact]
    public void Decide_MatchingEmbeddedAndSidecarLocationsAreReported()
    {
        var candidates = Location("GPS", 34.25, -118.5, 25);
        var sidecar = Sidecar(34.2500005, -118.5000005, 25.05);

        var decision = Assert.IsType<KeepEmbeddedLocationDecision>(
            LocationDecisionMaker.Decide(ParseResult(candidates), sidecar));

        Assert.Equal(
            EmbeddedSidecarLocationComparisonKind.Match,
            Assert.Single(decision.SidecarComparisons).Kind);
    }

    [Fact]
    public void Decide_SidecarCoordinateConflictIsReportedButEmbeddedIsKept()
    {
        var candidates = Location("GPS", 34.25, -118.5, 25);
        var sidecar = Sidecar(34.25, -118.51, 25);

        var decision = Assert.IsType<KeepEmbeddedLocationDecision>(
            LocationDecisionMaker.Decide(ParseResult(candidates), sidecar));

        Assert.Equal(
            EmbeddedSidecarLocationComparisonKind.CoordinateConflict,
            Assert.Single(decision.SidecarComparisons).Kind);
    }

    [Fact]
    public void Decide_SidecarAltitudeConflictIsReportedButEmbeddedIsKept()
    {
        var candidates = Location("GPS", 34.25, -118.5, 25);
        var sidecar = Sidecar(34.25, -118.5, 25.2);

        var decision = Assert.IsType<KeepEmbeddedLocationDecision>(
            LocationDecisionMaker.Decide(ParseResult(candidates), sidecar));

        Assert.Equal(
            EmbeddedSidecarLocationComparisonKind.AltitudeConflict,
            Assert.Single(decision.SidecarComparisons).Kind);
    }

    [Fact]
    public void Decide_MissingAltitudeDoesNotMakeEquivalentCoordinatesConflict()
    {
        var candidates = CompleteLocations(
            exifLatitude: 34.25,
            exifLongitude: -118.5,
            exifAltitude: 25,
            xmpLatitude: 34.25,
            xmpLongitude: -118.5,
            xmpAltitude: null);

        var decision = Assert.IsType<KeepEmbeddedLocationDecision>(
            LocationDecisionMaker.Decide(ParseResult(candidates), Sidecar()));

        Assert.Equal(2, decision.EmbeddedLocationResult.Candidates.Count);
        Assert.Contains(
            decision.EmbeddedLocationResult.Candidates,
            location => location.Altitude is null);
        Assert.Contains(
            decision.EmbeddedLocationResult.Candidates,
            location => location.Altitude is not null);
    }

    [Fact]
    public void Decide_CompleteSidecarLocationIsProposedAsGeoDataFallback()
    {
        var sidecar = Sidecar(34.25, -118.5);

        var decision = Assert.IsType<ProposeSidecarLocationDecision>(
            LocationDecisionMaker.Decide(ParseResult(), sidecar));

        Assert.Equal(34.25, decision.Proposal.Latitude);
        Assert.Equal(-118.5, decision.Proposal.Longitude);
        Assert.Null(decision.Proposal.Altitude);
        Assert.Equal(SidecarLocationSource.GeoData, decision.Proposal.Source);
        Assert.Same(sidecar, decision.Proposal.SourceMetadata);
    }

    [Fact]
    public void Decide_SidecarProposalRetainsOptionalAltitude()
    {
        var sidecar = Sidecar(34.25, -118.5, 125.5);

        var decision = Assert.IsType<ProposeSidecarLocationDecision>(
            LocationDecisionMaker.Decide(ParseResult(), sidecar));

        Assert.Equal(125.5, decision.Proposal.Altitude);
        Assert.Equal(SidecarLocationStatus.Complete, decision.SidecarLocation.Status);
    }

    [Fact]
    public void Decide_GpsParsingIssuesPreventAutomaticSidecarFallback()
    {
        var issue = GpsIssue("invalid latitude");
        var sidecar = Sidecar(34.25, -118.5);

        var decision = Assert.IsType<ReviewRequiredLocationDecision>(
            LocationDecisionMaker.Decide(ParseResult(issues: [issue]), sidecar));

        Assert.Contains(
            LocationReviewReason.EmbeddedGpsParsingIssuesWithoutCompleteLocation,
            decision.Reasons);
        Assert.Same(issue, Assert.Single(decision.EmbeddedGpsResult.Issues));
    }

    [Fact]
    public void Decide_LocationBuildingIssuesPreventAutomaticSidecarFallback()
    {
        var latitude = Gps(EmbeddedMetadataField.GpsLatitude, "GPS", 34.25, "N");
        var sidecar = Sidecar(34.25, -118.5);

        var decision = Assert.IsType<ReviewRequiredLocationDecision>(
            LocationDecisionMaker.Decide(ParseResult([latitude]), sidecar));

        Assert.Contains(
            LocationReviewReason.EmbeddedLocationBuildingIssuesWithoutCompleteLocation,
            decision.Reasons);
        Assert.Equal(
            EmbeddedLocationIssueKind.LatitudeWithoutLongitude,
            Assert.Single(decision.EmbeddedLocationResult.Issues).Kind);
    }

    [Fact]
    public void Decide_IncompleteSidecarCoordinatesRequireReview()
    {
        var incomplete = Sidecar(latitude: 34.25);

        var withoutEmbedded = Assert.IsType<ReviewRequiredLocationDecision>(
            LocationDecisionMaker.Decide(ParseResult(), incomplete));

        Assert.Equal(SidecarLocationStatus.Incomplete, withoutEmbedded.SidecarLocation.Status);
        Assert.Contains(LocationReviewReason.IncompleteSidecarLocation, withoutEmbedded.Reasons);

        var withEmbedded = Assert.IsType<ReviewRequiredLocationDecision>(
            LocationDecisionMaker.Decide(
                ParseResult(Location("GPS", 34.25, -118.5)),
                incomplete));
        Assert.Contains(LocationReviewReason.IncompleteSidecarLocation, withEmbedded.Reasons);
        Assert.Equal(
            EmbeddedSidecarLocationComparisonKind.IncompleteSidecarLocation,
            Assert.Single(withEmbedded.SidecarComparisons).Kind);
    }

    [Fact]
    public void Decide_SidecarZeroZeroIsRetainedAsPlaceholderAndNotProposed()
    {
        var sidecar = Sidecar(0, 0, 10);

        var decision = Assert.IsType<LocationMissingDecision>(
            LocationDecisionMaker.Decide(ParseResult(), sidecar));

        Assert.Equal(
            SidecarLocationStatus.ZeroZeroPlaceholder,
            decision.SidecarLocation.Status);
        Assert.Equal(0, decision.SidecarLocation.Latitude);
        Assert.Equal(0, decision.SidecarLocation.Longitude);
        Assert.Equal(10, decision.SidecarLocation.Altitude);
        Assert.Same(sidecar, decision.SidecarLocation.SourceMetadata);
    }

    [Fact]
    public void Decide_EmbeddedZeroZeroLocationRemainsValid()
    {
        var sidecar = Sidecar(0, 0);

        var decision = Assert.IsType<KeepEmbeddedLocationDecision>(
            LocationDecisionMaker.Decide(
                ParseResult(Location("GPS", 0, 0)),
                sidecar));

        var location = Assert.Single(decision.EmbeddedLocationResult.Candidates);
        Assert.Equal(0, location.Latitude.ParsedValue);
        Assert.Equal(0, location.Longitude.ParsedValue);
        Assert.Equal(
            EmbeddedSidecarLocationComparisonKind.SidecarZeroZeroPlaceholder,
            Assert.Single(decision.SidecarComparisons).Kind);
    }

    [Fact]
    public void Decide_CompletelyMissingLocationReturnsMissingResult()
    {
        var decision = Assert.IsType<LocationMissingDecision>(
            LocationDecisionMaker.Decide(ParseResult(), Sidecar()));

        Assert.Equal(SidecarLocationStatus.Missing, decision.SidecarLocation.Status);
        Assert.Empty(decision.EmbeddedGpsResult.Candidates);
        Assert.Empty(decision.EmbeddedLocationResult.Candidates);
        Assert.Empty(decision.SidecarComparisons);
    }

    [Theory]
    [InlineData(0.000001, false)]
    [InlineData(0.00000101, true)]
    public void Decide_CoordinateToleranceBoundaryIsExplicit(
        double latitudeDifference,
        bool expectReview)
    {
        var candidates = CompleteLocations(
            exifLatitude: 10,
            exifLongitude: 20,
            xmpLatitude: 10 + latitudeDifference,
            xmpLongitude: 20);

        var decision = LocationDecisionMaker.Decide(ParseResult(candidates), Sidecar());

        Assert.Equal(expectReview, decision is ReviewRequiredLocationDecision);
    }

    [Theory]
    [InlineData(0.1, false)]
    [InlineData(0.100001, true)]
    public void Decide_AltitudeToleranceBoundaryIsExplicit(
        double altitudeDifference,
        bool expectReview)
    {
        var candidates = CompleteLocations(
            exifLatitude: 10,
            exifLongitude: 20,
            exifAltitude: 100,
            xmpLatitude: 10,
            xmpLongitude: 20,
            xmpAltitude: 100 + altitudeDifference);

        var decision = LocationDecisionMaker.Decide(ParseResult(candidates), Sidecar());

        Assert.Equal(expectReview, decision is ReviewRequiredLocationDecision);
    }

    [Fact]
    public void Decide_ShuffledInputProducesDeterministicRetainedResults()
    {
        var candidates = CompleteLocations(
            exifLatitude: 34.25,
            exifLongitude: -118.5,
            xmpLatitude: 34.25,
            xmpLongitude: -118.5);
        var firstIssue = GpsIssue("a invalid", "GPS");
        var secondIssue = GpsIssue("z invalid", "XMP-exif");
        var sidecar = Sidecar(34.25, -118.5);

        var first = Assert.IsType<KeepEmbeddedLocationDecision>(
            LocationDecisionMaker.Decide(
                ParseResult(candidates.Reverse().ToList(), [secondIssue, firstIssue]),
                sidecar));
        var second = Assert.IsType<KeepEmbeddedLocationDecision>(
            LocationDecisionMaker.Decide(
                ParseResult(candidates, [firstIssue, secondIssue]),
                sidecar));

        Assert.Equal(first.EmbeddedGpsResult.Candidates, second.EmbeddedGpsResult.Candidates);
        Assert.Equal(first.EmbeddedGpsResult.Issues, second.EmbeddedGpsResult.Issues);
        Assert.Equal(
            first.EmbeddedLocationResult.Candidates,
            second.EmbeddedLocationResult.Candidates);
        Assert.Equal(first.EmbeddedLocationResult.Issues, second.EmbeddedLocationResult.Issues);
        Assert.Equal(first.SidecarComparisons, second.SidecarComparisons);
        Assert.Equal(
            ["GPS", "XMP-exif"],
            first.EmbeddedLocationResult.Candidates.Select(location => location.GroupName));
    }

    private static IReadOnlyList<EmbeddedGpsCandidate> CompleteLocations(
        double exifLatitude,
        double exifLongitude,
        double? exifAltitude = null,
        double? xmpLatitude = null,
        double? xmpLongitude = null,
        double? xmpAltitude = null)
    {
        var values = new List<EmbeddedGpsCandidate>();
        values.AddRange(Location("GPS", exifLatitude, exifLongitude, exifAltitude));

        if (xmpLatitude is not null && xmpLongitude is not null)
        {
            values.AddRange(Location(
                "XMP-exif",
                xmpLatitude.Value,
                xmpLongitude.Value,
                xmpAltitude));
        }

        return values.AsReadOnly();
    }

    private static IReadOnlyList<EmbeddedGpsCandidate> Location(
        string groupName,
        double latitude,
        double longitude,
        double? altitude = null)
    {
        var values = new List<EmbeddedGpsCandidate>
        {
            Gps(
                EmbeddedMetadataField.GpsLatitude,
                groupName,
                latitude,
                latitude < 0 ? "S" : "N"),
            Gps(
                EmbeddedMetadataField.GpsLongitude,
                groupName,
                longitude,
                longitude < 0 ? "W" : "E")
        };

        if (altitude is not null)
        {
            values.Add(Gps(
                EmbeddedMetadataField.GpsAltitude,
                groupName,
                altitude.Value,
                altitude < 0 ? "1" : "0"));
        }

        return values.AsReadOnly();
    }

    private static EmbeddedGpsCandidate Gps(
        EmbeddedMetadataField field,
        string groupName,
        double parsedValue,
        string? reference)
    {
        var source = new EmbeddedMetadataValue(
            field,
            groupName,
            TagName(field),
            parsedValue.ToString("R", CultureInfo.InvariantCulture),
            JsonValueKind.Number);
        var referenceValue = groupName == "GPS" && reference is not null
            ? new EmbeddedMetadataValue(
                ReferenceField(field),
                groupName,
                $"{TagName(field)}Ref",
                reference,
                JsonValueKind.String)
            : null;

        return new EmbeddedGpsCandidate(source, parsedValue, referenceValue);
    }

    private static EmbeddedGpsParsingIssue GpsIssue(
        string rawValue,
        string groupName = "GPS") =>
        new(
            EmbeddedGpsParsingIssueKind.MalformedNumber,
            new EmbeddedMetadataValue(
                EmbeddedMetadataField.GpsLatitude,
                groupName,
                "GPSLatitude",
                rawValue,
                JsonValueKind.String),
            null,
            "Synthetic invalid GPS value.");

    private static EmbeddedGpsParseResult ParseResult(
        IReadOnlyList<EmbeddedGpsCandidate>? candidates = null,
        IReadOnlyList<EmbeddedGpsParsingIssue>? issues = null) =>
        new(
            candidates ?? Array.Empty<EmbeddedGpsCandidate>(),
            issues ?? Array.Empty<EmbeddedGpsParsingIssue>());

    private static TakeoutSidecarMetadata Sidecar(
        double? latitude = null,
        double? longitude = null,
        double? altitude = null) =>
        new(
            Title: null,
            Description: null,
            CreationTime: null,
            PhotoTakenTime: null,
            Latitude: latitude,
            Longitude: longitude,
            Altitude: altitude,
            Url: null);

    private static string TagName(EmbeddedMetadataField field) =>
        field switch
        {
            EmbeddedMetadataField.GpsLatitude => "GPSLatitude",
            EmbeddedMetadataField.GpsLongitude => "GPSLongitude",
            EmbeddedMetadataField.GpsAltitude => "GPSAltitude",
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };

    private static EmbeddedMetadataField ReferenceField(EmbeddedMetadataField field) =>
        field switch
        {
            EmbeddedMetadataField.GpsLatitude => EmbeddedMetadataField.GpsLatitudeReference,
            EmbeddedMetadataField.GpsLongitude => EmbeddedMetadataField.GpsLongitudeReference,
            EmbeddedMetadataField.GpsAltitude => EmbeddedMetadataField.GpsAltitudeReference,
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };
}
