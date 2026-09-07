using System.Text.Json;

namespace PhotoMigration.Core.Tests;

public sealed class EmbeddedGpsParserTests
{
    [Theory]
    [InlineData(
        EmbeddedMetadataField.GpsLatitude,
        "GPSLatitude",
        EmbeddedMetadataField.GpsLatitudeReference,
        "GPSLatitudeRef",
        "N",
        12.5)]
    [InlineData(
        EmbeddedMetadataField.GpsLatitude,
        "GPSLatitude",
        EmbeddedMetadataField.GpsLatitudeReference,
        "GPSLatitudeRef",
        "S",
        -12.5)]
    [InlineData(
        EmbeddedMetadataField.GpsLongitude,
        "GPSLongitude",
        EmbeddedMetadataField.GpsLongitudeReference,
        "GPSLongitudeRef",
        "E",
        12.5)]
    [InlineData(
        EmbeddedMetadataField.GpsLongitude,
        "GPSLongitude",
        EmbeddedMetadataField.GpsLongitudeReference,
        "GPSLongitudeRef",
        "W",
        -12.5)]
    public void Parse_ExifCoordinatesApplyCardinalReference(
        EmbeddedMetadataField field,
        string tagName,
        EmbeddedMetadataField referenceField,
        string referenceTagName,
        string rawReference,
        double expectedValue)
    {
        var source = Value(field, "GPS", tagName, "12.5", JsonValueKind.Number);
        var reference = Value(
            referenceField,
            "GPS",
            referenceTagName,
            rawReference,
            JsonValueKind.String);

        var result = EmbeddedGpsParser.Parse([reference, source]);

        var candidate = Assert.Single(result.Candidates);
        Assert.Same(source, candidate.SourceValue);
        Assert.Equal(expectedValue, candidate.ParsedValue);
        Assert.Same(reference, candidate.ReferenceValue);
        Assert.Empty(result.Issues);
    }

