using System.Text;

namespace PhotoMigration.Core.Tests;

[Collection(ExternalProcessTestCollection.Name)]
public sealed class TakeoutMetadataPlannerTests
{
    [Fact]
    public void Plan_MatchedJpegUsesSidecarForSafeProposalAndAbsolutePathsWithSpaces()
    {
        if (!SupportsPosixScripts())
        {
            return;
        }

        using var fixture = new PlannerFixture();
        fixture.WriteTakeout("album with spaces/photo name.jpg", "media");
        fixture.WriteTakeout(
            "album with spaces/photo name.jpg.json",
            SidecarJson(photoTakenTime: 1_600_000_000));
        var exifToolPath = fixture.CreateExifTool();

        var result = TakeoutMetadataPlanner.Plan(
            fixture.TakeoutRootPath,
            exifToolPath);

        var item = Assert.Single(result.Items);
        Assert.Equal("album with spaces/photo name.jpg", item.MediaEntry.RelativePath);
        var state = Assert.IsType<MatchedTakeoutSidecarState>(item.SidecarState);
        Assert.Same(result.AnalysisResult.MatchedMedia[0], state.AnalysisResult);
        Assert.Same(state.AnalysisResult.MediaEntry, item.MediaEntry);
        var plan = Assert.IsType<SuccessfulMediaMetadataPlan>(item.MetadataPlan);
        Assert.Equal(MediaMetadataPlanStatus.SafeMetadataChangesProposed, plan.Status);
        Assert.IsType<ProposeSidecarPhotoTakenTimeDecision>(plan.CaptureTimeDecision);
        Assert.Equal(
            DateTimeOffset.FromUnixTimeSeconds(1_600_000_000),
            plan.SidecarMetadata.PhotoTakenTime);

        var invokedPath = Assert.Single(fixture.ReadInvokedMediaPaths());
        Assert.True(Path.IsPathFullyQualified(invokedPath));
        Assert.Equal(
            Path.Combine(
                fixture.TakeoutRootPath,
                "album with spaces",
                "photo name.jpg"),
            invokedPath);
    }

    [Fact]
    public void Plan_DistinguishesUnmatchedInvalidAndAmbiguousSidecarStates()
    {
        if (!SupportsPosixScripts())
        {
            return;
        }

        using var fixture = new PlannerFixture();
        fixture.WriteTakeout("unmatched.jpg", "media");
        fixture.WriteTakeout("invalid.jpg", "media");
        fixture.WriteTakeout("invalid.jpg.json", "{ invalid");
        fixture.WriteTakeout("ambiguous.jpg", "media");
        fixture.WriteTakeout(
            "ambiguous.jpg.json",
            SidecarJson(photoTakenTime: 1_600_000_000));
        fixture.WriteTakeout(
            "ambiguous.jpg.supplemental-metadata.json",
            SidecarJson(photoTakenTime: 1_700_000_000));

        var result = TakeoutMetadataPlanner.Plan(
            fixture.TakeoutRootPath,
            fixture.CreateExifTool());

        Assert.Collection(
            result.Items,
            ambiguous =>
            {
                var state = Assert.IsType<AmbiguousTakeoutSidecarState>(
                    ambiguous.SidecarState);
                Assert.Same(result.AnalysisResult.AmbiguousMedia[0], state.AnalysisResult);
                AssertEmptyPlanningSidecar(ambiguous.MetadataPlan);
                Assert.Equal(2, state.AnalysisResult.Candidates.Count);
            },
            invalid =>
            {
                var state = Assert.IsType<InvalidTakeoutSidecarState>(
                    invalid.SidecarState);
                Assert.Same(result.AnalysisResult.InvalidSidecars[0], state.AnalysisResult);
                AssertEmptyPlanningSidecar(invalid.MetadataPlan);
                Assert.Contains("invalid.jpg.json", state.AnalysisResult.Error.SidecarPath);
            },
            unmatched =>
            {
                var state = Assert.IsType<UnmatchedTakeoutSidecarState>(
                    unmatched.SidecarState);
                Assert.Same(result.AnalysisResult.UnmatchedMedia[0], state.AnalysisResult);
                AssertEmptyPlanningSidecar(unmatched.MetadataPlan);
            });
        Assert.Equal(3, fixture.ReadInvokedMediaPaths().Count);
    }

    [Fact]
    public void Plan_NonJpegUsesUnsupportedReaderResultWithoutRunningExifTool()
    {
        if (!SupportsPosixScripts())
        {
            return;
        }

        using var fixture = new PlannerFixture();
        fixture.WriteTakeout("video.mp4", "media");

        var result = TakeoutMetadataPlanner.Plan(
            fixture.TakeoutRootPath,
            fixture.CreateExifTool());

        var item = Assert.Single(result.Items);
        Assert.IsType<UnmatchedTakeoutSidecarState>(item.SidecarState);
        var plan = Assert.IsType<UnsupportedEmbeddedMetadataFormatPlan>(
            item.MetadataPlan);
        Assert.Equal(
            MediaMetadataPlanStatus.EmbeddedMetadataFormatNotSupportedYet,
            plan.Status);
        Assert.Empty(fixture.ReadInvokedMediaPaths());
    }

