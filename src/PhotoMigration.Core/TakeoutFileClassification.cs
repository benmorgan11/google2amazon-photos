namespace PhotoMigration.Core;

public sealed record TakeoutFileClassification(
    InventoryEntry Entry,
    TakeoutFileCategory Category);
