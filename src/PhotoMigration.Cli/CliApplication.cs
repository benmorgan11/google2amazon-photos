using PhotoMigration.Core;

namespace PhotoMigration.Cli;

public static class CliApplication
{
    private const string Usage =
        "Usage: PhotoMigration.Cli inventory <folder>" +
        "\n       PhotoMigration.Cli analyze <folder>" +
        "\n       PhotoMigration.Cli check-exiftool [--path <executable-path>]" +
        "\n       PhotoMigration.Cli plan <takeout-folder> " +
        "[--exiftool <executable-path>]" +
        "\n       PhotoMigration.Cli prepare <takeout-folder> <output-folder> " +
        "[--exiftool <executable-path>]";

    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        if (args.Length == 0)
        {
            error.WriteLine(Usage);
            return 1;
        }

        try
        {
            return args[0] switch
            {
                "inventory" when args.Length == 2 => RunInventory(args[1], output),
                "analyze" when args.Length == 2 => RunAnalysis(args[1], output),
                "check-exiftool" => RunExifToolCheck(args, output, error),
                "plan" => RunPlan(args, output, error),
                "prepare" => RunPrepare(args, output, error),
                _ => WriteUsageError(error)
            };
        }
        catch (Exception exception) when (exception is ArgumentException
                                          or DirectoryNotFoundException
                                          or InventoryTraversalException
                                          or PathTooLongException
                                          or NotSupportedException
                                          or InvalidOperationException)
        {
            error.WriteLine($"Error: {exception.Message}");
            return 1;
        }
    }

    private static int RunExifToolCheck(
        string[] args,
        TextWriter output,
        TextWriter error)
    {
        string? explicitExecutablePath;

        if (args.Length == 1)
        {
            explicitExecutablePath = null;
        }
        else if (args.Length == 3
                 && string.Equals(args[1], "--path", StringComparison.Ordinal)
                 && !string.IsNullOrWhiteSpace(args[2]))
        {
            explicitExecutablePath = args[2];
        }
        else
        {
            return WriteUsageError(error);
        }

        var result = ExifToolDetector.Detect(explicitExecutablePath);
        return result is ExifToolFoundResult found
            ? WriteExifToolFound(found, output)
            : WriteExifToolDetectionError(result, error);
    }

    private static int RunPlan(
        string[] args,
        TextWriter output,
        TextWriter error)
    {
        if (!TryParsePlanArguments(
                args,
                out var takeoutFolder,
                out var explicitExifToolPath))
        {
            return WriteUsageError(error);
        }

        var detectionResult = ExifToolDetector.Detect(explicitExifToolPath);
        if (detectionResult is not ExifToolFoundResult found)
        {
            return WriteExifToolDetectionError(detectionResult, error);
        }

        var planningResult = TakeoutMetadataPlanner.Plan(
            takeoutFolder!,
            found.ExecutablePath);
        WritePlanningSummary(planningResult, output);
        WritePlanningAttentionItems(planningResult.Items, output);

        output.WriteLine();
        output.WriteLine("Planning completed read-only; no files were changed.");

        return planningResult.Items.Any(NeedsPlanningAttention) ? 2 : 0;
    }

    private static int RunPrepare(
        string[] args,
        TextWriter output,
        TextWriter error)
    {
        if (!TryParsePrepareArguments(
                args,
                out var takeoutFolder,
                out var outputFolder,
                out var explicitExifToolPath))
        {
            return WriteUsageError(error);
        }

        if (!TryValidatePreparationRoots(
                takeoutFolder!,
                outputFolder!,
                out var absoluteTakeoutFolder,
                out var absoluteOutputFolder,
                out var rootError))
        {
            return WriteError(error, rootError!);
        }

        var detectionResult = ExifToolDetector.Detect(explicitExifToolPath);
        if (detectionResult is not ExifToolFoundResult found)
        {
            return WriteExifToolDetectionError(detectionResult, error);
        }

        output.WriteLine("Preparing media files. Large libraries may take some time.");
        output.WriteLine();

        var preparationResult = TakeoutPreparationService.Prepare(
            absoluteTakeoutFolder!,
            absoluteOutputFolder!,
            found.ExecutablePath);
        WritePreparationSummary(preparationResult, output);
        WritePreparationAttentionAndFailures(preparationResult.Outcomes, output);

        output.WriteLine();
        output.WriteLine("Preparation completed. The Takeout source was not changed.");
        output.WriteLine($"Output directory: {absoluteOutputFolder}");

        return preparationResult.AttentionRequiredCount > 0
               || preparationResult.FailedCount > 0
            ? 2
            : 0;
    }

    private static bool TryParsePrepareArguments(
        string[] args,
        out string? takeoutFolder,
        out string? outputFolder,
        out string? explicitExifToolPath)
    {
        takeoutFolder = null;
        outputFolder = null;
        explicitExifToolPath = null;

        if (args.Length == 3
            && !string.IsNullOrWhiteSpace(args[1])
            && !string.IsNullOrWhiteSpace(args[2]))
        {
            takeoutFolder = args[1];
            outputFolder = args[2];
            return true;
        }

        if (args.Length == 5
            && !string.IsNullOrWhiteSpace(args[1])
            && !string.IsNullOrWhiteSpace(args[2])
            && string.Equals(args[3], "--exiftool", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(args[4]))
        {
            takeoutFolder = args[1];
            outputFolder = args[2];
            explicitExifToolPath = args[4];
            return true;
        }

        return false;
    }

    private static bool TryValidatePreparationRoots(
        string takeoutFolder,
        string outputFolder,
        out string? absoluteTakeoutFolder,
        out string? absoluteOutputFolder,
        out string? errorMessage)
    {
        absoluteTakeoutFolder = null;
        absoluteOutputFolder = null;
        errorMessage = null;

        try
        {
            absoluteTakeoutFolder = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(takeoutFolder));
            absoluteOutputFolder = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(outputFolder));
        }
        catch (Exception exception) when (exception is ArgumentException
                                          or NotSupportedException
                                          or PathTooLongException)
        {
            errorMessage = $"A preparation folder path is invalid: {exception.Message}";
            return false;
        }

        if (!TryValidateExistingDirectory(
                absoluteTakeoutFolder,
                "Takeout source",
                out errorMessage)
            || !TryValidateExistingDirectory(
                absoluteOutputFolder,
                "Output",
                out errorMessage))
        {
            return false;
        }

        if (IsSameOrDescendant(absoluteTakeoutFolder, absoluteOutputFolder)
            || IsSameOrDescendant(absoluteOutputFolder, absoluteTakeoutFolder))
        {
            errorMessage =
                "The Takeout source and output directories must be separate and must not overlap.";
            return false;
        }

        return true;
    }

    private static bool TryValidateExistingDirectory(
        string path,
        string description,
        out string? errorMessage)
    {
        errorMessage = null;

        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                errorMessage =
                    $"The {description} directory must not be a symbolic link or reparse point: " +
                    $"'{path}'.";
                return false;
            }

            if ((attributes & FileAttributes.Directory) == 0)
            {
                errorMessage = $"The {description} path is not a directory: '{path}'.";
                return false;
            }

            return true;
        }
        catch (Exception exception) when (exception is FileNotFoundException
                                          or DirectoryNotFoundException)
        {
            errorMessage = $"The {description} directory does not exist: '{path}'.";
            return false;
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or NotSupportedException
                                          or System.Security.SecurityException)
        {
            errorMessage =
                $"The {description} directory could not be inspected: {exception.Message}";
            return false;
        }
    }

    private static bool IsSameOrDescendant(string path, string root)
    {
        if (StringComparer.OrdinalIgnoreCase.Equals(path, root))
        {
            return true;
        }

        var rootWithSeparator = Path.EndsInDirectorySeparator(root)
            ? root
            : root + Path.DirectorySeparatorChar;
        return path.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    private static void WritePreparationSummary(
        TakeoutPreparationResult result,
        TextWriter output)
    {
        output.WriteLine("Preparation summary:");
        output.WriteLine($"Total media: {result.TotalMediaCount}");
        output.WriteLine($"Published unchanged: {result.PublishedUnchangedCount}");
        output.WriteLine(
            $"Published with verified JPEG GPS: {result.PublishedJpegGpsCount}");
        output.WriteLine($"Attention required: {result.AttentionRequiredCount}");
        output.WriteLine($"Failed: {result.FailedCount}");
        output.WriteLine(
            $"Unused JSON: {result.AnalysisResult.UnusedJsonCandidates.Count}");
        output.WriteLine($"Other files: {result.AnalysisResult.OtherFiles.Count}");
    }

    private static void WritePreparationAttentionAndFailures(
        IReadOnlyList<TakeoutPreparationItemOutcome> outcomes,
        TextWriter output)
    {
        var unpublishedItems = outcomes
            .Where(outcome => outcome.Kind is TakeoutPreparationOutcomeKind.AttentionRequired
                or TakeoutPreparationOutcomeKind.Failed)
            .OrderBy(
                outcome => outcome.PlanningItem.MediaEntry.RelativePath,
                StringComparer.Ordinal)
            .ToList();
        if (unpublishedItems.Count == 0)
        {
            return;
        }

        output.WriteLine();
        output.WriteLine("Items not published:");
        foreach (var outcome in unpublishedItems)
        {
            output.WriteLine($"  {outcome.PlanningItem.MediaEntry.RelativePath}");
            if (outcome.Kind == TakeoutPreparationOutcomeKind.AttentionRequired)
            {
                output.WriteLine($"    Reason: {outcome.Message}");
            }
            else
            {
                output.WriteLine($"    Failed stage: {outcome.FailureStage}");
                output.WriteLine($"    Error: {outcome.Message}");
            }
        }
    }

    private static bool TryParsePlanArguments(
        string[] args,
        out string? takeoutFolder,
        out string? explicitExifToolPath)
    {
        takeoutFolder = null;
        explicitExifToolPath = null;

        if (args.Length == 2 && !string.IsNullOrWhiteSpace(args[1]))
        {
            takeoutFolder = args[1];
            return true;
        }

        if (args.Length == 4
            && !string.IsNullOrWhiteSpace(args[1])
            && string.Equals(args[2], "--exiftool", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(args[3]))
        {
            takeoutFolder = args[1];
            explicitExifToolPath = args[3];
            return true;
        }

        return false;
    }

    private static void WritePlanningSummary(
        TakeoutMetadataPlanningResult result,
        TextWriter output)
    {
        output.WriteLine("Planning summary:");
        output.WriteLine($"Total media: {result.TotalMediaCount}");
        output.WriteLine($"Matched sidecars: {result.MatchedSidecarCount}");
        output.WriteLine($"Unmatched media: {result.UnmatchedMediaCount}");
        output.WriteLine($"Invalid sidecars: {result.InvalidSidecarCount}");
        output.WriteLine($"Ambiguous sidecars: {result.AmbiguousSidecarCount}");
        output.WriteLine(
            $"No metadata changes proposed: {result.NoChangeMetadataPlanCount}");
        output.WriteLine(
            $"Safe metadata changes proposed: {result.SafeChangeMetadataPlanCount}");
        output.WriteLine(
            $"Review required: {result.ReviewRequiredMetadataPlanCount}");
        output.WriteLine(
            $"Embedded metadata unavailable: {result.EmbeddedMetadataUnavailableCount}");
        output.WriteLine(
            "Embedded formats not supported yet: " +
            result.EmbeddedFormatNotSupportedYetCount);
        output.WriteLine(
            $"Unused JSON candidates: {result.AnalysisResult.UnusedJsonCandidates.Count}");
        output.WriteLine($"Other files: {result.AnalysisResult.OtherFiles.Count}");
    }

    private static void WritePlanningAttentionItems(
        IReadOnlyList<TakeoutMetadataPlanningItem> items,
        TextWriter output)
    {
        var attentionItems = items.Where(NeedsPlanningAttention).ToList();
        if (attentionItems.Count == 0)
        {
            return;
        }

        output.WriteLine();
        output.WriteLine("Items needing attention:");
        foreach (var item in attentionItems)
        {
            output.WriteLine($"  {item.MediaEntry.RelativePath}");
            WriteSidecarAttention(item.SidecarState, output);
            WriteMetadataPlanAttention(item.MetadataPlan, output);
        }
    }

    private static bool NeedsPlanningAttention(TakeoutMetadataPlanningItem item) =>
        item.SidecarState is UnmatchedTakeoutSidecarState
            or InvalidTakeoutSidecarState
            or AmbiguousTakeoutSidecarState
        || item.MetadataPlan.Status is MediaMetadataPlanStatus.ReviewRequired
            or MediaMetadataPlanStatus.EmbeddedMetadataUnavailable
            or MediaMetadataPlanStatus.EmbeddedMetadataFormatNotSupportedYet;

    private static void WriteSidecarAttention(
        TakeoutPlanningSidecarState state,
        TextWriter output)
    {
        switch (state)
        {
            case UnmatchedTakeoutSidecarState:
                output.WriteLine("    Sidecar: no matching sidecar.");
                break;
            case InvalidTakeoutSidecarState invalid:
                output.WriteLine(
                    $"    Sidecar: invalid '{invalid.AnalysisResult.SidecarEntry.RelativePath}' " +
                    $"[{invalid.AnalysisResult.MatchRule}].");
                break;
            case AmbiguousTakeoutSidecarState ambiguous:
                output.WriteLine("    Sidecar: ambiguous candidates.");
                foreach (var candidate in ambiguous.AnalysisResult.Candidates)
                {
                    output.WriteLine(
                        $"      {candidate.SidecarEntry.RelativePath} [{candidate.Rule}]");
                }

                break;
        }
    }

    private static void WriteMetadataPlanAttention(
        MediaMetadataPlan plan,
        TextWriter output)
    {
        switch (plan)
        {
            case SuccessfulMediaMetadataPlan successful
                when successful.Status == MediaMetadataPlanStatus.ReviewRequired:
                foreach (var reason in successful.ReviewReasons)
                {
                    output.WriteLine($"    Metadata: {DescribeReviewReason(reason)}");
                }

                break;
            case UnsupportedEmbeddedMetadataFormatPlan unsupported:
                output.WriteLine(
                    "    Embedded metadata: reader not implemented yet for format " +
                    $"'{DisplayExtension(unsupported.UnsupportedRead.Extension)}'.");
                break;
            case MissingMediaMetadataPlan:
                output.WriteLine("    Embedded metadata: media file was unavailable.");
                break;
            case ExifToolFailureMetadataPlan:
                output.WriteLine("    Embedded metadata: ExifTool could not read the file.");
                break;
            case EmbeddedMetadataTimeoutPlan:
                output.WriteLine("    Embedded metadata: ExifTool read timed out.");
                break;
            case MalformedEmbeddedMetadataJsonPlan:
                output.WriteLine("    Embedded metadata: ExifTool returned malformed JSON.");
                break;
        }
    }

    private static string DescribeReviewReason(MediaMetadataPlanReviewReason reason) =>
        reason switch
        {
            MediaMetadataPlanReviewReason.LowConfidenceCreationTimeFallback =>
                "sidecar creation time is only a low-confidence fallback.",
            MediaMetadataPlanReviewReason.CaptureTimeReviewRequired =>
                "capture-time candidates require review.",
            MediaMetadataPlanReviewReason.CaptureTimeConflict =>
                "embedded and sidecar capture times conflict.",
            MediaMetadataPlanReviewReason.EmbeddedCaptureTimeParsingIssues =>
                "embedded capture-time values could not all be parsed.",
            MediaMetadataPlanReviewReason.LocationReviewRequired =>
                "location candidates require review.",
            MediaMetadataPlanReviewReason.LocationConflict =>
                "embedded and sidecar locations conflict.",
            MediaMetadataPlanReviewReason.EmbeddedGpsParsingIssues =>
                "embedded GPS values could not all be parsed.",
            MediaMetadataPlanReviewReason.EmbeddedLocationBuildingIssues =>
                "embedded GPS values did not form complete locations.",
            MediaMetadataPlanReviewReason.ExifToolDiagnostics =>
                "ExifTool reported diagnostics.",
            _ => "metadata requires review."
        };

    private static string DisplayExtension(string extension) =>
        string.IsNullOrEmpty(extension) ? "(none)" : extension;

    private static int WriteExifToolDetectionError(
        ExifToolDetectionResult result,
        TextWriter error) =>
        result switch
        {
            ExifToolNotFoundResult notFound => WriteError(error, notFound.Message),
            ExifToolUnableToRunResult unableToRun => WriteError(
                error,
                $"{unableToRun.Message} Executable: '{unableToRun.ExecutablePath}'."),
            ExifToolInvalidVersionResult invalidVersion => WriteError(
                error,
                $"ExifTool at '{invalidVersion.ExecutablePath}' returned invalid version output."),
            ExifToolVersionCheckTimedOutResult timedOut => WriteTimeoutError(error, timedOut),
            _ => WriteError(error, "ExifTool detection returned an unsupported result.")
        };

    private static int WriteExifToolFound(ExifToolFoundResult found, TextWriter output)
    {
        output.WriteLine($"ExifTool version: {found.Version}");
        output.WriteLine($"Executable path: {found.ExecutablePath}");
        return 0;
    }

    private static int WriteTimeoutError(
        TextWriter error,
        ExifToolVersionCheckTimedOutResult timedOut)
    {
        var message = $"ExifTool version check timed out for '{timedOut.ExecutablePath}'.";
        if (timedOut.TerminationError is not null)
        {
            message += $" {timedOut.TerminationError}";
        }

        return WriteError(error, message);
    }

    private static int RunInventory(string rootPath, TextWriter output)
    {
        var inventory = FileInventory.Create(rootPath);

        foreach (var entry in inventory.Entries)
        {
            output.WriteLine($"{entry.RelativePath}\t{entry.SizeInBytes}");
        }

        output.WriteLine($"Total files: {inventory.TotalFileCount}");
        return 0;
    }

    private static int RunAnalysis(string rootPath, TextWriter output)
    {
        var analysis = TakeoutAnalyzer.Analyze(rootPath);

        output.WriteLine("Analysis summary:");
        output.WriteLine($"Matched media: {analysis.MatchedMedia.Count}");
        output.WriteLine($"Invalid sidecars: {analysis.InvalidSidecars.Count}");
        output.WriteLine($"Unmatched media: {analysis.UnmatchedMedia.Count}");
        output.WriteLine($"Ambiguous media: {analysis.AmbiguousMedia.Count}");
        output.WriteLine($"Unused JSON candidates: {analysis.UnusedJsonCandidates.Count}");
        output.WriteLine($"Other files: {analysis.OtherFiles.Count}");

        WriteInvalidSidecars(analysis.InvalidSidecars, output);
        WriteUnmatchedMedia(analysis.UnmatchedMedia, output);
        WriteAmbiguousMedia(analysis.AmbiguousMedia, output);

        output.WriteLine();
        output.WriteLine("Analysis completed read-only; no files were changed.");

        return analysis.InvalidSidecars.Count > 0
               || analysis.UnmatchedMedia.Count > 0
               || analysis.AmbiguousMedia.Count > 0
            ? 2
            : 0;
    }

    private static void WriteInvalidSidecars(
        IReadOnlyList<InvalidSidecarResult> invalidSidecars,
        TextWriter output)
    {
        if (invalidSidecars.Count == 0)
        {
            return;
        }

        output.WriteLine();
        output.WriteLine("Invalid sidecars:");
        foreach (var invalid in invalidSidecars)
        {
            output.WriteLine($"  {invalid.MediaEntry.RelativePath}");
            output.WriteLine($"    Sidecar: {invalid.SidecarEntry.RelativePath}");
            output.WriteLine($"    Rule: {invalid.MatchRule}");
            output.WriteLine($"    Error: {invalid.Error.Message}");
        }
    }

    private static void WriteUnmatchedMedia(
        IReadOnlyList<UnmatchedMediaResult> unmatchedMedia,
        TextWriter output)
    {
        if (unmatchedMedia.Count == 0)
        {
            return;
        }

        output.WriteLine();
        output.WriteLine("Unmatched media:");
        foreach (var unmatched in unmatchedMedia)
        {
            output.WriteLine($"  {unmatched.MediaEntry.RelativePath}");
        }
    }

    private static void WriteAmbiguousMedia(
        IReadOnlyList<AmbiguousMediaSidecarResult> ambiguousMedia,
        TextWriter output)
    {
        if (ambiguousMedia.Count == 0)
        {
            return;
        }

        output.WriteLine();
        output.WriteLine("Ambiguous media:");
        foreach (var ambiguous in ambiguousMedia)
        {
            output.WriteLine($"  {ambiguous.MediaEntry.RelativePath}");
            foreach (var candidate in ambiguous.Candidates)
            {
                output.WriteLine(
                    $"    {candidate.SidecarEntry.RelativePath} [{candidate.Rule}]");
            }
        }
    }

    private static int WriteUsageError(TextWriter error)
    {
        error.WriteLine(Usage);
        return 1;
    }

    private static int WriteError(TextWriter error, string message)
    {
        error.WriteLine($"Error: {message}");
        return 1;
    }
}
