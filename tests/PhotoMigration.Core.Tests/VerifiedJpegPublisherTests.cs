using PhotoMigration.Core;

namespace PhotoMigration.Core.Tests;

public sealed class VerifiedJpegPublisherTests
{
    private const string MediaHash =
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    [Fact]
    public void Publish_MovesVerifiedFileWithoutChangingBytesOrSource()
    {
        using var fixture = new PublisherFixture("Publisher 旅行");
        var context = fixture.CreateVerifiedResult("Family Photos/Café image.jpg");
        var temporaryBytes = File.ReadAllBytes(context.VerifiedResult.TemporaryCopyPath);
        var sourceBytes = File.ReadAllBytes(context.SourcePath);
        var sourceLastWriteTime = File.GetLastWriteTimeUtc(context.SourcePath);

        var result = Assert.IsType<VerifiedJpegPublicationSuccessResult>(
            VerifiedJpegPublisher.Publish(fixture.OutputRoot, context.VerifiedResult));

        Assert.Same(context.VerifiedResult, result.VerificationResult);
        Assert.Equal(context.VerifiedResult.TemporaryCopyPath, result.FormerTemporaryPath);
        Assert.Equal(
            context.VerifiedResult.IntendedFinalDestinationPath,
            result.FinalDestinationPath);
        Assert.Equal(temporaryBytes.Length, result.PublishedByteCount);
        Assert.False(File.Exists(result.FormerTemporaryPath));
        Assert.True(File.Exists(result.FinalDestinationPath));
        Assert.Equal(temporaryBytes, File.ReadAllBytes(result.FinalDestinationPath));
        Assert.Equal(sourceBytes, File.ReadAllBytes(context.SourcePath));
        Assert.Equal(sourceLastWriteTime, File.GetLastWriteTimeUtc(context.SourcePath));
    }

    [Fact]
    public void Publish_ExistingDestinationIsNeverOverwritten()
    {
        using var fixture = new PublisherFixture();
        var context = fixture.CreateVerifiedResult("photo.jpg");
        byte[] existingBytes = [9, 8, 7];
        File.WriteAllBytes(
            context.VerifiedResult.IntendedFinalDestinationPath,
            existingBytes);

        var result = Assert.IsType<VerifiedJpegPublicationFailureResult>(
            VerifiedJpegPublisher.Publish(fixture.OutputRoot, context.VerifiedResult));

        Assert.Equal(VerifiedJpegPublicationFailureKind.ExistingDestination,
            result.FailureKind);
        Assert.Equal(existingBytes, File.ReadAllBytes(result.FinalDestinationPath));
        Assert.True(File.Exists(result.FormerTemporaryPath));
    }

    [Fact]
    public void Publish_MissingLinkedOrNonRegularTemporaryPathReturnsTypedFailure()
    {
        using var missingFixture = new PublisherFixture();
        var missing = missingFixture.CreateVerifiedResult("missing.jpg");
        File.Delete(missing.VerifiedResult.TemporaryCopyPath);
        var missingResult = Assert.IsType<VerifiedJpegPublicationFailureResult>(
            VerifiedJpegPublisher.Publish(
                missingFixture.OutputRoot, missing.VerifiedResult));
        Assert.Equal(VerifiedJpegPublicationFailureKind.MissingTemporaryFile,
            missingResult.FailureKind);

        using var linkedFixture = new PublisherFixture();
        var linked = linkedFixture.CreateVerifiedResult("linked.jpg");
        var target = Path.Combine(linkedFixture.RootPath, "target.jpg");
        File.WriteAllBytes(target, [1, 2, 3]);
        File.Delete(linked.VerifiedResult.TemporaryCopyPath);
        if (TryCreateFileLink(linked.VerifiedResult.TemporaryCopyPath, target))
        {
            var linkedResult = Assert.IsType<VerifiedJpegPublicationFailureResult>(
                VerifiedJpegPublisher.Publish(
                    linkedFixture.OutputRoot, linked.VerifiedResult));
            Assert.Equal(VerifiedJpegPublicationFailureKind.LinkedPath,
                linkedResult.FailureKind);
            Assert.Equal([1, 2, 3], File.ReadAllBytes(target));
        }

        using var directoryFixture = new PublisherFixture();
        var directory = directoryFixture.CreateVerifiedResult("directory.jpg");
        File.Delete(directory.VerifiedResult.TemporaryCopyPath);
        Directory.CreateDirectory(directory.VerifiedResult.TemporaryCopyPath);
        var directoryResult = Assert.IsType<VerifiedJpegPublicationFailureResult>(
            VerifiedJpegPublisher.Publish(
                directoryFixture.OutputRoot, directory.VerifiedResult));
        Assert.Equal(VerifiedJpegPublicationFailureKind.NonRegularTemporaryFile,
            directoryResult.FailureKind);
    }

