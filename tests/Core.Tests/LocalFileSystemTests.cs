using Resesh.Core.Local;

namespace Resesh.Core.Tests;

public sealed class LocalFileSystemTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "resesh-local-files-" + Guid.NewGuid().ToString("N"));

    public LocalFileSystemTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void ResolveDirectory_uses_profile_home_and_current_directory()
    {
        var child = Directory.CreateDirectory(Path.Combine(_root, "child")).FullName;
        var files = new LocalFileSystem(_root);

        Assert.Equal(Path.GetFullPath(_root), files.ResolveDirectory(null));
        Assert.Equal(child, files.ResolveDirectory("child", _root));
        Assert.Equal(child, files.ResolveDirectory(child));
        Assert.Equal(child, files.ResolveDirectory("child", "/"));
    }

    [Fact]
    public void ListDirectory_sorts_folders_first_and_returns_file_metadata()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Folder"));
        File.WriteAllText(Path.Combine(_root, "item.txt"), "data");
        var files = new LocalFileSystem(_root);

        var entries = files.ListDirectory(_root);

        Assert.Collection(entries,
            folder =>
            {
                Assert.Equal("Folder", folder.Name);
                Assert.True(folder.IsDirectory);
            },
            file =>
            {
                Assert.Equal("item.txt", file.Name);
                Assert.False(file.IsDirectory);
                Assert.Equal(4, file.Size);
            });
    }

    [Fact]
    public void Rename_create_and_delete_apply_to_local_filesystem()
    {
        var files = new LocalFileSystem(_root);
        var source = Path.Combine(_root, "before.txt");
        var destination = Path.Combine(_root, "after.txt");
        File.WriteAllText(source, "data");
        var entry = Assert.Single(files.ListDirectory(_root));

        files.Rename(entry, destination);
        files.CreateDirectory(Path.Combine(_root, "folder"));

        Assert.False(File.Exists(source));
        Assert.True(File.Exists(destination));
        Assert.True(Directory.Exists(Path.Combine(_root, "folder")));

        files.Delete(files.ListDirectory(_root).Single(item => item.Name == "after.txt"));
        files.Delete(files.ListDirectory(_root).Single(item => item.Name == "folder"));
        Assert.Empty(files.ListDirectory(_root));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
