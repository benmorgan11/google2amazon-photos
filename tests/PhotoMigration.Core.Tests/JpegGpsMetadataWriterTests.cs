using System.Diagnostics;
using System.Text;
using PhotoMigration.Core;

namespace PhotoMigration.Core.Tests;

[Collection(ExternalProcessTestCollection.Name)]
public sealed class JpegGpsMetadataWriterTests
{
    [Theory]
    [InlineData(12.5, 45.25, "N", "E")]
    [InlineData(-12.5, -45.25, "S", "W")]
    public void Execute_UsesExactCoordinateArgumentsInDeterministicOrder(
        double latitude,
        double longitude,
        string latitudeReference,
        string longitudeReference)
    {
        if (!SupportsPosixScripts()) return;

        using var fixture = new WriterFixture("Writer arguments 旅行");
        var context = fixture.CreatePlan(
            "Family Photos/Café image.jpg", latitude, longitude);
        var argumentsPath = Path.Combine(fixture.RootPath, "captured arguments.txt");
        var executable = fixture.CreateExecutable(
            "tools with spaces/fake exiftool",
            CaptureArgumentsScript(argumentsPath));

        var result = Assert.IsType<JpegGpsMetadataWriteSuccessResult>(
            JpegGpsMetadataWriter.Execute(
                fixture.OutputRoot, context.WritePlan, executable));

        Assert.Equal(JpegGpsMetadataWriteStatus.PendingVerification, result.Status);
        Assert.Same(context.WritePlan, result.WritePlan);
        Assert.Equal(
            [
                "-overwrite_original",
                "-EXIF:GPSLatitude=12.5",
                $"-EXIF:GPSLatitudeRef={latitudeReference}",
                "-EXIF:GPSLongitude=45.25",
                $"-EXIF:GPSLongitudeRef={longitudeReference}",
                context.Staging.TemporaryCopyPath
            ],
            File.ReadAllLines(argumentsPath));
        Assert.Equal(context.SourceBytes, File.ReadAllBytes(context.SourcePath));
        Assert.Equal(context.SourceLastWriteTimeUtc,
            File.GetLastWriteTimeUtc(context.SourcePath));
        Assert.False(File.Exists(context.Staging.IntendedFinalDestinationPath));
    }

    [Theory]
    [InlineData(150.75, "0")]
    [InlineData(-150.75, "1")]
    public void Execute_AltitudeUsesMagnitudeAndNumericReference(
        double altitude,
        string altitudeReference)
    {
        if (!SupportsPosixScripts()) return;

        using var fixture = new WriterFixture();
        var context = fixture.CreatePlan("photo.jpg", 1, 2, altitude);
        var argumentsPath = Path.Combine(fixture.RootPath, "arguments.txt");
        var executable = fixture.CreateExecutable(
            "exiftool", CaptureArgumentsScript(argumentsPath));

        Assert.IsType<JpegGpsMetadataWriteSuccessResult>(
            JpegGpsMetadataWriter.Execute(fixture.OutputRoot, context.WritePlan, executable));

        var arguments = File.ReadAllLines(argumentsPath);
        Assert.Equal("-EXIF:GPSAltitude=150.75", arguments[5]);
        Assert.Equal($"-EXIF:GPSAltitudeRef#={altitudeReference}", arguments[6]);
        Assert.Equal(context.Staging.TemporaryCopyPath, arguments[7]);
    }

    [Fact]
    public void Execute_MissingAltitudeOmitsBothAltitudeArguments()
    {
        if (!SupportsPosixScripts()) return;

        using var fixture = new WriterFixture();
        var context = fixture.CreatePlan("photo.jpg", 1, 2);
        var argumentsPath = Path.Combine(fixture.RootPath, "arguments.txt");
        var executable = fixture.CreateExecutable(
            "exiftool", CaptureArgumentsScript(argumentsPath));

        Assert.IsType<JpegGpsMetadataWriteSuccessResult>(
            JpegGpsMetadataWriter.Execute(fixture.OutputRoot, context.WritePlan, executable));

        Assert.DoesNotContain(File.ReadAllLines(argumentsPath),
            argument => argument.Contains("GPSAltitude", StringComparison.Ordinal));
    }

