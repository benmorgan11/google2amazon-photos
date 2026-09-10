using System.Diagnostics;
using System.Text;
using PhotoMigration.Core;

namespace PhotoMigration.Core.Tests;

[Collection(ExternalProcessTestCollection.Name)]
public sealed class AmazonCaptureTimeMetadataWriterTests
{
    [Fact]
    public void Execute_UsesPlannedArgumentsInOrderForEverySupportedFormat()
    {
        if (!SupportsPosixScripts()) return;

        using var fixture = new WriterFixture("Amazon writer paths 旅行");
        WriterCase[] cases =
        [
            ImageCase("Family Photos/photo one.JpG"),
            ImageCase("Family Photos/photo two.JPEG"),
            ImageCase("Family Photos/photo three.HEIC"),
            ImageCase("Family Photos/photo four.HeIf"),
            ImageCase("Family Photos/photo five.PnG"),
            new(
                "Family Videos/movie six.MoV",
                ["-QuickTime:CreateDate=2020:01:02 11:04:05"]),
            new(
                "Family Videos/movie seven.mP4",
                ["-Keys:CreationDate=2020:01:02 11:04:05+00:00"])
        ];

        foreach (var value in cases)
        {
            var context = fixture.CreatePlan(value.RelativePath);
            var argumentsPath = Path.Combine(
                fixture.RootPath,
                $"arguments-{Path.GetFileName(value.RelativePath)}.txt");
            var executable = fixture.CreateExecutable(
                $"tools with spaces/exiftool-{Path.GetExtension(value.RelativePath)[1..]}",
                CaptureArgumentsScript(argumentsPath));

            var result = Assert.IsType<AmazonCaptureTimeMetadataWriteSuccessResult>(
                AmazonCaptureTimeMetadataWriter.Execute(
                    context.WritePlan,
                    context.Staging,
                    context.BaselineHash,
                    fixture.OutputRoot,
                    executable));

            Assert.Equal(
                AmazonCaptureTimeMetadataWriteStatus.PendingVerification,
                result.Status);
            Assert.Same(context.WritePlan, result.WritePlan);
            Assert.Same(context.Staging, result.StagingResult);
            Assert.Same(context.BaselineHash, result.BaselineHashResult);
            Assert.Equal(context.Staging.TemporaryCopyPath, result.TemporaryCopyPath);
            Assert.Equal(
                context.Staging.IntendedFinalDestinationPath,
                result.IntendedFinalDestinationPath);
            Assert.Equal(Path.GetFullPath(fixture.OutputRoot), result.OutputRootPath);
            Assert.Equal(Path.GetFullPath(executable), result.ExifToolExecutablePath);
            Assert.Equal(
                new[] { "-overwrite_original" }
                    .Concat(value.AssignmentArguments)
                    .Append(context.Staging.TemporaryCopyPath),
                File.ReadAllLines(argumentsPath));
            Assert.Equal(context.SourceBytes, File.ReadAllBytes(context.SourcePath));
            Assert.Equal(
                context.SourceLastWriteTimeUtc,
                File.GetLastWriteTimeUtc(context.SourcePath));
            Assert.True(File.Exists(context.Staging.TemporaryCopyPath));
            Assert.NotEqual(
                context.SourceBytes,
                File.ReadAllBytes(context.Staging.TemporaryCopyPath));
            Assert.False(File.Exists(context.Staging.IntendedFinalDestinationPath));
        }

        Assert.Empty(Directory.GetFiles(
            fixture.RootPath,
            "*_original",
            SearchOption.AllDirectories));
    }

    [Fact]
    public void Execute_MismatchedPipelineResultsAndExistingDestinationPreventExecution()
    {
        using var fixture = new WriterFixture();
        var first = fixture.CreatePlan("first.jpg");
        var second = fixture.CreatePlan("second.jpg");

        var mismatch = Assert.IsType<AmazonCaptureTimeMetadataWriteFailureResult>(
            AmazonCaptureTimeMetadataWriter.Execute(
                first.WritePlan,
                second.Staging,
                second.BaselineHash,
                fixture.OutputRoot,
                fixture.MissingExecutablePath));
        Assert.Equal(
            AmazonCaptureTimeMetadataWriteFailureKind.InvalidWritePlan,
            mismatch.FailureKind);

        File.WriteAllBytes(first.Staging.IntendedFinalDestinationPath, [9]);
        var collision = Assert.IsType<AmazonCaptureTimeMetadataWriteFailureResult>(
            AmazonCaptureTimeMetadataWriter.Execute(
                first.WritePlan,
                first.Staging,
                first.BaselineHash,
                fixture.OutputRoot,
                fixture.MissingExecutablePath));
        Assert.Equal(
            AmazonCaptureTimeMetadataWriteFailureKind.ExistingFinalDestination,
            collision.FailureKind);
        Assert.Equal([9], File.ReadAllBytes(first.Staging.IntendedFinalDestinationPath));
    }

