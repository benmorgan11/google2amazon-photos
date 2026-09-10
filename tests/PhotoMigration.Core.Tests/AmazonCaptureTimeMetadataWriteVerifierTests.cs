using System.Diagnostics;
using System.Text;
using System.Text.Json;
using PhotoMigration.Core;

namespace PhotoMigration.Core.Tests;

[Collection(ExternalProcessTestCollection.Name)]
public sealed class AmazonCaptureTimeMetadataWriteVerifierTests
{
    private const string BaselineHash =
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string DifferentHash =
        "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";

    [Fact]
    public void Verify_MatchingImageMovAndMp4ValuesReturnVerifiedResults()
    {
        if (!SupportsPosixScripts()) return;

        using var fixture = new VerifierFixture("Capture verifier paths 旅行");
        foreach (var relativePath in new[]
                 {
                     "Images/photo one.PnG",
                     "Videos/movie two.MoV",
                     "Videos/movie three.mP4"
                 })
        {
            var context = fixture.CreateWriteResult(relativePath);
            var argumentsPath = Path.Combine(
                fixture.RootPath,
                $"arguments-{Path.GetFileName(relativePath)}.txt");
            var executable = fixture.CreateExecutable(
                $"tools with spaces/exiftool-{Path.GetExtension(relativePath)[1..]}",
                CaptureArgumentsAndOutputScript(
                    argumentsPath,
                    Json(context, BaselineHash.ToLowerInvariant(),
                        warning: "synthetic warning")));

            var result = Assert.IsType<AmazonCaptureTimeMetadataWriteVerifiedResult>(
                AmazonCaptureTimeMetadataWriteVerifier.Verify(
                    context.WriteResult,
                    executable));

            Assert.Same(context.WriteResult, result.WriteResult);
            Assert.Equal(context.WriteResult.WritePlan.Assignments,
                result.ExpectedAssignments);
            Assert.Equal(result.ExpectedAssignments, result.ActualAssignments);
            Assert.Equal(BaselineHash, result.BaselineImageDataHashSha256);
            Assert.Equal(BaselineHash, result.ActualImageDataHashSha256);
            Assert.Equal(Path.GetFullPath(executable), result.ExifToolExecutablePath);
            Assert.Equal(["synthetic warning"], result.Warnings);
            Assert.Equal(ExpectedArguments(context), File.ReadAllLines(argumentsPath));
            Assert.Equal(context.SourceBytes, File.ReadAllBytes(context.SourcePath));
            Assert.Equal(context.SourceLastWriteTimeUtc,
                File.GetLastWriteTimeUtc(context.SourcePath));
            Assert.False(File.Exists(
                context.WriteResult.IntendedFinalDestinationPath));
        }
    }

    [Theory]
    [InlineData(true, false, "MissingCaptureTimeValues")]
    [InlineData(false, true, "CaptureTimeMismatch")]
    public void Verify_MissingOrMismatchedCaptureTimeReturnsTypedFailure(
        bool omitCaptureTime,
        bool mismatchCaptureTime,
        string expectedKind)
    {
        if (!SupportsPosixScripts()) return;

        using var fixture = new VerifierFixture();
        var context = fixture.CreateWriteResult("photo.heic");
        var executable = fixture.CreateOutputExecutable(Json(
            context,
            BaselineHash,
            omitCaptureTime,
            mismatchCaptureTime));

        var result = Assert.IsType<
            AmazonCaptureTimeMetadataWriteVerificationFailureResult>(
            AmazonCaptureTimeMetadataWriteVerifier.Verify(
                context.WriteResult,
                executable));

        Assert.Equal(
            Enum.Parse<AmazonCaptureTimeMetadataWriteVerificationFailureKind>(
                expectedKind),
            result.FailureKind);
    }