    [Fact]
    public void Execute_ChangedHashOrByteCountPreventsExifToolExecution()
    {
        using var fixture = new WriterFixture();

        var changedHash = fixture.CreatePlan("hash.jpg", 1, 2);
        File.WriteAllBytes(changedHash.Staging.TemporaryCopyPath,
            changedHash.SourceBytes.Select(value => (byte)(value + 1)).ToArray());
        var hashResult = Assert.IsType<JpegGpsMetadataWriteFailureResult>(
            JpegGpsMetadataWriter.Execute(
                fixture.OutputRoot, changedHash.WritePlan, fixture.MissingExecutablePath));
        Assert.Equal(JpegGpsMetadataWriteFailureKind.TemporaryFileChanged,
            hashResult.FailureKind);
        Assert.Equal(changedHash.SourceBytes.Length, hashResult.ActualByteCount);

        var changedLength = fixture.CreatePlan("length.jpg", 3, 4);
        File.AppendAllText(changedLength.Staging.TemporaryCopyPath, "changed");
        var lengthResult = Assert.IsType<JpegGpsMetadataWriteFailureResult>(
            JpegGpsMetadataWriter.Execute(
                fixture.OutputRoot, changedLength.WritePlan, fixture.MissingExecutablePath));
        Assert.Equal(JpegGpsMetadataWriteFailureKind.TemporaryFileChanged,
            lengthResult.FailureKind);
        Assert.NotEqual(changedLength.SourceBytes.Length, lengthResult.ActualByteCount);
    }

    [Fact]
    public void Execute_MissingOrLinkedTemporaryFileReturnsTypedFailure()
    {
        using var missingFixture = new WriterFixture();
        var missing = missingFixture.CreatePlan("missing.jpg", 1, 2);
        File.Delete(missing.Staging.TemporaryCopyPath);
        var missingResult = Assert.IsType<JpegGpsMetadataWriteFailureResult>(
            JpegGpsMetadataWriter.Execute(missingFixture.OutputRoot, missing.WritePlan,
                missingFixture.MissingExecutablePath));
        Assert.Equal(JpegGpsMetadataWriteFailureKind.MissingTemporaryFile,
            missingResult.FailureKind);

        using var linkedFixture = new WriterFixture();
        var linked = linkedFixture.CreatePlan("linked.jpg", 1, 2);
        var target = Path.Combine(linkedFixture.RootPath, "target.jpg");
        File.WriteAllBytes(target, linked.SourceBytes);
        File.Delete(linked.Staging.TemporaryCopyPath);
        if (!TryCreateFileLink(linked.Staging.TemporaryCopyPath, target)) return;

        var linkedResult = Assert.IsType<JpegGpsMetadataWriteFailureResult>(
            JpegGpsMetadataWriter.Execute(linkedFixture.OutputRoot, linked.WritePlan,
                linkedFixture.MissingExecutablePath));
        Assert.Equal(JpegGpsMetadataWriteFailureKind.LinkedPath,
            linkedResult.FailureKind);
        Assert.Equal(linked.SourceBytes, File.ReadAllBytes(target));
    }

    [Fact]
    public void Execute_NonRegularTemporaryFileReturnsTypedFailure()
    {
        using var fixture = new WriterFixture();
        var context = fixture.CreatePlan("photo.jpg", 1, 2);
        File.Delete(context.Staging.TemporaryCopyPath);
        Directory.CreateDirectory(context.Staging.TemporaryCopyPath);

        var result = Assert.IsType<JpegGpsMetadataWriteFailureResult>(
            JpegGpsMetadataWriter.Execute(
                fixture.OutputRoot, context.WritePlan, fixture.MissingExecutablePath));

        Assert.Equal(JpegGpsMetadataWriteFailureKind.NonRegularTemporaryFile,
            result.FailureKind);
    }

    [Fact]
    public void Execute_InvalidOrMissingRootsAndPathsReturnTypedFailures()
    {
        using var fixture = new WriterFixture();
        var context = fixture.CreatePlan("photo.jpg", 1, 2);

        var outsidePath = Path.Combine(fixture.RootPath, "outside.jpg");
        var forgedStaging = context.Staging with { TemporaryCopyPath = outsidePath };
        var forgedPlan = context.WritePlan with
        {
            StagingResult = forgedStaging,
            BaselineHashResult = context.WritePlan.BaselineHashResult with
            {
                StagingResult = forgedStaging,
                TemporaryCopyPath = outsidePath
            }
        };
        var outside = Assert.IsType<JpegGpsMetadataWriteFailureResult>(
            JpegGpsMetadataWriter.Execute(
                fixture.OutputRoot, forgedPlan, fixture.MissingExecutablePath));
        Assert.Equal(JpegGpsMetadataWriteFailureKind.InvalidPath, outside.FailureKind);

        Directory.Move(
            fixture.OutputRoot,
            Path.Combine(fixture.RootPath, "moved-output"));
        var missingRoot = Assert.IsType<JpegGpsMetadataWriteFailureResult>(
            JpegGpsMetadataWriter.Execute(
                fixture.OutputRoot, context.WritePlan, fixture.MissingExecutablePath));
        Assert.Equal(JpegGpsMetadataWriteFailureKind.InvalidOutputRoot,
            missingRoot.FailureKind);
    }

