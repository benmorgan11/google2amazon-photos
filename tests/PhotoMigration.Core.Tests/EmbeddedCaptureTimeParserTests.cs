using System.Text.Json;

namespace PhotoMigration.Core.Tests;

public sealed class EmbeddedCaptureTimeParserTests
{
    [Fact]
    public void Parse_DateWithoutOffsetPreservesUnspecifiedTimeAndSource()
    {
        var source = Value(
            EmbeddedMetadataField.CaptureDateTime,
            "XMP-exif",
            "DateTimeOriginal",
            "2020:01:02 03:04:05");

        var result = EmbeddedCaptureTimeParser.Parse([source]);

        var candidate = Assert.Single(result.Candidates);
        Assert.Same(source, candidate.SourceValue);
        Assert.Equal(
            new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Unspecified),
            candidate.ParsedDateTime);
        Assert.Equal(DateTimeKind.Unspecified, candidate.ParsedDateTime.Kind);
        Assert.Null(candidate.ParsedDateTimeOffset);
        Assert.Equal(EmbeddedCaptureTimeOffsetSource.Unknown, candidate.OffsetSource);
        Assert.Null(candidate.OffsetValue);
        Assert.Empty(result.Issues);
    }

    [Theory]
    [InlineData("2020:01:02 03:04:05Z", 0)]
    [InlineData("2020:01:02 03:04:05+05:30", 330)]
    [InlineData("2020:01:02 03:04:05-07:00", -420)]
    [InlineData("2020:01:02 03:04:05+0530", 330)]
    [InlineData("2020:01:02 03:04:05-0700", -420)]
    public void Parse_DateWithIncludedOffsetReturnsDateTimeOffset(
        string rawValue,
        int expectedOffsetMinutes)
    {
        var result = EmbeddedCaptureTimeParser.Parse(
            [Value(EmbeddedMetadataField.CaptureDateTime, "XMP-exif", "CreateDate", rawValue)]);

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal(
            TimeSpan.FromMinutes(expectedOffsetMinutes),
            candidate.ParsedDateTimeOffset!.Value.Offset);
        Assert.Equal(
            EmbeddedCaptureTimeOffsetSource.IncludedInDateValue,
            candidate.OffsetSource);
        Assert.Null(candidate.OffsetValue);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void Parse_FractionalSecondsArePreserved()
    {
        var result = EmbeddedCaptureTimeParser.Parse(
            [Value(
                EmbeddedMetadataField.CaptureDateTime,
                "XMP-exif",
                "DateTimeOriginal",
                "2020:01:02 03:04:05.1234567")]);

        var candidate = Assert.Single(result.Candidates);
        var expected = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Unspecified)
            .AddTicks(1_234_567);
        Assert.Equal(expected, candidate.ParsedDateTime);
        Assert.Null(candidate.ParsedDateTimeOffset);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void Parse_SeparateExifOffsetIsAppliedOnlyToExifOriginalDate()
    {
        var xmpDate = Value(
            EmbeddedMetadataField.CaptureDateTime,
            "XMP-exif",
            "DateTimeOriginal",
            "2021:02:03 04:05:06");
        var offset = Value(
            EmbeddedMetadataField.CaptureTimezoneOffset,
            "ExifIFD",
            "OffsetTimeOriginal",
            "-07:00");
        var exifDate = Value(
            EmbeddedMetadataField.CaptureDateTime,
            "ExifIFD",
            "DateTimeOriginal",
            "2020:01:02 03:04:05");

        var result = EmbeddedCaptureTimeParser.Parse([xmpDate, offset, exifDate]);

        Assert.Collection(
            result.Candidates,
            candidate =>
            {
                Assert.Same(exifDate, candidate.SourceValue);
                Assert.Same(offset, candidate.OffsetValue);
                Assert.Equal(
                    new DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.FromHours(-7)),
                    candidate.ParsedDateTimeOffset);
                Assert.Equal(
                    EmbeddedCaptureTimeOffsetSource.OffsetTimeOriginal,
                    candidate.OffsetSource);
            },
            candidate =>
            {
                Assert.Same(xmpDate, candidate.SourceValue);
                Assert.Null(candidate.ParsedDateTimeOffset);
                Assert.Equal(EmbeddedCaptureTimeOffsetSource.Unknown, candidate.OffsetSource);
                Assert.Null(candidate.OffsetValue);
            });
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void Parse_IncludedOffsetIsNotReplacedBySeparateExifOffset()
    {
        var date = Value(
            EmbeddedMetadataField.CaptureDateTime,
            "ExifIFD",
            "DateTimeOriginal",
            "2020:01:02 03:04:05Z");
        var offset = Value(
            EmbeddedMetadataField.CaptureTimezoneOffset,
            "ExifIFD",
            "OffsetTimeOriginal",
            "+05:30");

        var result = EmbeddedCaptureTimeParser.Parse([offset, date]);

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal(TimeSpan.Zero, candidate.ParsedDateTimeOffset!.Value.Offset);
        Assert.Equal(
            EmbeddedCaptureTimeOffsetSource.IncludedInDateValue,
            candidate.OffsetSource);
        Assert.Null(candidate.OffsetValue);
    }

    [Fact]
    public void Parse_InvalidDateIsRetainedAsIssue()
    {
        var source = Value(
            EmbeddedMetadataField.CaptureDateTime,
            "ExifIFD",
            "DateTimeOriginal",
            "not a date");

        var result = EmbeddedCaptureTimeParser.Parse([source]);

        Assert.Empty(result.Candidates);
        var issue = Assert.Single(result.Issues);
        Assert.Equal(EmbeddedCaptureTimeParsingIssueKind.InvalidDateValue, issue.Kind);
        Assert.Same(source, issue.SourceValue);
        Assert.Contains("not a supported ExifTool date", issue.Message);
    }

    [Fact]
    public void Parse_InvalidSeparateOffsetIsRetainedAndDateRemainsUnzoned()
    {
        var date = Value(
            EmbeddedMetadataField.CaptureDateTime,
            "ExifIFD",
            "DateTimeOriginal",
            "2020:01:02 03:04:05");
        var offset = Value(
            EmbeddedMetadataField.CaptureTimezoneOffset,
            "ExifIFD",
            "OffsetTimeOriginal",
            "+15:00");

        var result = EmbeddedCaptureTimeParser.Parse([offset, date]);

        var candidate = Assert.Single(result.Candidates);
        Assert.Null(candidate.ParsedDateTimeOffset);
        Assert.Equal(EmbeddedCaptureTimeOffsetSource.Unknown, candidate.OffsetSource);
        var issue = Assert.Single(result.Issues);
        Assert.Equal(EmbeddedCaptureTimeParsingIssueKind.InvalidOffsetValue, issue.Kind);
        Assert.Same(offset, issue.SourceValue);
    }

    [Fact]
    public void Parse_MultipleDatesHaveDeterministicOrdinalOrder()
    {
        var laterGroup = Value(
            EmbeddedMetadataField.CaptureDateTime,
            "XMP-exif",
            "DateTimeOriginal",
            "2021:01:01 00:00:00");
        var earlierGroup = Value(
            EmbeddedMetadataField.CaptureDateTime,
            "ExifIFD",
            "CreateDate",
            "2020:01:01 00:00:00");

        var first = EmbeddedCaptureTimeParser.Parse([laterGroup, earlierGroup]);
        var second = EmbeddedCaptureTimeParser.Parse([earlierGroup, laterGroup]);

        Assert.Equal(first.Candidates, second.Candidates);
        Assert.Collection(
            first.Candidates,
            candidate => Assert.Same(earlierGroup, candidate.SourceValue),
            candidate => Assert.Same(laterGroup, candidate.SourceValue));
    }

    [Fact]
    public void Parse_QuickTimeDatesPreserveExplicitAndUnknownTimezoneStatus()
    {
        var unknownTimezone = Value(
            EmbeddedMetadataField.CaptureDateTime,
            "QuickTime",
            "CreateDate",
            "2020:01:02 03:04:05");
        var explicitTimezone = Value(
            EmbeddedMetadataField.CaptureDateTime,
            "Keys",
            "CreationDate",
            "2020:01:02 03:04:05-07:00");

        var result = EmbeddedCaptureTimeParser.Parse(
            [unknownTimezone, explicitTimezone]);

        Assert.Collection(
            result.Candidates,
            candidate =>
            {
                Assert.Same(explicitTimezone, candidate.SourceValue);
                Assert.Equal(
                    EmbeddedCaptureTimeOffsetSource.IncludedInDateValue,
                    candidate.OffsetSource);
                Assert.Equal(
                    TimeSpan.FromHours(-7),
                    candidate.ParsedDateTimeOffset!.Value.Offset);
            },
            candidate =>
            {
                Assert.Same(unknownTimezone, candidate.SourceValue);
                Assert.Equal(
                    EmbeddedCaptureTimeOffsetSource.Unknown,
                    candidate.OffsetSource);
                Assert.Equal(DateTimeKind.Unspecified, candidate.ParsedDateTime.Kind);
                Assert.Null(candidate.ParsedDateTimeOffset);
            });
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void Parse_NoCaptureTimeValuesReturnsEmptyResult()
    {
        var result = EmbeddedCaptureTimeParser.Parse(
            [Value(EmbeddedMetadataField.GpsLatitude, "GPS", "GPSLatitude", "34.25")]);

        Assert.Empty(result.Candidates);
        Assert.Empty(result.Issues);
    }

    private static EmbeddedMetadataValue Value(
        EmbeddedMetadataField field,
        string groupName,
        string tagName,
        string rawValue) =>
        new(field, groupName, tagName, rawValue, JsonValueKind.String);
}
