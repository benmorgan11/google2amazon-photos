namespace PhotoMigration.Core;

public sealed class InventoryResult
{
    public InventoryResult(IReadOnlyList<InventoryEntry> entries)
    {
        Entries = entries;
    }

    public IReadOnlyList<InventoryEntry> Entries { get; }

    public int TotalFileCount => Entries.Count;
}