    [Fact]
    public void Execute_LinkedOutputComponentPreventsExecution()
    {
        using var fixture = new WriterFixture();
        var context = fixture.CreatePlan("Album/photo.jpg", 1, 2);
        var albumPath = Path.Combine(fixture.OutputRoot, "Album");
        var targetPath = Path.Combine(fixture.RootPath, "linked target");
        Directory.Move(albumPath, targetPath);
        if (!TryCreateDirectoryLink(albumPath, targetPath)) return;

        var result = Assert.IsType<JpegGpsMetadataWriteFailureResult>(
            JpegGpsMetadataWriter.Execute(
                fixture.OutputRoot, context.WritePlan, fixture.MissingExecutablePath));

        Assert.Equal(JpegGpsMetadataWriteFailureKind.LinkedPath, result.FailureKind);
        Assert.True(File.Exists(context.Staging.TemporaryCopyPath));
    }

    [Fact]
    public void Execute_ExistingFinalDestinationPreventsExecution()
    {
        using var fixture = new WriterFixture();
        var context = fixture.CreatePlan("photo.jpg", 1, 2);
        File.WriteAllBytes(context.Staging.IntendedFinalDestinationPath, [9]);

        var result = Assert.IsType<JpegGpsMetadataWriteFailureResult>(
            JpegGpsMetadataWriter.Execute(
                fixture.OutputRoot, context.WritePlan, fixture.MissingExecutablePath));

        Assert.Equal(JpegGpsMetadataWriteFailureKind.ExistingFinalDestination,
            result.FailureKind);
        Assert.Equal([9], File.ReadAllBytes(context.Staging.IntendedFinalDestinationPath));
    }

    [Fact]
    public void Execute_IncompleteAltitudeSetIsInvalidWithoutExecution()
    {
        using var fixture = new WriterFixture();
        var context = fixture.CreatePlan("photo.jpg", 1, 2, 3);
        var forged = context.WritePlan with
        {
            Assignments = context.WritePlan.Assignments
                .Where(a => a.Tag != JpegMetadataAssignmentTag.GpsAltitudeReference)
                .ToArray()
        };

        var result = Assert.IsType<JpegGpsMetadataWriteFailureResult>(
            JpegGpsMetadataWriter.Execute(
                fixture.OutputRoot, forged, fixture.MissingExecutablePath));

        Assert.Equal(JpegGpsMetadataWriteFailureKind.InvalidWritePlan,
            result.FailureKind);
    }

