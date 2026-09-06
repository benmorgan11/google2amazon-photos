using PhotoMigration.Core;

namespace PhotoMigration.Core.Tests;

public sealed class TakeoutFileClassifierTests
{
    [Theory]
    [InlineData("photo.JpEg", TakeoutFileCategory.PhotoCandidate)]
    [InlineData("photo.HEIC", TakeoutFileCategory.PhotoCandidate)]
    [InlineData("raw.DnG", TakeoutFileCategory.PhotoCandidate)]
    [InlineData("video.Mp4", TakeoutFileCategory.VideoCandidate)]
    [InlineData("video.MOV", TakeoutFileCategory.VideoCandidate)]
    public void Classify_RecognizesRepresentativeMediaExtensionsIgnoringCase(
        string relativePath,
        TakeoutFileCategory expectedCategory)
    {
        var entry = Entry(relativePath);

        var result = Assert.Single(TakeoutFileClassifier.Classify([entry]));

        Assert.Same(entry, result.Entry);
        Assert.Equal(expectedCategory, result.Category);
    }

    [Fact]
    public void Classify_RecognizesJsonWithoutReadingTheFile()
    {
        var entry = Entry("missing/sidecar.JsOn");

        var result = Assert.Single(TakeoutFileClassifier.Classify([entry]));

        Assert.Same(entry, result.Entry);
        Assert.Equal(TakeoutFileCategory.JsonCandidate, result.Category);
    }

    [Theory]
    [InlineData("notes.txt")]
    [InlineData("README")]
    public void Classify_RetainsUnsupportedAndExtensionlessFilesAsOther(string relativePath)
    {
        var entry = Entry(relativePath);

        var result = Assert.Single(TakeoutFileClassifier.Classify([entry]));

        Assert.Same(entry, result.Entry);
        Assert.Equal(TakeoutFileCategory.Other, result.Category);
    }

    [Fact]
    public void Classify_UsesTheFinalExtensionOfAMultiDotFilename()
    {
        var entry = Entry("vacation.final.JPG");

        var result = Assert.Single(TakeoutFileClassifier.Classify([entry]));

        Assert.Equal(TakeoutFileCategory.PhotoCandidate, result.Category);
    }

    [Fact]
    public void Classify_IgnoresExtensionLikeDirectoryNames()
    {
        var entry = Entry("folder.jpg/video.mp4/notes.unsupported");

        var result = Assert.Single(TakeoutFileClassifier.Classify([entry]));

        Assert.Equal(TakeoutFileCategory.Other, result.Category);
    }

    [Fact]
    public void Classify_ProducesDeterministicOrdinalResultsForShuffledInput()
    {
        var entries = new[]
        {
            Entry("z-last.jpg"),
            Entry("nested/middle.MOV"),
            Entry("A-first.json"),
            Entry("other.txt")
        };

        var forward = TakeoutFileClassifier.Classify(entries);
        var shuffled = TakeoutFileClassifier.Classify(entries.Reverse());

        Assert.Equal(forward, shuffled);
        Assert.Equal(
            ["A-first.json", "nested/middle.MOV", "other.txt", "z-last.jpg"],
            forward.Select(result => result.Entry.RelativePath));
    }

    [Fact]
    public void Classify_RejectsDuplicateLogicalPaths()
    {
        var first = Entry("photo.jpg", 10);
        var second = Entry("photo.jpg", 20);

        var exception = Assert.Throws<ArgumentException>(
            () => TakeoutFileClassifier.Classify([first, second]));

        Assert.Equal("entries", exception.ParamName);
        Assert.Contains("photo.jpg", exception.Message);
    }

    [Fact]
    public void Classify_DoesNotModifyTheSuppliedCollectionOrEntries()
    {
        var first = Entry("z-photo.jpg", 10);
        var second = Entry("A-video.mp4", 20);
        var supplied = new[] { first, second };
        var suppliedBefore = supplied.ToArray();

        var results = TakeoutFileClassifier.Classify(supplied);

        Assert.Equal(suppliedBefore, supplied);
        Assert.Same(first, supplied[0]);
        Assert.Same(second, supplied[1]);
        Assert.Same(second, results[0].Entry);
        Assert.Same(first, results[1].Entry);
        Assert.Equal("z-photo.jpg", first.RelativePath);
        Assert.Equal(10, first.SizeInBytes);
        Assert.Equal("A-video.mp4", second.RelativePath);
        Assert.Equal(20, second.SizeInBytes);
    }

    private static InventoryEntry Entry(string relativePath, long sizeInBytes = 0)
    {
        return new InventoryEntry(relativePath, sizeInBytes);
    }
}
