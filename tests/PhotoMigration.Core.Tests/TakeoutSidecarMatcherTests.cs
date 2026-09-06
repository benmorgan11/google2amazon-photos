using System.Text;
using PhotoMigration.Core;

namespace PhotoMigration.Core.Tests;

public sealed class TakeoutSidecarMatcherTests
{
    [Theory]
    [InlineData("IMG_1234.jpg.json", SidecarMatchRule.LegacyJson)]
    [InlineData(
        "IMG_1234.jpg.supplemental-metadata.json",
        SidecarMatchRule.SupplementalMetadataJson)]
    public void Match_ContinuesToSupportExactPatterns(
        string sidecarPath,
        SidecarMatchRule expectedRule)
    {
        var media = Entry("IMG_1234.jpg", 10);
        var sidecar = Entry(sidecarPath, 20);

        var result = Assert.IsType<MatchedMediaSidecarResult>(
            Assert.Single(TakeoutSidecarMatcher.Match([media], [sidecar])));

        Assert.Same(media, result.MediaEntry);
        Assert.Same(sidecar, result.SidecarEntry);
        Assert.Equal(expectedRule, result.Rule);
    }

    [Theory]
    [InlineData("README", "README.json", SidecarMatchRule.LegacyJson)]
    [InlineData(
        ".hidden",
        ".hidden.supplemental-metadata.json",
        SidecarMatchRule.SupplementalMetadataJson)]
    public void Match_ChecksExactRulesWithoutRequiringANormalExtension(
        string mediaPath,
        string sidecarPath,
        SidecarMatchRule expectedRule)
    {
        var media = Entry(mediaPath);
        var sidecar = Entry(sidecarPath);

        var result = Assert.IsType<MatchedMediaSidecarResult>(
            Assert.Single(TakeoutSidecarMatcher.Match([media], [sidecar])));

        Assert.Same(media, result.MediaEntry);
        Assert.Same(sidecar, result.SidecarEntry);
        Assert.Equal(expectedRule, result.Rule);
    }

    [Theory]
    [InlineData(
        "photo(1).jpg",
        "photo.jpg.supplemental-metadata(1).json")]
    [InlineData(
        "video(123).mp4",
        "video.mp4.supplemental-metadata(123).json")]
    [InlineData(
        "Negative0-36-35(1)(1).jpg",
        "Negative0-36-35(1).jpg.supplemental-metadata(1).json")]
    public void Match_SupportsPositiveDuplicateNumbersAndPreservesEarlierParentheses(
        string mediaPath,
        string sidecarPath)
    {
        var media = Entry(mediaPath);
        var sidecar = Entry(sidecarPath);

        var result = Assert.IsType<MatchedMediaSidecarResult>(
            Assert.Single(TakeoutSidecarMatcher.Match([media], [sidecar])));

        Assert.Same(sidecar, result.SidecarEntry);
        Assert.Equal(SidecarMatchRule.DuplicateNumber, result.Rule);
    }

    [Theory]
    [InlineData(
        "Peanut Butter Balls.jpg",
        "Peanut Butter Balls.supplemental-metadata.json")]
    [InlineData(
        "vacation.final.jpg",
        "vacation.final.supplemental-metadata.json")]
    public void Match_SupportsUniqueExtensionOmittedSidecars(
        string mediaPath,
        string sidecarPath)
    {
        var media = Entry(mediaPath);
        var sidecar = Entry(sidecarPath);

        var result = Assert.IsType<MatchedMediaSidecarResult>(
            Assert.Single(TakeoutSidecarMatcher.Match([media], [sidecar])));

        Assert.Same(sidecar, result.SidecarEntry);
        Assert.Equal(SidecarMatchRule.ExtensionOmitted, result.Rule);
    }

    [Fact]
    public void Match_ReportsDifferentMediaExtensionsClaimingOneSidecarAsAmbiguous()
    {
        var jpg = Entry("photo.jpg");
        var png = Entry("photo.png");
        var sidecar = Entry("photo.supplemental-metadata.json");

        var results = TakeoutSidecarMatcher.Match([png, jpg], [sidecar]);

        Assert.All(results, result => Assert.IsType<AmbiguousMediaSidecarResult>(result));
        Assert.Equal(
            ["photo.jpg", "photo.png"],
            results.Select(result => result.MediaEntry.RelativePath));
        Assert.All(
            results.Cast<AmbiguousMediaSidecarResult>(),
            result =>
            {
                var candidate = Assert.Single(result.Candidates);
                Assert.Same(sidecar, candidate.SidecarEntry);
                Assert.Equal(SidecarMatchRule.ExtensionOmitted, candidate.Rule);
            });
    }

