using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using PhotoMigration.Core;

namespace PhotoMigration.Core.Tests;

[Collection(ExternalProcessTestCollection.Name)]
public sealed class ExifToolImageDataHashReaderTests
{
    private const string LowercaseHash =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string UppercaseHash =
        "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";

    [Fact]
    public void Read_UsesExactArgumentsAndReturnsNormalizedHashWithoutChangingTemporaryCopy()
    {
        if (!SupportsPosixScripts())
        {
            return;
        }

        using var fixture = new HashReaderFixture("Hash Reader 旅行");
        var staging = fixture.Stage("Family Photos/Café photo.JpG", [1, 2, 3, 4]);
        var lastWriteTime = DateTime.UtcNow.AddDays(-3);
        File.SetLastWriteTimeUtc(staging.TemporaryCopyPath, lastWriteTime);
        var originalContents = File.ReadAllBytes(staging.TemporaryCopyPath);
        var originalLastWriteTime = File.GetLastWriteTimeUtc(staging.TemporaryCopyPath);
        var executablePath = fixture.CreateExecutable(
            "tools with spaces/fake exiftool",
            ExactArgumentScript(LowercaseHash, staging.TemporaryCopyPath));

        var result = Assert.IsType<ImageDataHashReadSuccessResult>(
            ExifToolImageDataHashReader.Read(staging, executablePath));

        Assert.Same(staging, result.StagingResult);
        Assert.Equal(staging.TemporaryCopyPath, result.TemporaryCopyPath);
        Assert.Equal(Path.GetFullPath(executablePath), result.ExifToolExecutablePath);
        Assert.Equal("SHA256", result.AlgorithmName);
        Assert.Equal(LowercaseHash, result.RawImageDataHash);
        Assert.Equal(UppercaseHash, result.ImageDataHashSha256);
        Assert.Empty(result.Warnings);
        Assert.Equal(string.Empty, result.StandardError);
        Assert.Equal(originalContents, File.ReadAllBytes(staging.TemporaryCopyPath));
        Assert.Equal(
            originalLastWriteTime,
            File.GetLastWriteTimeUtc(staging.TemporaryCopyPath));
        Assert.False(File.Exists(staging.IntendedFinalDestinationPath));
    }

    [Theory]
    [InlineData("photo.JpG")]
    [InlineData("photo.jPeG")]
    [InlineData("photo.HEic")]
    [InlineData("photo.HeIf")]
    [InlineData("video.MoV")]
    [InlineData("video.mP4")]
    public void Read_SupportsCurrentPlannedMediaFormatsIgnoringCase(string relativePath)
    {
        if (!SupportsPosixScripts())
        {
            return;
        }

        using var fixture = new HashReaderFixture();
        var staging = fixture.Stage(relativePath, [1]);
        var executablePath = fixture.CreateExecutable(
            "exiftool",
            OutputScript($"[{{\"File:ImageDataHash\":\"{UppercaseHash}\"}}]"));

        var result = Assert.IsType<ImageDataHashReadSuccessResult>(
            ExifToolImageDataHashReader.Read(staging, executablePath));

        Assert.Equal(UppercaseHash, result.ImageDataHashSha256);
    }

    [Fact]
    public void Read_MissingTemporaryCopyReturnsTypedResultWithoutRunningExifTool()
    {
        using var fixture = new HashReaderFixture();
        var staging = fixture.Stage("photo.jpg", [1]);
        File.Delete(staging.TemporaryCopyPath);

        var result = Assert.IsType<ImageDataHashMissingTemporaryFileResult>(
            ExifToolImageDataHashReader.Read(staging, fixture.MissingExecutablePath));

        Assert.Same(staging, result.StagingResult);
        Assert.Equal(staging.TemporaryCopyPath, result.TemporaryCopyPath);
    }

    [Fact]
    public void Read_LinkedTemporaryCopyReturnsTypedResult_WhenLinksAreSupported()
    {
        using var fixture = new HashReaderFixture();
        using var outside = new HashReaderFixture();
        var staging = fixture.Stage("photo.jpg", [1]);
        var target = outside.WriteFile("target.jpg", [9]);
        File.Delete(staging.TemporaryCopyPath);
        if (!TryCreateFileLink(staging.TemporaryCopyPath, target))
        {
            return;
        }

        var result = Assert.IsType<ImageDataHashLinkedTemporaryFileResult>(
            ExifToolImageDataHashReader.Read(staging, fixture.MissingExecutablePath));

        Assert.Equal([9], File.ReadAllBytes(target));
        Assert.Equal(staging.TemporaryCopyPath, result.TemporaryCopyPath);
    }

