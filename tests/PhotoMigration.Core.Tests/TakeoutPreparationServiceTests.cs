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

        public string CreateExifTool()
        {
            if (!SupportsPosixScripts()) throw new PlatformNotSupportedException();

            var path = Path.Combine(RootPath, "tools with spaces", "fake exiftool");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var script = $$"""
                #!/bin/sh
                last=''
                has_hash=0
                has_exif_gps=0
                for argument in "$@"; do
                  last="$argument"
                  if [ "$argument" = "-ImageDataHash" ]; then has_hash=1; fi
                  if [ "$argument" = "-EXIF:GPSLatitude#" ]; then has_exif_gps=1; fi
                done
                printf '%s|%s\n' "$1" "$last" >> '{{_callLogPath}}'
                if [ "$1" = "-overwrite_original" ]; then
                  exit 0
                fi
                if [ "$has_hash" -eq 1 ] && [ "$has_exif_gps" -eq 1 ]; then
                  printf '%s' '[{"GPS:GPSLatitude":1,"GPS:GPSLatitudeRef":"N","GPS:GPSLongitude":2,"GPS:GPSLongitudeRef":"E","GPS:GPSAltitude":3,"GPS:GPSAltitudeRef":0,"File:ImageDataHash":"{{MediaHash}}"}]'
                  exit 0
                fi
                if [ "$has_hash" -eq 1 ]; then
                  printf '%s' '[{"File:ImageDataHash":"{{MediaHash}}"}]'
                  exit 0
                fi
                printf '%s' '[{}]'
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