    [Fact]
    public void Match_KeepsExtensionOmittedMatchesWithinTheSameLogicalDirectory()
    {
        var yearMedia = Entry("2024/photo.jpg");
        var albumMedia = Entry("Album/photo.jpg");
        var yearSidecar = Entry("2024/photo.supplemental-metadata.json");

        var results = TakeoutSidecarMatcher.Match(
            [albumMedia, yearMedia],
            [yearSidecar]);

        var yearResult = Assert.IsType<MatchedMediaSidecarResult>(results[0]);
        Assert.Same(yearMedia, yearResult.MediaEntry);
        Assert.Same(yearSidecar, yearResult.SidecarEntry);
        Assert.IsType<UnmatchedMediaResult>(results[1]);
        Assert.Same(albumMedia, results[1].MediaEntry);
    }

    [Fact]
    public void Match_LeavesCaseOnlyExtensionOmittedDifferencesUnmatched()
    {
        var media = Entry("photo.jpg");
        var sidecar = Entry("Photo.supplemental-metadata.json");

        var result = Assert.IsType<UnmatchedMediaResult>(
            Assert.Single(TakeoutSidecarMatcher.Match([media], [sidecar])));

        Assert.Same(media, result.MediaEntry);
    }

    [Fact]
    public void Match_ReportsAnExactAndExtensionOmittedCandidateAsAmbiguous()
    {
        var media = Entry("photo.jpg");
        var exact = Entry("photo.jpg.json");
        var extensionOmitted = Entry("photo.supplemental-metadata.json");

        var result = Assert.IsType<AmbiguousMediaSidecarResult>(
            Assert.Single(TakeoutSidecarMatcher.Match(
                [media],
                [extensionOmitted, exact])));

        Assert.Equal(2, result.Candidates.Count);
        Assert.Contains(
            result.Candidates,
            candidate => ReferenceEquals(candidate.SidecarEntry, exact)
                         && candidate.Rule == SidecarMatchRule.LegacyJson);
        Assert.Contains(
            result.Candidates,
            candidate => ReferenceEquals(candidate.SidecarEntry, extensionOmitted)
                         && candidate.Rule == SidecarMatchRule.ExtensionOmitted);
    }

    [Theory]
    [InlineData("README", "supplemental-metadata.json")]
    [InlineData(".hidden", ".supplemental-metadata.json")]
    [InlineData(".photo.jpg", ".photo.supplemental-metadata.json")]
    public void Match_DoesNotTransformExtensionlessNamesOrDotfiles(
        string mediaPath,
        string sidecarPath)
    {
        var media = Entry(mediaPath);

        var result = Assert.IsType<UnmatchedMediaResult>(
            Assert.Single(TakeoutSidecarMatcher.Match(
                [media],
                [Entry(sidecarPath)])));

        Assert.Same(media, result.MediaEntry);
    }

    [Fact]
    public void Match_DoesNotCombineDuplicateNumberAndExtensionOmission()
    {
        var media = Entry("photo(1).jpg");
        var combinedSidecar = Entry("photo.supplemental-metadata(1).json");

        var result = Assert.IsType<UnmatchedMediaResult>(
            Assert.Single(TakeoutSidecarMatcher.Match([media], [combinedSidecar])));

        Assert.Same(media, result.MediaEntry);
    }

    [Theory]
    [InlineData(
        "photo(0).jpg",
        "photo.jpg.supplemental-metadata(0).json")]
    [InlineData(
        "photo(01).jpg",
        "photo.jpg.supplemental-metadata(01).json")]
    [InlineData(
        "photo(1)-copy.jpg",
        "photo-copy.jpg.supplemental-metadata(1).json")]
    public void Match_DoesNotTransformUnsupportedNumberSuffixes(
        string mediaPath,
        string sidecarPath)
    {
        var media = Entry(mediaPath);

        var result = Assert.IsType<UnmatchedMediaResult>(
            Assert.Single(TakeoutSidecarMatcher.Match(
                [media],
                [Entry(sidecarPath)])));

        Assert.Same(media, result.MediaEntry);
    }

    [Fact]
    public void Match_KeepsDuplicateNumberMatchesWithinTheSameLogicalDirectory()
    {
        var yearMedia = Entry("2024/photo(1).jpg");
        var albumMedia = Entry("Album/photo(1).jpg");
        var yearSidecar = Entry("2024/photo.jpg.supplemental-metadata(1).json");

        var results = TakeoutSidecarMatcher.Match(
            [albumMedia, yearMedia],
            [yearSidecar]);

        var yearResult = Assert.IsType<MatchedMediaSidecarResult>(results[0]);
        Assert.Same(yearMedia, yearResult.MediaEntry);
        Assert.Same(yearSidecar, yearResult.SidecarEntry);
        Assert.IsType<UnmatchedMediaResult>(results[1]);
        Assert.Same(albumMedia, results[1].MediaEntry);
    }

    [Fact]
    public void Match_LeavesCaseOnlyDuplicateNumberDifferencesUnmatched()
    {
        var media = Entry("photo(1).jpg");
        var sidecar = Entry("Photo.jpg.supplemental-metadata(1).json");

        var result = Assert.IsType<UnmatchedMediaResult>(
            Assert.Single(TakeoutSidecarMatcher.Match([media], [sidecar])));

        Assert.Same(media, result.MediaEntry);
    }

