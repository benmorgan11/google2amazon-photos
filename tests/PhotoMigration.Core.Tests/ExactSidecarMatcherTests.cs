using System.Text;
using PhotoMigration.Core;

namespace PhotoMigration.Core.Tests;

public sealed class ExactSidecarMatcherTests
{
    [Theory]
    [InlineData("IMG_1234.jpg.json", ExactSidecarMatchRule.LegacyJson)]
    [InlineData(
        "IMG_1234.jpg.supplemental-metadata.json",
        ExactSidecarMatchRule.SupplementalMetadataJson)]
    public void Match_ReturnsAnExactMatchForEachSupportedPattern(
        string sidecarPath,
        ExactSidecarMatchRule expectedRule)
    {
        var media = Entry("IMG_1234.jpg", 10);
        var sidecar = Entry(sidecarPath, 20);

        var result = Assert.IsType<MatchedMediaSidecarResult>(
            Assert.Single(ExactSidecarMatcher.Match([media], [sidecar])));

        Assert.Same(media, result.MediaEntry);
        Assert.Same(sidecar, result.SidecarEntry);
        Assert.Equal(expectedRule, result.Rule);
    }

    [Fact]
    public void Match_MatchesOnlyWithinTheSameNestedDirectory()
    {
        var media = Entry("Takeout/2024/IMG_1234.jpg");
        var wrongDirectory = Entry("Takeout/2023/IMG_1234.jpg.json");
        var sameDirectory = Entry("Takeout/2024/IMG_1234.jpg.json");

        var result = Assert.IsType<MatchedMediaSidecarResult>(
            Assert.Single(ExactSidecarMatcher.Match(
                [media],
                [wrongDirectory, sameDirectory])));

        Assert.Same(sameDirectory, result.SidecarEntry);
    }

    [Fact]
    public void Match_HandlesTheSameFilenameIndependentlyInDifferentDirectories()
    {
        var albumMedia = Entry("Album/IMG_1234.jpg");
        var yearMedia = Entry("2024/IMG_1234.jpg");
        var albumSidecar = Entry("Album/IMG_1234.jpg.json");
        var yearSidecar = Entry("2024/IMG_1234.jpg.json");

        var results = ExactSidecarMatcher.Match(
            [yearMedia, albumMedia],
            [albumSidecar, yearSidecar]);

        Assert.Collection(
            results.Cast<MatchedMediaSidecarResult>(),
            result =>
            {
                Assert.Same(yearMedia, result.MediaEntry);
                Assert.Same(yearSidecar, result.SidecarEntry);
            },
            result =>
            {
                Assert.Same(albumMedia, result.MediaEntry);
                Assert.Same(albumSidecar, result.SidecarEntry);
            });
    }

    [Fact]
    public void Match_ReportsBothExactFormsAsAmbiguous()
    {
        var media = Entry("IMG_1234.jpg");
        var legacy = Entry("IMG_1234.jpg.json");
        var supplemental = Entry("IMG_1234.jpg.supplemental-metadata.json");

        var result = Assert.IsType<AmbiguousMediaSidecarResult>(
            Assert.Single(ExactSidecarMatcher.Match(
                [media],
                [supplemental, legacy])));

        Assert.Collection(
            result.Candidates,
            candidate =>
            {
                Assert.Same(legacy, candidate.SidecarEntry);
                Assert.Equal(ExactSidecarMatchRule.LegacyJson, candidate.Rule);
            },
            candidate =>
            {
                Assert.Same(supplemental, candidate.SidecarEntry);
                Assert.Equal(ExactSidecarMatchRule.SupplementalMetadataJson, candidate.Rule);
            });
    }

    [Fact]
    public void Match_LeavesCaseOnlyDifferencesUnmatched()
    {
        var media = Entry("IMG_1234.jpg");
        var sidecar = Entry("img_1234.jpg.json");

        var result = Assert.IsType<UnmatchedMediaResult>(
            Assert.Single(ExactSidecarMatcher.Match([media], [sidecar])));

        Assert.Same(media, result.MediaEntry);
    }

