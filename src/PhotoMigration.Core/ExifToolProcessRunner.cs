using System.ComponentModel;
using System.Diagnostics;

namespace PhotoMigration.Core;

internal enum ExifToolProcessFailureKind
{
    StartFailure,
    OutputCaptureFailure,
    NonzeroExit,
    TimedOut
}

internal sealed record ExifToolProcessOutcome(
    bool Succeeded,
    ExifToolProcessFailureKind? FailureKind = null,
    string Message = "",
    int? ExitCode = null,
    TimeSpan? Timeout = null,
    string? TerminationError = null,
    string StandardOutput = "",
    string StandardError = "");

internal static class ExifToolProcessRunner
{
    private const int CleanupWaitMilliseconds = 1_000;

    internal static ExifToolProcessOutcome Run(
        string exifToolPath,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        string operationDescription)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = exifToolPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return Failure(
                    ExifToolProcessFailureKind.StartFailure,
                    "ExifTool could not be started.");
            }
        }
        catch (Exception exception) when (exception is Win32Exception
                                          or InvalidOperationException
                                          or IOException
                                          or UnauthorizedAccessException
                                          or NotSupportedException)
        {
            return Failure(
                ExifToolProcessFailureKind.StartFailure,
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
            TryCaptureOutput(
                standardOutput,
                standardError,
                out var output,
                out var error,
                ref terminationError);
            return new ExifToolProcessOutcome(
                false,
                ExifToolProcessFailureKind.TimedOut,
                $"ExifTool {operationDescription} timed out after {timeout}.",
                Timeout: timeout,
                TerminationError: terminationError,
                StandardOutput: output,
                StandardError: error);
        }

        string? captureError = null;
        if (!TryCaptureOutput(
                standardOutput,
                standardError,
                out var capturedOutput,
                out var capturedError,
                ref captureError))
        {
            return new ExifToolProcessOutcome(
                false,
                ExifToolProcessFailureKind.OutputCaptureFailure,
                captureError!,
                process.ExitCode,
                StandardOutput: capturedOutput,
                StandardError: capturedError);
        }

        return process.ExitCode == 0
            ? new ExifToolProcessOutcome(
                true,
                StandardOutput: capturedOutput,
                StandardError: capturedError)
            : new ExifToolProcessOutcome(
                false,
                ExifToolProcessFailureKind.NonzeroExit,
                $"ExifTool {operationDescription} exited with code {process.ExitCode}.",
                process.ExitCode,
                StandardOutput: capturedOutput,
                StandardError: capturedError);
    }

    private static bool TryCaptureOutput(
        Task<string> standardOutput,
        Task<string> standardError,
        out string output,
        out string error,
        ref string? captureError)
    {
        output = string.Empty;
        error = string.Empty;
        if (captureError is not null)
        {
            return false;
        }

        try
        {
            var redirectedOutput = Task.WhenAll(standardOutput, standardError);
            if (!redirectedOutput.Wait(CleanupWaitMilliseconds))
            {
                captureError = "ExifTool output streams did not close within one second.";
                return false;
            }

            var captured = redirectedOutput.GetAwaiter().GetResult();
            output = captured[0];
            error = captured[1];
            return true;
        }
        catch (Exception exception) when (exception is AggregateException
                                          or IOException
                                          or InvalidOperationException)
        {
            captureError = $"ExifTool output could not be read: {exception.Message}";
            return false;
        }
    }

    private static string? Terminate(Process process)
    {
        try
        {
            if (process.HasExited)
            {
                return null;
            }

            process.Kill(entireProcessTree: true);
            return process.WaitForExit(CleanupWaitMilliseconds)
                ? null
                : "The timed-out ExifTool process did not exit within one second " +
                  "after termination was requested.";
        }
        catch (InvalidOperationException exception)
        {
            return HasExited(process)
                ? null
                : $"The timed-out ExifTool process could not be terminated: " +
                  exception.Message;
        }
        catch (Exception exception) when (exception is NotSupportedException
                                          or Win32Exception)
        {
            return $"The timed-out ExifTool process could not be terminated: " +
                   exception.Message;
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

    private static ExifToolProcessOutcome Failure(
        ExifToolProcessFailureKind kind,
        string message) =>
        new(false, kind, message);
}