    [Fact]
    public void Plan_ExifToolFailureDoesNotStopOtherItemsAndOrderingIsDeterministic()
    {
        if (!SupportsPosixScripts())
        {
            return;
        }

        using var fixture = new PlannerFixture();
        fixture.WriteTakeout("z-last.jpg", "media");
        fixture.WriteTakeout("middle-failure.jpg", "media");
        fixture.WriteTakeout("A-first.jpg", "media");
        var exifToolPath = fixture.CreateExifTool();

        var result = TakeoutMetadataPlanner.Plan(
            fixture.TakeoutRootPath,
            exifToolPath);

        Assert.Equal(
            ["A-first.jpg", "middle-failure.jpg", "z-last.jpg"],
            result.Items.Select(item => item.MediaEntry.RelativePath));
        Assert.Equal(3, result.Items.Select(item => item.MediaEntry.RelativePath).Distinct().Count());
        Assert.IsType<SuccessfulMediaMetadataPlan>(result.Items[0].MetadataPlan);
        Assert.IsType<ExifToolFailureMetadataPlan>(result.Items[1].MetadataPlan);
        Assert.IsType<SuccessfulMediaMetadataPlan>(result.Items[2].MetadataPlan);
        Assert.Equal(3, fixture.ReadInvokedMediaPaths().Count);
    }

    [Fact]
    public void Plan_DerivesAllSummaryCountsAndPreservesNonMediaAnalysisEntries()
    {
        if (!SupportsPosixScripts())
        {
            return;
        }

        using var fixture = new PlannerFixture();
        fixture.WriteTakeout("A-safe.jpg", "media");
        fixture.WriteTakeout(
            "A-safe.jpg.json",
            SidecarJson(photoTakenTime: 1_600_000_000));
        fixture.WriteTakeout("B-unmatched.jpg", "media");
        fixture.WriteTakeout("C-invalid.jpg", "media");
        fixture.WriteTakeout("C-invalid.jpg.json", "{ invalid");
        fixture.WriteTakeout("D-ambiguous.jpg", "media");
        fixture.WriteTakeout("D-ambiguous.jpg.json", "{}");
        fixture.WriteTakeout("D-ambiguous.jpg.supplemental-metadata.json", "{}");
        fixture.WriteTakeout("E-video.mp4", "media");
        fixture.WriteTakeout("F-failure.jpg", "media");
        fixture.WriteTakeout("G-review.jpg", "media");
        fixture.WriteTakeout(
            "G-review.jpg.json",
            SidecarJson(photoTakenTime: 1_600_000_000));
        fixture.WriteTakeout("album.json", "not parsed");
        fixture.WriteTakeout("notes.txt", "other");

        var result = TakeoutMetadataPlanner.Plan(
            fixture.TakeoutRootPath,
            fixture.CreateExifTool());

        Assert.Equal(7, result.TotalMediaCount);
        Assert.Equal(2, result.MatchedSidecarCount);
        Assert.Equal(3, result.UnmatchedMediaCount);
        Assert.Equal(1, result.InvalidSidecarCount);
        Assert.Equal(1, result.AmbiguousSidecarCount);
        Assert.Equal(3, result.NoChangeMetadataPlanCount);
        Assert.Equal(1, result.SafeChangeMetadataPlanCount);
        Assert.Equal(1, result.ReviewRequiredMetadataPlanCount);
        Assert.Equal(1, result.EmbeddedMetadataUnavailableCount);
        Assert.Equal(1, result.EmbeddedFormatNotSupportedYetCount);
        var analyzedMediaPaths = result.AnalysisResult.MatchedMedia
            .Select(item => item.MediaEntry.RelativePath)
            .Concat(result.AnalysisResult.UnmatchedMedia.Select(
                item => item.MediaEntry.RelativePath))
            .Concat(result.AnalysisResult.InvalidSidecars.Select(
                item => item.MediaEntry.RelativePath))
            .Concat(result.AnalysisResult.AmbiguousMedia.Select(
                item => item.MediaEntry.RelativePath))
            .Order(StringComparer.Ordinal);
        Assert.Equal(
            analyzedMediaPaths,
            result.Items.Select(item => item.MediaEntry.RelativePath));
        Assert.Equal(
            [
                "D-ambiguous.jpg.json",
                "D-ambiguous.jpg.supplemental-metadata.json",
                "album.json"
            ],
            result.AnalysisResult.UnusedJsonCandidates.Select(
                entry => entry.RelativePath));
        Assert.Equal(
            "notes.txt",
            Assert.Single(result.AnalysisResult.OtherFiles).RelativePath);
    }