    [Fact]
    public void Read_NonRegularTemporaryCopyReturnsTypedResult()
    {
        using var fixture = new HashReaderFixture();
        var staging = fixture.Stage("photo.jpg", [1]);
        File.Delete(staging.TemporaryCopyPath);
        Directory.CreateDirectory(staging.TemporaryCopyPath);

        var result = Assert.IsType<ImageDataHashNonRegularTemporaryFileResult>(
            ExifToolImageDataHashReader.Read(staging, fixture.MissingExecutablePath));

        Assert.Equal(staging.TemporaryCopyPath, result.TemporaryCopyPath);
    }

    [Fact]
    public void Read_TemporaryPathOutsideFinalDirectoryReturnsInvalidStagingResult()
    {
        using var fixture = new HashReaderFixture();
        var staging = fixture.Stage("photo.jpg", [1]);
        var forged = staging with
        {
            TemporaryCopyPath = Path.Combine(fixture.RootPath, "elsewhere.jpg")
        };

        var result = Assert.IsType<ImageDataHashInvalidStagingResult>(
            ExifToolImageDataHashReader.Read(forged, fixture.MissingExecutablePath));

        Assert.Contains("not beside", result.Message);
    }

    [Fact]
    public void Read_UnsupportedFormatDoesNotRunExifTool()
    {
        using var fixture = new HashReaderFixture();
        var staging = fixture.Stage("photo.png", [1]);

        var result = Assert.IsType<ImageDataHashUnsupportedMediaResult>(
            ExifToolImageDataHashReader.Read(staging, fixture.MissingExecutablePath));

        Assert.Equal(".png", result.Extension);
    }

    [Fact]
    public void Read_MissingImageDataHashReturnsTypedFailure()
    {
        if (!SupportsPosixScripts())
        {
            return;
        }

        using var fixture = new HashReaderFixture();
        var staging = fixture.Stage("photo.jpg", [1]);
        var executablePath = fixture.CreateExecutable(
            "exiftool",
            OutputScript("[{\"SourceFile\":\"photo.jpg\"}]"));

        var result = Assert.IsType<ImageDataHashMissingValueResult>(
            ExifToolImageDataHashReader.Read(staging, executablePath));

        Assert.Empty(result.Warnings);
    }

    [Theory]
    [InlineData("not JSON")]
    [InlineData("[]")]
    [InlineData("[{},{}]")]
    public void Read_MalformedOrWrongJsonShapeReturnsTypedFailure(string output)
    {
        if (!SupportsPosixScripts())
        {
            return;
        }

        using var fixture = new HashReaderFixture();
        var staging = fixture.Stage("photo.jpg", [1]);
        var executablePath = fixture.CreateExecutable("exiftool", OutputScript(output));

        var result = Assert.IsType<ImageDataHashMalformedJsonResult>(
            ExifToolImageDataHashReader.Read(staging, executablePath));

        Assert.Equal(output, result.StandardOutput);
    }

    [Fact]
    public void Read_WrongGroupDoesNotSatisfyRequiredFileHash()
    {
        if (!SupportsPosixScripts())
        {
            return;
        }

        using var fixture = new HashReaderFixture();
        var staging = fixture.Stage("photo.jpg", [1]);
        var executablePath = fixture.CreateExecutable(
            "exiftool",
            OutputScript($"[{{\"QuickTime:ImageDataHash\":\"{UppercaseHash}\"}}]"));

        Assert.IsType<ImageDataHashMissingValueResult>(
            ExifToolImageDataHashReader.Read(staging, executablePath));
    }

    [Fact]
    public void Read_DuplicateFileHashValuesReturnMalformedJsonResult()
    {
        if (!SupportsPosixScripts())
        {
            return;
        }

        using var fixture = new HashReaderFixture();
        var staging = fixture.Stage("photo.jpg", [1]);
        var json = $"[{{\"File:ImageDataHash\":\"{UppercaseHash}\"," +
                   $"\"File:ImageDataHash\":\"{UppercaseHash}\"}}]";
        var executablePath = fixture.CreateExecutable("exiftool", OutputScript(json));

        var result = Assert.IsType<ImageDataHashMalformedJsonResult>(
            ExifToolImageDataHashReader.Read(staging, executablePath));

        Assert.Contains("exactly one File:ImageDataHash", result.Message);
    }

