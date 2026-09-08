using System.Diagnostics;
using System.Text;
using PhotoMigration.Core;

namespace PhotoMigration.Core.Tests;

[Collection(ExternalProcessTestCollection.Name)]
public sealed class JpegGpsMetadataWriteVerifierTests
{
    private const string BaselineHash =
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string DifferentHash =
        "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";

    [Fact]
    public void Verify_MatchingGpsAltitudeAndImageDataHashReturnsVerifiedResult()
    {
        if (!SupportsPosixScripts()) return;

        using var fixture = new VerifierFixture("Verifier 旅行");
        var context = fixture.CreateWriteResult(
            "Family Photos/Café image.jpg", -12.5, 45.25, -150.0);
        var argumentsPath = Path.Combine(fixture.RootPath, "captured arguments.txt");
        var output = Json(
            latitude: 12.5000004,
            latitudeReference: "S",
            longitude: 45.2500004,
            longitudeReference: "E",
            altitude: 150.05,
            altitudeReference: "1",
            hash: BaselineHash,
            extra: ",\"ExifTool:Warning\":\"synthetic warning\""
                + ",\"ExifTool:Error\":\"synthetic diagnostic\"");
        var executable = fixture.CreateExecutable(
            "tools with spaces/fake exiftool",
            CaptureArgumentsAndOutputScript(argumentsPath, output));
        var sourceBytes = File.ReadAllBytes(context.SourcePath);
        var sourceLastWriteTime = File.GetLastWriteTimeUtc(context.SourcePath);

        var result = Assert.IsType<JpegGpsMetadataWriteVerifiedResult>(
            JpegGpsMetadataWriteVerifier.Verify(context.WriteResult, executable));

        Assert.Same(context.WriteResult, result.WriteResult);
        Assert.Equal(BaselineHash, result.BaselineImageDataHashSha256);
        Assert.Equal(BaselineHash, result.ActualImageDataHashSha256);
        Assert.Equal(new JpegGpsMetadataVerificationValues(
            -12.5, "S", 45.25, "E", -150, "1"), result.ExpectedGps);
        Assert.Equal(new JpegGpsMetadataVerificationValues(
            -12.5000004, "S", 45.2500004, "E", -150.05, "1"), result.ActualGps);
        Assert.Equal(["synthetic warning"], result.Warnings);
        Assert.Equal(["synthetic diagnostic"], result.Errors);
        Assert.Equal(
            [
                "-json",
                "-G1",
                "-s",
                "-api",
                "ImageHashType=SHA256",
                "-EXIF:GPSLatitude#",
                "-EXIF:GPSLatitudeRef#",
                "-EXIF:GPSLongitude#",
                "-EXIF:GPSLongitudeRef#",
                "-EXIF:GPSAltitude#",
                "-EXIF:GPSAltitudeRef#",
                "-ImageDataHash",
                context.WriteResult.TemporaryCopyPath
            ],
            File.ReadAllLines(argumentsPath));
        Assert.Equal(sourceBytes, File.ReadAllBytes(context.SourcePath));
        Assert.Equal(sourceLastWriteTime, File.GetLastWriteTimeUtc(context.SourcePath));
        Assert.False(File.Exists(context.WriteResult.IntendedFinalDestinationPath));
    }

    [Fact]
    public void Verify_MatchingGpsWithoutAltitudeReturnsVerifiedResult()
    {
        if (!SupportsPosixScripts()) return;

        using var fixture = new VerifierFixture();
        var context = fixture.CreateWriteResult("photo.jpg", 1, -2);
        var executable = fixture.CreateOutputExecutable(Json(
            1, "N", 2, "W", hash: BaselineHash));

        var result = Assert.IsType<JpegGpsMetadataWriteVerifiedResult>(
            JpegGpsMetadataWriteVerifier.Verify(context.WriteResult, executable));

        Assert.Null(result.ExpectedGps.Altitude);
        Assert.Null(result.ActualGps.Altitude);
        Assert.Null(result.ExpectedGps.AltitudeReference);
        Assert.Null(result.ActualGps.AltitudeReference);
    }

