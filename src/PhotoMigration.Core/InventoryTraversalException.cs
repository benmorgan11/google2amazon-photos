namespace PhotoMigration.Core;

public sealed class InventoryTraversalException : IOException
{
    public InventoryTraversalException(string path, Exception innerException)
        : base($"Could not complete the inventory while reading '{path}'.", innerException)
    {
        Path = path;
    }

    public string Path { get; }
}
