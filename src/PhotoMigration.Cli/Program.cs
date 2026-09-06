using PhotoMigration.Core;

if (args.Length != 2 || !string.Equals(args[0], "inventory", StringComparison.Ordinal))
{
    Console.Error.WriteLine("Usage: PhotoMigration.Cli inventory <folder>");
    return 1;
}

try
{
    var inventory = FileInventory.Create(args[1]);

    foreach (var entry in inventory.Entries)
    {
        Console.WriteLine($"{entry.RelativePath}\t{entry.SizeInBytes}");
    }

    Console.WriteLine($"Total files: {inventory.TotalFileCount}");
    return 0;
}
catch (Exception exception) when (exception is ArgumentException
                                  or DirectoryNotFoundException
                                  or InventoryTraversalException
                                  or PathTooLongException
                                  or NotSupportedException)
{
    Console.Error.WriteLine($"Error: {exception.Message}");
    return 1;
}
