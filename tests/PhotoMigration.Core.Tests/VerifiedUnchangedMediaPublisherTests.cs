using System.Security.Cryptography;
using PhotoMigration.Core;

namespace PhotoMigration.Core.Tests;

public sealed class VerifiedUnchangedMediaPublisherTests
{
    [Theory]
    [InlineData("Family Photos/Café image.jpg")]
    [InlineData("Videos/旅行 clip.mp4")]
    public void Publish_AnyFormatPreservesExactBytesHashAndSource(string relativePath)
    {
        using var fixture = new PublisherFixture();
        byte[] contents = [0, 1, 2, 3, 0xFE, 0xFF];
        var context = fixture.Stage(relativePath, contents);
        var sourceBytes = File.ReadAllBytes(context.SourcePath);
        var sourceLastWriteTime = File.GetLastWriteTimeUtc(context.SourcePath);
        var expectedHash = Convert.ToHexString(SHA256.HashData(contents));

        var result = Assert.IsType<VerifiedUnchangedMediaPublicationSuccessResult>(
            VerifiedUnchangedMediaPublisher.Publish(
                fixture.OutputRoot, context.StagingResult));

        Assert.Same(context.StagingResult, result.StagingResult);
        Assert.Equal(context.StagingResult.TemporaryCopyPath, result.FormerTemporaryPath);
        Assert.Equal(
            context.StagingResult.IntendedFinalDestinationPath,
            result.FinalDestinationPath);
        Assert.Equal(contents.Length, result.PublishedByteCount);
        Assert.Equal(expectedHash, result.PublishedSha256);
        Assert.False(File.Exists(result.FormerTemporaryPath));
        Assert.True(File.Exists(result.FinalDestinationPath));
        Assert.Equal(contents, File.ReadAllBytes(result.FinalDestinationPath));
        Assert.Equal(sourceBytes, File.ReadAllBytes(context.SourcePath));
        Assert.Equal(sourceLastWriteTime, File.GetLastWriteTimeUtc(context.SourcePath));
    }

    [Fact]
    public void Publish_ChangedTemporaryContentsAreRejected()
    {
        using var fixture = new PublisherFixture();
        var context = fixture.Stage("photo.jpg", [1, 2, 3, 4]);
        File.WriteAllBytes(context.StagingResult.TemporaryCopyPath, [4, 3, 2, 1]);

        var result = Assert.IsType<VerifiedUnchangedMediaPublicationFailureResult>(
            VerifiedUnchangedMediaPublisher.Publish(
                fixture.OutputRoot, context.StagingResult));

        Assert.Equal(VerifiedUnchangedMediaPublicationFailureKind.ChangedTemporaryFile,
            result.FailureKind);
        Assert.Equal(4, result.ActualByteCount);
        Assert.NotEqual(context.StagingResult.TemporaryCopySha256, result.ActualSha256);
        Assert.True(File.Exists(result.FormerTemporaryPath));
        Assert.False(File.Exists(result.FinalDestinationPath));
    }

