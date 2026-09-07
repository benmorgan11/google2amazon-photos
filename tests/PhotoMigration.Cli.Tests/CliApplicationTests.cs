using System.Text;

namespace PhotoMigration.Cli.Tests;

[Collection(ExternalProcessTestCollection.Name)]
public sealed class CliApplicationTests
{
    [Fact]
    public void Analyze_WithValidMatchedMedia_ReturnsSuccessAndSummaryOnly()
    {
        using var takeout = new TemporaryTakeout();
        takeout.Write("photo.jpg", "media");
        takeout.Write("photo.jpg.json", "{ \"title\": \"Photo\" }");

        var execution = Run("analyze", takeout.RootPath);

        Assert.Equal(0, execution.ExitCode);
        Assert.Contains("Matched media: 1", execution.Output);
        Assert.Contains("Invalid sidecars: 0", execution.Output);
        Assert.Contains("Unmatched media: 0", execution.Output);
        Assert.Contains("Ambiguous media: 0", execution.Output);
        Assert.Contains("Unused JSON candidates: 0", execution.Output);
        Assert.Contains("Other files: 0", execution.Output);
        Assert.Contains("Analysis completed read-only; no files were changed.", execution.Output);
        Assert.DoesNotContain("photo.jpg", execution.Output);
        Assert.Empty(execution.Error);
    }

    [Fact]
    public void Analyze_WithUnmatchedMedia_ReturnsAttentionAndPrintsOrdinalPaths()
    {
        using var takeout = new TemporaryTakeout();
        takeout.Write("z-photo.jpg", "media");
        takeout.Write("A-photo.jpg", "media");

        var execution = Run("analyze", takeout.RootPath);

        Assert.Equal(2, execution.ExitCode);
        Assert.Contains("Unmatched media: 2", execution.Output);
        Assert.True(
            execution.Output.IndexOf("A-photo.jpg", StringComparison.Ordinal)
            < execution.Output.IndexOf("z-photo.jpg", StringComparison.Ordinal));
        Assert.Empty(execution.Error);
    }

    [Fact]
    public void Analyze_WithAmbiguousMedia_PrintsCandidatePathsAndRules()
    {
        using var takeout = new TemporaryTakeout();
        takeout.Write("photo.jpg", "media");
        takeout.Write("photo.jpg.json", "{}");
        takeout.Write("photo.supplemental-metadata.json", "{}");

        var execution = Run("analyze", takeout.RootPath);

        Assert.Equal(2, execution.ExitCode);
        Assert.Contains("Ambiguous media: 1", execution.Output);
        Assert.Contains("photo.jpg.json [LegacyJson]", execution.Output);
        Assert.Contains(
            "photo.supplemental-metadata.json [ExtensionOmitted]",
            execution.Output);
        Assert.Empty(execution.Error);
    }

    [Fact]
    public void Analyze_WithInvalidMatchedSidecar_ReturnsAttentionWithoutJsonContents()
    {
        const string sidecarContents = "PRIVATE malformed sidecar contents";
        using var takeout = new TemporaryTakeout();
        takeout.Write("photo.jpg", "media");
        takeout.Write("photo.jpg.json", sidecarContents);

        var execution = Run("analyze", takeout.RootPath);

        Assert.Equal(2, execution.ExitCode);
        Assert.Contains("Invalid sidecars: 1", execution.Output);
        Assert.Contains("photo.jpg", execution.Output);
        Assert.Contains("Sidecar: photo.jpg.json", execution.Output);
        Assert.Contains("Rule: LegacyJson", execution.Output);
        Assert.DoesNotContain(sidecarContents, execution.Output);
        Assert.DoesNotContain(sidecarContents, execution.Error);
        Assert.Empty(execution.Error);
    }

    [Fact]
    public void Analyze_WithOnlyUnusedJsonAndOtherFiles_ReturnsSuccessAndCountsBoth()
    {
        using var takeout = new TemporaryTakeout();
        takeout.Write("album.json", "not parsed");
        takeout.Write("notes.txt", "other");

        var execution = Run("analyze", takeout.RootPath);

        Assert.Equal(0, execution.ExitCode);
        Assert.Contains("Unused JSON candidates: 1", execution.Output);
        Assert.Contains("Other files: 1", execution.Output);
        Assert.DoesNotContain("album.json", execution.Output);
        Assert.DoesNotContain("notes.txt", execution.Output);
        Assert.Empty(execution.Error);
    }

    [Fact]
    public void Run_WithInvalidArgumentsOrMissingFolder_ReturnsUsageOrOperationalError()
    {
        using var takeout = new TemporaryTakeout();

        var invalidArguments = Run("analyze");
        var missingFolder = Run("analyze", Path.Combine(takeout.RootPath, "missing"));

        Assert.Equal(1, invalidArguments.ExitCode);
        Assert.Contains("Usage: PhotoMigration.Cli inventory <folder>", invalidArguments.Error);
        Assert.Contains("PhotoMigration.Cli analyze <folder>", invalidArguments.Error);
        Assert.Equal(1, missingFolder.ExitCode);
        Assert.Contains("Error: Input folder does not exist:", missingFolder.Error);
        Assert.Empty(missingFolder.Output);
    }

