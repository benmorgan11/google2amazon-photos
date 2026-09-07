using System.Security.Cryptography;
using PhotoMigration.Core;

namespace PhotoMigration.Core.Tests;

public sealed class VerifiedMediaFileStagerTests
{
    [Fact]
    public void Stage_CopiesNestedFileAndVerifiesHashesWithoutChangingSourceOrFinalPath()
    {
        using var fixture = new StagingFixture();
        var contents = new byte[] { 0, 1, 2, 3, 127, 128, 255 };
        var sourcePath = fixture.WriteSource("nested/trip/photo.jpg", contents);
        var sourceLastWriteTime = DateTime.UtcNow.AddDays(-7);
        File.SetLastWriteTimeUtc(sourcePath, sourceLastWriteTime);
        sourceLastWriteTime = File.GetLastWriteTimeUtc(sourcePath);
        var unrelatedPath = fixture.WriteOutput("keep.txt", [9, 8, 7]);
        var unrelatedLastWriteTime = File.GetLastWriteTimeUtc(unrelatedPath);
        var item = fixture.PlanItem("nested/trip/photo.jpg", contents.Length);

        var result = Assert.IsType<VerifiedMediaFileStagingSuccessResult>(
            VerifiedMediaFileStager.Stage(
                fixture.SourceRoot,
                fixture.OutputRoot,
                item));

        var expectedHash = Convert.ToHexString(SHA256.HashData(contents));
        Assert.Same(item, result.PlanningItem);
        Assert.Equal(item.AbsoluteDestinationPath, result.IntendedFinalDestinationPath);
        Assert.Equal(contents.Length, result.CopiedByteCount);
        Assert.Equal(expectedHash, result.SourceSha256);
        Assert.Equal(expectedHash, result.TemporaryCopySha256);
        Assert.Equal(contents, File.ReadAllBytes(result.TemporaryCopyPath));
        Assert.Equal(Path.GetDirectoryName(item.AbsoluteDestinationPath),
            Path.GetDirectoryName(result.TemporaryCopyPath));
        Assert.False(File.Exists(item.AbsoluteDestinationPath));

        Assert.Equal(contents, File.ReadAllBytes(sourcePath));
        Assert.Equal(sourceLastWriteTime, File.GetLastWriteTimeUtc(sourcePath));
        Assert.Equal(new byte[] { 9, 8, 7 }, File.ReadAllBytes(unrelatedPath));
        Assert.Equal(unrelatedLastWriteTime, File.GetLastWriteTimeUtc(unrelatedPath));
    }

    [Fact]
    public void Stage_SupportsSpacesAndUnicodePaths()
    {
        using var fixture = new StagingFixture("roots with spaces");
        var relativePath = "Family Photos/Café 旅行.heic";
        var contents = new byte[] { 4, 5, 6 };
        fixture.WriteSource(relativePath, contents);
        var item = fixture.PlanItem(relativePath, contents.Length);

        var result = Assert.IsType<VerifiedMediaFileStagingSuccessResult>(
            VerifiedMediaFileStager.Stage(
                fixture.SourceRoot,
                fixture.OutputRoot,
                item));

        Assert.Equal(contents, File.ReadAllBytes(result.TemporaryCopyPath));
        Assert.EndsWith(".heic", result.TemporaryCopyPath, StringComparison.Ordinal);
        Assert.False(File.Exists(item.AbsoluteDestinationPath));
    }

    [Fact]
    public void Stage_ReturnsMissingSourceWithoutCreatingOutput()
    {
        using var fixture = new StagingFixture();
        var item = fixture.PlanItem("missing/photo.jpg", 10);

        var result = Failure(fixture, item);

        Assert.Equal(VerifiedMediaFileStagingFailureKind.MissingSource, result.FailureKind);
        Assert.False(Directory.Exists(Path.Combine(fixture.OutputRoot, "missing")));
    }