    [Theory]
    [InlineData("0123456789abcdef")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0")]
    [InlineData("g123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    public void Read_InvalidHashLengthOrCharactersReturnsTypedFailure(string hash)
    {
        if (!SupportsPosixScripts())
        {
            return;
        }

        using var fixture = new HashReaderFixture();
        var staging = fixture.Stage("photo.jpg", [1]);
        var executablePath = fixture.CreateExecutable(
            "exiftool",
            OutputScript($"[{{\"File:ImageDataHash\":\"{hash}\"}}]"));

        var result = Assert.IsType<ImageDataHashInvalidValueResult>(
            ExifToolImageDataHashReader.Read(staging, executablePath));

        Assert.Equal(hash, result.RawImageDataHash);
    }

    [Fact]
    public void Read_NonzeroExitReturnsExecutionFailureWithCapturedOutput()
    {
        if (!SupportsPosixScripts())
        {
            return;
        }

        using var fixture = new HashReaderFixture();
        var staging = fixture.Stage("photo.jpg", [1]);
        var executablePath = fixture.CreateExecutable(
            "exiftool",
            OutputScript("failure output", "failure error", exitCode: 7));

        var result = Assert.IsType<ImageDataHashExifToolFailureResult>(
            ExifToolImageDataHashReader.Read(staging, executablePath));

        Assert.Equal(7, result.ExitCode);
        Assert.Equal("failure output", result.StandardOutput);
        Assert.Equal("failure error", result.StandardError);
    }

    [Fact]
    public void Read_TimeoutUsesExplicitLimitAndBoundedCleanup()
    {
        if (!SupportsPosixScripts())
        {
            return;
        }

        using var fixture = new HashReaderFixture();
        var staging = fixture.Stage("video.mp4", [1]);
        var executablePath = fixture.CreateExecutable(
            "exiftool",
            "#!/bin/sh\nsleep 10\nprintf '[]'");
        var timeout = TimeSpan.FromMilliseconds(100);
        var stopwatch = Stopwatch.StartNew();

        var result = Assert.IsType<ImageDataHashReadTimedOutResult>(
            ExifToolImageDataHashReader.Read(staging, executablePath, timeout));

        stopwatch.Stop();
        Assert.Equal(TimeSpan.FromMinutes(10),
            ExifToolImageDataHashReader.DefaultHashTimeout);
        Assert.Equal(timeout, result.Timeout);
        Assert.Null(result.TerminationError);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Read_WarningsAndStandardErrorAreRetainedButNotUsedAsHashValues()
    {
        if (!SupportsPosixScripts())
        {
            return;
        }

        using var fixture = new HashReaderFixture();
        var staging = fixture.Stage("photo.jpg", [1]);
        var json = $"[{{\"ExifTool:Warning\":\"{LowercaseHash}\"," +
                   $"\"File:ImageDataHash\":\"{UppercaseHash}\"}}]";
        var executablePath = fixture.CreateExecutable(
            "exiftool",
            OutputScript(json, "synthetic standard error"));

        var result = Assert.IsType<ImageDataHashReadSuccessResult>(
            ExifToolImageDataHashReader.Read(staging, executablePath));

        Assert.Equal([LowercaseHash], result.Warnings);
        Assert.Equal("synthetic standard error", result.StandardError);
        Assert.Equal(UppercaseHash, result.ImageDataHashSha256);
    }

    [Fact]
    public void Read_RejectsSameSizeTemporaryContentChangedByExifTool()
    {
        if (!SupportsPosixScripts())
        {
            return;
        }

        using var fixture = new HashReaderFixture();
        var staging = fixture.Stage("photo.jpg", [1, 2, 3, 4]);
        var executablePath = fixture.CreateExecutable(
            "exiftool",
            MutatingOutputScript(
                "WXYZ",
                $"[{{\"File:ImageDataHash\":\"{UppercaseHash}\"}}]"));

        var result = Assert.IsType<ImageDataHashTemporaryVerificationFailureResult>(
            ExifToolImageDataHashReader.Read(staging, executablePath));

        Assert.Equal(4, result.ExpectedByteCount);
        Assert.Equal(4, result.ActualByteCount);
        Assert.Equal(staging.TemporaryCopySha256, result.ExpectedWholeFileSha256);
        Assert.NotEqual(result.ExpectedWholeFileSha256, result.ActualWholeFileSha256);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("WXYZ"))),
            result.ActualWholeFileSha256);
        Assert.Equal("WXYZ", File.ReadAllText(staging.TemporaryCopyPath));
        Assert.False(File.Exists(staging.IntendedFinalDestinationPath));
    }

    [Fact]
    public void Read_RejectsTemporarySizeChangedByExifTool()
    {
        if (!SupportsPosixScripts())
        {
            return;
        }

        using var fixture = new HashReaderFixture();
        var staging = fixture.Stage("photo.jpg", [1, 2, 3, 4]);
        var executablePath = fixture.CreateExecutable(
            "exiftool",
            MutatingOutputScript(
                "changed-size",
                $"[{{\"File:ImageDataHash\":\"{UppercaseHash}\"}}]"));

        var result = Assert.IsType<ImageDataHashTemporaryVerificationFailureResult>(
            ExifToolImageDataHashReader.Read(staging, executablePath));

        Assert.Equal(4, result.ExpectedByteCount);
        Assert.Equal(12, result.ActualByteCount);
        Assert.NotEqual(result.ExpectedWholeFileSha256, result.ActualWholeFileSha256);
        Assert.Equal("changed-size", File.ReadAllText(staging.TemporaryCopyPath));
        Assert.False(File.Exists(staging.IntendedFinalDestinationPath));
    }

    private static bool SupportsPosixScripts() =>
        OperatingSystem.IsMacOS() || OperatingSystem.IsLinux();

    private static bool TryCreateFileLink(string linkPath, string targetPath)
    {
        try
        {
            File.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
                                          or IOException
                                          or PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static string ExactArgumentScript(string hash, string expectedMediaPath) =>
        $$"""
        #!/bin/sh
        if [ "$#" -ne 7 ]; then printf 'unexpected argument count' >&2; exit 91; fi
        if [ "$1" != "-json" ]; then exit 92; fi
        if [ "$2" != "-G1" ]; then exit 93; fi
        if [ "$3" != "-s" ]; then exit 94; fi
        if [ "$4" != "-api" ]; then exit 95; fi
        if [ "$5" != "ImageHashType=SHA256" ]; then exit 96; fi
        if [ "$6" != "-ImageDataHash" ]; then exit 97; fi
        if [ "$7" != '{{expectedMediaPath}}' ]; then exit 98; fi
        printf '%s' '[{"File:ImageDataHash":"{{hash}}"}]'
        """;

    private static string OutputScript(
        string standardOutput,
        string standardError = "",
        int exitCode = 0) =>
        $"""
        #!/bin/sh
        printf '%s' '{standardOutput}'
        printf '%s' '{standardError}' >&2
        exit {exitCode}
        """;

    private static string MutatingOutputScript(
        string replacementContents,
        string standardOutput) =>
        $"""
        #!/bin/sh
        printf '%s' '{replacementContents}' > "$7"
        printf '%s' '{standardOutput}'
        """;

    private sealed class HashReaderFixture : IDisposable
    {
        public HashReaderFixture(string? name = null)
        {
            RootPath = Path.Combine(
                Path.GetTempPath(),
                name ?? "PhotoMigration-HashReader",
                Guid.NewGuid().ToString("N"));
            SourceRoot = Directory.CreateDirectory(Path.Combine(RootPath, "source")).FullName;
            OutputRoot = Directory.CreateDirectory(Path.Combine(RootPath, "output")).FullName;
        }

        public string RootPath { get; }

        public string SourceRoot { get; }

        public string OutputRoot { get; }

        public string MissingExecutablePath => Path.Combine(RootPath, "missing-exiftool");

        public VerifiedMediaFileStagingSuccessResult Stage(
            string relativePath,
            byte[] contents)
        {
            var sourcePath = WriteFile(
                Path.Combine("source", relativePath),
                contents);
            var planning = Assert.IsType<DestinationPathPlanningSuccessResult>(
                DestinationPathPlanner.Plan(
                    SourceRoot,
                    OutputRoot,
                    [new InventoryEntry(relativePath, contents.Length)]));
            var staging = Assert.IsType<VerifiedMediaFileStagingSuccessResult>(
                VerifiedMediaFileStager.Stage(
                    SourceRoot,
                    OutputRoot,
                    Assert.Single(planning.Items)));
            Assert.Equal(sourcePath, staging.PlanningItem.AbsoluteSourcePath);
            return staging;
        }

        public string WriteFile(string relativePath, byte[] contents)
        {
            var path = Path.Combine(
                RootPath,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, contents);
            return path;
        }

        public string CreateExecutable(string relativePath, string contents)
        {
            var path = WriteText(relativePath, contents);
            if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
            {
                File.SetUnixFileMode(
                    path,
                    UnixFileMode.UserRead
                    | UnixFileMode.UserWrite
                    | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead
                    | UnixFileMode.GroupExecute
                    | UnixFileMode.OtherRead
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

        private string WriteText(string relativePath, string contents)
        {
            var path = Path.Combine(
                RootPath,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(
                path,
                contents,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return path;
        }
    }
}
