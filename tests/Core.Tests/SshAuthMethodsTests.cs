using System.Security.Cryptography;
using Renci.SshNet;
using Session = Resesh.Core.Models.Session;
using AuthMethod = Resesh.Core.Models.AuthMethod;
using Resesh.Core.Ssh;

namespace Resesh.Core.Tests;

public sealed class SshAuthMethodsTests : IDisposable
{
    private readonly string _keyPath = Path.Combine(Path.GetTempPath(), $"resesh-auth-{Guid.NewGuid():N}.pem");

    public SshAuthMethodsTests()
    {
        using var rsa = RSA.Create(2048);
        File.WriteAllText(_keyPath, rsa.ExportRSAPrivateKeyPem());
    }

    private Session KeySession => new()
    {
        Host = "h",
        Username = "root",
        AuthMethod = AuthMethod.PrivateKey,
        PrivateKeyPath = _keyPath,
    };

    [Fact]
    public void KeyLogin_FallsBackToKeyboardInteractiveWhenSomeoneCanAnswer()
    {
        var methods = SshConnectionFactory.BuildAuthMethods(KeySession, null, _ => ["typed"]);

        Assert.Collection(methods,
            method => Assert.IsType<PrivateKeyAuthenticationMethod>(method),
            method => Assert.IsType<KeyboardInteractiveAuthenticationMethod>(method));
    }

    [Fact]
    public void KeyLogin_WithoutAResponder_OffersOnlyTheKey()
    {
        var method = Assert.Single(SshConnectionFactory.BuildAuthMethods(KeySession, null));
        Assert.IsType<PrivateKeyAuthenticationMethod>(method);
    }

    public void Dispose() => File.Delete(_keyPath);
}
