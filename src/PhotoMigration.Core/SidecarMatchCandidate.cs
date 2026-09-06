namespace PhotoMigration.Core;

public sealed record SidecarMatchCandidate(
    InventoryEntry SidecarEntry,
    ExactSidecarMatchRule Rule);
