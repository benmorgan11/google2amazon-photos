using PhotoMigration.Core;

namespace PhotoMigration.Core.Tests;

public sealed class FileInventoryTests
{
    [Fact]
    public void Create_ReturnsNestedFilesWithByteLengthsInOrdinalOrder()
    {
        using var fixture = new TemporaryDirectory();
        fixture.WriteBytes("z-last.bin", [1, 2, 3, 4]);
        fixture.WriteBytes(Path.Combine("nested", "two.txt"), [10, 20]);
        fixture.WriteBytes("A-first.txt", []);
        fixture.WriteBytes("a-second.txt", [42]);

        var result = FileInventory.Create(fixture.Path);

        Assert.Equal(4, result.TotalFileCount);
        Assert.Collection(
            result.Entries,
            entry => Assert.Equal(new InventoryEntry("A-first.txt", 0), entry),
            entry => Assert.Equal(new InventoryEntry("a-second.txt", 1), entry),
            entry => Assert.Equal(new InventoryEntry("nested/two.txt", 2), entry),
            entry => Assert.Equal(new InventoryEntry("z-last.bin", 4), entry));
    }

    [Fact]
    public void Create_ReturnsAnEmptyInventoryForAnEmptyFolder()
    {
        using var fixture = new TemporaryDirectory();

        var result = FileInventory.Create(fixture.Path);

        Assert.Empty(result.Entries);
        Assert.Equal(0, result.TotalFileCount);
    }

    [Fact]
    public void Create_RejectsInvalidInputPaths()
    {
        using var fixture = new TemporaryDirectory();
        var filePath = fixture.WriteBytes("not-a-folder.txt", [1]);
        var missingPath = System.IO.Path.Combine(fixture.Path, "missing");

        Assert.Throws<ArgumentNullException>(() => FileInventory.Create(null!));
        Assert.Throws<ArgumentException>(() => FileInventory.Create(" "));
        Assert.Throws<DirectoryNotFoundException>(() => FileInventory.Create(missingPath));
        Assert.Throws<ArgumentException>(() => FileInventory.Create(filePath));
    }

    [Fact]
    public void Create_DoesNotChangeFixtureFiles()
    {
        using var fixture = new TemporaryDirectory();
        fixture.WriteBytes("one.bin", [0, 1, 2, 3]);
        fixture.WriteBytes(Path.Combine("nested", "two.bin"), [255, 128, 64]);
        var before = fixture.ReadAllFiles();

        _ = FileInventory.Create(fixture.Path);

        var after = fixture.ReadAllFiles();
        Assert.Equal(before.Keys.Order(StringComparer.Ordinal), after.Keys.Order(StringComparer.Ordinal));
        foreach (var path in before.Keys)
        {
            Assert.Equal(before[path], after[path]);
        }
    }

    [Fact]
    public void Create_SkipsLinkedEntriesAndRejectsALinkedRoot_WhenLinksAreSupported()
    {
        using var fixture = new TemporaryDirectory();
        using var outside = new TemporaryDirectory();
        outside.WriteBytes("outside.txt", [1, 2, 3]);
        fixture.WriteBytes("included.txt", [9]);

        var linkedFile = System.IO.Path.Combine(fixture.Path, "linked-file.txt");
        var linkedDirectory = System.IO.Path.Combine(fixture.Path, "linked-directory");
        var linkedRoot = System.IO.Path.Combine(outside.Path, "linked-root");

        try
        {
            File.CreateSymbolicLink(linkedFile, System.IO.Path.Combine(outside.Path, "outside.txt"));
            Directory.CreateSymbolicLink(linkedDirectory, outside.Path);
            Directory.CreateSymbolicLink(linkedRoot, fixture.Path);
        }
        catch (Exception linkCreationException) when (linkCreationException is UnauthorizedAccessException
                                                      or IOException
                                                      or PlatformNotSupportedException)
        {
            return;
        }

        var result = FileInventory.Create(fixture.Path);

        Assert.Equal([new InventoryEntry("included.txt", 1)], result.Entries);
        var exception = Assert.Throws<ArgumentException>(() => FileInventory.Create(linkedRoot));
        Assert.Contains("symbolic link or reparse point", exception.Message);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"PhotoMigration.Tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string WriteBytes(string relativePath, byte[] contents)
        {
            var fullPath = System.IO.Path.Combine(Path, relativePath);
            var parent = System.IO.Path.GetDirectoryName(fullPath)!;
            Directory.CreateDirectory(parent);
            File.WriteAllBytes(fullPath, contents);
            return fullPath;
        }

        public Dictionary<string, byte[]> ReadAllFiles()
        {
            return Directory
                .EnumerateFiles(Path, "*", SearchOption.AllDirectories)
                .ToDictionary(
                    file => System.IO.Path.GetRelativePath(Path, file),
                    File.ReadAllBytes,
                    StringComparer.Ordinal);
        }

        public void Dispose()
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