    [Fact]
    public void Stage_RejectsSourceWhoseSizeChangedSinceInventory()
    {
        using var fixture = new StagingFixture();
        var sourcePath = fixture.WriteSource("photo.jpg", [1, 2]);
        var item = fixture.PlanItem("photo.jpg", 2);
        File.WriteAllBytes(sourcePath, [1, 2, 3]);

        var result = Failure(fixture, item);

        Assert.Equal(VerifiedMediaFileStagingFailureKind.SourceChanged, result.FailureKind);
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.OutputRoot));
    }

    [Fact]
    public void Stage_RejectsForgedPlanningItem()
    {
        using var fixture = new StagingFixture();
        fixture.WriteSource("photo.jpg", [1]);
        var planned = fixture.PlanItem("photo.jpg", 1);
        var forged = planned with
        {
            AbsoluteDestinationPath = Path.Combine(fixture.OutputRoot, "different.jpg")
        };

        var result = Failure(fixture, forged);

        Assert.Equal(VerifiedMediaFileStagingFailureKind.InvalidPlan, result.FailureKind);
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.OutputRoot));
    }

    [Fact]
    public void Stage_RejectsOverlappingRoots()
    {
        using var fixture = new StagingFixture();
        var nestedOutput = Directory.CreateDirectory(
            Path.Combine(fixture.SourceRoot, "output")).FullName;
        var sourcePath = fixture.WriteSource("photo.jpg", [1]);
        var entry = new InventoryEntry("photo.jpg", 1);
        var forged = new DestinationPathPlanningItem(
            entry,
            sourcePath,
            Path.Combine(nestedOutput, "photo.jpg"));

        var result = Assert.IsType<VerifiedMediaFileStagingFailureResult>(
            VerifiedMediaFileStager.Stage(fixture.SourceRoot, nestedOutput, forged));

        Assert.Equal(VerifiedMediaFileStagingFailureKind.OverlappingRoots, result.FailureKind);
        Assert.False(File.Exists(forged.AbsoluteDestinationPath));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Stage_RequiresBothRootsToExist(bool removeSourceRoot)
    {
        using var fixture = new StagingFixture();
        fixture.WriteSource("photo.jpg", [1]);
        var item = fixture.PlanItem("photo.jpg", 1);
        Directory.Delete(
            removeSourceRoot ? fixture.SourceRoot : fixture.OutputRoot,
            recursive: true);

        var result = Failure(fixture, item);

        Assert.Equal(VerifiedMediaFileStagingFailureKind.InvalidRoot, result.FailureKind);
    }

    [Fact]
    public void Stage_RejectsLinkedSourceFile_WhenLinksAreSupported()
    {
        using var fixture = new StagingFixture();
        using var outside = new StagingFixture();
        var target = outside.WriteSource("target.jpg", [1, 2, 3]);
        var linkedPath = Path.Combine(fixture.SourceRoot, "linked.jpg");
        if (!TryCreateFileLink(linkedPath, target))
        {
            return;
        }

        var result = Failure(fixture, fixture.PlanItem("linked.jpg", 3));

        Assert.Equal(VerifiedMediaFileStagingFailureKind.LinkedPath, result.FailureKind);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Stage_RejectsLinkedSourceOrOutputRoot_WhenLinksAreSupported(
        bool linkSourceRoot)
    {
        using var fixture = new StagingFixture();
        fixture.WriteSource("photo.jpg", [1]);
        var linkedRoot = Path.Combine(
            fixture.Root,
            linkSourceRoot ? "linked-source-root" : "linked-output-root");
        var targetRoot = linkSourceRoot ? fixture.SourceRoot : fixture.OutputRoot;
        if (!TryCreateDirectoryLink(linkedRoot, targetRoot))
        {
            return;
        }

        var sourceRoot = linkSourceRoot ? linkedRoot : fixture.SourceRoot;
        var outputRoot = linkSourceRoot ? fixture.OutputRoot : linkedRoot;
        var planningResult = Assert.IsType<DestinationPathPlanningSuccessResult>(
            DestinationPathPlanner.Plan(
                sourceRoot,
                outputRoot,
                [new InventoryEntry("photo.jpg", 1)]));

        var result = Assert.IsType<VerifiedMediaFileStagingFailureResult>(
            VerifiedMediaFileStager.Stage(
                sourceRoot,
                outputRoot,
                Assert.Single(planningResult.Items)));

        Assert.Equal(VerifiedMediaFileStagingFailureKind.LinkedPath, result.FailureKind);
    }

    [Fact]
    public void Stage_RejectsLinkedSourceDirectoryComponent_WhenLinksAreSupported()
    {
        using var fixture = new StagingFixture();
        using var outside = new StagingFixture();
        outside.WriteSource("photo.jpg", [1, 2, 3]);
        var linkedDirectory = Path.Combine(fixture.SourceRoot, "linked");
        if (!TryCreateDirectoryLink(linkedDirectory, outside.SourceRoot))
        {
            return;
        }

        var result = Failure(fixture, fixture.PlanItem("linked/photo.jpg", 3));

        Assert.Equal(VerifiedMediaFileStagingFailureKind.LinkedPath, result.FailureKind);
    }

    [Fact]
    public void Stage_RejectsLinkedOutputDirectoryComponent_WhenLinksAreSupported()
    {
        using var fixture = new StagingFixture();
        using var outside = new StagingFixture();
        fixture.WriteSource("linked/photo.jpg", [1, 2, 3]);
        var linkedDirectory = Path.Combine(fixture.OutputRoot, "linked");
        if (!TryCreateDirectoryLink(linkedDirectory, outside.OutputRoot))
        {
            return;
        }

        var result = Failure(fixture, fixture.PlanItem("linked/photo.jpg", 3));

        Assert.Equal(VerifiedMediaFileStagingFailureKind.LinkedPath, result.FailureKind);
        Assert.Empty(Directory.EnumerateFileSystemEntries(outside.OutputRoot));
    }

    [Fact]
    public void Stage_RejectsLinkedFinalDestinationAsExisting_WhenLinksAreSupported()
    {
        using var fixture = new StagingFixture();
        using var outside = new StagingFixture();
        fixture.WriteSource("photo.jpg", [1]);
        var target = outside.WriteOutput("target.jpg", [8, 9]);
        var item = fixture.PlanItem("photo.jpg", 1);
        if (!TryCreateFileLink(item.AbsoluteDestinationPath, target))
        {
            return;
        }

        var result = Failure(fixture, item);

        Assert.Equal(
            VerifiedMediaFileStagingFailureKind.ExistingDestination,
            result.FailureKind);
        Assert.Equal([8, 9], File.ReadAllBytes(target));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Stage_RejectsExistingFinalDestinationFileOrDirectory(bool createDirectory)
    {
        using var fixture = new StagingFixture();
        fixture.WriteSource("photo.jpg", [1]);
        var item = fixture.PlanItem("photo.jpg", 1);
        if (createDirectory)
        {
            Directory.CreateDirectory(item.AbsoluteDestinationPath);
        }
        else
        {
            File.WriteAllBytes(item.AbsoluteDestinationPath, [9]);
        }

        var result = Failure(fixture, item);

        Assert.Equal(
            VerifiedMediaFileStagingFailureKind.ExistingDestination,
            result.FailureKind);
        Assert.True(createDirectory
            ? Directory.Exists(item.AbsoluteDestinationPath)
            : File.Exists(item.AbsoluteDestinationPath));
    }

    [Fact]
    public void Stage_RejectsSourceThatIsNotARegularFile()
    {
        using var fixture = new StagingFixture();
        Directory.CreateDirectory(Path.Combine(fixture.SourceRoot, "photo.jpg"));
        var item = fixture.PlanItem("photo.jpg", 0);

        var result = Failure(fixture, item);

        Assert.Equal(
            VerifiedMediaFileStagingFailureKind.InvalidSourceFile,
            result.FailureKind);
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.OutputRoot));
    }

    [Fact]
    public void Stage_RetriesTemporaryNameCollisionWithoutOverwritingExistingFile()
    {
        using var fixture = new StagingFixture();
        fixture.WriteSource("nested/photo.jpg", [1, 2, 3]);
        var item = fixture.PlanItem("nested/photo.jpg", 3);
        var destinationDirectory = Directory.CreateDirectory(
            Path.GetDirectoryName(item.AbsoluteDestinationPath)!).FullName;
        var collisionPath = Path.Combine(destinationDirectory, "occupied.staging.jpg");
        File.WriteAllBytes(collisionPath, [9, 9]);
        var names = new Queue<string>(
            ["occupied.staging.jpg", "available.staging.jpg"]);

        var result = Assert.IsType<VerifiedMediaFileStagingSuccessResult>(
            VerifiedMediaFileStager.Stage(
                fixture.SourceRoot,
                fixture.OutputRoot,
                item,
                _ => names.Dequeue(),
                _ => { },
                File.Delete));

        Assert.Equal([9, 9], File.ReadAllBytes(collisionPath));
        Assert.Equal(
            Path.Combine(destinationDirectory, "available.staging.jpg"),
            result.TemporaryCopyPath);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(result.TemporaryCopyPath));
    }

    [Fact]
    public void Stage_VerificationFailureDeletesOnlyItsTemporaryFile()
    {
        using var fixture = new StagingFixture();
        fixture.WriteSource("photo.jpg", [1, 2, 3]);
        var unrelatedPath = fixture.WriteOutput("unrelated.txt", [7, 8, 9]);
        var item = fixture.PlanItem("photo.jpg", 3);

        var result = Assert.IsType<VerifiedMediaFileStagingFailureResult>(
            VerifiedMediaFileStager.Stage(
                fixture.SourceRoot,
                fixture.OutputRoot,
                item,
                _ => "temporary.staging.jpg",
                path => File.WriteAllBytes(path, [3, 2, 1]),
                File.Delete));

        Assert.Equal(
            VerifiedMediaFileStagingFailureKind.VerificationFailure,
            result.FailureKind);
        Assert.Null(result.TemporaryCopyPath);
        Assert.False(File.Exists(Path.Combine(fixture.OutputRoot, "temporary.staging.jpg")));
        Assert.Equal([7, 8, 9], File.ReadAllBytes(unrelatedPath));
        Assert.False(File.Exists(item.AbsoluteDestinationPath));
    }

    [Fact]
    public void Stage_CleanupFailureRetainsTemporaryPathAndOriginalFailureKind()
    {
        using var fixture = new StagingFixture();
        fixture.WriteSource("photo.jpg", [1, 2, 3]);
        var item = fixture.PlanItem("photo.jpg", 3);

        var result = Assert.IsType<VerifiedMediaFileStagingFailureResult>(
            VerifiedMediaFileStager.Stage(
                fixture.SourceRoot,
                fixture.OutputRoot,
                item,
                _ => "temporary.staging.jpg",
                path => File.WriteAllBytes(path, [3, 2, 1]),
                _ => throw new IOException("synthetic cleanup failure")));

        Assert.Equal(VerifiedMediaFileStagingFailureKind.CleanupFailure, result.FailureKind);
        Assert.Equal(
            VerifiedMediaFileStagingFailureKind.VerificationFailure,
            result.FailureBeforeCleanup);
        Assert.NotNull(result.TemporaryCopyPath);
        Assert.True(File.Exists(result.TemporaryCopyPath));
        Assert.Contains("synthetic cleanup failure", result.Message);
        Assert.False(File.Exists(item.AbsoluteDestinationPath));
    }

    [Fact]
    public void Stage_ReportsDirectoryCreationFailureWithoutRemovingExistingFile()
    {
        using var fixture = new StagingFixture();
        fixture.WriteSource("blocked/photo.jpg", [1]);
        var blockingPath = fixture.WriteOutput("blocked", [6, 6]);
        var item = fixture.PlanItem("blocked/photo.jpg", 1);

        var result = Failure(fixture, item);

        Assert.Equal(
            VerifiedMediaFileStagingFailureKind.DirectoryCreationFailure,
            result.FailureKind);
        Assert.Equal([6, 6], File.ReadAllBytes(blockingPath));
        Assert.False(File.Exists(item.AbsoluteDestinationPath));
    }

    private static VerifiedMediaFileStagingFailureResult Failure(
        StagingFixture fixture,
        DestinationPathPlanningItem item) =>
        Assert.IsType<VerifiedMediaFileStagingFailureResult>(
            VerifiedMediaFileStager.Stage(
                fixture.SourceRoot,
                fixture.OutputRoot,
                item));

    private static bool TryCreateFileLink(string linkPath, string targetPath)
    {
        try
        {
            File.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception exception) when (CannotCreateLink(exception))
        {
            return false;
        }
    }

    private static bool TryCreateDirectoryLink(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception exception) when (CannotCreateLink(exception))
        {
            return false;
        }
    }

    private static bool CannotCreateLink(Exception exception) =>
        exception is UnauthorizedAccessException
            or IOException
            or PlatformNotSupportedException;

    private sealed class StagingFixture : IDisposable
    {
        public StagingFixture(string? directoryName = null)
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                directoryName ?? $"PhotoMigration-Stager-{Guid.NewGuid():N}",
                Guid.NewGuid().ToString("N"));
            SourceRoot = Directory.CreateDirectory(Path.Combine(Root, "source")).FullName;
            OutputRoot = Directory.CreateDirectory(Path.Combine(Root, "output")).FullName;
        }

        public string Root { get; }

        public string SourceRoot { get; }

        public string OutputRoot { get; }

        public string WriteSource(string relativePath, byte[] contents) =>
            Write(SourceRoot, relativePath, contents);

        public string WriteOutput(string relativePath, byte[] contents) =>
            Write(OutputRoot, relativePath, contents);

        public DestinationPathPlanningItem PlanItem(
            string relativePath,
            long sizeInBytes)
        {
            var result = Assert.IsType<DestinationPathPlanningSuccessResult>(
                DestinationPathPlanner.Plan(
                    SourceRoot,
                    OutputRoot,
                    [new InventoryEntry(relativePath, sizeInBytes)]));
            return Assert.Single(result.Items);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }

        private static string Write(string root, string relativePath, byte[] contents)
        {
            var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, contents);
            return path;
        }
    }
}
