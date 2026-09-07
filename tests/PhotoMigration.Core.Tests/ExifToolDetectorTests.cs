using System.Diagnostics;
using System.Text;

namespace PhotoMigration.Core.Tests;

[Collection(ExternalProcessTestCollection.Name)]
public sealed class ExifToolDetectorTests
{
    [Fact]
    public void Detect_ValidExplicitExecutableReturnsAbsolutePathAndTrimmedVersion()
    {
        if (!SupportsPosixScripts())
        {
            return;
        }

        using var scripts = new TemporaryScripts();
        var executable = scripts.CreateExecutable(
            "explicit tool",
            VersionScript("  13.59  "));

        var result = Assert.IsType<ExifToolFoundResult>(
            ExifToolDetector.Detect(explicitExecutablePath: executable));

        Assert.Equal(Path.GetFullPath(executable), result.ExecutablePath);
        Assert.Equal("13.59", result.Version);
        Assert.Equal([Path.GetFullPath(executable)], result.CandidatePaths);
    }

    [Fact]
    public void Detect_ExplicitPathTakesPriorityOverSearchPath()
    {
        if (!SupportsPosixScripts())
        {
            return;
        }

        using var scripts = new TemporaryScripts();
        var explicitExecutable = scripts.CreateExecutable(
            "explicit/exiftool",
            VersionScript("13.59"));
        var pathExecutable = scripts.CreateExecutable(
            "on-path/exiftool",
            VersionScript("12.00"));

        var result = Assert.IsType<ExifToolFoundResult>(
            ExifToolDetector.Detect(
                explicitExecutablePath: explicitExecutable,
                searchPath: Path.GetDirectoryName(pathExecutable)));

        Assert.Equal(Path.GetFullPath(explicitExecutable), result.ExecutablePath);
        Assert.Equal("13.59", result.Version);
        Assert.Single(result.CandidatePaths);
    }

    [Fact]
    public void Detect_MissingExplicitPathDoesNotFallBackToSearchPath()
    {
        if (!SupportsPosixScripts())
        {
            return;
        }

        using var scripts = new TemporaryScripts();
        var pathExecutable = scripts.CreateExecutable(
            "on-path/exiftool",
            VersionScript("13.59"));
        var missingExecutable = Path.Combine(scripts.RootPath, "missing", "exiftool");

        var result = Assert.IsType<ExifToolNotFoundResult>(
            ExifToolDetector.Detect(
                explicitExecutablePath: missingExecutable,
                searchPath: Path.GetDirectoryName(pathExecutable)));

        Assert.Equal([Path.GetFullPath(missingExecutable)], result.CandidatePaths);
        Assert.Contains("explicit ExifTool path does not exist", result.Message);
    }

    [Fact]
    public void Detect_FindsExecutableInSuppliedPathContainingSpaces()
    {
        if (!SupportsPosixScripts())
        {
            return;
        }

        using var scripts = new TemporaryScripts();
        var executable = scripts.CreateExecutable(
            "directory with spaces/exiftool",
            VersionScript("13.59"));

        var result = Assert.IsType<ExifToolFoundResult>(
            ExifToolDetector.Detect(searchPath: Path.GetDirectoryName(executable)));

        Assert.Equal(Path.GetFullPath(executable), result.ExecutablePath);
        Assert.Equal("13.59", result.Version);
    }

    [Fact]
    public void Detect_IgnoresEmptyAndDuplicatePathEntriesAndPreservesPathOrder()
    {
        if (!SupportsPosixScripts())
        {
            return;
        }

        using var scripts = new TemporaryScripts();
        var firstExecutable = scripts.CreateExecutable(
            "first/exiftool",
            VersionScript("13.01"));
        var secondExecutable = scripts.CreateExecutable(
            "second/exiftool",
            VersionScript("13.02"));
        var firstDirectory = Path.GetDirectoryName(firstExecutable)!;
        var secondDirectory = Path.GetDirectoryName(secondExecutable)!;
        var searchPath = string.Join(
            Path.PathSeparator,
            string.Empty,
            firstDirectory,
            firstDirectory,
            string.Empty,
            secondDirectory,
            string.Empty);

        var first = Assert.IsType<ExifToolFoundResult>(
            ExifToolDetector.Detect(searchPath: searchPath));
        var second = Assert.IsType<ExifToolFoundResult>(
            ExifToolDetector.Detect(searchPath: searchPath));

        Assert.Equal(Path.GetFullPath(firstExecutable), first.ExecutablePath);
        Assert.Equal("13.01", first.Version);
        Assert.Equal(first.CandidatePaths, second.CandidatePaths);
        Assert.Equal(
            [Path.GetFullPath(firstExecutable), Path.GetFullPath(secondExecutable)],
            first.CandidatePaths.Take(2));
        Assert.Equal(
            1,
            first.CandidatePaths.Count(path =>
                StringComparer.Ordinal.Equals(path, Path.GetFullPath(firstExecutable))));
        Assert.DoesNotContain(
            Path.GetFullPath("exiftool"),
            first.CandidatePaths,
            StringComparer.Ordinal);
    }