    [Fact]
    public void Publish_LinkedOutputComponentPreventsMove()
    {
        using var fixture = new PublisherFixture();
        var context = fixture.CreateVerifiedResult("Album/photo.jpg");
        var albumPath = Path.Combine(fixture.OutputRoot, "Album");
        var targetPath = Path.Combine(fixture.RootPath, "linked target");
        Directory.Move(albumPath, targetPath);
        if (!TryCreateDirectoryLink(albumPath, targetPath)) return;

        var result = Assert.IsType<VerifiedJpegPublicationFailureResult>(
            VerifiedJpegPublisher.Publish(fixture.OutputRoot, context.VerifiedResult));

        Assert.Equal(VerifiedJpegPublicationFailureKind.LinkedPath, result.FailureKind);
        Assert.True(File.Exists(context.VerifiedResult.TemporaryCopyPath));
        Assert.False(File.Exists(context.VerifiedResult.IntendedFinalDestinationPath));
    }

    [Fact]
    public void Publish_InvalidRootOrMismatchedPathsReturnsTypedFailure()
    {
        using var fixture = new PublisherFixture();
        var context = fixture.CreateVerifiedResult("photo.jpg");

        var invalidRoot = Assert.IsType<VerifiedJpegPublicationFailureResult>(
            VerifiedJpegPublisher.Publish("relative-output", context.VerifiedResult));
        Assert.Equal(VerifiedJpegPublicationFailureKind.InvalidOutputRoot,
            invalidRoot.FailureKind);

        var forged = context.VerifiedResult with
        {
            TemporaryCopyPath = Path.Combine(fixture.RootPath, "outside.jpg")
        };
        var mismatch = Assert.IsType<VerifiedJpegPublicationFailureResult>(
            VerifiedJpegPublisher.Publish(fixture.OutputRoot, forged));
        Assert.Equal(VerifiedJpegPublicationFailureKind.MismatchedPaths,
            mismatch.FailureKind);
        Assert.True(File.Exists(context.VerifiedResult.TemporaryCopyPath));

        var otherRoot = Path.Combine(fixture.RootPath, "other-output");
        Directory.CreateDirectory(otherRoot);
        var outside = Assert.IsType<VerifiedJpegPublicationFailureResult>(
            VerifiedJpegPublisher.Publish(otherRoot, context.VerifiedResult));
        Assert.Equal(VerifiedJpegPublicationFailureKind.InvalidPath,
            outside.FailureKind);
    }

