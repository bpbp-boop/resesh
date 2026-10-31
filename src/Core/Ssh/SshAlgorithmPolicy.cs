using Renci.SshNet;
using Resesh.Core.Models;
using Session = Resesh.Core.Models.Session;

namespace Resesh.Core.Ssh;

public enum SshAlgorithmKind
{
    KeyExchange,
    Cipher,
    Mac,
    HostKey,
}

/// <summary>What an OpenSSH algorithm list resolves to.</summary>
/// <param name="Algorithms">The algorithms to offer, most preferred first.</param>
/// <param name="Unsupported">Names in the list that SSH.NET does not implement.</param>
public sealed record SshAlgorithmList(IReadOnlyList<string> Algorithms, IReadOnlyList<string> Unsupported)
{
    /// <summary>Set when the list leaves nothing to offer.</summary>
    public string? Error { get; init; }
}

/// <summary>
/// Decides which SSH algorithms a connection offers: SSH.NET's own list, in its order, less
/// what the session turns off, or for a kind the session does not set, what the app settings
/// turn off (<see cref="AppDefaults"/>). Also reads OpenSSH's KexAlgorithms-style lists, so
/// <c>~/.ssh/config</c> imports as the same on/off choices.
/// </summary>
public static class SshAlgorithmPolicy
{
    public static readonly IReadOnlyList<SshAlgorithmKind> Kinds =
        [SshAlgorithmKind.KeyExchange, SshAlgorithmKind.Cipher, SshAlgorithmKind.Mac, SshAlgorithmKind.HostKey];

    private static readonly Lazy<ConnectionInfo> Defaults = new(() =>
        new ConnectionInfo("localhost", "user", new NoneAuthenticationMethod("user")));

    private static volatile SshAlgorithms? _appDefaults;

    /// <summary>The app settings' choices, which sessions follow for each kind they leave
    /// unset. The app sets this at startup and whenever settings are saved.</summary>
    public static SshAlgorithms? AppDefaults
    {
        get => _appDefaults;
        set => _appDefaults = value;
    }

    /// <summary>What SSH.NET offers by default, in its order of preference. SSH.NET
    /// implements nothing beyond these, so this is every algorithm that can be turned on.</summary>
    public static IReadOnlyList<string> Supported(SshAlgorithmKind kind) => kind switch
    {
        SshAlgorithmKind.KeyExchange => [.. Defaults.Value.KeyExchangeAlgorithms.Keys],
        SshAlgorithmKind.Cipher => [.. Defaults.Value.Encryptions.Keys],
        SshAlgorithmKind.Mac => [.. Defaults.Value.HmacAlgorithms.Keys],
        _ => [.. Defaults.Value.HostKeyAlgorithms.Keys],
    };

    public static string Title(SshAlgorithmKind kind) => kind switch
    {
        SshAlgorithmKind.KeyExchange => "Key exchange",
        SshAlgorithmKind.Cipher => "Ciphers",
        SshAlgorithmKind.Mac => "MACs",
        _ => "Host key types",
    };

    /// <summary>Kept for compatibility with old servers and weaker than the rest: SHA-1
    /// key exchange, MACs and RSA signatures, CBC-mode and 3DES ciphers.</summary>
    public static bool IsLegacy(string name) =>
        name is "diffie-hellman-group1-sha1" or "diffie-hellman-group14-sha1" or "diffie-hellman-group-exchange-sha1"
            or "3des-cbc" or "hmac-sha1" or "hmac-sha1-etm@openssh.com" or "ssh-rsa" or "ssh-rsa-cert-v01@openssh.com"
        || name.EndsWith("-cbc", StringComparison.Ordinal);

    public static IReadOnlyList<string>? Disabled(SshAlgorithms? algorithms, SshAlgorithmKind kind) => kind switch
    {
        SshAlgorithmKind.KeyExchange => algorithms?.DisabledKeyExchange,
        SshAlgorithmKind.Cipher => algorithms?.DisabledCiphers,
        SshAlgorithmKind.Mac => algorithms?.DisabledMacs,
        _ => algorithms?.DisabledHostKeys,
    };

    /// <summary><paramref name="algorithms"/> with one kind's list replaced (null clears it).
    /// The list is kept to known names in SSH.NET's order. Null when nothing is left set.</summary>
    public static SshAlgorithms? With(SshAlgorithms? algorithms, SshAlgorithmKind kind, IEnumerable<string>? disabled)
    {
        IReadOnlyList<string>? list = disabled is null
            ? null
            : Supported(kind).Where(name => disabled.Contains(name, StringComparer.Ordinal)).ToList();
        var current = algorithms ?? new SshAlgorithms();
        var result = kind switch
        {
            SshAlgorithmKind.KeyExchange => current with { DisabledKeyExchange = list },
            SshAlgorithmKind.Cipher => current with { DisabledCiphers = list },
            SshAlgorithmKind.Mac => current with { DisabledMacs = list },
            _ => current with { DisabledHostKeys = list },
        };
        return result.IsEmpty ? null : result;
    }

    /// <summary>What a connection offers for <paramref name="kind"/>, in SSH.NET's order.</summary>
    public static IReadOnlyList<string> Offered(SshAlgorithmKind kind, SshAlgorithms? session, SshAlgorithms? app)
    {
        var off = Disabled(session, kind) ?? Disabled(app, kind) ?? [];
        return Supported(kind).Where(name => !off.Contains(name, StringComparer.Ordinal)).ToList();
    }

