using PhotoMigration.Core;

namespace PhotoMigration.Core.Tests;

public sealed class DestinationPathPlannerTests
{
    [Fact]
    public void Plan_PreservesNestedPathsAndOriginalEntries()
    {
        var roots = Roots();
        var first = Entry("2024/Trip/photo.jpg", 10);
        var second = Entry("video.mp4", 20);

        var result = Assert.IsType<DestinationPathPlanningSuccessResult>(
            DestinationPathPlanner.Plan(roots.Source, roots.Output, [first, second]));

        Assert.Collection(
            result.Items,
            item =>
            {
                Assert.Same(first, item.MediaEntry);
                Assert.Equal(
                    Path.Combine(roots.Source, "2024", "Trip", "photo.jpg"),
                    item.AbsoluteSourcePath);
                Assert.Equal(
                    Path.Combine(roots.Output, "2024", "Trip", "photo.jpg"),
                    item.AbsoluteDestinationPath);
            },
            item =>
            {
                Assert.Same(second, item.MediaEntry);
                Assert.Equal(
                    Path.Combine(roots.Source, "video.mp4"),
                    item.AbsoluteSourcePath);
                Assert.Equal(
                    Path.Combine(roots.Output, "video.mp4"),
                    item.AbsoluteDestinationPath);
            });
        Assert.Equal("2024/Trip/photo.jpg", first.RelativePath);
        Assert.Equal(10, first.SizeInBytes);
        Assert.Equal("video.mp4", second.RelativePath);
        Assert.Equal(20, second.SizeInBytes);
    }

