using System.Text;
using PhotoMigration.Core;

namespace PhotoMigration.Core.Tests;

[Collection(ExternalProcessTestCollection.Name)]
public sealed class TakeoutPreparationServiceTests
{
    private const string MediaHash =
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    [Fact]
    public void Prepare_PublishesMultipleUnchangedFilesInOrdinalOrder()
    {
        if (!SupportsPosixScripts()) return;

        using var fixture = new PreparationFixture();
        fixture.WriteTakeout("z-last.mp4", [9, 8, 7]);
        fixture.WriteTakeout("A-first.jpg", [1, 2, 3]);
        var before = fixture.SnapshotTakeout();

        var result = TakeoutPreparationService.Prepare(
            fixture.SourceRoot,
            fixture.OutputRoot,
            fixture.CreateExifTool());

        Assert.Equal(
            ["A-first.jpg", "z-last.mp4"],
            result.Outcomes.Select(outcome => outcome.PlanningItem.MediaEntry.RelativePath));
        Assert.All(result.Outcomes, outcome =>
        {
            Assert.Equal(TakeoutPreparationOutcomeKind.PublishedUnchanged, outcome.Kind);
            Assert.IsType<VerifiedUnchangedMediaPublicationSuccessResult>(
                outcome.RetainedResult);
            Assert.IsType<UnmatchedTakeoutSidecarState>(
                outcome.PlanningItem.SidecarState);
        });
        Assert.Equal(2, result.TotalMediaCount);
        Assert.Equal(2, result.PublishedCount);
        Assert.Equal(2, result.PublishedUnchangedCount);
        Assert.Equal(2, result.PublishedUnchangedFiles.Count);
        Assert.Equal(0, result.PublishedJpegGpsCount);
        Assert.Empty(result.PublishedJpegGpsFiles);
        Assert.Equal(0, result.AttentionRequiredCount);
        Assert.Empty(result.AttentionRequiredFiles);
        Assert.Equal(0, result.FailedCount);
        Assert.Empty(result.FailedFiles);
        Assert.Same(result.MetadataPlanningResult.AnalysisResult, result.AnalysisResult);
        Assert.IsType<DestinationPathPlanningSuccessResult>(
            result.DestinationPlanningResult);
        Assert.Equal([1, 2, 3], fixture.ReadOutput("A-first.jpg"));
        Assert.Equal([9, 8, 7], fixture.ReadOutput("z-last.mp4"));
        Assert.Equal(2, fixture.ReadExifToolCalls().Count);
        fixture.AssertTakeoutUnchanged(before);
    }

    [Fact]
    public void Prepare_CompletesJpegGpsPipelineAndPublishesVerifiedFile()
    {
        if (!SupportsPosixScripts()) return;

        using var fixture = new PreparationFixture();
        fixture.WriteTakeout("Album/photo.jpg", [1, 2, 3, 4]);
        fixture.WriteTakeoutText(
            "Album/photo.jpg.json",
            "{ \"geoData\": { \"latitude\": 1, \"longitude\": 2, \"altitude\": 3 } }");
        var before = fixture.SnapshotTakeout();

        var result = TakeoutPreparationService.Prepare(
            fixture.SourceRoot,
            fixture.OutputRoot,
            fixture.CreateExifTool());

        var outcome = Assert.Single(result.Outcomes);
        Assert.Equal(TakeoutPreparationOutcomeKind.PublishedJpegGps, outcome.Kind);
        var publication = Assert.IsType<VerifiedJpegPublicationSuccessResult>(
            outcome.RetainedResult);
        Assert.Equal(MediaHash,
            publication.VerificationResult.ActualImageDataHashSha256);
        Assert.Equal(
            new JpegGpsMetadataVerificationValues(1, "N", 2, "E", 3, "0"),
            publication.VerificationResult.ActualGps);
        Assert.Equal(1, result.PublishedCount);
        Assert.Equal(0, result.PublishedUnchangedCount);
        Assert.Equal(1, result.PublishedJpegGpsCount);
        Assert.Same(outcome, Assert.Single(result.PublishedJpegGpsFiles));
        Assert.Equal(0, result.AttentionRequiredCount);
        Assert.Equal(0, result.FailedCount);
        Assert.Equal([1, 2, 3, 4], fixture.ReadOutput("Album/photo.jpg"));
        Assert.Empty(fixture.FindStagingFiles());
        Assert.Equal(4, fixture.ReadExifToolCalls().Count);
        fixture.AssertTakeoutUnchanged(before);
    }