    [Fact]
    public void Inventory_StillPrintsEntriesAndTotalCount()
    {
        using var takeout = new TemporaryTakeout();
        var filePath = takeout.Write("nested/photo.jpg", "media");
        var expectedLength = new FileInfo(filePath).Length;

        var execution = Run("inventory", takeout.RootPath);

        Assert.Equal(0, execution.ExitCode);
        Assert.Equal(
            $"nested/photo.jpg\t{expectedLength}{Environment.NewLine}Total files: 1{Environment.NewLine}",
            execution.Output);
        Assert.Empty(execution.Error);
    }

    [Fact]
    public void CheckExifTool_WithValidExplicitExecutable_PrintsVersionAndPath()
    {
        if (!SupportsPosixScripts())
        {
            return;
        }

        using var executable = new TemporaryExecutable("13.59");

        var execution = Run("check-exiftool", "--path", executable.Path);

        Assert.Equal(0, execution.ExitCode);
        Assert.Equal(
            $"ExifTool version: 13.59{Environment.NewLine}" +
            $"Executable path: {Path.GetFullPath(executable.Path)}{Environment.NewLine}",
            execution.Output);
        Assert.Empty(execution.Error);
    }

    [Fact]
    public void CheckExifTool_WithMissingExplicitExecutable_PrintsUsefulError()
    {
        using var directory = new TemporaryDirectory();
        var missingPath = Path.Combine(directory.Path, "missing-exiftool");

        var execution = Run("check-exiftool", "--path", missingPath);

        Assert.Equal(1, execution.ExitCode);
        Assert.Empty(execution.Output);
        Assert.Contains("Error:", execution.Error);
        Assert.Contains("explicit ExifTool path does not exist", execution.Error);
        Assert.Contains(Path.GetFullPath(missingPath), execution.Error);
    }

    [Fact]
    public void CheckExifTool_WithInvalidVersion_PrintsUsefulError()
    {
        if (!SupportsPosixScripts())
        {
            return;
        }

        using var executable = new TemporaryExecutable("not a version");

        var execution = Run("check-exiftool", "--path", executable.Path);

        Assert.Equal(1, execution.ExitCode);
        Assert.Empty(execution.Output);
        Assert.Contains("returned invalid version output", execution.Error);
        Assert.Contains(Path.GetFullPath(executable.Path), execution.Error);
        Assert.DoesNotContain("not a version", execution.Error);
    }

    [Fact]
    public void CheckExifTool_WithInvalidArguments_PrintsUsage()
    {
        var missingPathValue = Run("check-exiftool", "--path");
        var unknownOption = Run("check-exiftool", "--other", "value");

        Assert.Equal(1, missingPathValue.ExitCode);
        Assert.Empty(missingPathValue.Output);
        Assert.Contains(
            "PhotoMigration.Cli check-exiftool [--path <executable-path>]",
            missingPathValue.Error);
        Assert.Equal(1, unknownOption.ExitCode);
        Assert.Empty(unknownOption.Output);
        Assert.Contains(
            "PhotoMigration.Cli check-exiftool [--path <executable-path>]",
            unknownOption.Error);
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

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"PhotoMigration-Cli-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            Directory.Delete(Path, recursive: true);
        }
    }

    private sealed class TemporaryExecutable : IDisposable
    {
        private readonly TemporaryDirectory _directory = new();

        public TemporaryExecutable(string versionOutput)
        {
            Path = System.IO.Path.Combine(_directory.Path, "exiftool");
            File.WriteAllText(
                Path,
                $"#!/bin/sh\nprintf '%s\\n' '{versionOutput}'\n",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
            {
                File.SetUnixFileMode(
                    Path,
                    UnixFileMode.UserRead
                    | UnixFileMode.UserWrite
                    | UnixFileMode.UserExecute);
            }
        }

        public string Path { get; }

        public void Dispose()
        {
            _directory.Dispose();
        }
    }

    private sealed class TemporaryTakeout : IDisposable
    {
        public TemporaryTakeout()
        {
            RootPath = Path.Combine(Path.GetTempPath(), $"PhotoMigration-Cli-{Guid.NewGuid():N}");
            Directory.CreateDirectory(RootPath);
        }

        public string RootPath { get; }

        public string Write(string relativePath, string contents)
        {
            var path = Path.Combine(RootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, contents, Encoding.UTF8);
            return path;
        }

        public void Dispose()
        {
            Directory.Delete(RootPath, recursive: true);
        }
    }
}