    [Fact]
    public void Plan_PreservesSpacesAndUnicodeFilenames()
    {
        var roots = Roots();
        var entry = Entry("Family Photos/Café 旅行.jpg");

        var result = Assert.IsType<DestinationPathPlanningSuccessResult>(
            DestinationPathPlanner.Plan(roots.Source, roots.Output, [entry]));

        var item = Assert.Single(result.Items);
        Assert.Same(entry, item.MediaEntry);
        Assert.EndsWith(
            Path.Combine("Family Photos", "Café 旅行.jpg"),
            item.AbsoluteDestinationPath,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_ShuffledInputProducesDeterministicOrdinalOrder()
    {
        var roots = Roots();
        var entries = new[]
        {
            Entry("z-last.jpg"),
            Entry("nested/middle.mov"),
            Entry("A-first.heic")
        };

        var forward = Assert.IsType<DestinationPathPlanningSuccessResult>(
            DestinationPathPlanner.Plan(roots.Source, roots.Output, entries));
        var shuffled = Assert.IsType<DestinationPathPlanningSuccessResult>(
            DestinationPathPlanner.Plan(roots.Source, roots.Output, entries.Reverse()));

        Assert.Equal(
            ["A-first.heic", "nested/middle.mov", "z-last.jpg"],
            forward.Items.Select(item => item.MediaEntry.RelativePath));
        Assert.Equal(
            forward.Items.Select(ItemDescription),
            shuffled.Items.Select(ItemDescription));
    }

    [Fact]
    public void Plan_RejectsEqualOrOverlappingRootsInEitherDirection()
    {
        var roots = Roots();

        var equal = Failure(roots.Source, roots.Source, [Entry("photo.jpg")]);
        var outputInsideSource = Failure(
            roots.Source,
            Path.Combine(roots.Source, "output"),
            [Entry("photo.jpg")]);
        var sourceInsideOutput = Failure(
            Path.Combine(roots.Output, "source"),
            roots.Output,
            [Entry("photo.jpg")]);

        Assert.All(
            [equal, outputInsideSource, sourceInsideOutput],
            result => Assert.Equal(
                DestinationPathPlanningIssueKind.OverlappingRoots,
                Assert.Single(result.Issues).Kind));
    }

    [Fact]
    public void Plan_RejectsRootsThatAreNotAbsolute()
    {
        var roots = Roots();

        var invalidSource = Failure("relative-source", roots.Output, []);
        var invalidOutput = Failure(roots.Source, "relative-output", []);

        Assert.Equal(
            DestinationPathPlanningIssueKind.InvalidSourceRoot,
            Assert.Single(invalidSource.Issues).Kind);
        Assert.Equal(
            DestinationPathPlanningIssueKind.InvalidOutputRoot,
            Assert.Single(invalidOutput.Issues).Kind);
    }

    [Theory]
    [InlineData("/absolute/photo.jpg")]
    [InlineData("C:\\absolute\\photo.jpg")]
    public void Plan_RejectsRootedLogicalPaths(string relativePath)
    {
        var roots = Roots();
        var entry = Entry(relativePath);

        var result = Failure(roots.Source, roots.Output, [entry]);

        var issue = Assert.Single(result.Issues);
        Assert.Equal(DestinationPathPlanningIssueKind.RootedLogicalPath, issue.Kind);
        Assert.Same(entry, issue.MediaEntry);
    }

    [Theory]
    [InlineData("./photo.jpg")]
    [InlineData("album/../photo.jpg")]
    public void Plan_RejectsCurrentAndParentTraversalSegments(string relativePath)
    {
        var roots = Roots();

        var result = Failure(roots.Source, roots.Output, [Entry(relativePath)]);

        Assert.Equal(
            DestinationPathPlanningIssueKind.TraversalSegment,
            Assert.Single(result.Issues).Kind);
    }

    [Fact]
    public void Plan_RejectsEmptyLogicalPaths()
    {
        var roots = Roots();

        var result = Failure(roots.Source, roots.Output, [Entry(string.Empty)]);

        Assert.Equal(
            DestinationPathPlanningIssueKind.EmptyLogicalPath,
            Assert.Single(result.Issues).Kind);
    }

    [Fact]
    public void Plan_DetectsDuplicateLogicalInputPaths()
    {
        var roots = Roots();
        var first = Entry("photo.jpg", 10);
        var duplicate = Entry("photo.jpg", 20);

        var result = Failure(roots.Source, roots.Output, [first, duplicate]);

        var issue = Assert.Single(result.Issues);
        Assert.Equal(DestinationPathPlanningIssueKind.DuplicateLogicalPath, issue.Kind);
        Assert.Same(first, issue.MediaEntry);
        Assert.Same(duplicate, issue.ConflictingMediaEntry);
    }

    [Fact]
    public void Plan_DetectsCaseInsensitiveDestinationCollisions()
    {
        var roots = Roots();
        var entries = new[] { Entry("Photo.jpg"), Entry("photo.jpg") };

        var result = Failure(roots.Source, roots.Output, entries);
        var shuffled = Failure(roots.Source, roots.Output, entries.Reverse());

        Assert.Equal(
            DestinationPathPlanningIssueKind.DestinationCollision,
            Assert.Single(result.Issues).Kind);
        Assert.Equal(
            result.Issues.Select(IssueDescription),
            shuffled.Issues.Select(IssueDescription));
    }

    [Fact]
    public void Plan_DetectsUnicodeNormalizationEquivalentDestinationCollisions()
    {
        var roots = Roots();

        var result = Failure(
            roots.Source,
            roots.Output,
            [Entry("Café.jpg"), Entry("Cafe\u0301.jpg")]);

        Assert.Equal(
            DestinationPathPlanningIssueKind.DestinationCollision,
            Assert.Single(result.Issues).Kind);
    }

    [Fact]
    public void Plan_DetectsFileVersusDirectoryDestinationConflicts()
    {
        var roots = Roots();
        var file = Entry("album");
        var child = Entry("album/photo.jpg");

        var result = Failure(roots.Source, roots.Output, [child, file]);

        var issue = Assert.Single(result.Issues);
        Assert.Equal(DestinationPathPlanningIssueKind.FileDirectoryConflict, issue.Kind);
        Assert.Same(file, issue.MediaEntry);
        Assert.Same(child, issue.ConflictingMediaEntry);
    }

    [Fact]
    public void Plan_DoesNotCreateSourceOrOutputDirectoriesOrFiles()
    {
        var roots = Roots();

        var result = DestinationPathPlanner.Plan(
            roots.Source,
            roots.Output,
            [Entry("nested/photo.jpg")]);

        Assert.IsType<DestinationPathPlanningSuccessResult>(result);
        Assert.False(Directory.Exists(roots.Source));
        Assert.False(Directory.Exists(roots.Output));
        Assert.False(File.Exists(Path.Combine(roots.Output, "nested", "photo.jpg")));
    }

    private static DestinationPathPlanningFailureResult Failure(
        string sourceRoot,
        string outputRoot,
        IEnumerable<InventoryEntry> entries) =>
        Assert.IsType<DestinationPathPlanningFailureResult>(
            DestinationPathPlanner.Plan(sourceRoot, outputRoot, entries));

    private static (string Source, string Output) Roots()
    {
        var parent = Path.Combine(
            Path.GetTempPath(),
            $"PhotoMigration-DestinationPathPlanner-{Guid.NewGuid():N}");
        return (
            Path.GetFullPath(Path.Combine(parent, "source")),
            Path.GetFullPath(Path.Combine(parent, "output")));
    }

    private static InventoryEntry Entry(string relativePath, long sizeInBytes = 1) =>
        new(relativePath, sizeInBytes);

    private static string ItemDescription(DestinationPathPlanningItem item) =>
        $"{item.MediaEntry.RelativePath}|{item.AbsoluteSourcePath}|" +
        item.AbsoluteDestinationPath;

    private static string IssueDescription(DestinationPathPlanningIssue issue) =>
        $"{issue.Kind}|{issue.MediaEntry?.RelativePath}|" +
        $"{issue.ConflictingMediaEntry?.RelativePath}|{issue.Message}";
}