    [Theory]
    [InlineData(1.000002, "N", 2, "E", null, null)]
    [InlineData(1, "S", 2, "E", null, null)]
    [InlineData(1, "N", 2, "E", 3.10001, "0")]
    public void Verify_GpsMismatchReturnsExpectedAndActualValues(
        double latitude,
        string latitudeReference,
        double longitude,
        string longitudeReference,
        double? altitude,
        string? altitudeReference)
    {
        if (!SupportsPosixScripts()) return;

        using var fixture = new VerifierFixture();
        var context = fixture.CreateWriteResult("photo.jpg", 1, 2, altitude is null ? null : 3);
        var executable = fixture.CreateOutputExecutable(Json(
            latitude,
            latitudeReference,
            longitude,
            longitudeReference,
            altitude,
            altitudeReference,
            BaselineHash));

        var result = Assert.IsType<JpegGpsMetadataWriteVerificationFailureResult>(
            JpegGpsMetadataWriteVerifier.Verify(context.WriteResult, executable));

        Assert.Equal(JpegGpsMetadataWriteVerificationFailureKind.GpsMismatch,
            result.FailureKind);
        Assert.NotNull(result.ExpectedGps);
        Assert.NotNull(result.ActualGps);
    }

    [Theory]
    [InlineData(false, "1", "MissingGpsValues")]
    [InlineData(true, "not-a-number", "InvalidGpsValues")]
    public void Verify_MissingOrMalformedGpsReturnsTypedFailure(
        bool includeLongitude,
        string latitude,
        string expectedKind)
    {
        if (!SupportsPosixScripts()) return;

        using var fixture = new VerifierFixture();
        var context = fixture.CreateWriteResult("photo.jpg", 1, 2);
        var longitude = includeLongitude
            ? ",\"GPS:GPSLongitude\":2,\"GPS:GPSLongitudeRef\":\"E\""
            : string.Empty;
        var output = "[{\"GPS:GPSLatitude\":\"" + latitude
            + "\",\"GPS:GPSLatitudeRef\":\"N\"" + longitude
            + ",\"File:ImageDataHash\":\"" + BaselineHash + "\"}]";
        var executable = fixture.CreateOutputExecutable(output);

        var result = Assert.IsType<JpegGpsMetadataWriteVerificationFailureResult>(
            JpegGpsMetadataWriteVerifier.Verify(context.WriteResult, executable));

        Assert.Equal(
            Enum.Parse<JpegGpsMetadataWriteVerificationFailureKind>(expectedKind),
            result.FailureKind);
    }

    [Fact]
    public void Verify_DifferentImageDataHashReturnsMediaHashMismatch()
    {
        if (!SupportsPosixScripts()) return;

        using var fixture = new VerifierFixture();
        var context = fixture.CreateWriteResult("photo.jpg", 1, 2);
        var executable = fixture.CreateOutputExecutable(Json(
            1, "N", 2, "E", hash: DifferentHash));

        var result = Assert.IsType<JpegGpsMetadataWriteVerificationFailureResult>(
            JpegGpsMetadataWriteVerifier.Verify(context.WriteResult, executable));

        Assert.Equal(JpegGpsMetadataWriteVerificationFailureKind.MediaHashMismatch,
            result.FailureKind);
        Assert.Equal(BaselineHash, result.BaselineImageDataHashSha256);
        Assert.Equal(DifferentHash, result.ActualImageDataHashSha256);
        Assert.NotNull(result.ActualGps);
    }

    [Theory]
    [InlineData("missing", "MissingImageDataHash")]
    [InlineData("invalid", "InvalidImageDataHash")]
    [InlineData("duplicate", "MalformedOutput")]
    public void Verify_MissingInvalidOrDuplicateHashReturnsTypedFailure(
        string hashCase,
        string expectedKind)
    {
        if (!SupportsPosixScripts()) return;

        using var fixture = new VerifierFixture();
        var context = fixture.CreateWriteResult("photo.jpg", 1, 2);
        var hashProperty = hashCase switch
        {
            "missing" => string.Empty,
            "invalid" => ",\"File:ImageDataHash\":\"not-a-hash\"",
            "duplicate" => ",\"File:ImageDataHash\":\"" + BaselineHash
                + "\",\"File:ImageDataHash\":\"" + BaselineHash + "\"",
            _ => throw new ArgumentOutOfRangeException(nameof(hashCase))
        };
        var output = "[{\"GPS:GPSLatitude\":1,\"GPS:GPSLatitudeRef\":\"N\","
            + "\"GPS:GPSLongitude\":2,\"GPS:GPSLongitudeRef\":\"E\""
            + hashProperty + "}]";
        var executable = fixture.CreateOutputExecutable(output);

        var result = Assert.IsType<JpegGpsMetadataWriteVerificationFailureResult>(
            JpegGpsMetadataWriteVerifier.Verify(context.WriteResult, executable));

        Assert.Equal(
            Enum.Parse<JpegGpsMetadataWriteVerificationFailureKind>(expectedKind),
            result.FailureKind);
    }

