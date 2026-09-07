using System.Text.Json;

namespace PhotoMigration.Core.Tests;

public sealed class EmbeddedLocationBuilderTests
{
    [Fact]
    public void Build_CompleteExifLocationPreservesAllProvenance()
    {
        var latitude = Candidate(
            EmbeddedMetadataField.GpsLatitude,
            "GPS",
            -34.25,
            "34.25",
            "S");
        var longitude = Candidate(
            EmbeddedMetadataField.GpsLongitude,
            "GPS",
            118.5,
            "118.5",
            "E");
        var altitude = Candidate(
            EmbeddedMetadataField.GpsAltitude,
            "GPS",
            -12.5,
            "12.5",
            "1");

        var result = EmbeddedLocationBuilder.Build([altitude, longitude, latitude]);

        var location = Assert.Single(result.Candidates);
        Assert.Equal("GPS", location.GroupName);
        Assert.Same(latitude, location.Latitude);
        Assert.Same(longitude, location.Longitude);
        Assert.Same(altitude, location.Altitude);
        Assert.Same(latitude.ReferenceValue, location.Latitude.ReferenceValue);
        Assert.Same(longitude.ReferenceValue, location.Longitude.ReferenceValue);
        Assert.Same(altitude.ReferenceValue, location.Altitude!.ReferenceValue);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void Build_CompleteXmpLocationPreservesSignedValues()
    {
        var latitude = Candidate(
            EmbeddedMetadataField.GpsLatitude,
            "XMP-exif",
            -34.25,
            "-34.25");
        var longitude = Candidate(
            EmbeddedMetadataField.GpsLongitude,
            "XMP-exif",
            -118.5,
            "-118.5");
        var altitude = Candidate(
            EmbeddedMetadataField.GpsAltitude,
            "XMP-exif",
            25,
            "25");

        var result = EmbeddedLocationBuilder.Build([longitude, latitude, altitude]);

        var location = Assert.Single(result.Candidates);
        Assert.Equal("XMP-exif", location.GroupName);
        Assert.Same(latitude, location.Latitude);
        Assert.Same(longitude, location.Longitude);
        Assert.Same(altitude, location.Altitude);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void Build_LocationWithoutAltitudeIsComplete()
    {
        var latitude = Candidate(EmbeddedMetadataField.GpsLatitude, "GPS", 10, "10", "N");
        var longitude = Candidate(EmbeddedMetadataField.GpsLongitude, "GPS", 20, "20", "E");

        var result = EmbeddedLocationBuilder.Build([latitude, longitude]);

        var location = Assert.Single(result.Candidates);
        Assert.Same(latitude, location.Latitude);
        Assert.Same(longitude, location.Longitude);
        Assert.Null(location.Altitude);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void Build_ZeroCoordinatesArePreserved()
    {
        var latitude = Candidate(EmbeddedMetadataField.GpsLatitude, "GPS", 0, "0", "S");
        var longitude = Candidate(EmbeddedMetadataField.GpsLongitude, "GPS", 0, "0", "W");

        var result = EmbeddedLocationBuilder.Build([longitude, latitude]);

        var location = Assert.Single(result.Candidates);
        Assert.Equal(0, location.Latitude.ParsedValue);
        Assert.Equal(0, location.Longitude.ParsedValue);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void Build_DoesNotPairCoordinatesAcrossGroups()
    {
        var exifLatitude = Candidate(
            EmbeddedMetadataField.GpsLatitude,
            "GPS",
            10,
            "10",
            "N");
        var xmpLongitude = Candidate(
            EmbeddedMetadataField.GpsLongitude,
            "XMP-exif",
            20,
            "20");

        var result = EmbeddedLocationBuilder.Build([xmpLongitude, exifLatitude]);

        Assert.Empty(result.Candidates);
        Assert.Equal(2, result.Issues.Count);
        Assert.Contains(
            result.Issues,
            issue => issue.GroupName == "GPS"
                     && issue.Kind == EmbeddedLocationIssueKind.LatitudeWithoutLongitude);
        Assert.Contains(
            result.Issues,
            issue => issue.GroupName == "XMP-exif"
                     && issue.Kind == EmbeddedLocationIssueKind.LongitudeWithoutLatitude);
    }

    [Fact]
    public void Build_IncompleteCoordinateGroupsReturnTypedIssues()
    {
        var latitude = Candidate(EmbeddedMetadataField.GpsLatitude, "latitude-only", 1, "1");
        var longitude = Candidate(EmbeddedMetadataField.GpsLongitude, "longitude-only", 2, "2");

        var result = EmbeddedLocationBuilder.Build([longitude, latitude]);

        Assert.Empty(result.Candidates);
        Assert.Collection(
            result.Issues,
            issue =>
            {
                Assert.Equal("latitude-only", issue.GroupName);
                Assert.Equal(
                    EmbeddedLocationIssueKind.LatitudeWithoutLongitude,
                    issue.Kind);
                Assert.Same(latitude, Assert.Single(issue.Values));
            },
            issue =>
            {
                Assert.Equal("longitude-only", issue.GroupName);
                Assert.Equal(
                    EmbeddedLocationIssueKind.LongitudeWithoutLatitude,
                    issue.Kind);
                Assert.Same(longitude, Assert.Single(issue.Values));
            });
    }

    [Fact]
    public void Build_AltitudeWithoutCoordinatePairReturnsTypedIssue()
    {
        var altitude = Candidate(EmbeddedMetadataField.GpsAltitude, "GPS", 50, "50", "0");

        var result = EmbeddedLocationBuilder.Build([altitude]);

        Assert.Empty(result.Candidates);
        var issue = Assert.Single(result.Issues);
        Assert.Equal(EmbeddedLocationIssueKind.AltitudeWithoutCoordinatePair, issue.Kind);
        Assert.Equal("GPS", issue.GroupName);
        Assert.Same(altitude, Assert.Single(issue.Values));
    }

    [Fact]
    public void Build_MultipleValuesProduceEveryCombination()
    {
        var latitude1 = Candidate(EmbeddedMetadataField.GpsLatitude, "GPS", 1, "1", "N");
        var latitude2 = Candidate(EmbeddedMetadataField.GpsLatitude, "GPS", 2, "2", "N");
        var longitude3 = Candidate(EmbeddedMetadataField.GpsLongitude, "GPS", 3, "3", "E");
        var longitude4 = Candidate(EmbeddedMetadataField.GpsLongitude, "GPS", 4, "4", "E");
        var altitude10 = Candidate(EmbeddedMetadataField.GpsAltitude, "GPS", 10, "10", "0");
        var altitude20 = Candidate(EmbeddedMetadataField.GpsAltitude, "GPS", 20, "20", "0");

        var result = EmbeddedLocationBuilder.Build(
            [longitude4, altitude20, latitude2, longitude3, altitude10, latitude1]);

        Assert.Equal(8, result.Candidates.Count);
        Assert.Equal(
            [
                (1d, 3d, 10d),
                (1d, 3d, 20d),
                (1d, 4d, 10d),
                (1d, 4d, 20d),
                (2d, 3d, 10d),
                (2d, 3d, 20d),
                (2d, 4d, 10d),
                (2d, 4d, 20d)
            ],
            result.Candidates.Select(location => (
                location.Latitude.ParsedValue,
                location.Longitude.ParsedValue,
                location.Altitude!.ParsedValue)));
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void Build_CombinedQuickTimeLocationsRemainAtomicWithinOneGroup()
    {
        var firstSource = new EmbeddedMetadataValue(
            EmbeddedMetadataField.GpsCoordinates,
            "Keys",
            "GPSCoordinates",
            "+1+2/",
            JsonValueKind.String);
        var secondSource = new EmbeddedMetadataValue(
            EmbeddedMetadataField.GpsCoordinates,
            "Keys",
            "GPSCoordinates",
            "+3+4/",
            JsonValueKind.String);
        var firstLatitude = CombinedCandidate(
            firstSource,
            EmbeddedMetadataField.GpsLatitude,
            1);
        var firstLongitude = CombinedCandidate(
            firstSource,
            EmbeddedMetadataField.GpsLongitude,
            2);
        var secondLatitude = CombinedCandidate(
            secondSource,
            EmbeddedMetadataField.GpsLatitude,
            3);
        var secondLongitude = CombinedCandidate(
            secondSource,
            EmbeddedMetadataField.GpsLongitude,
            4);

        var result = EmbeddedLocationBuilder.Build(
            [secondLongitude, firstLatitude, secondLatitude, firstLongitude]);

        Assert.Equal(2, result.Candidates.Count);
        Assert.Equal(
            [(1d, 2d), (3d, 4d)],
            result.Candidates.Select(location => (
                location.Latitude.ParsedValue,
                location.Longitude.ParsedValue)));
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void Build_ResultsAreDeterministicForShuffledGroups()
    {
        var xmpLatitude = Candidate(EmbeddedMetadataField.GpsLatitude, "XMP-exif", 3, "3");
        var xmpLongitude = Candidate(EmbeddedMetadataField.GpsLongitude, "XMP-exif", 4, "4");
        var exifLatitude = Candidate(
            EmbeddedMetadataField.GpsLatitude,
            "GPS",
            1,
            "1",
            "N");
        var exifLongitude = Candidate(
            EmbeddedMetadataField.GpsLongitude,
            "GPS",
            2,
            "2",
            "E");

        var first = EmbeddedLocationBuilder.Build(
            [xmpLongitude, exifLatitude, xmpLatitude, exifLongitude]);
        var second = EmbeddedLocationBuilder.Build(
            [exifLongitude, xmpLatitude, exifLatitude, xmpLongitude]);

        Assert.Equal(first.Candidates, second.Candidates);
        Assert.Equal(first.Issues, second.Issues);
        Assert.Collection(
            first.Candidates,
            location => Assert.Equal("GPS", location.GroupName),
            location => Assert.Equal("XMP-exif", location.GroupName));
    }

    [Fact]
    public void Build_EmptyInputReturnsEmptyResult()
    {
        var result = EmbeddedLocationBuilder.Build([]);

        Assert.Empty(result.Candidates);
        Assert.Empty(result.Issues);
    }

    private static EmbeddedGpsCandidate Candidate(
        EmbeddedMetadataField field,
        string groupName,
        double parsedValue,
        string rawValue,
        string? rawReference = null)
    {
        var source = new EmbeddedMetadataValue(
            field,
            groupName,
            TagName(field),
            rawValue,
            JsonValueKind.Number);
        var reference = rawReference is null
            ? null
            : new EmbeddedMetadataValue(
                ReferenceField(field),
                groupName,
                $"{TagName(field)}Ref",
                rawReference,
                JsonValueKind.String);

        return new EmbeddedGpsCandidate(source, parsedValue, reference);
    }

    private static EmbeddedGpsCandidate CombinedCandidate(
        EmbeddedMetadataValue source,
        EmbeddedMetadataField semanticField,
        double parsedValue) =>
        new(source, parsedValue, null)
        {
            SemanticField = semanticField
        };

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
