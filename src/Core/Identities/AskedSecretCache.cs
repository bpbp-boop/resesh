using System.Collections.Concurrent;

namespace Resesh.Core.Identities;

/// <summary>Passwords typed for "ask every time" identities, held in memory until the app
/// closes and never written anywhere. Shared by every window.</summary>
public sealed class AskedSecretCache
{
    private readonly ConcurrentDictionary<Guid, string> _secrets = new();

    public string? Get(Guid identityId) => _secrets.TryGetValue(identityId, out var secret) ? secret : null;

    public void Set(Guid identityId, string secret) => _secrets[identityId] = secret;

    public void Forget(Guid identityId) => _secrets.TryRemove(identityId, out _);
}
