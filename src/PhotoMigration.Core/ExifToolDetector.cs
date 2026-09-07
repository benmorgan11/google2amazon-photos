using System.ComponentModel;
using System.Diagnostics;

namespace PhotoMigration.Core;

public static class ExifToolDetector
{
    private const int TerminationWaitMilliseconds = 1_000;

    public static readonly TimeSpan DefaultVersionCheckTimeout = TimeSpan.FromSeconds(3);

    public static ExifToolDetectionResult Detect(
        string? explicitExecutablePath = null,
        string? searchPath = null,
        TimeSpan? versionCheckTimeout = null)
    {
        var timeout = versionCheckTimeout ?? DefaultVersionCheckTimeout;
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(versionCheckTimeout),
                "The version check timeout must be greater than zero.");
        }

        if (explicitExecutablePath is not null)
        {
            return DetectExplicitPath(explicitExecutablePath, timeout);
        }

        var candidates = BuildCandidatePaths(
            searchPath ?? Environment.GetEnvironmentVariable("PATH"));
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return CheckVersion(candidate, candidates, timeout);
            }
        }

        return new ExifToolNotFoundResult(
            candidates,
            "ExifTool was not found in the configured search locations.");
    }

    private static ExifToolDetectionResult DetectExplicitPath(
        string explicitExecutablePath,
        TimeSpan timeout)
    {
        string absolutePath;

        try
        {
            absolutePath = Path.GetFullPath(explicitExecutablePath);
        }
        catch (Exception exception) when (exception is ArgumentException
                                          or NotSupportedException
                                          or PathTooLongException)
        {
            return new ExifToolNotFoundResult(
                Array.Empty<string>(),
                $"The explicit ExifTool path is invalid: {exception.Message}");
        }

        IReadOnlyList<string> candidates = Array.AsReadOnly([absolutePath]);
        if (!File.Exists(absolutePath))
        {
            return new ExifToolNotFoundResult(
                candidates,
                $"The explicit ExifTool path does not exist: '{absolutePath}'.");
        }

        return CheckVersion(absolutePath, candidates, timeout);
    }

    private static IReadOnlyList<string> BuildCandidatePaths(string? searchPath)
    {
        var executableName = OperatingSystem.IsWindows() ? "exiftool.exe" : "exiftool";
        var candidates = new List<string>();
        var uniquePaths = new HashSet<string>(StringComparer.Ordinal);

        if (searchPath is not null)
        {
            foreach (var directory in searchPath.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(directory))
                {
                    continue;
                }

                TryAddCandidate(
                    Path.Combine(directory, executableName),
                    candidates,
                    uniquePaths);
            }
        }

        if (OperatingSystem.IsMacOS())
        {
            TryAddCandidate(
                "/usr/local/bin/exiftool",
                candidates,
                uniquePaths);
        }

        return candidates.AsReadOnly();
    }

    private static void TryAddCandidate(
        string path,
        ICollection<string> candidates,
        ISet<string> uniquePaths)
    {
        string absolutePath;

        try
        {
            absolutePath = Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException
                                          or NotSupportedException
                                          or PathTooLongException)
        {
            return;
        }

        if (uniquePaths.Add(absolutePath))
        {
            candidates.Add(absolutePath);
        }
    }

    private static ExifToolDetectionResult CheckVersion(
        string executablePath,
        IReadOnlyList<string> candidates,
        TimeSpan timeout)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-ver");

        using var process = new Process { StartInfo = startInfo };

        try
        {
            if (!process.Start())
            {
                return UnableToRun(
                    executablePath,
                    candidates,
                    "ExifTool could not be started.");
            }
        }
        catch (Exception exception) when (exception is Win32Exception
                                          or InvalidOperationException
                                          or IOException
                                          or UnauthorizedAccessException
                                          or NotSupportedException)
        {
            return UnableToRun(
                executablePath,
                candidates,
                $"ExifTool could not be started: {exception.Message}");
        }

        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        var timeoutMilliseconds = (int)Math.Min(
            Math.Ceiling(timeout.TotalMilliseconds),
            int.MaxValue);

        if (!process.WaitForExit(timeoutMilliseconds))
        {
            var terminationError = Terminate(process);
            var timeoutOutput = string.Empty;
            var timeoutError = string.Empty;

            if (terminationError is null)
            {
                try
                {
                    var redirectedOutput = Task.WhenAll(standardOutput, standardError);
                    if (redirectedOutput.Wait(TerminationWaitMilliseconds))
                    {
                        var capturedOutput = redirectedOutput.GetAwaiter().GetResult();
                        timeoutOutput = capturedOutput[0];
                        timeoutError = capturedOutput[1];
                    }
                    else
                    {
                        terminationError =
                            "Output streams from the timed-out ExifTool process did not close " +
                            "within one second.";
                    }
                }
                catch (Exception exception) when (exception is AggregateException
                                                  or IOException
                                                  or InvalidOperationException)
                {
                    terminationError =
                        $"Output from the timed-out ExifTool process could not be read: {exception.Message}";
                }
            }

            return new ExifToolVersionCheckTimedOutResult(
                executablePath,
                timeout,
                terminationError,
                timeoutOutput,
                timeoutError,
                candidates);
        }

        string output;
        string error;

        try
        {
            output = standardOutput.GetAwaiter().GetResult();
            error = standardError.GetAwaiter().GetResult();
        }
        catch (Exception exception) when (exception is IOException
                                          or InvalidOperationException)
        {
            return UnableToRun(
                executablePath,
                candidates,
                $"ExifTool output could not be read: {exception.Message}");
        }

        if (process.ExitCode != 0)
        {
            return new ExifToolUnableToRunResult(
                executablePath,
                $"ExifTool version check exited with code {process.ExitCode}.",
                process.ExitCode,
                output,
                error,
                candidates);
        }

        var versionText = output.Trim();
        if (!Version.TryParse(versionText, out _))
        {
            return new ExifToolInvalidVersionResult(
                executablePath,
                output,
                error,
                candidates);
        }

        return new ExifToolFoundResult(
            executablePath,
            versionText,
            candidates);
    }

    internal static string? Terminate(Process process)
    {
        try
        {
            if (process.HasExited)
            {
                return null;
            }

            process.Kill(entireProcessTree: true);

            return process.WaitForExit(TerminationWaitMilliseconds)
                ? null
                : "The timed-out ExifTool process did not exit within one second " +
                  "after termination was requested.";
        }
        catch (InvalidOperationException exception)
        {
            if (HasExited(process))
            {
                return null;
            }

            return $"The timed-out ExifTool process could not be terminated: {exception.Message}";
        }
        catch (Exception exception) when (exception is NotSupportedException or Win32Exception)
        {
            return $"The timed-out ExifTool process could not be terminated: {exception.Message}";
        }
    }

    private static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static ExifToolUnableToRunResult UnableToRun(
        string executablePath,
        IReadOnlyList<string> candidates,
        string message)
    {
        return new ExifToolUnableToRunResult(
            executablePath,
            message,
            null,
            string.Empty,
            string.Empty,
            candidates);
    }
}
