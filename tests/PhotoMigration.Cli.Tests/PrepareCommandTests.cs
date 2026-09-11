using System.Text;

namespace PhotoMigration.Cli.Tests;

[Collection(ExternalProcessTestCollection.Name)]
public sealed class PrepareCommandTests
{
    [Fact]
    public void Prepare_WithExplicitExifToolPublishesAllMediaAndReturnsSuccess()
    {
        if (!SupportsPosixScripts()) return;

        using var fixture = new PrepareFixture();
        fixture.WriteTakeout("z-video.mp4", [9, 8, 7]);
        fixture.WriteTakeout("A-photo.jpg", [1, 2, 3]);
        fixture.WriteTakeoutText("album.json", "PRIVATE ALBUM JSON");
        fixture.WriteTakeoutText("notes.txt", "PRIVATE OTHER FILE");
        var sourceBefore = fixture.SnapshotTakeout();
        var executablePath = fixture.CreateExifTool();

        var execution = Run(
            "prepare",
            fixture.SourceRoot,
            fixture.OutputRoot,
            "--exiftool",
            executablePath);

        Assert.Equal(0, execution.ExitCode);
        Assert.Contains(
            "Preparing media files. Large libraries may take some time.",
            execution.Output);
        Assert.Contains("Total media: 2", execution.Output);
        Assert.Contains("Published unchanged: 2", execution.Output);
        Assert.Contains("Published with verified JPEG GPS: 0", execution.Output);
        Assert.Contains("Attention required: 0", execution.Output);
        Assert.Contains("Failed: 0", execution.Output);
        Assert.Contains("Unused JSON: 1", execution.Output);
        Assert.Contains("Other files: 1", execution.Output);
        Assert.Contains(
            "Preparation completed. The Takeout source was not changed.",
            execution.Output);
        Assert.Contains($"Output directory: {fixture.OutputRoot}", execution.Output);
        Assert.DoesNotContain("A-photo.jpg", execution.Output);
        Assert.DoesNotContain("z-video.mp4", execution.Output);
        Assert.DoesNotContain("PRIVATE", execution.Output);
        Assert.Empty(execution.Error);
        Assert.Equal([1, 2, 3], fixture.ReadOutput("A-photo.jpg"));
        Assert.Equal([9, 8, 7], fixture.ReadOutput("z-video.mp4"));
        Assert.Equal(1, fixture.ReadExifToolCalls().Count(call => call == "detect"));
        Assert.Equal(
            [
                "detect",
                $"read:{Path.Combine(fixture.SourceRoot, "A-photo.jpg")}",
                $"read:{Path.Combine(fixture.SourceRoot, "z-video.mp4")}"
            ],
            fixture.ReadExifToolCalls());
        fixture.AssertTakeoutUnchanged(sourceBefore);
    }

    [Fact]
    public void Prepare_AttentionAndFailureAreOrderedReportedAndReturnTwo()
    {
        if (!SupportsPosixScripts()) return;

        using var fixture = new PrepareFixture();
        fixture.WriteTakeout("A-invalid.jpg", [1]);
        fixture.WriteTakeoutText(
            "A-invalid.jpg.json",
            "PRIVATE INVALID SIDECAR CONTENTS");
        fixture.WriteTakeout("B-existing.jpg", [2]);
        fixture.WriteTakeout("z-safe.mp4", [3]);
        fixture.WriteOutput("B-existing.jpg", [7, 7]);
        var sourceBefore = fixture.SnapshotTakeout();

        var execution = Run(
            "prepare",
            fixture.SourceRoot,
            fixture.OutputRoot,
            "--exiftool",
            fixture.CreateExifTool());

        Assert.Equal(2, execution.ExitCode);
        Assert.Contains("Total media: 3", execution.Output);
        Assert.Contains("Published unchanged: 1", execution.Output);
        Assert.Contains("Attention required: 1", execution.Output);
        Assert.Contains("Failed: 1", execution.Output);
        Assert.Contains("Items not published:", execution.Output);
        Assert.Contains("A-invalid.jpg", execution.Output);
        Assert.Contains("Reason: The matched sidecar is invalid.", execution.Output);
        Assert.Contains("B-existing.jpg", execution.Output);
        Assert.Contains("Failed stage: Staging", execution.Output);
        Assert.Contains(
            "Error: The media file could not be staged.",
            execution.Output);
        Assert.True(
            execution.Output.IndexOf("A-invalid.jpg", StringComparison.Ordinal)
            < execution.Output.IndexOf("B-existing.jpg", StringComparison.Ordinal));
        Assert.DoesNotContain("z-safe.mp4", execution.Output);
        Assert.DoesNotContain("PRIVATE INVALID SIDECAR CONTENTS", execution.Output);
        Assert.DoesNotContain("PRIVATE INVALID SIDECAR CONTENTS", execution.Error);
        Assert.Empty(execution.Error);
        Assert.Equal([7, 7], fixture.ReadOutput("B-existing.jpg"));
        Assert.Equal([3], fixture.ReadOutput("z-safe.mp4"));
        fixture.AssertTakeoutUnchanged(sourceBefore);
    }