    [Fact]
    public void Prepare_PublishesCaptureTimeOnlyImageAndVideo()
    {
        if (!SupportsPosixScripts()) return;

        using var fixture = new PreparationFixture();
        fixture.WriteTakeout("Images/photo.png", [1, 2, 3]);
        fixture.WriteTakeoutText(
            "Images/photo.png.json",
            PhotoTakenTimeSidecar());
        fixture.WriteTakeout("Videos/movie.mov", [4, 5, 6, 7]);
        fixture.WriteTakeoutText(
            "Videos/movie.mov.json",
            PhotoTakenTimeSidecar());
        var before = fixture.SnapshotTakeout();

        var result = TakeoutPreparationService.Prepare(
            fixture.SourceRoot,
            fixture.OutputRoot,
            fixture.CreateExifTool());

        Assert.All(result.Outcomes, outcome =>
        {
            Assert.Equal(
                TakeoutPreparationOutcomeKind.PublishedWithCaptureTime,
                outcome.Kind);
            var publication = Assert.IsType<
                VerifiedAmazonCaptureTimePublicationSuccessResult>(
                outcome.RetainedResult);
            Assert.Equal(MediaHash,
                publication.VerificationResult.ActualImageDataHashSha256);
            Assert.Equal(
                publication.VerificationResult.ExpectedAssignments,
                publication.VerificationResult.ActualAssignments);
        });
        Assert.Equal(2, result.PublishedWithCaptureTimeCount);
        Assert.Equal(2, result.PublishedCount);
        Assert.Equal([1, 2, 3], fixture.ReadOutput("Images/photo.png"));
        Assert.Equal([4, 5, 6, 7], fixture.ReadOutput("Videos/movie.mov"));
        Assert.Empty(fixture.FindStagingFiles());
        fixture.AssertTakeoutUnchanged(before);
    }

    [Fact]
    public void Prepare_PublishesPngCaptureTimeWhenSidecarLocationIsZeroPlaceholder()
    {
        if (!SupportsPosixScripts()) return;

        using var fixture = new PreparationFixture();
        fixture.WriteTakeout("photo.png", [1, 2, 3]);
        fixture.WriteTakeoutText(
            "photo.png.json",
            $$"""
            {
              "photoTakenTime": { "timestamp": "1600000000" },
              "geoData": { "latitude": 0, "longitude": 0, "altitude": 42 }
            }
            """);
        var before = fixture.SnapshotTakeout();

        var result = TakeoutPreparationService.Prepare(
            fixture.SourceRoot,
            fixture.OutputRoot,
            fixture.CreateExifTool());

        var outcome = Assert.Single(result.Outcomes);
        Assert.Equal(
            TakeoutPreparationOutcomeKind.PublishedWithCaptureTime,
            outcome.Kind);
        var publication = Assert.IsType<
            VerifiedAmazonCaptureTimePublicationSuccessResult>(
            outcome.RetainedResult);
        Assert.Equal(
            publication.VerificationResult.ExpectedAssignments,
            publication.VerificationResult.ActualAssignments);
        Assert.Equal(MediaHash,
            publication.VerificationResult.ActualImageDataHashSha256);
        Assert.Equal([1, 2, 3], fixture.ReadOutput("photo.png"));
        Assert.Equal(1, result.PublishedWithCaptureTimeCount);
        Assert.Equal(0, result.AttentionRequiredCount);
        fixture.AssertTakeoutUnchanged(before);
    }