    [Fact]
    public void Execute_ChangedTemporaryContentPreventsExifToolAndPreservesSource()
    {
        using var fixture = new WriterFixture();
        var context = fixture.CreatePlan("photo.png");
        File.AppendAllText(context.Staging.TemporaryCopyPath, "changed");

        var result = Assert.IsType<AmazonCaptureTimeMetadataWriteFailureResult>(
            AmazonCaptureTimeMetadataWriter.Execute(
                context.WritePlan,
                context.Staging,
                context.BaselineHash,
                fixture.OutputRoot,
                fixture.MissingExecutablePath));

        Assert.Equal(
            AmazonCaptureTimeMetadataWriteFailureKind.TemporaryFileChanged,
            result.FailureKind);
        Assert.NotEqual(context.SourceBytes.Length, result.ActualByteCount);
        Assert.NotEqual(context.Staging.TemporaryCopySha256, result.ActualWholeFileSha256);
        Assert.Equal(context.SourceBytes, File.ReadAllBytes(context.SourcePath));
        Assert.Equal(
            context.SourceLastWriteTimeUtc,
            File.GetLastWriteTimeUtc(context.SourcePath));
    }

    [Fact]
    public void Execute_NonzeroExitRetainsTemporaryFileAndDiagnostics()
    {
        if (!SupportsPosixScripts()) return;

        using var fixture = new WriterFixture();
        var context = fixture.CreatePlan("video.mp4");
        var executable = fixture.CreateExecutable(
            "exiftool",
            "printf 'write output'\nprintf 'write error' >&2\nexit 23");

        var result = Assert.IsType<AmazonCaptureTimeMetadataWriteFailureResult>(
            AmazonCaptureTimeMetadataWriter.Execute(
                context.WritePlan,
                context.Staging,
                context.BaselineHash,
                fixture.OutputRoot,
                executable));

        Assert.Equal(
            AmazonCaptureTimeMetadataWriteFailureKind.ExifToolFailure,
            result.FailureKind);
        Assert.Equal(23, result.ExitCode);
        Assert.Equal("write output", result.StandardOutput);
        Assert.Equal("write error", result.StandardError);
        Assert.True(File.Exists(context.Staging.TemporaryCopyPath));
        Assert.False(File.Exists(context.Staging.IntendedFinalDestinationPath));
    }

