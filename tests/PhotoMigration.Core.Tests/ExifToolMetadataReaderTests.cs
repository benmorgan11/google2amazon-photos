using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace PhotoMigration.Core.Tests;

[Collection(ExternalProcessTestCollection.Name)]
public sealed class ExifToolMetadataReaderTests
{
    [Fact]
    public void Read_SelectedTagsPreserveGroupsRawValuesDiagnosticsAndSourceFile()
    {
        if (!SupportsPosixScripts())
        {
            return;
        }

        using var fixture = new TemporaryFixture();
        var mediaPath = fixture.WriteMedia("photos with spaces/photo.JPG", [1, 2, 3, 4]);
        var lastWriteTime = DateTime.UtcNow.AddDays(-2);
        File.SetLastWriteTimeUtc(mediaPath, lastWriteTime);
        var originalContents = File.ReadAllBytes(mediaPath);
        var originalLastWriteTime = File.GetLastWriteTimeUtc(mediaPath);
        var executablePath = fixture.CreateExecutable(
            "tools with spaces/fake exiftool",
            SuccessfulScript(
                """
                [{
                  "SourceFile": "ignored.jpg",
                  "ExifIFD:DateTimeOriginal": "2020:01:02 03:04:05",
                  "ExifIFD:OffsetTimeOriginal": "-07:00",
                  "XMP-exif:GPSLatitude": -34.25,
                  "GPS:GPSLatitude": 34.25,
                  "GPS:GPSLatitudeRef": "N",
                  "GPS:GPSLongitude": 118.25,
                  "GPS:GPSLongitudeRef": "W",
                  "GPS:GPSAltitude": 123.5,
                  "GPS:GPSAltitudeRef": 0,
                  "Warning": "synthetic warning",
                  "Error": "synthetic diagnostic"
                }]
                """,
                "synthetic standard error"));

        var result = Assert.IsType<EmbeddedMetadataReadSuccessResult>(
            ExifToolMetadataReader.Read(mediaPath, executablePath));

        Assert.Equal(Path.GetFullPath(mediaPath), result.MediaPath);
        Assert.Equal(Path.GetFullPath(executablePath), result.ExifToolExecutablePath);
        Assert.Collection(
            result.Values,
            value => AssertValue(
                value,
                EmbeddedMetadataField.CaptureDateTime,
                "ExifIFD",
                "DateTimeOriginal",
                "2020:01:02 03:04:05",
                JsonValueKind.String),
            value => AssertValue(
                value,
                EmbeddedMetadataField.CaptureTimezoneOffset,
                "ExifIFD",
                "OffsetTimeOriginal",
                "-07:00",
                JsonValueKind.String),
            value => AssertValue(
                value,
                EmbeddedMetadataField.GpsAltitude,
                "GPS",
                "GPSAltitude",
                "123.5",
                JsonValueKind.Number),
            value => AssertValue(
                value,
                EmbeddedMetadataField.GpsAltitudeReference,
                "GPS",
                "GPSAltitudeRef",
                "0",
                JsonValueKind.Number),
            value => AssertValue(
                value,
                EmbeddedMetadataField.GpsLatitude,
                "GPS",
                "GPSLatitude",
                "34.25",
                JsonValueKind.Number),
            value => AssertValue(
                value,
                EmbeddedMetadataField.GpsLatitudeReference,
                "GPS",
                "GPSLatitudeRef",
                "N",
                JsonValueKind.String),
            value => AssertValue(
                value,
                EmbeddedMetadataField.GpsLongitude,
                "GPS",
                "GPSLongitude",
                "118.25",
                JsonValueKind.Number),
            value => AssertValue(
                value,
                EmbeddedMetadataField.GpsLongitudeReference,
                "GPS",
                "GPSLongitudeRef",
                "W",
                JsonValueKind.String),
            value => AssertValue(
                value,
                EmbeddedMetadataField.GpsLatitude,
                "XMP-exif",
                "GPSLatitude",
                "-34.25",
                JsonValueKind.Number));
        Assert.Equal(["synthetic warning"], result.Warnings);
        Assert.Equal(["synthetic diagnostic"], result.Errors);
        Assert.Equal("synthetic standard error", result.StandardError);
        Assert.Equal(originalContents, File.ReadAllBytes(mediaPath));
        Assert.Equal(originalLastWriteTime, File.GetLastWriteTimeUtc(mediaPath));
    }

    [Fact]
    public void Read_EmptyMetadataIsSuccessfulForMixedCaseJpegExtension()
    {
        if (!SupportsPosixScripts())
        {
            return;
        }

        using var fixture = new TemporaryFixture();
        var mediaPath = fixture.WriteMedia("empty.JpEg", [1]);
        var executablePath = fixture.CreateExecutable(
            "exiftool",
            SuccessfulScript("[{\"SourceFile\":\"ignored.jpeg\"}]"));

        var result = Assert.IsType<EmbeddedMetadataReadSuccessResult>(
            ExifToolMetadataReader.Read(mediaPath, executablePath));

        Assert.Empty(result.Values);
        Assert.Empty(result.Warnings);
        Assert.Empty(result.Errors);
        Assert.Empty(result.StandardError);
    }