    [Fact]
    public void Prepare_WritesAndVerifiesJpegGpsAndCaptureTimeBeforeOnePublish()
    {
        if (!SupportsPosixScripts()) return;

        using var fixture = new PreparationFixture();
        fixture.WriteTakeout("Album/photo.jpg", [1, 2, 3, 4]);
        fixture.WriteTakeoutText(
            "Album/photo.jpg.json",
            $$"""
            {
              "photoTakenTime": { "timestamp": "1600000000" },
              "geoData": { "latitude": 1, "longitude": 2, "altitude": 3 }
            }
            """);
        var before = fixture.SnapshotTakeout();

        var result = TakeoutPreparationService.Prepare(
            fixture.SourceRoot,
            fixture.OutputRoot,
            fixture.CreateExifTool());

        var outcome = Assert.Single(result.Outcomes);
        Assert.Equal(
            TakeoutPreparationOutcomeKind.PublishedWithGpsAndCaptureTime,
            outcome.Kind);
        var combined = Assert.IsType<
            VerifiedJpegGpsAndCaptureTimePublicationResult>(outcome.RetainedResult);
        Assert.Equal(MediaHash,
            combined.CaptureTimePublication.VerificationResult
                .ActualImageDataHashSha256);
        Assert.Equal(MediaHash,
            combined.GpsVerification.ActualImageDataHashSha256);
        Assert.Equal(
            new JpegGpsMetadataVerificationValues(1, "N", 2, "E", 3, "0"),
            combined.GpsVerification.ActualGps);
        Assert.Equal(1, result.PublishedWithGpsAndCaptureTimeCount);
        Assert.Equal(1, result.PublishedCount);
        Assert.Equal([1, 2, 3, 4], fixture.ReadOutput("Album/photo.jpg"));
        Assert.Single(
            fixture.ReadExifToolCalls(),
            call => call.StartsWith("-overwrite_original|", StringComparison.Ordinal)
                    && call.EndsWith("|1|1", StringComparison.Ordinal));
        Assert.DoesNotContain(
            Directory.EnumerateFiles(fixture.OutputRoot, "*", SearchOption.AllDirectories),
            path => path.EndsWith("_original", StringComparison.Ordinal));
        fixture.AssertTakeoutUnchanged(before);
    }

    [Fact]
    public void Prepare_VerificationFailurePreventsPublishAndDoesNotStopLaterFile()
    {
        if (!SupportsPosixScripts()) return;

        using var fixture = new PreparationFixture();
        fixture.WriteTakeout("A-capture.mp4", [1, 2, 3]);
        fixture.WriteTakeoutText(
            "A-capture.mp4.json",
            PhotoTakenTimeSidecar());
        fixture.WriteTakeout("z-later.jpg", [9, 8, 7]);
        var before = fixture.SnapshotTakeout();

        var result = TakeoutPreparationService.Prepare(
            fixture.SourceRoot,
            fixture.OutputRoot,
            fixture.CreateExifTool(mismatchCaptureTime: true));

        Assert.Collection(
            result.Outcomes,
            failed =>
            {
                Assert.Equal(TakeoutPreparationOutcomeKind.Failed, failed.Kind);
                Assert.Equal(TakeoutPreparationFailureStage.CaptureTimeVerification,
                    failed.FailureStage);
                var verification = Assert.IsType<
                    AmazonCaptureTimeMetadataWriteVerificationFailureResult>(
                    failed.RetainedResult);
                Assert.Equal(
                    AmazonCaptureTimeMetadataWriteVerificationFailureKind
                        .CaptureTimeMismatch,
                    verification.FailureKind);
            },
            published => Assert.Equal(
                TakeoutPreparationOutcomeKind.PublishedUnchanged,
                published.Kind));
        Assert.False(fixture.OutputExists("A-capture.mp4"));
        Assert.True(fixture.OutputExists("z-later.jpg"));
        Assert.Equal(1, result.FailedCount);
        Assert.Equal(1, result.PublishedCount);
        fixture.AssertTakeoutUnchanged(before);
    }

    [Fact]
    public void Prepare_UnresolvedLocationReviewPreventsPartialCapturePublication()
    {
        if (!SupportsPosixScripts()) return;

        using var fixture = new PreparationFixture();
        fixture.WriteTakeout("photo.jpg", [1, 2, 3]);
        fixture.WriteTakeoutText(
            "photo.jpg.json",
            $$"""
            {
              "photoTakenTime": { "timestamp": "1600000000" },
              "geoData": { "latitude": 1, "longitude": 2 }
            }
            """);
        var before = fixture.SnapshotTakeout();

        var result = TakeoutPreparationService.Prepare(
            fixture.SourceRoot,
            fixture.OutputRoot,
            fixture.CreateExifTool(conflictingEmbeddedGps: true));

        var outcome = Assert.Single(result.Outcomes);
        Assert.Equal(TakeoutPreparationOutcomeKind.AttentionRequired, outcome.Kind);
        Assert.Equal(TakeoutPreparationAttentionReason.MetadataReviewRequired,
            outcome.AttentionReason);
        Assert.False(fixture.OutputExists("photo.jpg"));
        Assert.Empty(fixture.FindStagingFiles());
        Assert.DoesNotContain(
            fixture.ReadExifToolCalls(),
            call => call.StartsWith("-overwrite_original|", StringComparison.Ordinal));
        fixture.AssertTakeoutUnchanged(before);
    }