    [Fact]
    public void Detect_NonzeroExitReturnsUnableToRunWithSeparateOutput()
    {
        if (!SupportsPosixScripts())
        {
            return;
        }

        using var scripts = new TemporaryScripts();
        var executable = scripts.CreateExecutable(
            "exiftool",
            """
            #!/bin/sh
            printf 'version output'
            printf 'failure detail' >&2
            exit 7
            """);

        var result = Assert.IsType<ExifToolUnableToRunResult>(
            ExifToolDetector.Detect(explicitExecutablePath: executable));

        Assert.Equal(7, result.ExitCode);
        Assert.Equal("version output", result.StandardOutput);
        Assert.Equal("failure detail", result.StandardError);
        Assert.Contains("exited with code 7", result.Message);
    }

    [Fact]
    public void Detect_InvalidVersionOutputReturnsTypedResult()
    {
        if (!SupportsPosixScripts())
        {
            return;
        }

        using var scripts = new TemporaryScripts();
        var executable = scripts.CreateExecutable(
            "exiftool",
            VersionScript("ExifTool version 13.59"));

        var result = Assert.IsType<ExifToolInvalidVersionResult>(
            ExifToolDetector.Detect(explicitExecutablePath: executable));

        Assert.Equal("ExifTool version 13.59\n", result.StandardOutput);
        Assert.Empty(result.StandardError);
    }

    [Fact]
    public void Detect_TimeoutTerminatesVersionCheckAndReturnsTypedResult()
    {
        if (!SupportsPosixScripts())
        {
            return;
        }

        using var scripts = new TemporaryScripts();
        var executable = scripts.CreateExecutable(
            "exiftool",
            """
            #!/bin/sh
            printf 'started\n'
            sleep 10
            printf '13.59\n'
            """);
        var timeout = TimeSpan.FromMilliseconds(100);
        var stopwatch = Stopwatch.StartNew();

        var result = Assert.IsType<ExifToolVersionCheckTimedOutResult>(
            ExifToolDetector.Detect(
                explicitExecutablePath: executable,
                versionCheckTimeout: timeout));

        stopwatch.Stop();
        Assert.Equal(timeout, result.Timeout);
        Assert.Null(result.TerminationError);
        Assert.Empty(result.StandardError);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Terminate_ProcessThatAlreadyExitedIsNotReportedAsFailure()
    {
        if (!SupportsPosixScripts())
        {
            return;
        }

        using var scripts = new TemporaryScripts();
        var executable = scripts.CreateExecutable(
            "exit-immediately",
            """
            #!/bin/sh
            exit 0
            """);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false
            }
        };

        Assert.True(process.Start());
        Assert.True(process.WaitForExit(milliseconds: 1_000));

        Assert.Null(ExifToolDetector.Terminate(process));
    }

    private static bool SupportsPosixScripts() =>
        OperatingSystem.IsMacOS() || OperatingSystem.IsLinux();

    private static string VersionScript(string version) =>
        $"""
        #!/bin/sh
        if [ "$#" -ne 1 ] || [ "$1" != "-ver" ]; then
          printf 'unexpected argument' >&2
          exit 9
        fi
        printf '%s\n' '{version}'
        """;

    private sealed class TemporaryScripts : IDisposable
    {
        public TemporaryScripts()
        {
            RootPath = Path.Combine(
                Path.GetTempPath(),
                $"PhotoMigration-ExifTool-{Guid.NewGuid():N}");
            Directory.CreateDirectory(RootPath);
        }

        public string RootPath { get; }

        public string CreateExecutable(string relativePath, string contents)
        {
            var path = Path.Combine(
                RootPath,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(
                path,
                contents,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
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
            Directory.Delete(RootPath, recursive: true);
        }
    }
}
