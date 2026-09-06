using System.Text;

namespace PhotoMigration.Core.Tests;

public sealed class TakeoutAnalyzerTests
{
    [Fact]
    public void Analyze_MatchesAndParsesPhotoAndVideoCandidates()
    {
        using var takeout = new TemporaryTakeout();
        takeout.Write("nested/photo.jpg", "photo bytes");
        takeout.Write(
            "nested/photo.jpg.json",
            """
            {
              "title": "A photo",
              "photoTakenTime": { "timestamp": "1700000000" }
            }
            """);
        takeout.Write("clip.MP4", "video bytes");
        takeout.Write(
            "clip.MP4.supplemental-metadata.json",
            """
            { "title": "A video" }
            """);

        var result = TakeoutAnalyzer.Analyze(takeout.RootPath);

        Assert.Collection(
            result.MatchedMedia,
            video =>
            {
                Assert.Equal("clip.MP4", video.MediaEntry.RelativePath);
                Assert.Equal("clip.MP4.supplemental-metadata.json", video.SidecarEntry.RelativePath);
                Assert.Equal(SidecarMatchRule.SupplementalMetadataJson, video.MatchRule);
                Assert.Equal("A video", video.Metadata.Title);
            },
            photo =>
            {
                Assert.Equal("nested/photo.jpg", photo.MediaEntry.RelativePath);
                Assert.Equal("nested/photo.jpg.json", photo.SidecarEntry.RelativePath);
                Assert.Equal(SidecarMatchRule.LegacyJson, photo.MatchRule);
                Assert.Equal("A photo", photo.Metadata.Title);
                Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), photo.Metadata.PhotoTakenTime);
            });
        Assert.Empty(result.InvalidSidecars);
        Assert.Empty(result.UnmatchedMedia);
        Assert.Empty(result.AmbiguousMedia);
        Assert.Empty(result.UnusedJsonCandidates);
        Assert.Empty(result.OtherFiles);
    }

    [Fact]
    public void Analyze_RetainsUnmatchedMediaUnusedJsonAndOtherFiles()
    {
        using var takeout = new TemporaryTakeout();
        takeout.Write("unmatched.jpeg", "media");
        takeout.Write("album.json", "not valid JSON");
        takeout.Write("notes.txt", "notes");

        var result = TakeoutAnalyzer.Analyze(takeout.RootPath);

        var unmatched = Assert.Single(result.UnmatchedMedia);
        Assert.Equal("unmatched.jpeg", unmatched.MediaEntry.RelativePath);
        Assert.Equal("album.json", Assert.Single(result.UnusedJsonCandidates).RelativePath);
        Assert.Equal("notes.txt", Assert.Single(result.OtherFiles).RelativePath);
        Assert.Empty(result.MatchedMedia);
        Assert.Empty(result.InvalidSidecars);
        Assert.Empty(result.AmbiguousMedia);
    }

    [Fact]
    public void Analyze_DoesNotParseAmbiguousSidecars()
    {
        using var takeout = new TemporaryTakeout();
        takeout.Write("photo.jpg", "media");
        takeout.Write("photo.jpg.json", "not valid JSON");
        takeout.Write("photo.supplemental-metadata.json", "also not valid JSON");

        var result = TakeoutAnalyzer.Analyze(takeout.RootPath);

        var ambiguous = Assert.Single(result.AmbiguousMedia);
        Assert.Equal("photo.jpg", ambiguous.MediaEntry.RelativePath);
        Assert.Equal(
            ["photo.jpg.json", "photo.supplemental-metadata.json"],
            ambiguous.Candidates.Select(candidate => candidate.SidecarEntry.RelativePath));
        Assert.Equal(
            ["photo.jpg.json", "photo.supplemental-metadata.json"],
            result.UnusedJsonCandidates.Select(entry => entry.RelativePath));
        Assert.Empty(result.MatchedMedia);
        Assert.Empty(result.InvalidSidecars);
    }

    [Fact]
    public void Analyze_RecordsInvalidSidecarAndContinues()
    {
        using var takeout = new TemporaryTakeout();
        takeout.Write("bad.jpg", "bad media");
        takeout.Write("bad.jpg.json", "{ invalid");
        takeout.Write("good.jpg", "good media");
        takeout.Write("good.jpg.json", "{ \"title\": \"Good\" }");

        var result = TakeoutAnalyzer.Analyze(takeout.RootPath);

        var invalid = Assert.Single(result.InvalidSidecars);
        Assert.Equal("bad.jpg", invalid.MediaEntry.RelativePath);
        Assert.Equal("bad.jpg.json", invalid.SidecarEntry.RelativePath);
        Assert.Equal(SidecarMatchRule.LegacyJson, invalid.MatchRule);
        Assert.Equal(Path.Combine(takeout.RootPath, "bad.jpg.json"), invalid.Error.SidecarPath);

        var parsed = Assert.Single(result.MatchedMedia);
        Assert.Equal("good.jpg", parsed.MediaEntry.RelativePath);
        Assert.Equal("Good", parsed.Metadata.Title);
        Assert.Empty(result.UnusedJsonCandidates);
    }

    [Fact]
    public void Analyze_ReturnsDeterministicOrdinalOrdering()
    {
        using var takeout = new TemporaryTakeout();
        takeout.Write("z.jpg", "media");
        takeout.Write("z.jpg.json", "{}");
        takeout.Write("A.jpg", "media");
        takeout.Write("A.jpg.json", "{}");
        takeout.Write("z-unmatched.mov", "media");
        takeout.Write("A-unmatched.mp4", "media");
        takeout.Write("z-unused.json", "invalid");
        takeout.Write("A-unused.json", "invalid");
        takeout.Write("z.txt", "other");
        takeout.Write("A.bin", "other");

        var first = TakeoutAnalyzer.Analyze(takeout.RootPath);
        var second = TakeoutAnalyzer.Analyze(takeout.RootPath);

        Assert.Equal(["A.jpg", "z.jpg"], MediaPaths(first.MatchedMedia));
        Assert.Equal(["A-unmatched.mp4", "z-unmatched.mov"], MediaPaths(first.UnmatchedMedia));
        Assert.Equal(
            ["A-unused.json", "z-unused.json"],
            first.UnusedJsonCandidates.Select(entry => entry.RelativePath));
        Assert.Equal(["A.bin", "z.txt"], first.OtherFiles.Select(entry => entry.RelativePath));

        Assert.Equal(MediaPaths(first.MatchedMedia), MediaPaths(second.MatchedMedia));
        Assert.Equal(MediaPaths(first.UnmatchedMedia), MediaPaths(second.UnmatchedMedia));
        Assert.Equal(
            first.UnusedJsonCandidates.Select(entry => entry.RelativePath),
            second.UnusedJsonCandidates.Select(entry => entry.RelativePath));
        Assert.Equal(
            first.OtherFiles.Select(entry => entry.RelativePath),
            second.OtherFiles.Select(entry => entry.RelativePath));
    }

    [Fact]
    public void Analyze_DoesNotChangeSourceFilesOrLastWriteTimes()
    {
        using var takeout = new TemporaryTakeout();
        takeout.Write("photo.jpg", "media bytes");
        takeout.Write("photo.jpg.json", "{ \"title\": \"Photo\" }");
        takeout.Write("unused.json", "not parsed");
        takeout.Write("notes.txt", "notes");
        var before = takeout.Snapshot();

        _ = TakeoutAnalyzer.Analyze(takeout.RootPath);

        var after = takeout.Snapshot();
        Assert.Equal(before.Keys.Order(StringComparer.Ordinal), after.Keys.Order(StringComparer.Ordinal));
        foreach (var relativePath in before.Keys)
        {
            Assert.Equal(before[relativePath].Contents, after[relativePath].Contents);
            Assert.Equal(before[relativePath].LastWriteTimeUtc, after[relativePath].LastWriteTimeUtc);
        }
    }

    [Fact]
    public void Analyze_PropagatesInventoryRootFailures()
    {
        using var takeout = new TemporaryTakeout();
        var filePath = takeout.Write("not-a-directory", "contents");
        var missingPath = Path.Combine(takeout.RootPath, "missing");

        Assert.Throws<ArgumentException>(() => TakeoutAnalyzer.Analyze(filePath));
        Assert.Throws<DirectoryNotFoundException>(() => TakeoutAnalyzer.Analyze(missingPath));
    }

    private static IEnumerable<string> MediaPaths(IEnumerable<ParsedMediaResult> results) =>
        results.Select(result => result.MediaEntry.RelativePath);

    private static IEnumerable<string> MediaPaths(IEnumerable<UnmatchedMediaResult> results) =>
        results.Select(result => result.MediaEntry.RelativePath);

    private sealed class TemporaryTakeout : IDisposable
    {
        public TemporaryTakeout()
        {
            RootPath = Path.Combine(Path.GetTempPath(), $"PhotoMigration-{Guid.NewGuid():N}");
            Directory.CreateDirectory(RootPath);
        }

        public string RootPath { get; }

        public string Write(string relativePath, string contents)
        {
            var path = Path.Combine(RootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, contents, Encoding.UTF8);
            return path;
        }

        public IReadOnlyDictionary<string, FileSnapshot> Snapshot() =>
            Directory.EnumerateFiles(RootPath, "*", SearchOption.AllDirectories)
                .ToDictionary(
                    path => Path.GetRelativePath(RootPath, path).Replace(Path.DirectorySeparatorChar, '/'),
                    path => new FileSnapshot(File.ReadAllBytes(path), File.GetLastWriteTimeUtc(path)),
                    StringComparer.Ordinal);

        public void Dispose()
        {
            Directory.Delete(RootPath, recursive: true);
        }
    }

    private sealed record FileSnapshot(byte[] Contents, DateTime LastWriteTimeUtc);
}