    [Theory]
    [InlineData("0", 123.5)]
    [InlineData("1", -123.5)]
    public void Parse_ExifAltitudeAppliesSeaLevelReference(
        string rawReference,
        double expectedValue)
    {
        var source = Value(
            EmbeddedMetadataField.GpsAltitude,
            "GPS",
            "GPSAltitude",
            "123.5",
            JsonValueKind.Number);
        var reference = Value(
            EmbeddedMetadataField.GpsAltitudeReference,
            "GPS",
            "GPSAltitudeRef",
            rawReference,
            JsonValueKind.Number);

        var result = EmbeddedGpsParser.Parse([source, reference]);

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal(expectedValue, candidate.ParsedValue);
        Assert.Same(source, candidate.SourceValue);
        Assert.Same(reference, candidate.ReferenceValue);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void Parse_SignedXmpValuesArePreservedWithoutExifReferences()
    {
        var latitude = Value(
            EmbeddedMetadataField.GpsLatitude,
            "XMP-exif",
            "GPSLatitude",
            "-34.25",
            JsonValueKind.Number);
        var longitude = Value(
            EmbeddedMetadataField.GpsLongitude,
            "XMP-exif",
            "GPSLongitude",
            "-118.5",
            JsonValueKind.Number);
        var altitude = Value(
            EmbeddedMetadataField.GpsAltitude,
            "XMP-exif",
            "GPSAltitude",
            "-5.5",
            JsonValueKind.Number);

        var result = EmbeddedGpsParser.Parse([longitude, latitude, altitude]);

        Assert.Collection(
            result.Candidates,
            candidate => AssertCandidate(candidate, altitude, -5.5, null),
            candidate => AssertCandidate(candidate, latitude, -34.25, null),
            candidate => AssertCandidate(candidate, longitude, -118.5, null));
        Assert.Empty(result.Issues);
    }

    [Theory]
    [InlineData(EmbeddedMetadataField.GpsLatitude, "GPSLatitude", "-90", -90)]
    [InlineData(EmbeddedMetadataField.GpsLatitude, "GPSLatitude", "90", 90)]
    [InlineData(EmbeddedMetadataField.GpsLongitude, "GPSLongitude", "-180", -180)]
    [InlineData(EmbeddedMetadataField.GpsLongitude, "GPSLongitude", "180", 180)]
    public void Parse_CoordinateBoundaryIsValid(
        EmbeddedMetadataField field,
        string tagName,
        string rawValue,
        double expectedValue)
    {
        var source = Value(field, "XMP-exif", tagName, rawValue, JsonValueKind.Number);

        var result = EmbeddedGpsParser.Parse([source]);

        var candidate = Assert.Single(result.Candidates);
        AssertCandidate(candidate, source, expectedValue, null);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void Parse_ExifZeroCoordinatesRemainZero()
    {
        var latitude = Value(
            EmbeddedMetadataField.GpsLatitude,
            "GPS",
            "GPSLatitude",
            "0",
            JsonValueKind.Number);
        var latitudeReference = Value(
            EmbeddedMetadataField.GpsLatitudeReference,
            "GPS",
            "GPSLatitudeRef",
            "S",
            JsonValueKind.String);
        var longitude = Value(
            EmbeddedMetadataField.GpsLongitude,
            "GPS",
            "GPSLongitude",
            "0",
            JsonValueKind.Number);
        var longitudeReference = Value(
            EmbeddedMetadataField.GpsLongitudeReference,
            "GPS",
            "GPSLongitudeRef",
            "W",
            JsonValueKind.String);

        var result = EmbeddedGpsParser.Parse(
            [longitudeReference, latitude, longitude, latitudeReference]);

        Assert.Collection(
            result.Candidates,
            candidate => AssertCandidate(candidate, latitude, 0, latitudeReference),
            candidate => AssertCandidate(candidate, longitude, 0, longitudeReference));
        Assert.Empty(result.Issues);
    }

    [Theory]
    [InlineData("not-a-number", EmbeddedGpsParsingIssueKind.MalformedNumber)]
    [InlineData("NaN", EmbeddedGpsParsingIssueKind.NonFiniteNumber)]
    [InlineData("Infinity", EmbeddedGpsParsingIssueKind.NonFiniteNumber)]
    [InlineData("-Infinity", EmbeddedGpsParsingIssueKind.NonFiniteNumber)]
    public void Parse_MalformedAndNonFiniteNumbersBecomeIssues(
        string rawValue,
        EmbeddedGpsParsingIssueKind expectedKind)
    {
        var source = Value(
            EmbeddedMetadataField.GpsLatitude,
            "XMP-exif",
            "GPSLatitude",
            rawValue,
            JsonValueKind.String);

        var result = EmbeddedGpsParser.Parse([source]);

        Assert.Empty(result.Candidates);
        var issue = Assert.Single(result.Issues);
        Assert.Equal(expectedKind, issue.Kind);
        Assert.Same(source, issue.SourceValue);
        Assert.Null(issue.ReferenceValue);
    }

    [Theory]
    [InlineData(EmbeddedMetadataField.GpsLatitude, "GPSLatitude", "90.0001")]
    [InlineData(EmbeddedMetadataField.GpsLongitude, "GPSLongitude", "-180.0001")]
    public void Parse_OutOfRangeCoordinatesBecomeIssues(
        EmbeddedMetadataField field,
        string tagName,
        string rawValue)
    {
        var source = Value(field, "XMP-exif", tagName, rawValue, JsonValueKind.Number);

        var result = EmbeddedGpsParser.Parse([source]);

        Assert.Empty(result.Candidates);
        var issue = Assert.Single(result.Issues);
        Assert.Equal(EmbeddedGpsParsingIssueKind.OutOfRange, issue.Kind);
        Assert.Same(source, issue.SourceValue);
    }

    [Fact]
    public void Parse_MissingAndInvalidExifReferencesBecomeIssues()
    {
        var missingReference = Value(
            EmbeddedMetadataField.GpsLatitude,
            "GPS",
            "GPSLatitude",
            "10",
            JsonValueKind.Number);
        var invalidReferenceSource = Value(
            EmbeddedMetadataField.GpsLongitude,
            "GPS",
            "GPSLongitude",
            "20",
            JsonValueKind.Number);
        var invalidReference = Value(
            EmbeddedMetadataField.GpsLongitudeReference,
            "GPS",
            "GPSLongitudeRef",
            "Q",
            JsonValueKind.String);

        var result = EmbeddedGpsParser.Parse(
            [invalidReference, invalidReferenceSource, missingReference]);

        Assert.Empty(result.Candidates);
        Assert.Collection(
            result.Issues,
            issue =>
            {
                Assert.Equal(EmbeddedGpsParsingIssueKind.MissingReference, issue.Kind);
                Assert.Same(missingReference, issue.SourceValue);
                Assert.Null(issue.ReferenceValue);
            },
            issue =>
            {
                Assert.Equal(EmbeddedGpsParsingIssueKind.InvalidReference, issue.Kind);
                Assert.Same(invalidReferenceSource, issue.SourceValue);
                Assert.Same(invalidReference, issue.ReferenceValue);
            });
    }

    [Theory]
    [InlineData(
        EmbeddedMetadataField.GpsLatitude,
        "GPSLatitude",
        EmbeddedMetadataField.GpsLatitudeReference,
        "GPSLatitudeRef",
        "N")]
    [InlineData(
        EmbeddedMetadataField.GpsLongitude,
        "GPSLongitude",
        EmbeddedMetadataField.GpsLongitudeReference,
        "GPSLongitudeRef",
        "E")]
    [InlineData(
        EmbeddedMetadataField.GpsAltitude,
        "GPSAltitude",
        EmbeddedMetadataField.GpsAltitudeReference,
        "GPSAltitudeRef",
        "0")]
    public void Parse_ConflictingNegativeSignAndPositiveReferenceBecomesIssue(
        EmbeddedMetadataField field,
        string tagName,
        EmbeddedMetadataField referenceField,
        string referenceTagName,
        string rawReference)
    {
        var source = Value(field, "GPS", tagName, "-10", JsonValueKind.Number);
        var reference = Value(
            referenceField,
            "GPS",
            referenceTagName,
            rawReference,
            JsonValueKind.String);

        var result = EmbeddedGpsParser.Parse([source, reference]);

        Assert.Empty(result.Candidates);
        var issue = Assert.Single(result.Issues);
        Assert.Equal(
            EmbeddedGpsParsingIssueKind.ConflictingSignAndReference,
            issue.Kind);
        Assert.Same(source, issue.SourceValue);
        Assert.Same(reference, issue.ReferenceValue);
    }

    [Fact]
    public void Parse_MultipleCandidatesHaveDeterministicOrdinalOrder()
    {
        var xmpLatitude = Value(
            EmbeddedMetadataField.GpsLatitude,
            "XMP-exif",
            "GPSLatitude",
            "3",
            JsonValueKind.Number);
        var exifLongitude = Value(
            EmbeddedMetadataField.GpsLongitude,
            "GPS",
            "GPSLongitude",
            "2",
            JsonValueKind.Number);
        var longitudeReference = Value(
            EmbeddedMetadataField.GpsLongitudeReference,
            "GPS",
            "GPSLongitudeRef",
            "W",
            JsonValueKind.String);
        var exifLatitude = Value(
            EmbeddedMetadataField.GpsLatitude,
            "GPS",
            "GPSLatitude",
            "1",
            JsonValueKind.Number);
        var latitudeReference = Value(
            EmbeddedMetadataField.GpsLatitudeReference,
            "GPS",
            "GPSLatitudeRef",
            "N",
            JsonValueKind.String);

        var first = EmbeddedGpsParser.Parse(
            [xmpLatitude, exifLongitude, latitudeReference, exifLatitude, longitudeReference]);
        var second = EmbeddedGpsParser.Parse(
            [longitudeReference, exifLatitude, latitudeReference, exifLongitude, xmpLatitude]);

        Assert.Equal(first.Candidates, second.Candidates);
        Assert.Collection(
            first.Candidates,
            candidate => AssertCandidate(candidate, exifLatitude, 1, latitudeReference),
            candidate => AssertCandidate(candidate, exifLongitude, -2, longitudeReference),
            candidate => AssertCandidate(candidate, xmpLatitude, 3, null));
        Assert.Empty(first.Issues);
        Assert.Empty(second.Issues);
    }

    [Fact]
    public void Parse_EmptyInputReturnsEmptyResult()
    {
        var result = EmbeddedGpsParser.Parse([]);

        Assert.Empty(result.Candidates);
        Assert.Empty(result.Issues);
    }

    private static void AssertCandidate(
        EmbeddedGpsCandidate candidate,
        EmbeddedMetadataValue source,
        double expectedValue,
        EmbeddedMetadataValue? reference)
    {
        Assert.Same(source, candidate.SourceValue);
        Assert.Equal(expectedValue, candidate.ParsedValue);
        Assert.Same(reference, candidate.ReferenceValue);
    }

    private static EmbeddedMetadataValue Value(
        EmbeddedMetadataField field,
        string groupName,
        string tagName,
        string rawValue,
        JsonValueKind valueKind) =>
        new(field, groupName, tagName, rawValue, valueKind);
}