    [Theory]
    [InlineData("photos with spaces/photo.heic")]
    [InlineData("photos with spaces/photo.HeIf")]
    public void Read_HeicAndHeifUseExistingSelectedTagFlowWithoutChangingSource(
        string relativePath)
    {
        if (!SupportsPosixScripts())
        {
            return;
        }

        using var fixture = new TemporaryFixture();
        var mediaPath = fixture.WriteMedia(relativePath, [1, 2, 3, 4]);
        File.SetLastWriteTimeUtc(mediaPath, DateTime.UtcNow.AddDays(-2));
        var originalContents = File.ReadAllBytes(mediaPath);
        var originalLastWriteTime = File.GetLastWriteTimeUtc(mediaPath);
        var executablePath = fixture.CreateExecutable(
            "tools with spaces/fake exiftool",
            SuccessfulScript(
                """
                [{
                  "ExifIFD:DateTimeOriginal": "2020:01:02 03:04:05",
                  "GPS:GPSLatitude": 34.25
                }]
                """));

        var result = Assert.IsType<EmbeddedMetadataReadSuccessResult>(
            ExifToolMetadataReader.Read(mediaPath, executablePath));

        Assert.Equal(Path.GetFullPath(mediaPath), result.MediaPath);
        Assert.Collection(
            result.Values,
            value => AssertValue(
                value,
                EmbeddedMetadataField.CaptureDateTime,
                "ExifIFD",
                "DateTimeOriginal",
                "2020:01:02 03:04:05",
                JsonValueKind.String),
            value => AssertValue(
                value,
                EmbeddedMetadataField.GpsLatitude,
                "GPS",
                "GPSLatitude",
                "34.25",
                JsonValueKind.Number));
        Assert.Equal(originalContents, File.ReadAllBytes(mediaPath));
        Assert.Equal(originalLastWriteTime, File.GetLastWriteTimeUtc(mediaPath));
    }

    [Fact]
    public void Read_InvalidJsonReturnsMalformedResult()
    {
        if (!SupportsPosixScripts())
        {
            return;
        }

        using var fixture = new TemporaryFixture();
        var mediaPath = fixture.WriteMedia("photo.jpg", [1]);
        var executablePath = fixture.CreateExecutable(
            "exiftool",
            OutputScript("not JSON", "parse warning", exitCode: 0));

        var result = Assert.IsType<EmbeddedMetadataMalformedJsonResult>(
            ExifToolMetadataReader.Read(mediaPath, executablePath));

        Assert.Equal("not JSON", result.StandardOutput);
        Assert.Equal("parse warning", result.StandardError);
        Assert.Contains("malformed JSON", result.Message);
    }

    [Fact]
    public void Read_NonzeroExitReturnsExecutionFailureWithSeparateOutput()
    {
        if (!SupportsPosixScripts())
        {
            return;
        }

        using var fixture = new TemporaryFixture();
        var mediaPath = fixture.WriteMedia("photo.jpg", [1]);
        var executablePath = fixture.CreateExecutable(
            "exiftool",
            OutputScript("failure output", "failure error", exitCode: 9));

        var result = Assert.IsType<EmbeddedMetadataExifToolFailureResult>(
            ExifToolMetadataReader.Read(mediaPath, executablePath));

        Assert.Equal(9, result.ExitCode);
        Assert.Equal("failure output", result.StandardOutput);
        Assert.Equal("failure error", result.StandardError);
        Assert.Contains("exited with code 9", result.Message);
    }

