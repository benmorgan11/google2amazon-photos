using System.Text;
using PhotoMigration.Core;

namespace PhotoMigration.Core.Tests;

public sealed class TakeoutSidecarParserTests
{
    [Fact]
    public void Parse_ReturnsAllSupportedFieldsAndAcceptsBothTimestampRepresentations()
    {
        using var sidecar = new TemporarySidecar(
            """
            {
              "title": "IMG_1234.jpg",
              "description": "A synthetic photo",
              "creationTime": { "timestamp": "1700000000" },
              "photoTakenTime": { "timestamp": 1600000000 },
              "geoData": {
                "latitude": 47.6062,
                "longitude": -122.3321,
                "altitude": 52.5
              },
              "url": "https://photos.example.invalid/item",
              "unknownField": { "nested": true }
            }
            """);

        var metadata = TakeoutSidecarParser.Parse(sidecar.Path);

        Assert.Equal("IMG_1234.jpg", metadata.Title);
        Assert.Equal("A synthetic photo", metadata.Description);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), metadata.CreationTime);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1600000000), metadata.PhotoTakenTime);
        Assert.Equal(47.6062, metadata.Latitude);
        Assert.Equal(-122.3321, metadata.Longitude);
        Assert.Equal(52.5, metadata.Altitude);
        Assert.Equal("https://photos.example.invalid/item", metadata.Url);
    }

    [Fact]
    public void Parse_AllowsMissingOptionalFieldsAndIgnoresUnknownFields()
    {
        using var sidecar = new TemporarySidecar(
            """
            {
              "imageViews": "12",
              "googlePhotosOrigin": { "mobileUpload": {} }
            }
            """);

        var metadata = TakeoutSidecarParser.Parse(sidecar.Path);

        Assert.Equal(new TakeoutSidecarMetadata(null, null, null, null, null, null, null, null), metadata);
    }

    [Theory]
    [InlineData("""{ "creationTime": { "timestamp": "not-a-timestamp" } }""")]
    [InlineData("""{ "photoTakenTime": { "timestamp": 253402300800 } }""")]
    public void Parse_RejectsMalformedOrOutOfRangeTimestamps(string json)
    {
        using var sidecar = new TemporarySidecar(json);

        var exception = Assert.Throws<TakeoutSidecarParseException>(
            () => TakeoutSidecarParser.Parse(sidecar.Path));

        Assert.Equal(sidecar.Path, exception.SidecarPath);
        Assert.Contains(sidecar.Path, exception.Message);
        Assert.Contains("timestamp", exception.Message);
    }

    [Theory]
    [InlineData("""{ "geoData": { "latitude": "north" } }""")]
    [InlineData("""{ "geoData": { "longitude": 181 } }""")]
    public void Parse_RejectsMalformedCoordinates(string json)
    {
        using var sidecar = new TemporarySidecar(json);

        var exception = Assert.Throws<TakeoutSidecarParseException>(
            () => TakeoutSidecarParser.Parse(sidecar.Path));

        Assert.Equal(sidecar.Path, exception.SidecarPath);
        Assert.Contains("coordinate", exception.Message);
    }

    [Fact]
    public void Parse_RejectsMalformedJsonWithoutIncludingItsContentsInTheMessage()
    {
        const string privateContents = "private synthetic title";
        using var sidecar = new TemporarySidecar($"{{ \"title\": \"{privateContents}\"");

        var exception = Assert.Throws<TakeoutSidecarParseException>(
            () => TakeoutSidecarParser.Parse(sidecar.Path));

        Assert.Equal(sidecar.Path, exception.SidecarPath);
        Assert.Contains(sidecar.Path, exception.Message);
        Assert.DoesNotContain(privateContents, exception.Message);
    }

    [Fact]
    public void Parse_RejectsANonObjectRoot()
    {
        using var sidecar = new TemporarySidecar("[]");

        var exception = Assert.Throws<TakeoutSidecarParseException>(
            () => TakeoutSidecarParser.Parse(sidecar.Path));

        Assert.Equal(sidecar.Path, exception.SidecarPath);
        Assert.Contains("root must be an object", exception.Message);
    }

    [Fact]
    public void Parse_PreservesZeroValuedCoordinates()
    {
        using var sidecar = new TemporarySidecar(
            """
            {
              "geoData": {
                "latitude": 0,
                "longitude": 0.0,
                "altitude": 0
              }
            }
            """);

        var metadata = TakeoutSidecarParser.Parse(sidecar.Path);

        Assert.Equal(0d, metadata.Latitude);
        Assert.Equal(0d, metadata.Longitude);
        Assert.Equal(0d, metadata.Altitude);
    }

    [Fact]
    public void Parse_DoesNotModifyTheSourceFile()
    {
        using var sidecar = new TemporarySidecar("""{ "title": "unchanged.jpg" }""");
        var contentsBefore = File.ReadAllBytes(sidecar.Path);
        var lastWriteBefore = File.GetLastWriteTimeUtc(sidecar.Path);

        _ = TakeoutSidecarParser.Parse(sidecar.Path);

        Assert.Equal(contentsBefore, File.ReadAllBytes(sidecar.Path));
        Assert.Equal(lastWriteBefore, File.GetLastWriteTimeUtc(sidecar.Path));
    }

    private sealed class TemporarySidecar : IDisposable
    {
        private readonly string _directoryPath;

        public TemporarySidecar(string contents)
        {
            _directoryPath = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"PhotoMigration.Tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_directoryPath);

            Path = System.IO.Path.Combine(_directoryPath, "sidecar.json");
            File.WriteAllText(Path, contents, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        public string Path { get; }

        public void Dispose()
        {
            Directory.Delete(_directoryPath, recursive: true);
        }
    }
}