    [Fact]
    public void Plan_DoesNotChangeTakeoutContentsOrLastWriteTimes()
    {
        if (!SupportsPosixScripts())
        {
            return;
        }

        using var fixture = new PlannerFixture();
        fixture.WriteTakeout("photo.jpg", "media bytes");
        fixture.WriteTakeout(
            "photo.jpg.json",
            SidecarJson(photoTakenTime: 1_600_000_000));
        fixture.WriteTakeout("unused.json", "unused contents");
        fixture.WriteTakeout("notes.txt", "other contents");
        var before = fixture.SnapshotTakeout();

        _ = TakeoutMetadataPlanner.Plan(
            fixture.TakeoutRootPath,
            fixture.CreateExifTool());

        var after = fixture.SnapshotTakeout();
        Assert.Equal(before.Keys.Order(StringComparer.Ordinal), after.Keys.Order(StringComparer.Ordinal));
        foreach (var path in before.Keys)
        {
            Assert.Equal(before[path].Contents, after[path].Contents);
            Assert.Equal(before[path].LastWriteTimeUtc, after[path].LastWriteTimeUtc);
        }
    }

    [Fact]
    public void Plan_PropagatesAnalyzerFailureWithoutReturningAResult()
    {
        using var fixture = new PlannerFixture();
        var missingRoot = Path.Combine(fixture.RootPath, "missing Takeout");

        Assert.Throws<DirectoryNotFoundException>(() =>
            TakeoutMetadataPlanner.Plan(
                missingRoot,
                Path.Combine(fixture.RootPath, "unused exiftool")));
    }

    private static void AssertEmptyPlanningSidecar(MediaMetadataPlan metadataPlan)
    {
        Assert.Null(metadataPlan.SidecarMetadata.Title);
        Assert.Null(metadataPlan.SidecarMetadata.Description);
        Assert.Null(metadataPlan.SidecarMetadata.CreationTime);
        Assert.Null(metadataPlan.SidecarMetadata.PhotoTakenTime);
        Assert.Null(metadataPlan.SidecarMetadata.Latitude);
        Assert.Null(metadataPlan.SidecarMetadata.Longitude);
        Assert.Null(metadataPlan.SidecarMetadata.Altitude);
        Assert.Null(metadataPlan.SidecarMetadata.Url);
    }

    private static string SidecarJson(long photoTakenTime) =>
        $$"""
        {
          "photoTakenTime": { "timestamp": "{{photoTakenTime}}" }
        }
        """;

    private static bool SupportsPosixScripts() =>
        OperatingSystem.IsMacOS() || OperatingSystem.IsLinux();

    private sealed class PlannerFixture : IDisposable
    {
        public PlannerFixture()
        {
            RootPath = Path.Combine(
                Path.GetTempPath(),
                $"PhotoMigration Planner Tests {Guid.NewGuid():N}");
            TakeoutRootPath = Path.Combine(RootPath, "Takeout source");
            CallLogPath = Path.Combine(RootPath, "ExifTool calls.txt");
            Directory.CreateDirectory(TakeoutRootPath);
        }

        public string RootPath { get; }

        public string TakeoutRootPath { get; }

        private string CallLogPath { get; }

        public string WriteTakeout(string relativePath, string contents)
        {
            var path = Path.Combine(
                TakeoutRootPath,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, contents, Encoding.UTF8);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-2));
            return path;
        }

        public string CreateExifTool()
        {
            var executablePath = Path.Combine(
                RootPath,
                "tools with spaces",
                "fake exiftool");
            Directory.CreateDirectory(Path.GetDirectoryName(executablePath)!);
            var script = "#!/bin/sh\n"
                + $"log_path='{CallLogPath}'\n"
                + """
                media="${18}"
                printf '%s\n' "$media" >> "$log_path"
                case "$media" in
                  *failure.jpg)
                    printf '%s' 'synthetic failure' >&2
                    exit 9
                    ;;
                  *review.jpg)
                    printf '%s' '[{"ExifIFD:DateTimeOriginal":"2020:01:02 03:04:05Z"}]'
                    ;;
                  *)
                    printf '%s' '[{}]'
                    ;;
                esac
                """;
            File.WriteAllText(executablePath, script, new UTF8Encoding(false));
            if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
            {
                File.SetUnixFileMode(
                    executablePath,
                    UnixFileMode.UserRead
                    | UnixFileMode.UserWrite
                    | UnixFileMode.UserExecute);
            }
            return executablePath;
        }

        public IReadOnlyList<string> ReadInvokedMediaPaths() =>
            File.Exists(CallLogPath)
                ? File.ReadAllLines(CallLogPath)
                : Array.Empty<string>();

        public IReadOnlyDictionary<string, FileSnapshot> SnapshotTakeout() =>
            Directory.EnumerateFiles(
                    TakeoutRootPath,
                    "*",
                    SearchOption.AllDirectories)
                .ToDictionary(
                    path => Path.GetRelativePath(TakeoutRootPath, path)
                        .Replace(Path.DirectorySeparatorChar, '/'),
                    path => new FileSnapshot(
                        File.ReadAllBytes(path),
                        File.GetLastWriteTimeUtc(path)),
                    StringComparer.Ordinal);

        public void Dispose()
        {
            Directory.Delete(RootPath, recursive: true);
        }
    }

    private sealed record FileSnapshot(byte[] Contents, DateTime LastWriteTimeUtc);
}