    [Fact]
    public void Verify_MalformedJsonReturnsMalformedOutput()
    {
        if (!SupportsPosixScripts()) return;

        using var fixture = new VerifierFixture();
        var context = fixture.CreateWriteResult("photo.jpg", 1, 2);
        var executable = fixture.CreateOutputExecutable("not JSON");

        var result = Assert.IsType<JpegGpsMetadataWriteVerificationFailureResult>(
            JpegGpsMetadataWriteVerifier.Verify(context.WriteResult, executable));

        Assert.Equal(JpegGpsMetadataWriteVerificationFailureKind.MalformedOutput,
            result.FailureKind);
        Assert.Equal("not JSON", result.StandardOutput);
    }

    [Fact]
    public void Verify_NonzeroExitReturnsDiagnosticsAndLeavesFilesAlone()
    {
        if (!SupportsPosixScripts()) return;

        using var fixture = new VerifierFixture();
        var context = fixture.CreateWriteResult("photo.jpg", 1, 2);
        var executable = fixture.CreateExecutable(
            "exiftool", "printf 'read output'\nprintf 'read error' >&2\nexit 17");

        var result = Assert.IsType<JpegGpsMetadataWriteVerificationFailureResult>(
            JpegGpsMetadataWriteVerifier.Verify(context.WriteResult, executable));

        Assert.Equal(JpegGpsMetadataWriteVerificationFailureKind.ExifToolFailure,
            result.FailureKind);
        Assert.Equal(17, result.ExitCode);
        Assert.Equal("read output", result.StandardOutput);
        Assert.Equal("read error", result.StandardError);
        Assert.True(File.Exists(context.WriteResult.TemporaryCopyPath));
        Assert.False(File.Exists(context.WriteResult.IntendedFinalDestinationPath));
    }