    [Fact]
    public void Match_ReportsAnExactAndDuplicateNumberCandidateAsAmbiguous()
    {
        var media = Entry("photo(1).jpg");
        var exact = Entry("photo(1).jpg.supplemental-metadata.json");
        var transformed = Entry("photo.jpg.supplemental-metadata(1).json");

        var result = Assert.IsType<AmbiguousMediaSidecarResult>(
            Assert.Single(TakeoutSidecarMatcher.Match(
                [media],
                [transformed, exact])));

        Assert.Equal(2, result.Candidates.Count);
        Assert.Contains(
            result.Candidates,
            candidate => ReferenceEquals(candidate.SidecarEntry, exact)
                         && candidate.Rule == SidecarMatchRule.SupplementalMetadataJson);
        Assert.Contains(
            result.Candidates,
            candidate => ReferenceEquals(candidate.SidecarEntry, transformed)
                         && candidate.Rule == SidecarMatchRule.DuplicateNumber);
    }

    [Fact]
    public void Match_ReportsBothExactFormsAsAmbiguous()
    {
        var media = Entry("IMG_1234.jpg");
        var legacy = Entry("IMG_1234.jpg.json");
        var supplemental = Entry("IMG_1234.jpg.supplemental-metadata.json");

        var result = Assert.IsType<AmbiguousMediaSidecarResult>(
            Assert.Single(TakeoutSidecarMatcher.Match(
                [media],
                [supplemental, legacy])));

        Assert.Collection(
            result.Candidates,
            candidate =>
            {
                Assert.Same(legacy, candidate.SidecarEntry);
                Assert.Equal(SidecarMatchRule.LegacyJson, candidate.Rule);
            },
            candidate =>
            {
                Assert.Same(supplemental, candidate.SidecarEntry);
                Assert.Equal(SidecarMatchRule.SupplementalMetadataJson, candidate.Rule);
            });
    }

    [Fact]
    public void Match_ProducesDeterministicResultsForShuffledInputs()
    {
        var media = new[]
        {
            Entry("z-last(12).jpg"),
            Entry("nested/middle.jpg"),
            Entry("A-first.jpg")
        };
        var sidecars = new[]
        {
            Entry("nested/middle.jpg.json"),
            Entry("z-last.jpg.supplemental-metadata(12).json"),
            Entry("A-first.supplemental-metadata.json")
        };

        var forward = TakeoutSidecarMatcher.Match(media, sidecars);
        var shuffled = TakeoutSidecarMatcher.Match(
            media.Reverse(),
            sidecars.Reverse());

        Assert.Equal(forward, shuffled);
        Assert.Equal(
            ["A-first.jpg", "nested/middle.jpg", "z-last(12).jpg"],
            forward.Select(result => result.MediaEntry.RelativePath));
    }

    [Fact]
    public void Match_ReportsMediaWithoutASidecarAsUnmatched()
    {
        var media = Entry("no-sidecar.mp4");

        var result = Assert.IsType<UnmatchedMediaResult>(
            Assert.Single(TakeoutSidecarMatcher.Match([media], [])));

        Assert.Same(media, result.MediaEntry);
    }

    [Fact]
    public void Match_DoesNotAssignOneSidecarToTwoMediaCandidates()
    {
        var mediaWithDuplicateCandidate = Entry("photo(1).jpg");
        var mediaWithLegacyCandidate = Entry("photo.jpg.supplemental-metadata(1)");
        var sharedSidecar = Entry("photo.jpg.supplemental-metadata(1).json");

        var results = TakeoutSidecarMatcher.Match(
            [mediaWithLegacyCandidate, mediaWithDuplicateCandidate],
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
        var mediaPath = fixture.Write("photo.jpg", "synthetic media");
        var sidecarPath = fixture.Write(
            "photo.supplemental-metadata.json",
            "not parsed as JSON");
        var media = Entry("photo.jpg", new FileInfo(mediaPath).Length);
        var sidecar = Entry(
            "photo.supplemental-metadata.json",
            new FileInfo(sidecarPath).Length);
        var mediaBefore = File.ReadAllBytes(mediaPath);
        var sidecarBefore = File.ReadAllBytes(sidecarPath);
        var mediaLastWriteBefore = File.GetLastWriteTimeUtc(mediaPath);
        var sidecarLastWriteBefore = File.GetLastWriteTimeUtc(sidecarPath);

        var result = Assert.IsType<MatchedMediaSidecarResult>(
            Assert.Single(TakeoutSidecarMatcher.Match([media], [sidecar])));

        Assert.Same(media, result.MediaEntry);
        Assert.Same(sidecar, result.SidecarEntry);
        Assert.Equal("photo.jpg", media.RelativePath);
        Assert.Equal("photo.supplemental-metadata.json", sidecar.RelativePath);
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
