namespace PhotoMigration.Core.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ExternalProcessTestCollection
{
    public const string Name = "External process tests";
}
