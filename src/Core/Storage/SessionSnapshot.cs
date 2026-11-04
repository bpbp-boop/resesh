using Resesh.Core.Models;

namespace Resesh.Core.Storage;

// Records have mutable members for generated JSON metadata. Never share those members
// across the store boundary: even a read-only list can contain mutable records.
internal static class SessionSnapshot
{
    public static Session Copy(Session session) => session with
    {
        Local = session.Local is { } local ? local with
        {
            Arguments = local.Arguments.ToArray(),
            Environment = local.Environment?.ToDictionary(pair => pair.Key, pair => pair.Value),
        } : null,
        Serial = session.Serial is { } serial ? serial with { } : null,
        Proxy = session.Proxy is { } proxy ? proxy with { } : null,
        Tunnels = session.Tunnels?.Select(tunnel => tunnel with { }).ToArray(),
        Algorithms = session.Algorithms is { } algorithms ? algorithms with
        {
            DisabledKeyExchange = algorithms.DisabledKeyExchange?.ToArray(),
            DisabledCiphers = algorithms.DisabledCiphers?.ToArray(),
            DisabledMacs = algorithms.DisabledMacs?.ToArray(),
            DisabledHostKeys = algorithms.DisabledHostKeys?.ToArray(),
        } : null,
        Overrides = session.Overrides is { } overrides ? overrides with
        {
            EnabledRules = overrides.EnabledRules?.ToArray(),
            DisabledRules = overrides.DisabledRules?.ToArray(),
        } : null,
    };
}