    [Fact]
    public void Verify_TimeoutCleanupIsBounded()
    {
        if (!SupportsPosixScripts()) return;

        using var fixture = new VerifierFixture();
        var context = fixture.CreateWriteResult("photo.jpg", 1, 2);
        var executable = fixture.CreateExecutable(
            "exiftool", "trap '' TERM\nsleep 30 &\nwait");
        var stopwatch = Stopwatch.StartNew();

        var result = Assert.IsType<JpegGpsMetadataWriteVerificationFailureResult>(
            JpegGpsMetadataWriteVerifier.Verify(
                context.WriteResult, executable, TimeSpan.FromMilliseconds(100)));
        stopwatch.Stop();

        Assert.Equal(JpegGpsMetadataWriteVerificationFailureKind.TimedOut,
            result.FailureKind);
        Assert.Equal(TimeSpan.FromMilliseconds(100), result.Timeout);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5));
        Assert.True(File.Exists(context.WriteResult.TemporaryCopyPath));
    }

    [Fact]
    public void Verify_MissingOrLinkedTemporaryFilePreventsExecution()
    {
        using var missingFixture = new VerifierFixture();
        var missing = missingFixture.CreateWriteResult("missing.jpg", 1, 2);
        File.Delete(missing.WriteResult.TemporaryCopyPath);
        var missingResult = Assert.IsType<JpegGpsMetadataWriteVerificationFailureResult>(
            JpegGpsMetadataWriteVerifier.Verify(
                missing.WriteResult, missingFixture.MissingExecutablePath));
        Assert.Equal(JpegGpsMetadataWriteVerificationFailureKind.MissingTemporaryFile,
            missingResult.FailureKind);

        using var linkedFixture = new VerifierFixture();
        var linked = linkedFixture.CreateWriteResult("linked.jpg", 1, 2);
        var target = Path.Combine(linkedFixture.RootPath, "target.jpg");
        File.WriteAllBytes(target, [1, 2, 3]);
        File.Delete(linked.WriteResult.TemporaryCopyPath);
        if (!TryCreateFileLink(linked.WriteResult.TemporaryCopyPath, target)) return;
        var linkedResult = Assert.IsType<JpegGpsMetadataWriteVerificationFailureResult>(
            JpegGpsMetadataWriteVerifier.Verify(
                linked.WriteResult, linkedFixture.MissingExecutablePath));
        Assert.Equal(JpegGpsMetadataWriteVerificationFailureKind.LinkedTemporaryFile,
            linkedResult.FailureKind);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(target));
    }

    [Fact]
    public void Verify_ExistingFinalDestinationPreventsExecution()
    {
        using var fixture = new VerifierFixture();
        var context = fixture.CreateWriteResult("photo.jpg", 1, 2);
        File.WriteAllBytes(context.WriteResult.IntendedFinalDestinationPath, [9]);

        var result = Assert.IsType<JpegGpsMetadataWriteVerificationFailureResult>(
            JpegGpsMetadataWriteVerifier.Verify(
                context.WriteResult, fixture.MissingExecutablePath));

        Assert.Equal(JpegGpsMetadataWriteVerificationFailureKind.ExistingFinalDestination,
            result.FailureKind);
        Assert.Equal([9], File.ReadAllBytes(context.WriteResult.IntendedFinalDestinationPath));
    }

    [Fact]
    public void Verify_InvalidExecutablePathReturnsInvalidInput()
    {
        using var fixture = new VerifierFixture();
        var context = fixture.CreateWriteResult("photo.jpg", 1, 2);

        var result = Assert.IsType<JpegGpsMetadataWriteVerificationFailureResult>(
            JpegGpsMetadataWriteVerifier.Verify(context.WriteResult, "relative-exiftool"));

        Assert.Equal(JpegGpsMetadataWriteVerificationFailureKind.InvalidInput,
            result.FailureKind);
    }

    private static string Json(
        double latitude,
        string latitudeReference,
        double longitude,
        string longitudeReference,
        double? altitude = null,
        string? altitudeReference = null,
        string? hash = null,
        string extra = "")
    {
        var altitudeProperties = altitude is null
            ? string.Empty
            : $",\"GPS:GPSAltitude\":{altitude.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
              + $",\"GPS:GPSAltitudeRef\":{altitudeReference}";
        var hashProperty = hash is null
            ? string.Empty
            : $",\"File:ImageDataHash\":\"{hash}\"";
        return $"[{{\"GPS:GPSLatitude\":{latitude.ToString(System.Globalization.CultureInfo.InvariantCulture)},"
            + $"\"GPS:GPSLatitudeRef\":\"{latitudeReference}\","
            + $"\"GPS:GPSLongitude\":{longitude.ToString(System.Globalization.CultureInfo.InvariantCulture)},"
            + $"\"GPS:GPSLongitudeRef\":\"{longitudeReference}\""
            + altitudeProperties + hashProperty + extra + "}]";
    }

    private static string CaptureArgumentsAndOutputScript(
        string argumentsPath,
        string output) =>
        $"printf '' > {ShellQuote(argumentsPath)}\n"
        + $"for argument do printf '%s\\n' \"$argument\" >> {ShellQuote(argumentsPath)}; done\n"
        + $"printf '%s' {ShellQuote(output)}";

    private static string ShellQuote(string value) =>
        "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    private static bool SupportsPosixScripts() =>
        OperatingSystem.IsMacOS() || OperatingSystem.IsLinux();

    private static bool TryCreateFileLink(string linkPath, string targetPath)
    {
        try { File.CreateSymbolicLink(linkPath, targetPath); return true; }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or PlatformNotSupportedException) { return false; }
    }

    private sealed class VerifierFixture : IDisposable
    {
        public VerifierFixture(string? name = null)
        {
            RootPath = Path.Combine(Path.GetTempPath(),
                name ?? "PhotoMigration-JpegGpsVerifierTests",
                Guid.NewGuid().ToString("N"));
            SourceRoot = Path.Combine(RootPath, "source");
            OutputRoot = Path.Combine(RootPath, "output");
            Directory.CreateDirectory(SourceRoot);
            Directory.CreateDirectory(OutputRoot);
        }

        public string RootPath { get; }
        public string SourceRoot { get; }
        public string OutputRoot { get; }
        public string MissingExecutablePath => Path.Combine(RootPath, "missing-exiftool");

        public VerificationContext CreateWriteResult(
            string relativePath,
            double latitude,
            double longitude,
            double? altitude = null)
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
                    SourceRoot, OutputRoot, Assert.Single(destinationPlan.Items)));
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
                "synthetic.jpg", null, null, null,
                latitude, longitude, altitude, null);
            var read = new EmbeddedMetadataReadSuccessResult(
                sourcePath,
                MissingExecutablePath,
                Array.Empty<EmbeddedMetadataValue>(),
                Array.Empty<string>(),
                Array.Empty<string>(),
                string.Empty);
            var metadataPlan = Assert.IsType<SuccessfulMediaMetadataPlan>(
                MediaMetadataPlanBuilder.Build(read, sidecar));
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
                MissingExecutablePath,
                JpegGpsMetadataWriteStatus.PendingVerification,
                string.Empty,
                string.Empty);
            return new VerificationContext(writeResult, sourcePath);
        }

        public string CreateOutputExecutable(string output) =>
            CreateExecutable("exiftool", $"printf '%s' {ShellQuote(output)}");

        public string CreateExecutable(string relativePath, string body)
        {
            if (!SupportsPosixScripts()) throw new PlatformNotSupportedException();

            var path = Combine(RootPath, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "#!/bin/sh\n" + body + "\n", new UTF8Encoding(false));
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

    private sealed record VerificationContext(
        JpegGpsMetadataWriteSuccessResult WriteResult,
        string SourcePath);
}