    [Fact]
    public void Read_TimeoutTerminatesExifToolAndReturnsPromptly()
    {
        if (!SupportsPosixScripts())
        {
            return;
        }

        using var fixture = new TemporaryFixture();
        var mediaPath = fixture.WriteMedia("photo.jpg", [1]);
        var executablePath = fixture.CreateExecutable(
            "exiftool",
            """
            #!/bin/sh
            sleep 10
            printf '[]\n'
            """);
        var timeout = TimeSpan.FromMilliseconds(100);
        var stopwatch = Stopwatch.StartNew();

        var result = Assert.IsType<EmbeddedMetadataReadTimedOutResult>(
            ExifToolMetadataReader.Read(mediaPath, executablePath, timeout));

        stopwatch.Stop();
        Assert.Equal(timeout, result.Timeout);
        Assert.Null(result.TerminationError);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Read_UnsupportedExtensionDoesNotRunExifTool()
    {
        using var fixture = new TemporaryFixture();
        var mediaPath = fixture.WriteMedia("photo.png", [1]);
        var missingExecutable = Path.Combine(fixture.RootPath, "missing-exiftool");

        var result = Assert.IsType<EmbeddedMetadataUnsupportedMediaResult>(
            ExifToolMetadataReader.Read(mediaPath, missingExecutable));

        Assert.Equal(Path.GetFullPath(mediaPath), result.MediaPath);
        Assert.Equal(".png", result.Extension);
    }

    [Fact]
    public void Read_MissingJpegDoesNotRunExifTool()
    {
        using var fixture = new TemporaryFixture();
        var mediaPath = Path.Combine(fixture.RootPath, "missing.jpg");
        var missingExecutable = Path.Combine(fixture.RootPath, "missing-exiftool");

        var result = Assert.IsType<EmbeddedMetadataMissingMediaResult>(
            ExifToolMetadataReader.Read(mediaPath, missingExecutable));

        Assert.Equal(Path.GetFullPath(mediaPath), result.MediaPath);
    }

    private static void AssertValue(
        EmbeddedMetadataValue value,
        EmbeddedMetadataField field,
        string groupName,
        string tagName,
        string rawValue,
        JsonValueKind valueKind)
    {
        Assert.Equal(field, value.Field);
        Assert.Equal(groupName, value.GroupName);
        Assert.Equal(tagName, value.TagName);
        Assert.Equal(rawValue, value.RawValue);
        Assert.Equal(valueKind, value.ValueKind);
    }

    private static bool SupportsPosixScripts() =>
        OperatingSystem.IsMacOS() || OperatingSystem.IsLinux();

    private static string SuccessfulScript(string json, string standardError = "") =>
        """
        #!/bin/sh
        if [ "$#" -ne 18 ]; then printf 'unexpected argument count' >&2; exit 91; fi
        if [ "$1" != "-json" ]; then printf 'missing -json' >&2; exit 92; fi
        if [ "$2" != "-G1" ]; then printf 'missing -G1' >&2; exit 93; fi
        if [ "$3" != "-s" ]; then printf 'missing -s' >&2; exit 94; fi
        if [ "$4" != "-EXIF:DateTimeOriginal" ]; then exit 95; fi
        if [ "$5" != "-EXIF:CreateDate" ]; then exit 95; fi
        if [ "$6" != "-XMP:DateTimeOriginal" ]; then exit 95; fi
        if [ "$7" != "-XMP:CreateDate" ]; then exit 95; fi
        if [ "$8" != "-EXIF:OffsetTimeOriginal" ]; then exit 95; fi
        if [ "$9" != "-EXIF:GPSLatitude#" ]; then exit 95; fi
        if [ "${10}" != "-EXIF:GPSLatitudeRef#" ]; then exit 95; fi
        if [ "${11}" != "-EXIF:GPSLongitude#" ]; then exit 95; fi
        if [ "${12}" != "-EXIF:GPSLongitudeRef#" ]; then exit 95; fi
        if [ "${13}" != "-EXIF:GPSAltitude#" ]; then exit 95; fi
        if [ "${14}" != "-EXIF:GPSAltitudeRef#" ]; then exit 95; fi
        if [ "${15}" != "-XMP:GPSLatitude#" ]; then exit 95; fi
        if [ "${16}" != "-XMP:GPSLongitude#" ]; then exit 95; fi
        if [ "${17}" != "-XMP:GPSAltitude#" ]; then exit 95; fi
        """ +
        $"\nprintf '%s' '{json}'\nprintf '%s' '{standardError}' >&2\n";

    private static string OutputScript(
        string standardOutput,
        string standardError,
        int exitCode) =>
        $"""
        #!/bin/sh
        printf '%s' '{standardOutput}'
        printf '%s' '{standardError}' >&2
        exit {exitCode}
        """;

    private sealed class TemporaryFixture : IDisposable
    {
        public TemporaryFixture()
        {
            RootPath = Path.Combine(
                Path.GetTempPath(),
                $"PhotoMigration Metadata Reader {Guid.NewGuid():N}");
            Directory.CreateDirectory(RootPath);
        }

        public string RootPath { get; }

        public string WriteMedia(string relativePath, byte[] contents)
        {
            var path = Path.Combine(
                RootPath,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, contents);
            return path;
        }

        public string CreateExecutable(string relativePath, string contents)
        {
            var path = Path.Combine(
                RootPath,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(
                path,
                contents,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
            {
                File.SetUnixFileMode(
                    path,
                    UnixFileMode.UserRead
                    | UnixFileMode.UserWrite
                    | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead
                    | UnixFileMode.GroupExecute
                    | UnixFileMode.OtherRead
                    | UnixFileMode.OtherExecute);
            }

            return path;
        }

        public void Dispose()
        {
            Directory.Delete(RootPath, recursive: true);
        }
    }
}