    /// <summary>Limits <paramref name="info"/> to what the session and the app settings leave
    /// on. Throws when a kind has nothing left, which would otherwise fail later and vaguer.</summary>
    internal static void Apply(ConnectionInfo info, Session session)
    {
        var app = AppDefaults;
        if (session.Algorithms is null && app is null)
            return;
        Restrict(info.KeyExchangeAlgorithms, Checked(SshAlgorithmKind.KeyExchange, session, app));
        Restrict(info.Encryptions, Checked(SshAlgorithmKind.Cipher, session, app));
        Restrict(info.HmacAlgorithms, Checked(SshAlgorithmKind.Mac, session, app));
        Restrict(info.HostKeyAlgorithms, Checked(SshAlgorithmKind.HostKey, session, app));
    }

    private static IReadOnlyList<string> Checked(SshAlgorithmKind kind, Session session, SshAlgorithms? app)
    {
        var offered = Offered(kind, session.Algorithms, app);
        if (offered.Count > 0)
            return offered;
        var where = Disabled(session.Algorithms, kind) is not null
            ? "the session's Advanced settings"
            : "Settings › Identities & Keys › SSH algorithms";
        throw new SshSessionException(SshFailureKind.Other,
            $"Every {Title(kind).ToLowerInvariant()} algorithm is turned off for {session.Name}. Turn one on in {where}.");
    }

    private static void Restrict<T>(IDictionary<string, T> dictionary, IReadOnlyList<string> offered)
    {
        foreach (var name in dictionary.Keys.ToList())
            if (!offered.Contains(name, StringComparer.Ordinal))
                dictionary.Remove(name);
    }

    // ---- OpenSSH lists (import) ----

    /// <summary>The on/off choices an OpenSSH config's algorithm options amount to; null when
    /// it sets none. Order is not kept: a connection offers SSH.NET's order.</summary>
    public static SshAlgorithms? FromOpenSsh(string? keyExchange, string? ciphers, string? macs, string? hostKeys)
    {
        SshAlgorithms? result = null;
        foreach (var (kind, spec) in new[]
        {
            (SshAlgorithmKind.KeyExchange, keyExchange),
            (SshAlgorithmKind.Cipher, ciphers),
            (SshAlgorithmKind.Mac, macs),
            (SshAlgorithmKind.HostKey, hostKeys),
        })
        {
            if (string.IsNullOrWhiteSpace(spec))
                continue;
            var list = Resolve(kind, spec);
            // A list naming nothing SSH.NET has would turn everything off; keep the defaults.
            if (list.Error is not null)
                continue;
            result = With(result, kind, Supported(kind).Except(list.Algorithms, StringComparer.Ordinal));
        }
        return result;
    }

    public static SshAlgorithmList Resolve(SshAlgorithmKind kind, string? spec) => Resolve(Supported(kind), spec);

    /// <summary>Resolves an OpenSSH list against <paramref name="defaults"/>: a plain list
    /// replaces them in its own order; <c>+list</c> appends; <c>-list</c> removes;
    /// <c>^list</c> moves to the front. <c>*</c> and <c>?</c> are wildcards.</summary>
    public static SshAlgorithmList Resolve(IReadOnlyList<string> defaults, string? spec)
    {
        var text = spec?.Trim() ?? "";
        if (text.Length == 0)
            return new SshAlgorithmList(defaults, []);

        var mode = text[0] is '+' or '-' or '^' ? text[0] : '\0';
        var names = (mode == '\0' ? text : text[1..])
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var unsupported = new List<string>();

        var listed = new List<string>();
        foreach (var name in names)
        {
            var matches = IsPattern(name)
                ? defaults.Where(candidate => Matches(candidate, name)).ToList()
                : defaults.Where(candidate => candidate.Equals(name, StringComparison.Ordinal)).ToList();
            if (matches.Count == 0)
                unsupported.Add(name);
            foreach (var match in matches)
                if (!listed.Contains(match, StringComparer.Ordinal))
                    listed.Add(match);
        }

        List<string> result = mode switch
        {
            // Everything SSH.NET has is already in its defaults, so appending only keeps them.
            '+' => [.. defaults],
            '-' => defaults.Where(candidate => !listed.Contains(candidate, StringComparer.Ordinal)).ToList(),
            '^' => [.. listed, .. defaults.Where(candidate => !listed.Contains(candidate, StringComparer.Ordinal))],
            _ => listed,
        };
        if (mode == '-')
            unsupported.Clear();
        return new SshAlgorithmList(result, unsupported)
        {
            Error = result.Count == 0 ? "This list leaves no algorithm to offer." : null,
        };
    }

    private static bool IsPattern(string name) => name.Contains('*') || name.Contains('?');

    /// <summary>OpenSSH's match_pattern: <c>*</c> is any run, <c>?</c> any one character.</summary>
    internal static bool Matches(string text, string pattern)
    {
        int t = 0, p = 0, star = -1, mark = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || pattern[p] == text[t]))
            {
                t++;
                p++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = t;
            }
            else if (star >= 0)
            {
                p = star + 1;
                t = ++mark;
            }
            else
                return false;
        }
        while (p < pattern.Length && pattern[p] == '*')
            p++;
        return p == pattern.Length;
    }
}