    [Fact]
    public void Execute_TimeoutCleanupIsBoundedAndLeavesTemporaryFile()
    {
        if (!SupportsPosixScripts()) return;

        using var fixture = new WriterFixture();
        var context = fixture.CreatePlan("video.mov");
        var executable = fixture.CreateExecutable(
            "exiftool",
            "trap '' TERM\nsleep 30 &\nwait");
        var timeout = TimeSpan.FromMilliseconds(100);
        var stopwatch = Stopwatch.StartNew();

        var result = Assert.IsType<AmazonCaptureTimeMetadataWriteFailureResult>(
            AmazonCaptureTimeMetadataWriter.Execute(
                context.WritePlan,
                context.Staging,
                context.BaselineHash,
                fixture.OutputRoot,
                executable,
                timeout));

        stopwatch.Stop();
        Assert.Equal(TimeSpan.FromMinutes(10),
            AmazonCaptureTimeMetadataWriter.DefaultWriteTimeout);
        Assert.Equal(AmazonCaptureTimeMetadataWriteFailureKind.TimedOut,
            result.FailureKind);
        Assert.Equal(timeout, result.Timeout);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5));
        Assert.True(File.Exists(context.Staging.TemporaryCopyPath));
        Assert.False(File.Exists(context.Staging.IntendedFinalDestinationPath));
    }

    private static WriterCase ImageCase(string relativePath) =>
        new(
            relativePath,
            [
                "-EXIF:DateTimeOriginal=2020:01:02 11:04:05",
                "-EXIF:OffsetTimeOriginal=+00:00"
            ]);

    private static string CaptureArgumentsScript(string outputPath) =>
        $"printf '' > {ShellQuote(outputPath)}\n" +
        $"for argument do last_argument=\"$argument\"; " +
        $"printf '%s\\n' \"$argument\" >> {ShellQuote(outputPath)}; done\n" +
        "printf 'synthetic metadata write' >> \"$last_argument\"";

    private static string ShellQuote(string value) =>
        "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    private static bool SupportsPosixScripts() =>
        OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    private sealed class WriterFixture : IDisposable
    {
        private const string MediaDataHash =
            "2222222222222222222222222222222222222222222222222222222222222222";

        public WriterFixture(string? name = null)
        {
            RootPath = Path.Combine(
                Path.GetTempPath(),
                name ?? "PhotoMigration-AmazonCaptureTimeWriterTests",
                Guid.NewGuid().ToString("N"));
            SourceRoot = Path.Combine(RootPath, "source");
            OutputRoot = Path.Combine(RootPath, "output");
            Directory.CreateDirectory(SourceRoot);
            Directory.CreateDirectory(OutputRoot);
        }

        public string RootPath { get; }

        public string SourceRoot { get; }

        public string OutputRoot { get; }

        public string MissingExecutablePath =>
            Path.Combine(RootPath, "missing-exiftool");

        public WriterContext CreatePlan(string relativePath)
        {
            byte[] sourceBytes = [1, 2, 3, 4, 5, 6, 7];
            var sourcePath = Combine(SourceRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
            File.WriteAllBytes(sourcePath, sourceBytes);
            File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddDays(-5));
            var sourceLastWriteTimeUtc = File.GetLastWriteTimeUtc(sourcePath);

            var entry = new InventoryEntry(relativePath, sourceBytes.Length);
            var destinationPlan = Assert.IsType<DestinationPathPlanningSuccessResult>(
                DestinationPathPlanner.Plan(SourceRoot, OutputRoot, [entry]));
            var staging = Assert.IsType<VerifiedMediaFileStagingSuccessResult>(
                VerifiedMediaFileStager.Stage(
                    SourceRoot,
                    OutputRoot,
                    Assert.Single(destinationPlan.Items)));
            var baselineHash = new ImageDataHashReadSuccessResult(
                staging,
                staging.TemporaryCopyPath,
                MissingExecutablePath,
                "SHA256",
                MediaDataHash,
                MediaDataHash,
                Array.Empty<string>(),
                string.Empty);
            var sidecar = new TakeoutSidecarMetadata(
                "synthetic",
                null,
                null,
                new DateTimeOffset(
                    2020, 1, 2, 3, 4, 5, TimeSpan.FromHours(-8)),
                null,
                null,
                null,
                null);
            var embeddedRead = new EmbeddedMetadataReadSuccessResult(
                sourcePath,
                MissingExecutablePath,
                Array.Empty<EmbeddedMetadataValue>(),
                Array.Empty<string>(),
                Array.Empty<string>(),
                string.Empty);
            var metadataPlan = MediaMetadataPlanBuilder.Build(embeddedRead, sidecar);
            var parsed = new ParsedMediaResult(
                entry,
                new InventoryEntry(relativePath + ".json", 1),
                SidecarMatchRule.LegacyJson,
                sidecar);
            var planningItem = new TakeoutMetadataPlanningItem(
                entry,
                new MatchedTakeoutSidecarState(parsed),
                metadataPlan);
            var writePlan = Assert.IsType<AmazonCaptureTimeWriteReadyResult>(
                AmazonCaptureTimeWritePlanBuilder.Build(planningItem));

            return new WriterContext(
                writePlan,
                staging,
                baselineHash,
                sourcePath,
                sourceBytes,
                sourceLastWriteTimeUtc);
        }

        public string CreateExecutable(string relativePath, string body)
        {
            if (!SupportsPosixScripts())
            {
                throw new PlatformNotSupportedException();
            }

            var path = Combine(RootPath, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(
                path,
                "#!/bin/sh\n" + body + "\n",
                new UTF8Encoding(false));
            if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
            {
                File.SetUnixFileMode(
                    path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite
                    | UnixFileMode.UserExecute | UnixFileMode.GroupRead
                    | UnixFileMode.GroupExecute | UnixFileMode.OtherRead
                    | UnixFileMode.OtherExecute);
            }
            return path;
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

    private sealed record WriterCase(
        string RelativePath,
        IReadOnlyList<string> AssignmentArguments);

    private sealed record WriterContext(
        AmazonCaptureTimeWriteReadyResult WritePlan,
        VerifiedMediaFileStagingSuccessResult Staging,
        ImageDataHashReadSuccessResult BaselineHash,
        string SourcePath,
        byte[] SourceBytes,
        DateTime SourceLastWriteTimeUtc);
}