    [Fact]
    public void Prepare_AttentionAndUnsupportedWritesDoNotStopLaterSafeFile()
    {
        if (!SupportsPosixScripts()) return;

        using var fixture = new PreparationFixture();
        fixture.WriteTakeout("A-invalid.jpg", [1]);
        fixture.WriteTakeoutText("A-invalid.jpg.json", "{ invalid");
        fixture.WriteTakeout("B-unsupported.heic", [2]);
        fixture.WriteTakeoutText(
            "B-unsupported.heic.json",
            "{ \"geoData\": { \"latitude\": 1, \"longitude\": 2 } }");
        fixture.WriteTakeout("z-safe.jpg", [3]);
        var before = fixture.SnapshotTakeout();

        var result = TakeoutPreparationService.Prepare(
            fixture.SourceRoot,
            fixture.OutputRoot,
            fixture.CreateExifTool());

        Assert.Collection(
            result.Outcomes,
            invalid =>
            {
                Assert.Equal("A-invalid.jpg", invalid.PlanningItem.MediaEntry.RelativePath);
                Assert.Equal(TakeoutPreparationOutcomeKind.AttentionRequired, invalid.Kind);
                Assert.Equal(TakeoutPreparationAttentionReason.InvalidSidecar,
                    invalid.AttentionReason);
                Assert.IsType<InvalidTakeoutSidecarState>(invalid.RetainedResult);
            },
            unsupported =>
            {
                Assert.Equal("B-unsupported.heic",
                    unsupported.PlanningItem.MediaEntry.RelativePath);
                Assert.Equal(TakeoutPreparationOutcomeKind.AttentionRequired,
                    unsupported.Kind);
                Assert.Equal(TakeoutPreparationAttentionReason.WriteFormatNotSupported,
                    unsupported.AttentionReason);
                Assert.IsType<JpegMetadataWriteUnsupportedFormatResult>(
                    unsupported.RetainedResult);
            },
            safe =>
            {
                Assert.Equal("z-safe.jpg", safe.PlanningItem.MediaEntry.RelativePath);
                Assert.Equal(TakeoutPreparationOutcomeKind.PublishedUnchanged, safe.Kind);
            });
        Assert.Equal(3, result.TotalMediaCount);
        Assert.Equal(1, result.PublishedCount);
        Assert.Equal(2, result.AttentionRequiredCount);
        Assert.Equal(2, result.AttentionRequiredFiles.Count);
        Assert.Equal(0, result.FailedCount);
        Assert.False(fixture.OutputExists("A-invalid.jpg"));
        Assert.False(fixture.OutputExists("B-unsupported.heic"));
        Assert.True(fixture.OutputExists("z-safe.jpg"));
        Assert.Single(fixture.FindStagingFiles());
        fixture.AssertTakeoutUnchanged(before);
    }

    [Fact]
    public void Prepare_PerFileFailureDoesNotStopLaterFileOrOverwriteDestination()
    {
        if (!SupportsPosixScripts()) return;

        using var fixture = new PreparationFixture();
        fixture.WriteTakeout("A-existing.jpg", [1]);
        fixture.WriteTakeout("z-later.mp4", [2]);
        byte[] existingBytes = [9, 9, 9];
        fixture.WriteOutput("A-existing.jpg", existingBytes);
        var before = fixture.SnapshotTakeout();

        var result = TakeoutPreparationService.Prepare(
            fixture.SourceRoot,
            fixture.OutputRoot,
            fixture.CreateExifTool());

        Assert.Collection(
            result.Outcomes,
            failed =>
            {
                Assert.Equal(TakeoutPreparationOutcomeKind.Failed, failed.Kind);
                Assert.Equal(TakeoutPreparationFailureStage.Staging,
                    failed.FailureStage);
                var stagingFailure = Assert.IsType<VerifiedMediaFileStagingFailureResult>(
                    failed.RetainedResult);
                Assert.Equal(VerifiedMediaFileStagingFailureKind.ExistingDestination,
                    stagingFailure.FailureKind);
            },
            published => Assert.Equal(
                TakeoutPreparationOutcomeKind.PublishedUnchanged,
                published.Kind));
        Assert.Equal(1, result.PublishedCount);
        Assert.Equal(0, result.AttentionRequiredCount);
        Assert.Equal(1, result.FailedCount);
        Assert.Single(result.FailedFiles);
        Assert.Equal(existingBytes, fixture.ReadOutput("A-existing.jpg"));
        Assert.Equal([2], fixture.ReadOutput("z-later.mp4"));
        fixture.AssertTakeoutUnchanged(before);
    }

