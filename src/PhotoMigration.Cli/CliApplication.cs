using PhotoMigration.Core;

namespace PhotoMigration.Cli;

public static class CliApplication
{
    private const string Usage =
        "Usage: PhotoMigration.Cli inventory <folder>" +
        "\n       PhotoMigration.Cli analyze <folder>";

    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        if (args.Length != 2)
        {
            error.WriteLine(Usage);
            return 1;
        }

        try
        {
            return args[0] switch
            {
                "inventory" => RunInventory(args[1], output),
                "analyze" => RunAnalysis(args[1], output),
                _ => WriteUsageError(error)
            };
        }
        catch (Exception exception) when (exception is ArgumentException
                                          or DirectoryNotFoundException
                                          or InventoryTraversalException
                                          or PathTooLongException
                                          or NotSupportedException)
        {
            error.WriteLine($"Error: {exception.Message}");
            return 1;
        }
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
}
