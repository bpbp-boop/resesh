using Resesh.Core.Credentials;
using Resesh.Core.Storage;

namespace Resesh.Core.Tests;

public sealed class ProtectedFileCredentialServiceTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("protected-credentials-test");
    private string StorePath => Path.Combine(_dir.FullName, ProtectedFileCredentialService.FileName);

    [Fact]
    public void Secrets_survive_a_restart_and_are_not_stored_in_clear()
    {
        var session = Guid.NewGuid();
        var key = Guid.NewGuid();
        ICredentialService first = new ProtectedFileCredentialService(StorePath);
        first.Write(session, "hunter2-session");
        first.WriteKey(key, "hunter2-key");
        first.WriteProxy(session, "hunter2-proxy");

        ICredentialService second = new ProtectedFileCredentialService(StorePath);
        Assert.Equal("hunter2-session", second.Read(session));
        Assert.Equal("hunter2-key", second.ReadKey(key));
        Assert.Equal("hunter2-proxy", second.ReadProxy(session));
        Assert.DoesNotContain("hunter2", File.ReadAllText(StorePath));

        second.Delete(session);
        Assert.Null(new ProtectedFileCredentialService(StorePath).Read(session));
        Assert.Equal("hunter2-proxy", second.ReadProxy(session));
    }

    [Fact]
    public void Too_long_secret_is_refused_before_anything_is_written()
    {
        var service = new ProtectedFileCredentialService(StorePath);
        var error = Assert.Throws<CredentialStoreException>(() =>
            service.Write(Guid.NewGuid(), new string('x', CredentialStoreException.MaxSecretLength + 1)));
        Assert.IsAssignableFrom<IOException>(error);
        Assert.False(File.Exists(StorePath));
        service.Write(Guid.NewGuid(), new string('x', CredentialStoreException.MaxSecretLength));
    }

    [Fact]
    public void Locked_file_is_never_saved_over()
    {
        new ProtectedFileCredentialService(StorePath).Write(Guid.NewGuid(), "secret");
        var original = File.ReadAllText(StorePath);
        var service = new ProtectedFileCredentialService(StorePath);
        using (new FileStream(StorePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Null(service.Read(Guid.NewGuid()));
            Assert.NotNull(service.LoadWarning);
        }
        Assert.Throws<StoreReadOnlyException>(() => service.Write(Guid.NewGuid(), "other"));
        Assert.Equal(original, File.ReadAllText(StorePath));
    }

    [Fact]
    public void Undecryptable_entry_reads_as_no_saved_password()
    {
        var id = Guid.NewGuid();
        File.WriteAllText(StorePath, $"{{\"{id}\":\"{Convert.ToBase64String(new byte[64])}\"}}");
        Assert.Null(new ProtectedFileCredentialService(StorePath).Read(id));
    }

    public void Dispose() => _dir.Delete(recursive: true);
}
