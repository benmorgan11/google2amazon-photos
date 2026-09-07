using System.Text;

namespace PhotoMigration.Cli.Tests;

[Collection(ExternalProcessTestCollection.Name)]
public sealed class PlanCommandTests
{
    [Fact]
    public void Plan_CleanMatchedMediaReturnsSuccessAndPrintsOnlySummary()
    {
        if (!SupportsPosixScripts())
        {
            return;
        }

        using var fixture = new PlanFixture();
        fixture.WriteTakeout("album with spaces/photo.jpg", "media");
        fixture.WriteTakeout(
            "album with spaces/photo.jpg.json",
            """
            { "photoTakenTime": { "timestamp": "1700000000" } }
            """);
        var executablePath = fixture.CreateExifTool();

        var execution = Run(
            "plan",
            fixture.TakeoutRootPath,
            "--exiftool",
            executablePath);

        Assert.Equal(0, execution.ExitCode);
        Assert.Equal(
            """
            Planning summary:
            Total media: 1
            Matched sidecars: 1
            Unmatched media: 0
            Invalid sidecars: 0
            Ambiguous sidecars: 0
            No metadata changes proposed: 0
            Safe metadata changes proposed: 1
            Review required: 0
            Embedded metadata unavailable: 0
            Embedded formats not supported yet: 0
            Unused JSON candidates: 0
            Other files: 0

            Planning completed read-only; no files were changed.
            """ + Environment.NewLine,
            execution.Output);
        Assert.Empty(execution.Error);
        Assert.Equal(1, fixture.ReadCalls().Count(call => call == "detect"));
        var readCall = Assert.Single(
            fixture.ReadCalls(),
            call => call.StartsWith("read:", StringComparison.Ordinal));
        Assert.Equal(
            $"read:{Path.Combine(fixture.TakeoutRootPath, "album with spaces", "photo.jpg")}",
            readCall);
        Assert.DoesNotContain("1700000000", execution.Output);
        Assert.DoesNotContain("photo.jpg", execution.Output);
    }

    [Fact]
    public void Plan_AttentionOutputIsDeterministicPrivateAndIncludesTypedReasons()
    {
        if (!SupportsPosixScripts())
        {
            return;
        }

        using var fixture = new PlanFixture();
        fixture.WriteTakeout("G-review.jpg", "media");
        fixture.WriteTakeout(
            "G-review.jpg.json",
            """
            {
              "title": "PRIVATE TITLE",
              "description": "PRIVATE DESCRIPTION",
              "photoTakenTime": { "timestamp": "1700000000" },
              "geoData": {
                "latitude": 12.345678,
                "longitude": 98.765432,
                "altitude": 42.1
              }
            }
            """);
        fixture.WriteTakeout("D-invalid.jpg", "media");
        fixture.WriteTakeout("D-invalid.jpg.json", "PRIVATE INVALID JSON");
        fixture.WriteTakeout("A-ambiguous.jpg", "media");
        fixture.WriteTakeout("A-ambiguous.jpg.json", "{}");
        fixture.WriteTakeout(
            "A-ambiguous.jpg.supplemental-metadata.json",
            "{}");
        fixture.WriteTakeout("C-video.m4v", "media");
        fixture.WriteTakeout("B-failure.jpg", "media");
        fixture.WriteTakeout("album.json", "PRIVATE ALBUM CONTENTS");
        fixture.WriteTakeout("notes.txt", "PRIVATE OTHER CONTENTS");
        var executablePath = fixture.CreateExifTool();

        var first = Run(
            "plan",
            fixture.TakeoutRootPath,
            "--exiftool",
            executablePath);
        var second = Run(
            "plan",
            fixture.TakeoutRootPath,
            "--exiftool",
            executablePath);

        Assert.Equal(2, first.ExitCode);
        Assert.Equal(first.Output, second.Output);
        Assert.Equal(first.Error, second.Error);
        Assert.Empty(first.Error);
        Assert.Contains("Total media: 5", first.Output);
        Assert.Contains("Matched sidecars: 1", first.Output);
        Assert.Contains("Unmatched media: 2", first.Output);
        Assert.Contains("Invalid sidecars: 1", first.Output);
        Assert.Contains("Ambiguous sidecars: 1", first.Output);
        Assert.Contains("No metadata changes proposed: 2", first.Output);
        Assert.Contains("Safe metadata changes proposed: 0", first.Output);
        Assert.Contains("Review required: 1", first.Output);
        Assert.Contains("Embedded metadata unavailable: 1", first.Output);
        Assert.Contains("Embedded formats not supported yet: 1", first.Output);
        Assert.Contains("Unused JSON candidates: 3", first.Output);
        Assert.Contains("Other files: 1", first.Output);

        Assert.Contains("A-ambiguous.jpg.json [LegacyJson]", first.Output);
        Assert.Contains(
            "A-ambiguous.jpg.supplemental-metadata.json [SupplementalMetadataJson]",
            first.Output);
        Assert.Contains(
            "Sidecar: invalid 'D-invalid.jpg.json' [LegacyJson].",
            first.Output);
        Assert.Contains("Embedded metadata: ExifTool could not read the file.", first.Output);
        Assert.Contains(
            "Embedded metadata: reader not implemented yet for format '.m4v'.",
            first.Output);
        Assert.Contains(
            "Metadata: embedded and sidecar capture times conflict.",
            first.Output);
        Assert.Contains(
            "Planning completed read-only; no files were changed.",
            first.Output);

        AssertAppearsBefore(first.Output, "A-ambiguous.jpg", "B-failure.jpg");
        AssertAppearsBefore(first.Output, "B-failure.jpg", "C-video.m4v");
        AssertAppearsBefore(first.Output, "C-video.m4v", "D-invalid.jpg");
        AssertAppearsBefore(first.Output, "D-invalid.jpg", "G-review.jpg");

        foreach (var privateValue in new[]
                 {
                     "PRIVATE TITLE",
                     "PRIVATE DESCRIPTION",
                     "1700000000",
                     "12.345678",
                     "98.765432",
                     "42.1",
                     "PRIVATE INVALID JSON",
                     "PRIVATE ALBUM CONTENTS",
                     "PRIVATE OTHER CONTENTS",
                     "CAPTURED PRIVATE OUTPUT",
                     "CAPTURED PRIVATE ERROR",
                     "2020:01:02 03:04:05Z"
                 })
        {
            Assert.DoesNotContain(privateValue, first.Output);
            Assert.DoesNotContain(privateValue, first.Error);
        }
    }