    [Fact]
    public void Publish_MissingOrLinkedTemporaryFileIsRejected()
    {
        using var missingFixture = new PublisherFixture();
        var missing = missingFixture.Stage("missing.mov", [1]);
        File.Delete(missing.StagingResult.TemporaryCopyPath);
        var missingResult = Assert.IsType<VerifiedUnchangedMediaPublicationFailureResult>(
            VerifiedUnchangedMediaPublisher.Publish(
                missingFixture.OutputRoot, missing.StagingResult));
        Assert.Equal(VerifiedUnchangedMediaPublicationFailureKind.MissingTemporaryFile,
            missingResult.FailureKind);

        using var linkedFixture = new PublisherFixture();
        var linked = linkedFixture.Stage("linked.heic", [1, 2, 3]);
        var target = Path.Combine(linkedFixture.RootPath, "target.heic");
        File.WriteAllBytes(target, [1, 2, 3]);
        File.Delete(linked.StagingResult.TemporaryCopyPath);
        if (!TryCreateFileLink(linked.StagingResult.TemporaryCopyPath, target)) return;

        var linkedResult = Assert.IsType<VerifiedUnchangedMediaPublicationFailureResult>(
            VerifiedUnchangedMediaPublisher.Publish(
                linkedFixture.OutputRoot, linked.StagingResult));
        Assert.Equal(VerifiedUnchangedMediaPublicationFailureKind.LinkedPath,
            linkedResult.FailureKind);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(target));
    }

    [Fact]
    public void Publish_ExistingDestinationIsNeverOverwritten()
    {
        using var fixture = new PublisherFixture();
        var context = fixture.Stage("photo.png", [1, 2, 3]);
        byte[] existingBytes = [9, 8, 7];
        File.WriteAllBytes(
            context.StagingResult.IntendedFinalDestinationPath,
            existingBytes);

        var result = Assert.IsType<VerifiedUnchangedMediaPublicationFailureResult>(
            VerifiedUnchangedMediaPublisher.Publish(
                fixture.OutputRoot, context.StagingResult));

        Assert.Equal(VerifiedUnchangedMediaPublicationFailureKind.ExistingDestination,
            result.FailureKind);
        Assert.Equal(existingBytes, File.ReadAllBytes(result.FinalDestinationPath));
        Assert.True(File.Exists(result.FormerTemporaryPath));
    }

    [Fact]
    public void Publish_LinkedOutputComponentIsRejected()
    {
        using var fixture = new PublisherFixture();
        var context = fixture.Stage("Album/photo.webp", [1, 2, 3]);
        var albumPath = Path.Combine(fixture.OutputRoot, "Album");
        var targetPath = Path.Combine(fixture.RootPath, "linked target");
        Directory.Move(albumPath, targetPath);
        if (!TryCreateDirectoryLink(albumPath, targetPath)) return;

        var result = Assert.IsType<VerifiedUnchangedMediaPublicationFailureResult>(
            VerifiedUnchangedMediaPublisher.Publish(
                fixture.OutputRoot, context.StagingResult));

        Assert.Equal(VerifiedUnchangedMediaPublicationFailureKind.LinkedPath,
            result.FailureKind);
        Assert.True(File.Exists(context.StagingResult.TemporaryCopyPath));
        Assert.False(File.Exists(context.StagingResult.IntendedFinalDestinationPath));
    }

    [Fact]
    public void Publish_MoveFailureLeavesTemporaryFileWhenMoveDoesNotComplete()
    {
        using var fixture = new PublisherFixture();
        var context = fixture.Stage("photo.tiff", [1, 2, 3]);
        byte[] collisionBytes = [7, 8, 9];

        var result = Assert.IsType<VerifiedUnchangedMediaPublicationFailureResult>(
            VerifiedUnchangedMediaPublisher.Publish(
                fixture.OutputRoot,
                context.StagingResult,
                (temporaryPath, finalPath) =>
                {
                    File.WriteAllBytes(finalPath, collisionBytes);
                    File.Move(temporaryPath, finalPath, overwrite: false);
                }));

        Assert.Equal(VerifiedUnchangedMediaPublicationFailureKind.MoveFailure,
            result.FailureKind);
        Assert.True(File.Exists(context.StagingResult.TemporaryCopyPath));
        Assert.Equal(collisionBytes,
            File.ReadAllBytes(context.StagingResult.IntendedFinalDestinationPath));
    }

    [Fact]
    public void Publish_InvalidOutputRootIsRejectedWithoutMoving()
    {
        using var fixture = new PublisherFixture();
        var context = fixture.Stage("photo.gif", [1, 2, 3]);

        var result = Assert.IsType<VerifiedUnchangedMediaPublicationFailureResult>(
            VerifiedUnchangedMediaPublisher.Publish(
                "relative-output", context.StagingResult));

        Assert.Equal(VerifiedUnchangedMediaPublicationFailureKind.InvalidOutputRoot,
            result.FailureKind);
        Assert.True(File.Exists(context.StagingResult.TemporaryCopyPath));
        Assert.False(File.Exists(context.StagingResult.IntendedFinalDestinationPath));
    }

    private static bool TryCreateFileLink(string linkPath, string targetPath)
    {
        try
        {
            File.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or PlatformNotSupportedException)
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
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or PlatformNotSupportedException)
        {
            return false;
        }
    }

    private sealed class PublisherFixture : IDisposable
    {
        public PublisherFixture()
        {
            RootPath = Path.Combine(
                Path.GetTempPath(),
                "PhotoMigration-VerifiedUnchangedPublisherTests",
                Guid.NewGuid().ToString("N"));
            SourceRoot = Path.Combine(RootPath, "source");
            OutputRoot = Path.Combine(RootPath, "output");
            Directory.CreateDirectory(SourceRoot);
            Directory.CreateDirectory(OutputRoot);
        }

        public string RootPath { get; }

        public string SourceRoot { get; }

        public string OutputRoot { get; }

        public StagingContext Stage(string relativePath, byte[] contents)
        {
            var sourcePath = Combine(SourceRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
            File.WriteAllBytes(sourcePath, contents);
            File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddDays(-5));
            var entry = new InventoryEntry(relativePath, contents.Length);
            var plan = Assert.IsType<DestinationPathPlanningSuccessResult>(
                DestinationPathPlanner.Plan(SourceRoot, OutputRoot, [entry]));
            var staging = Assert.IsType<VerifiedMediaFileStagingSuccessResult>(
                VerifiedMediaFileStager.Stage(
                    SourceRoot,
                    OutputRoot,
                    Assert.Single(plan.Items)));
            return new StagingContext(staging, sourcePath);
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

    private sealed record StagingContext(
        VerifiedMediaFileStagingSuccessResult StagingResult,
        string SourcePath);
}