    [Fact]
    public void Execute_NonzeroExitRetainsTemporaryFileAndDiagnostics()
    {
        if (!SupportsPosixScripts()) return;

        using var fixture = new WriterFixture();
        var context = fixture.CreatePlan("photo.jpg", 1, 2);
        var executable = fixture.CreateExecutable(
            "exiftool", "printf 'write output'\nprintf 'write error' >&2\nexit 23");

        var result = Assert.IsType<JpegGpsMetadataWriteFailureResult>(
            JpegGpsMetadataWriter.Execute(fixture.OutputRoot, context.WritePlan, executable));

        Assert.Equal(JpegGpsMetadataWriteFailureKind.ExifToolFailure, result.FailureKind);
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
        var context = fixture.CreatePlan("photo.jpg", 1, 2);
        var executable = fixture.CreateExecutable(
            "exiftool", "trap '' TERM\nsleep 30 &\nwait");
        var stopwatch = Stopwatch.StartNew();

        var result = Assert.IsType<JpegGpsMetadataWriteFailureResult>(
            JpegGpsMetadataWriter.Execute(
                fixture.OutputRoot, context.WritePlan, executable,
                TimeSpan.FromMilliseconds(100)));
        stopwatch.Stop();

        Assert.Equal(JpegGpsMetadataWriteFailureKind.TimedOut, result.FailureKind);
        Assert.Equal(TimeSpan.FromMilliseconds(100), result.Timeout);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5));
        Assert.True(File.Exists(context.Staging.TemporaryCopyPath));
        Assert.False(File.Exists(context.Staging.IntendedFinalDestinationPath));
    }

    private static string CaptureArgumentsScript(string outputPath) =>
        $"printf '' > {ShellQuote(outputPath)}\n" +
        $"for argument do printf '%s\\n' \"$argument\" >> {ShellQuote(outputPath)}; done";

    private static string ShellQuote(string value) =>
        "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    private static bool SupportsPosixScripts() =>
        OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    private static bool TryCreateFileLink(string linkPath, string targetPath)
    {
        try { File.CreateSymbolicLink(linkPath, targetPath); return true; }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or PlatformNotSupportedException) { return false; }
    }

    private static bool TryCreateDirectoryLink(string linkPath, string targetPath)
    {
        try { Directory.CreateSymbolicLink(linkPath, targetPath); return true; }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or PlatformNotSupportedException) { return false; }
    }

    private sealed class WriterFixture : IDisposable
    {
        public WriterFixture(string? name = null)
        {
            RootPath = Path.Combine(Path.GetTempPath(),
                name ?? "PhotoMigration-JpegGpsWriterTests", Guid.NewGuid().ToString("N"));
            SourceRoot = Path.Combine(RootPath, "source");
            OutputRoot = Path.Combine(RootPath, "output");
            Directory.CreateDirectory(SourceRoot);
            Directory.CreateDirectory(OutputRoot);
        }

        public string RootPath { get; }
        public string SourceRoot { get; }
        public string OutputRoot { get; }
        public string MissingExecutablePath => Path.Combine(RootPath, "missing-exiftool");

        public WriterContext CreatePlan(
            string relativePath,
            double latitude,
            double longitude,
            double? altitude = null)
        {
            byte[] sourceBytes = [0xFF, 0xD8, 1, 2, 3, 0xFF, 0xD9];
            var sourcePath = Combine(SourceRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
            File.WriteAllBytes(sourcePath, sourceBytes);
            var sourceLastWriteTime = DateTime.UtcNow.AddDays(-5);
            File.SetLastWriteTimeUtc(sourcePath, sourceLastWriteTime);
            sourceLastWriteTime = File.GetLastWriteTimeUtc(sourcePath);

            var entry = new InventoryEntry(relativePath, sourceBytes.Length);
            var planned = Assert.IsType<DestinationPathPlanningSuccessResult>(
                DestinationPathPlanner.Plan(SourceRoot, OutputRoot, [entry]));
            var staging = Assert.IsType<VerifiedMediaFileStagingSuccessResult>(
                VerifiedMediaFileStager.Stage(
                    SourceRoot, OutputRoot, Assert.Single(planned.Items)));
            var baselineHash = new ImageDataHashReadSuccessResult(
                staging,
                staging.TemporaryCopyPath,
                MissingExecutablePath,
                "SHA256",
                new string('2', 64),
                new string('2', 64),
                Array.Empty<string>(),
                string.Empty);
            var sidecar = new TakeoutSidecarMetadata(
                "synthetic.jpg", null, null, null,
                latitude, longitude, altitude, null);
            var embeddedRead = new EmbeddedMetadataReadSuccessResult(
                sourcePath,
                MissingExecutablePath,
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
                JpegMetadataWritePlanBuilder.Build(planningItem, baselineHash));
            return new WriterContext(
                writePlan, staging, sourcePath, sourceBytes, sourceLastWriteTime);
        }

        public string CreateExecutable(string relativePath, string body)
        {
            if (!SupportsPosixScripts())
            {
                throw new PlatformNotSupportedException();
            }

            var path = Combine(RootPath, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "#!/bin/sh\n" + body + "\n",
                new UTF8Encoding(false));
            if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
            {
                File.SetUnixFileMode(path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite
                    | UnixFileMode.UserExecute | UnixFileMode.GroupRead
                    | UnixFileMode.GroupExecute | UnixFileMode.OtherRead
                    | UnixFileMode.OtherExecute);
            }
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(RootPath)) Directory.Delete(RootPath, recursive: true);
        }

        private static string Combine(string root, string relativePath) =>
            Path.GetFullPath(Path.Combine(root,
                relativePath.Replace('/', Path.DirectorySeparatorChar)));
    }

    private sealed record WriterContext(
        JpegMetadataWriteReadyResult WritePlan,
        VerifiedMediaFileStagingSuccessResult Staging,
        string SourcePath,
        byte[] SourceBytes,
        DateTime SourceLastWriteTimeUtc);
}