    [Fact]
    public void Verify_ChangedImageDataHashReturnsMismatch()
    {
        if (!SupportsPosixScripts()) return;

        using var fixture = new VerifierFixture();
        var context = fixture.CreateWriteResult("video.mov");
        var executable = fixture.CreateOutputExecutable(Json(context, DifferentHash));

        var result = Assert.IsType<
            AmazonCaptureTimeMetadataWriteVerificationFailureResult>(
            AmazonCaptureTimeMetadataWriteVerifier.Verify(
                context.WriteResult,
                executable));

        Assert.Equal(
            AmazonCaptureTimeMetadataWriteVerificationFailureKind.MediaHashMismatch,
            result.FailureKind);
        Assert.Equal(BaselineHash, result.BaselineImageDataHashSha256);
        Assert.Equal(DifferentHash, result.ActualImageDataHashSha256);
        Assert.Equal(result.ExpectedAssignments, result.ActualAssignments);
    }

    [Theory]
    [InlineData("malformed", "MalformedOutput")]
    [InlineData("missing-hash", "MissingImageDataHash")]
    [InlineData("invalid-hash", "InvalidImageDataHash")]
    public void Verify_MalformedOrMissingHashOutputReturnsTypedFailure(
        string outputCase,
        string expectedKind)
    {
        if (!SupportsPosixScripts()) return;

        using var fixture = new VerifierFixture();
        var context = fixture.CreateWriteResult("photo.jpg");
        var output = outputCase switch
        {
            "malformed" => "not JSON",
            "missing-hash" => Json(context, null),
            "invalid-hash" => Json(context, "not-a-hash"),
            _ => throw new ArgumentOutOfRangeException(nameof(outputCase))
        };
        var executable = fixture.CreateOutputExecutable(output);

        var result = Assert.IsType<
            AmazonCaptureTimeMetadataWriteVerificationFailureResult>(
            AmazonCaptureTimeMetadataWriteVerifier.Verify(
                context.WriteResult,
                executable));

        Assert.Equal(
            Enum.Parse<AmazonCaptureTimeMetadataWriteVerificationFailureKind>(
                expectedKind),
            result.FailureKind);
        Assert.Equal(output, result.StandardOutput);
    }

    [Fact]
    public void Verify_ExifToolFailureRetainsDiagnostics()
    {
        if (!SupportsPosixScripts()) return;

        using var fixture = new VerifierFixture();
        var context = fixture.CreateWriteResult("photo.png");
        var executable = fixture.CreateExecutable(
            "exiftool",
            "printf 'read output'\nprintf 'read error' >&2\nexit 19");

        var result = Assert.IsType<
            AmazonCaptureTimeMetadataWriteVerificationFailureResult>(
            AmazonCaptureTimeMetadataWriteVerifier.Verify(
                context.WriteResult,
                executable));

        Assert.Equal(
            AmazonCaptureTimeMetadataWriteVerificationFailureKind.ExifToolFailure,
            result.FailureKind);
        Assert.Equal(19, result.ExitCode);
        Assert.Equal("read output", result.StandardOutput);
        Assert.Equal("read error", result.StandardError);
    }