    [Fact]
    public void Match_ProducesDeterministicOrderingForShuffledInputs()
    {
        var media = new[]
        {
            Entry("z-last.jpg"),
            Entry("nested/middle.jpg"),
            Entry("A-first.jpg")
        };
        var sidecars = new[]
        {
            Entry("nested/middle.jpg.supplemental-metadata.json"),
            Entry("z-last.jpg.json")
        };

        var forward = ExactSidecarMatcher.Match(media, sidecars);
        var shuffled = ExactSidecarMatcher.Match(
            media.Reverse(),
            sidecars.Reverse());

        Assert.Equal(forward, shuffled);
        Assert.Equal(
            ["A-first.jpg", "nested/middle.jpg", "z-last.jpg"],
            forward.Select(result => result.MediaEntry.RelativePath));
    }

    [Fact]
    public void Match_ReportsMediaWithoutASidecarAsUnmatched()
    {
        var media = Entry("no-sidecar.mp4");

        var result = Assert.IsType<UnmatchedMediaResult>(
            Assert.Single(ExactSidecarMatcher.Match([media], [])));

        Assert.Same(media, result.MediaEntry);
    }

    [Fact]
    public void Match_DoesNotAssignOneSidecarToTwoMediaCandidates()
    {
        var mediaWithLegacyCandidate = Entry("photo.supplemental-metadata");
        var mediaWithSupplementalCandidate = Entry("photo");
        var sharedSidecar = Entry("photo.supplemental-metadata.json");

        var results = ExactSidecarMatcher.Match(
            [mediaWithLegacyCandidate, mediaWithSupplementalCandidate],
            [sharedSidecar]);

        Assert.All(results, result => Assert.IsType<AmbiguousMediaSidecarResult>(result));
        Assert.All(
            results.Cast<AmbiguousMediaSidecarResult>(),
            result => Assert.Same(sharedSidecar, Assert.Single(result.Candidates).SidecarEntry));
    }

    [Fact]
    public void Match_LeavesSuppliedEntriesAndSourceFilesUnchanged()
    {
        using var fixture = new TemporaryFiles();
        var mediaPath = fixture.Write("IMG_1234.jpg", "synthetic media");
        var sidecarPath = fixture.Write("IMG_1234.jpg.json", "not parsed as JSON");
        var media = Entry("IMG_1234.jpg", new FileInfo(mediaPath).Length);
        var sidecar = Entry("IMG_1234.jpg.json", new FileInfo(sidecarPath).Length);
        var mediaBefore = File.ReadAllBytes(mediaPath);
        var sidecarBefore = File.ReadAllBytes(sidecarPath);
        var mediaLastWriteBefore = File.GetLastWriteTimeUtc(mediaPath);
        var sidecarLastWriteBefore = File.GetLastWriteTimeUtc(sidecarPath);

        var result = Assert.IsType<MatchedMediaSidecarResult>(
            Assert.Single(ExactSidecarMatcher.Match([media], [sidecar])));

        Assert.Same(media, result.MediaEntry);
        Assert.Same(sidecar, result.SidecarEntry);
        Assert.Equal("IMG_1234.jpg", media.RelativePath);
        Assert.Equal("IMG_1234.jpg.json", sidecar.RelativePath);
        Assert.True(File.Exists(mediaPath));
        Assert.True(File.Exists(sidecarPath));
        Assert.Equal(mediaBefore, File.ReadAllBytes(mediaPath));
        Assert.Equal(sidecarBefore, File.ReadAllBytes(sidecarPath));
        Assert.Equal(mediaLastWriteBefore, File.GetLastWriteTimeUtc(mediaPath));
        Assert.Equal(sidecarLastWriteBefore, File.GetLastWriteTimeUtc(sidecarPath));
    }

    private static InventoryEntry Entry(string relativePath, long sizeInBytes = 0)
    {
        return new InventoryEntry(relativePath, sizeInBytes);
    }

    private sealed class TemporaryFiles : IDisposable
    {
        public TemporaryFiles()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"PhotoMigration.Tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string Write(string relativePath, string contents)
        {
            var fullPath = System.IO.Path.Combine(Path, relativePath);
            File.WriteAllText(
                fullPath,
                contents,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return fullPath;
        }

        public void Dispose()
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