    [Fact]
    public void Prepare_MissingExplicitExifToolIsAuthoritativeAndReturnsOne()
    {
        using var fixture = new PrepareFixture();
        var missingPath = Path.Combine(fixture.RootPath, "missing", "exiftool");

        var execution = Run(
            "prepare",
            fixture.SourceRoot,
            fixture.OutputRoot,
            "--exiftool",
            missingPath);

        Assert.Equal(1, execution.ExitCode);
        Assert.Empty(execution.Output);
        Assert.Contains("explicit ExifTool path does not exist", execution.Error);
        Assert.Contains(Path.GetFullPath(missingPath), execution.Error);
    }

    [Fact]
    public void Prepare_MissingOrOverlappingFoldersReturnOperationalErrors()
    {
        using var fixture = new PrepareFixture();
        var missingOutput = Path.Combine(fixture.RootPath, "missing output");

        var missing = Run(
            "prepare",
            fixture.SourceRoot,
            missingOutput,
            "--exiftool",
            "unused");
        var overlapping = Run(
            "prepare",
            fixture.SourceRoot,
            fixture.SourceRoot,
            "--exiftool",
            "unused");

        Assert.Equal(1, missing.ExitCode);
        Assert.Empty(missing.Output);
        Assert.Contains("Output directory does not exist", missing.Error);
        Assert.Equal(1, overlapping.ExitCode);
        Assert.Empty(overlapping.Output);
        Assert.Contains("must be separate and must not overlap", overlapping.Error);
    }

    [Fact]
    public void Prepare_InvalidArgumentsPrintUpdatedUsage()
    {
        using var fixture = new PrepareFixture();
        var executions = new[]
        {
            Run("prepare"),
            Run("prepare", fixture.SourceRoot),
            Run("prepare", fixture.SourceRoot, fixture.OutputRoot, "--exiftool"),
            Run("prepare", fixture.SourceRoot, fixture.OutputRoot, "--other", "value"),
            Run("prepare", "", fixture.OutputRoot),
            Run("prepare", fixture.SourceRoot, "")
        };

        foreach (var execution in executions)
        {
            Assert.Equal(1, execution.ExitCode);
            Assert.Empty(execution.Output);
            Assert.Contains(
                "PhotoMigration.Cli prepare <takeout-folder> <output-folder> " +
                "[--exiftool <executable-path>]",
                execution.Error);
            Assert.Contains("PhotoMigration.Cli inventory <folder>", execution.Error);
            Assert.Contains("PhotoMigration.Cli analyze <folder>", execution.Error);
            Assert.Contains("PhotoMigration.Cli check-exiftool", execution.Error);
            Assert.Contains("PhotoMigration.Cli plan <takeout-folder>", execution.Error);
        }
    }

    private static CliExecution Run(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = CliApplication.Run(args, output, error);
        return new CliExecution(exitCode, output.ToString(), error.ToString());
    }