    private static bool SupportsPosixScripts() =>
        OperatingSystem.IsMacOS() || OperatingSystem.IsLinux();

    private static string PhotoTakenTimeSidecar() =>
        "{ \"photoTakenTime\": { \"timestamp\": \"1600000000\" } }";

    private sealed class PreparationFixture : IDisposable
    {
        private readonly string _callLogPath;

        public PreparationFixture()
        {
            RootPath = Path.Combine(
                Path.GetTempPath(),
                "PhotoMigration-TakeoutPreparationTests",
                Guid.NewGuid().ToString("N"));
            SourceRoot = Path.Combine(RootPath, "Takeout source");
            OutputRoot = Path.Combine(RootPath, "Prepared output");
            _callLogPath = Path.Combine(RootPath, "exiftool calls.txt");
            Directory.CreateDirectory(SourceRoot);
            Directory.CreateDirectory(OutputRoot);
        }

        public string RootPath { get; }

        public string SourceRoot { get; }

        public string OutputRoot { get; }

        public void WriteTakeout(string relativePath, byte[] contents)
        {
            var path = Combine(SourceRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, contents);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-4));
        }

        public void WriteTakeoutText(string relativePath, string contents)
        {
            var path = Combine(SourceRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, contents, new UTF8Encoding(false));
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-4));
        }

        public void WriteOutput(string relativePath, byte[] contents)
        {
            var path = Combine(OutputRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, contents);
        }

        public byte[] ReadOutput(string relativePath) =>
            File.ReadAllBytes(Combine(OutputRoot, relativePath));

        public bool OutputExists(string relativePath) =>
            File.Exists(Combine(OutputRoot, relativePath));

        public IReadOnlyList<string> FindStagingFiles() =>
            Directory.EnumerateFiles(OutputRoot, ".photomigration-*.staging*",
                    SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal)
                .ToList()
                .AsReadOnly();

        public IReadOnlyList<string> ReadExifToolCalls() =>
            File.Exists(_callLogPath)
                ? File.ReadAllLines(_callLogPath)
                : Array.Empty<string>();

        public string CreateExifTool(
            bool mismatchCaptureTime = false,
            bool conflictingEmbeddedGps = false)
        {
            if (!SupportsPosixScripts()) throw new PlatformNotSupportedException();

            var path = Path.Combine(RootPath, "tools with spaces", "fake exiftool");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var captureDateTime = mismatchCaptureTime
                ? "1999:01:01 00:00:00"
                : "2020:09:13 12:26:40";
            var mp4CaptureDateTime = captureDateTime + "+00:00";
            var imageMetadataJson = conflictingEmbeddedGps
                ? "[{\"ExifIFD:DateTimeOriginal\":\"2020:01:02 03:04:05\"," +
                  "\"GPS:GPSLatitude\":40,\"GPS:GPSLatitudeRef\":\"N\"," +
                  "\"GPS:GPSLongitude\":70,\"GPS:GPSLongitudeRef\":\"W\"}]"
                : "[{\"ExifIFD:DateTimeOriginal\":\"2020:01:02 03:04:05\"}]";
            var script = $$"""
                #!/bin/sh
                last=''
                has_hash=0
                has_exif_gps=0
                has_exif_time=0
                has_mov_time=0
                has_mp4_time=0
                writes_gps=0
                writes_capture=0
                for argument in "$@"; do
                  last="$argument"
                  if [ "$argument" = "-ImageDataHash" ]; then has_hash=1; fi
                  if [ "$argument" = "-EXIF:GPSLatitude#" ]; then has_exif_gps=1; fi
                  if [ "$argument" = "-EXIF:DateTimeOriginal" ]; then has_exif_time=1; fi
                  if [ "$argument" = "-QuickTime:CreateDate" ]; then has_mov_time=1; fi
                  if [ "$argument" = "-Keys:CreationDate" ]; then has_mp4_time=1; fi
                  case "$argument" in
                    -EXIF:GPSLatitude=*) writes_gps=1 ;;
                    -EXIF:DateTimeOriginal=*|-QuickTime:CreateDate=*|-Keys:CreationDate=*) writes_capture=1 ;;
                  esac
                done
                printf '%s|%s|%s|%s\n' "$1" "$last" "$writes_gps" "$writes_capture" >> '{{_callLogPath}}'
                if [ "$1" = "-overwrite_original" ]; then
                  exit 0
                fi
                if [ "$has_hash" -eq 1 ] && [ "$has_exif_gps" -eq 1 ]; then
                  printf '%s' '[{"GPS:GPSLatitude":1,"GPS:GPSLatitudeRef":"N","GPS:GPSLongitude":2,"GPS:GPSLongitudeRef":"E","GPS:GPSAltitude":3,"GPS:GPSAltitudeRef":0,"File:ImageDataHash":"{{MediaHash}}"}]'
                  exit 0
                fi
                if [ "$has_hash" -eq 1 ] && [ "$has_exif_time" -eq 1 ]; then
                  printf '%s' '[{"ExifIFD:DateTimeOriginal":"{{captureDateTime}}","ExifIFD:OffsetTimeOriginal":"+00:00","File:ImageDataHash":"{{MediaHash}}"}]'
                  exit 0
                fi
                if [ "$has_hash" -eq 1 ] && [ "$has_mov_time" -eq 1 ]; then
                  printf '%s' '[{"QuickTime:CreateDate":"{{captureDateTime}}","File:ImageDataHash":"{{MediaHash}}"}]'
                  exit 0
                fi
                if [ "$has_hash" -eq 1 ] && [ "$has_mp4_time" -eq 1 ]; then
                  printf '%s' '[{"Keys:CreationDate":"{{mp4CaptureDateTime}}","File:ImageDataHash":"{{MediaHash}}"}]'
                  exit 0
                fi
                if [ "$has_hash" -eq 1 ]; then
                  printf '%s' '[{"File:ImageDataHash":"{{MediaHash}}"}]'
                  exit 0
                fi
                case "$last" in
                  *.mov|*.MOV|*.mp4|*.MP4)
                    printf '%s' '[{"QuickTime:CreateDate":"2020:01:02 03:04:05"}]'
                    ;;
                  *)
                    printf '%s' '{{imageMetadataJson}}'
                    ;;
                esac
                """;
            File.WriteAllText(path, script, new UTF8Encoding(false));
            if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
            {
                File.SetUnixFileMode(
                    path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite
                    | UnixFileMode.UserExecute);
            }

            return path;
        }

        public IReadOnlyDictionary<string, FileSnapshot> SnapshotTakeout() =>
            Directory.EnumerateFiles(SourceRoot, "*", SearchOption.AllDirectories)
                .ToDictionary(
                    path => Path.GetRelativePath(SourceRoot, path),
                    path => new FileSnapshot(
                        File.ReadAllBytes(path),
                        File.GetLastWriteTimeUtc(path)),
                    StringComparer.Ordinal);

        public void AssertTakeoutUnchanged(
            IReadOnlyDictionary<string, FileSnapshot> before)
        {
            var after = SnapshotTakeout();
            Assert.Equal(
                before.Keys.Order(StringComparer.Ordinal),
                after.Keys.Order(StringComparer.Ordinal));
            foreach (var path in before.Keys)
            {
                Assert.Equal(before[path].Contents, after[path].Contents);
                Assert.Equal(before[path].LastWriteTimeUtc, after[path].LastWriteTimeUtc);
            }
        }

        public void Dispose()
        {
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
        }

        private static string Combine(string root, string relativePath) =>
            Path.GetFullPath(Path.Combine(
                root,
                relativePath.Replace('/', Path.DirectorySeparatorChar)));
    }

    private sealed record FileSnapshot(byte[] Contents, DateTime LastWriteTimeUtc);
}