    [Fact]
    public void Verify_TimeoutCleanupIsBounded()
    {
        if (!SupportsPosixScripts()) return;

        using var fixture = new VerifierFixture();
        var context = fixture.CreateWriteResult("video.mp4");
        var executable = fixture.CreateExecutable(
            "exiftool",
            "trap '' TERM\nsleep 30 &\nwait");
        var timeout = TimeSpan.FromMilliseconds(100);
        var stopwatch = Stopwatch.StartNew();

        var result = Assert.IsType<
            AmazonCaptureTimeMetadataWriteVerificationFailureResult>(
            AmazonCaptureTimeMetadataWriteVerifier.Verify(
                context.WriteResult,
                executable,
                timeout));

        stopwatch.Stop();
        Assert.Equal(TimeSpan.FromMinutes(10),
            AmazonCaptureTimeMetadataWriteVerifier.DefaultVerificationTimeout);
        Assert.Equal(AmazonCaptureTimeMetadataWriteVerificationFailureKind.TimedOut,
            result.FailureKind);
        Assert.Equal(timeout, result.Timeout);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5));
        Assert.True(File.Exists(context.WriteResult.TemporaryCopyPath));
    }

    [Fact]
    public void Verify_UnsafePathsAndDestinationCollisionPreventExecution()
    {
        using var missingFixture = new VerifierFixture();
        var missing = missingFixture.CreateWriteResult("missing.jpg");
        var invalidResult = Assert.IsType<
            AmazonCaptureTimeMetadataWriteVerificationFailureResult>(
            AmazonCaptureTimeMetadataWriteVerifier.Verify(
                missing.WriteResult,
                "relative-exiftool"));
        Assert.Equal(
            AmazonCaptureTimeMetadataWriteVerificationFailureKind.InvalidInput,
            invalidResult.FailureKind);

        File.Delete(missing.WriteResult.TemporaryCopyPath);
        var missingResult = Assert.IsType<
            AmazonCaptureTimeMetadataWriteVerificationFailureResult>(
            AmazonCaptureTimeMetadataWriteVerifier.Verify(
                missing.WriteResult,
                missingFixture.MissingExecutablePath));
        Assert.Equal(
            AmazonCaptureTimeMetadataWriteVerificationFailureKind.MissingTemporaryFile,
            missingResult.FailureKind);

        using var linkedFixture = new VerifierFixture();
        var linked = linkedFixture.CreateWriteResult("linked.heif");
        var target = Path.Combine(linkedFixture.RootPath, "target.heif");
        File.WriteAllBytes(target, [1, 2, 3]);
        File.Delete(linked.WriteResult.TemporaryCopyPath);
        if (TryCreateFileLink(linked.WriteResult.TemporaryCopyPath, target))
        {
            var linkedResult = Assert.IsType<
                AmazonCaptureTimeMetadataWriteVerificationFailureResult>(
                AmazonCaptureTimeMetadataWriteVerifier.Verify(
                    linked.WriteResult,
                    linkedFixture.MissingExecutablePath));
            Assert.Equal(
                AmazonCaptureTimeMetadataWriteVerificationFailureKind
                    .LinkedTemporaryFile,
                linkedResult.FailureKind);
            Assert.Equal([1, 2, 3], File.ReadAllBytes(target));
        }

        using var collisionFixture = new VerifierFixture();
        var collision = collisionFixture.CreateWriteResult("collision.png");
        File.WriteAllBytes(collision.WriteResult.IntendedFinalDestinationPath, [9]);
        var collisionResult = Assert.IsType<
            AmazonCaptureTimeMetadataWriteVerificationFailureResult>(
            AmazonCaptureTimeMetadataWriteVerifier.Verify(
                collision.WriteResult,
                collisionFixture.MissingExecutablePath));
        Assert.Equal(
            AmazonCaptureTimeMetadataWriteVerificationFailureKind
                .ExistingFinalDestination,
            collisionResult.FailureKind);
        Assert.Equal([9], File.ReadAllBytes(
            collision.WriteResult.IntendedFinalDestinationPath));
    }

    private static string Json(
        VerificationContext context,
        string? hash,
        bool omitCaptureTime = false,
        bool mismatchCaptureTime = false,
        string? warning = null)
    {
        var values = new Dictionary<string, object>(StringComparer.Ordinal);
        if (!omitCaptureTime)
        {
            foreach (var (property, assignment) in ExpectedProperties(context))
            {
                values[property] = mismatchCaptureTime
                    ? assignment.Value + " changed"
                    : assignment.Value;
            }
        }
        if (hash is not null)
        {
            values["File:ImageDataHash"] = hash;
        }
        if (warning is not null)
        {
            values["ExifTool:Warning"] = warning;
        }
        return JsonSerializer.Serialize(new[] { values });
    }

    private static IReadOnlyList<(string Property,
        AmazonCaptureTimeAssignment Assignment)> ExpectedProperties(
        VerificationContext context) => context.WriteResult.WritePlan.MediaFormat switch
        {
            AmazonCaptureTimeMediaFormat.Jpeg
                or AmazonCaptureTimeMediaFormat.Heic
                or AmazonCaptureTimeMediaFormat.Heif
                or AmazonCaptureTimeMediaFormat.Png =>
            [
                ("ExifIFD:DateTimeOriginal",
                    context.WriteResult.WritePlan.Assignments[0]),
                ("ExifIFD:OffsetTimeOriginal",
                    context.WriteResult.WritePlan.Assignments[1])
            ],
            AmazonCaptureTimeMediaFormat.Mov =>
            [("QuickTime:CreateDate", context.WriteResult.WritePlan.Assignments[0])],
            AmazonCaptureTimeMediaFormat.Mp4 =>
            [("Keys:CreationDate", context.WriteResult.WritePlan.Assignments[0])],
            _ => throw new ArgumentOutOfRangeException()
        };

    private static IReadOnlyList<string> ExpectedArguments(VerificationContext context)
    {
        var arguments = new List<string>
        {
            "-json",
            "-G1",
            "-s",
            "-api",
            "ImageHashType=SHA256"
        };
        arguments.AddRange(context.WriteResult.WritePlan.MediaFormat switch
        {
            AmazonCaptureTimeMediaFormat.Jpeg
                or AmazonCaptureTimeMediaFormat.Heic
                or AmazonCaptureTimeMediaFormat.Heif
                or AmazonCaptureTimeMediaFormat.Png =>
            ["-EXIF:DateTimeOriginal", "-EXIF:OffsetTimeOriginal"],
            AmazonCaptureTimeMediaFormat.Mov => ["-QuickTime:CreateDate"],
            AmazonCaptureTimeMediaFormat.Mp4 => ["-Keys:CreationDate"],
            _ => throw new ArgumentOutOfRangeException()
        });
        arguments.Add("-ImageDataHash");
        arguments.Add(context.WriteResult.TemporaryCopyPath);
        return arguments.AsReadOnly();
    }

    private static string CaptureArgumentsAndOutputScript(
        string argumentsPath,
        string output) =>
        $"printf '' > {ShellQuote(argumentsPath)}\n"
        + $"for argument do printf '%s\\n' \"$argument\" >> "
        + $"{ShellQuote(argumentsPath)}; done\n"
        + $"printf '%s' {ShellQuote(output)}";

    private static string ShellQuote(string value) =>
        "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    private static bool SupportsPosixScripts() =>
        OperatingSystem.IsMacOS() || OperatingSystem.IsLinux();

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

    private sealed class VerifierFixture : IDisposable
    {
        public VerifierFixture(string? name = null)
        {
            RootPath = Path.Combine(
                Path.GetTempPath(),
                name ?? "PhotoMigration-AmazonCaptureTimeVerifierTests",
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

        public VerificationContext CreateWriteResult(string relativePath)
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
            var baseline = new ImageDataHashReadSuccessResult(
                staging,
                staging.TemporaryCopyPath,
                MissingExecutablePath,
                "SHA256",
                BaselineHash.ToLowerInvariant(),
                BaselineHash,
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
            var writeResult = new AmazonCaptureTimeMetadataWriteSuccessResult(
                writePlan,
                staging,
                baseline,
                Path.GetFullPath(OutputRoot),
                MissingExecutablePath,
                AmazonCaptureTimeMetadataWriteStatus.PendingVerification,
                string.Empty,
                string.Empty);
            return new VerificationContext(
                writeResult,
                sourcePath,
                sourceBytes,
                sourceLastWriteTimeUtc);
        }

        public string CreateOutputExecutable(string output) =>
            CreateExecutable("exiftool", $"printf '%s' {ShellQuote(output)}");

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

    private sealed record VerificationContext(
        AmazonCaptureTimeMetadataWriteSuccessResult WriteResult,
        string SourcePath,
        byte[] SourceBytes,
        DateTime SourceLastWriteTimeUtc);
}