    private sealed record CliExecution(int ExitCode, string Output, string Error);

    private static bool SupportsPosixScripts() =>
        OperatingSystem.IsMacOS() || OperatingSystem.IsLinux();

    private sealed class PrepareFixture : IDisposable
    {
        private readonly string _callLogPath;

        public PrepareFixture()
        {
            RootPath = Path.Combine(
                Path.GetTempPath(),
                $"PhotoMigration Prepare CLI {Guid.NewGuid():N}");
            SourceRoot = Path.Combine(RootPath, "Takeout source");
            OutputRoot = Path.Combine(RootPath, "Prepared output");
            _callLogPath = Path.Combine(RootPath, "ExifTool calls.txt");
            Directory.CreateDirectory(SourceRoot);
            Directory.CreateDirectory(OutputRoot);
        }

        public string RootPath { get; }

        public string SourceRoot { get; }

        public string OutputRoot { get; }

        public void WriteTakeout(string relativePath, byte[] contents)
        {
            var path = Combine(SourceRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, contents);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-2));
        }

        public void WriteTakeoutText(string relativePath, string contents)
        {
            var path = Combine(SourceRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, contents, new UTF8Encoding(false));
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-2));
        }

        public void WriteOutput(string relativePath, byte[] contents)
        {
            var path = Combine(OutputRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, contents);
        }

        public byte[] ReadOutput(string relativePath) =>
            File.ReadAllBytes(Combine(OutputRoot, relativePath));

        public string CreateExifTool()
        {
            var executablePath = Path.Combine(
                RootPath,
                "tools with spaces",
                "fake exiftool");
            Directory.CreateDirectory(Path.GetDirectoryName(executablePath)!);
            var script = $$"""
                #!/bin/sh
                if [ "$#" -eq 1 ] && [ "$1" = "-ver" ]; then
                  printf '%s\n' 'detect' >> '{{_callLogPath}}'
                  printf '%s\n' '13.59'
                  exit 0
                fi
                media=''
                for argument in "$@"; do media="$argument"; done
                printf 'read:%s\n' "$media" >> '{{_callLogPath}}'
                case "$media" in
                  *.mov|*.MOV|*.mp4|*.MP4)
                    printf '%s' '[{"QuickTime:CreateDate":"2020:01:02 03:04:05"}]'
                    ;;
                  *)
                    printf '%s' '[{"ExifIFD:DateTimeOriginal":"2020:01:02 03:04:05"}]'
                    ;;
                esac
                """;
            File.WriteAllText(executablePath, script, new UTF8Encoding(false));
            if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
            {
                File.SetUnixFileMode(
                    executablePath,
                    UnixFileMode.UserRead
                    | UnixFileMode.UserWrite
                    | UnixFileMode.UserExecute);
            }

            return executablePath;
        }

        public IReadOnlyList<string> ReadExifToolCalls() =>
            File.Exists(_callLogPath)
                ? File.ReadAllLines(_callLogPath)
                : Array.Empty<string>();

        public IReadOnlyDictionary<string, FileSnapshot> SnapshotTakeout() =>
            Directory.EnumerateFiles(SourceRoot, "*", SearchOption.AllDirectories)
                .ToDictionary(
                    path => Path.GetRelativePath(SourceRoot, path),
                    path => new FileSnapshot(
                        File.ReadAllBytes(path),
                        File.GetLastWriteTimeUtc(path)),
                    StringComparer.Ordinal);

        public void AssertTakeoutUnchanged(
            IReadOnlyDictionary<string, FileSnapshot> before)
        {
            var after = SnapshotTakeout();
            Assert.Equal(
                before.Keys.Order(StringComparer.Ordinal),
                after.Keys.Order(StringComparer.Ordinal));
            foreach (var path in before.Keys)
            {
                Assert.Equal(before[path].Contents, after[path].Contents);
                Assert.Equal(before[path].LastWriteTimeUtc, after[path].LastWriteTimeUtc);
            }
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

    private sealed record FileSnapshot(byte[] Contents, DateTime LastWriteTimeUtc);
}