    [Fact]
    public void Plan_MissingExplicitExifToolIsAuthoritativeAndReturnsError()
    {
        using var fixture = new PlanFixture();
        var missingExecutable = Path.Combine(
            fixture.RootPath,
            "missing tools",
            "exiftool");

        var execution = Run(
            "plan",
            fixture.TakeoutRootPath,
            "--exiftool",
            missingExecutable);

        Assert.Equal(1, execution.ExitCode);
        Assert.Empty(execution.Output);
        Assert.Contains("explicit ExifTool path does not exist", execution.Error);
        Assert.Contains(Path.GetFullPath(missingExecutable), execution.Error);
        Assert.DoesNotContain(
            "Planning completed read-only; no files were changed.",
            execution.Error);
    }

    [Fact]
    public void Plan_InvalidArgumentsReturnUsageError()
    {
        using var fixture = new PlanFixture();

        var executions = new[]
        {
            Run("plan"),
            Run("plan", fixture.TakeoutRootPath, "--exiftool"),
            Run("plan", fixture.TakeoutRootPath, "--other", "value"),
            Run("plan", ""),
            Run("plan", fixture.TakeoutRootPath, "--exiftool", "")
        };

        foreach (var execution in executions)
        {
            Assert.Equal(1, execution.ExitCode);
            Assert.Empty(execution.Output);
            Assert.Contains(
                "PhotoMigration.Cli plan <takeout-folder> " +
                "[--exiftool <executable-path>]",
                execution.Error);
            Assert.Contains("PhotoMigration.Cli inventory <folder>", execution.Error);
            Assert.Contains("PhotoMigration.Cli analyze <folder>", execution.Error);
            Assert.Contains("PhotoMigration.Cli check-exiftool", execution.Error);
        }
    }

    private static void AssertAppearsBefore(
        string output,
        string first,
        string second) =>
        Assert.True(
            output.IndexOf(first, StringComparison.Ordinal)
            < output.IndexOf(second, StringComparison.Ordinal));

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

    private sealed class PlanFixture : IDisposable
    {
        public PlanFixture()
        {
            RootPath = Path.Combine(
                Path.GetTempPath(),
                $"PhotoMigration Plan CLI {Guid.NewGuid():N}");
            TakeoutRootPath = Path.Combine(RootPath, "Takeout source");
            CallLogPath = Path.Combine(RootPath, "ExifTool calls.txt");
            Directory.CreateDirectory(TakeoutRootPath);
        }

        public string RootPath { get; }

        public string TakeoutRootPath { get; }

        private string CallLogPath { get; }

        public void WriteTakeout(string relativePath, string contents)
        {
            var path = Path.Combine(
                TakeoutRootPath,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, contents, Encoding.UTF8);
        }

        public string CreateExifTool()
        {
            var executablePath = Path.Combine(
                RootPath,
                "tools with spaces",
                "fake exiftool");
            Directory.CreateDirectory(Path.GetDirectoryName(executablePath)!);
            var script = "#!/bin/sh\n"
                + $"log_path='{CallLogPath}'\n"
                + """
                if [ "$#" -eq 1 ] && [ "$1" = "-ver" ]; then
                  printf '%s\n' 'detect' >> "$log_path"
                  printf '%s\n' '13.59'
                  exit 0
                fi

                media=''
                for argument in "$@"; do
                  media="$argument"
                done
                printf 'read:%s\n' "$media" >> "$log_path"

                case "$media" in
                  *failure.jpg)
                    printf '%s' 'CAPTURED PRIVATE OUTPUT'
                    printf '%s' 'CAPTURED PRIVATE ERROR' >&2
                    exit 9
                    ;;
                  *review.jpg)
                    printf '%s' '[{"ExifIFD:DateTimeOriginal":"2020:01:02 03:04:05Z"}]'
                    ;;
                  *)
                    printf '%s' '[{}]'
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

        public IReadOnlyList<string> ReadCalls() =>
            File.Exists(CallLogPath)
                ? File.ReadAllLines(CallLogPath)
                : Array.Empty<string>();

        public void Dispose()
        {
            Directory.Delete(RootPath, recursive: true);
        }
    }
}