    [Fact]
    public void Publish_MoveFailureLeavesTemporaryFileWhenMoveDoesNotComplete()
    {
        using var fixture = new PublisherFixture();
        var context = fixture.CreateVerifiedResult("photo.jpg");
        byte[] collisionBytes = [4, 5, 6];

        var result = Assert.IsType<VerifiedJpegPublicationFailureResult>(
            VerifiedJpegPublisher.Publish(
                fixture.OutputRoot,
                context.VerifiedResult,
                (temporaryPath, finalPath) =>
                {
                    File.WriteAllBytes(finalPath, collisionBytes);
                    File.Move(temporaryPath, finalPath, overwrite: false);
                }));

        Assert.Equal(VerifiedJpegPublicationFailureKind.MoveFailure,
            result.FailureKind);
        Assert.True(File.Exists(context.VerifiedResult.TemporaryCopyPath));
        Assert.Equal(collisionBytes,
            File.ReadAllBytes(context.VerifiedResult.IntendedFinalDestinationPath));
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
        public PublisherFixture(string? name = null)
        {
            RootPath = Path.Combine(
                Path.GetTempPath(),
                name ?? "PhotoMigration-VerifiedJpegPublisherTests",
                Guid.NewGuid().ToString("N"));
            SourceRoot = Path.Combine(RootPath, "source");
            OutputRoot = Path.Combine(RootPath, "output");
            Directory.CreateDirectory(SourceRoot);
            Directory.CreateDirectory(OutputRoot);
        }

        public string RootPath { get; }

        public string SourceRoot { get; }

        public string OutputRoot { get; }

        public string ExifToolPath => Path.Combine(RootPath, "exiftool");

        public PublicationContext CreateVerifiedResult(string relativePath)
        {
            byte[] sourceBytes = [0xFF, 0xD8, 1, 2, 3, 0xFF, 0xD9];
            var sourcePath = Combine(SourceRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
            File.WriteAllBytes(sourcePath, sourceBytes);
            File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddDays(-5));

            var entry = new InventoryEntry(relativePath, sourceBytes.Length);
            var destinationPlan = Assert.IsType<DestinationPathPlanningSuccessResult>(
                DestinationPathPlanner.Plan(SourceRoot, OutputRoot, [entry]));
            var staging = Assert.IsType<VerifiedMediaFileStagingSuccessResult>(
                VerifiedMediaFileStager.Stage(
                    SourceRoot,
                    OutputRoot,
                    Assert.Single(destinationPlan.Items)));
            var baseline = new ImageDataHashReadSuccessResult(
                staging,
                staging.TemporaryCopyPath,
                ExifToolPath,
                "SHA256",
                MediaHash.ToLowerInvariant(),
                MediaHash,
                Array.Empty<string>(),
                string.Empty);
            var sidecar = new TakeoutSidecarMetadata(
                "synthetic.jpg", null, null, null, 1, 2, null, null);
            var embeddedRead = new EmbeddedMetadataReadSuccessResult(
                sourcePath,
                ExifToolPath,
                Array.Empty<EmbeddedMetadataValue>(),
                Array.Empty<string>(),
                Array.Empty<string>(),
                string.Empty);
            var metadataPlan = Assert.IsType<SuccessfulMediaMetadataPlan>(
                MediaMetadataPlanBuilder.Build(embeddedRead, sidecar));
            var parsed = new ParsedMediaResult(
                entry,
                new InventoryEntry(relativePath + ".json", 1),
                SidecarMatchRule.LegacyJson,
                sidecar);
            var planningItem = new TakeoutMetadataPlanningItem(
                entry,
                new MatchedTakeoutSidecarState(parsed),
                metadataPlan);
            var writePlan = Assert.IsType<JpegMetadataWriteReadyResult>(
                JpegMetadataWritePlanBuilder.Build(planningItem, baseline));
            var writeResult = new JpegGpsMetadataWriteSuccessResult(
                writePlan,
                staging.TemporaryCopyPath,
                staging.IntendedFinalDestinationPath,
                ExifToolPath,
                JpegGpsMetadataWriteStatus.PendingVerification,
                string.Empty,
                string.Empty);
            var gps = new JpegGpsMetadataVerificationValues(
                1, "N", 2, "E", null, null);
            var verifiedResult = new JpegGpsMetadataWriteVerifiedResult(
                writeResult,
                staging.TemporaryCopyPath,
                staging.IntendedFinalDestinationPath,
                MediaHash,
                MediaHash,
                gps,
                gps,
                ExifToolPath,
                Array.Empty<string>(),
                Array.Empty<string>(),
                string.Empty,
                string.Empty);
            return new PublicationContext(verifiedResult, sourcePath);
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

    private sealed record PublicationContext(
        JpegGpsMetadataWriteVerifiedResult VerifiedResult,
        string SourcePath);
}
