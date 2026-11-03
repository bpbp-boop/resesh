using Resesh.Core.Storage;

namespace Resesh.Core.Tests;

/// <summary>Imports, the key list and the identity planner read key paths one way, so the
/// same file written two ways is recognised as one key.</summary>
public sealed class KeyFilePathTests
{
    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    [Theory]
    [InlineData("~/.ssh/id_ed25519")]
    [InlineData(@"~\.ssh\id_ed25519")]
    [InlineData(@" "".ssh\id_ed25519"" ")]
    [InlineData("%USERPROFILE%/.ssh/id_ed25519")]
    public void TildeAndRelativePathsResolveFromHome(string path) =>
        Assert.Equal(Path.Combine(Home, ".ssh", "id_ed25519"), KeyFilePath.Normalize(path));

    [Fact]
    public void ABaseFolderReplacesHomeForRelativePaths() =>
        Assert.Equal(@"C:\config\keys\id", KeyFilePath.Normalize(@"keys\id", @"C:\config"));
}
