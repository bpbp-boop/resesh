using System.Text;

namespace Resesh.Core.Import;

/// <summary>Reads concrete Host entries from an OpenSSH client configuration file.</summary>
public static class OpenSshConfigImporter
{
    private readonly record struct ConfigEntry(string Directive, string Value, IReadOnlyList<string>? HostPatterns);

    public static string DefaultConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "config");

    public static ImportScanResult Scan(string configPath)
    {
        if (!File.Exists(configPath))
            return EmptyResult();

        return Parse(File.ReadAllText(configPath), configPath);
    }

    public static ImportScanResult Parse(string content, string sourcePath = "config")
    {
        var entries = ParseEntries(content);
        var aliases = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            if (entry.HostPatterns is null)
                continue;
            foreach (var alias in entry.HostPatterns)
            {
                if (IsConcreteAlias(alias) && seen.Add(alias))
                    aliases.Add(alias);
            }
        }

        var importable = new List<ImportCandidate>(aliases.Count);
        foreach (var alias in aliases)
        {
            importable.Add(ResolveOptions(entries, new ImportCandidate
            {
                Name = alias,
                RelativePath = sourcePath,
                Host = alias,
                Username = Environment.UserName,
                Protocol = "SSH2",
            }));
        }

        importable.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name));
        return new ImportScanResult
        {
            Importable = importable,
            Skipped = [],
        };
    }

    private static List<ConfigEntry> ParseEntries(string content)
    {
        var entries = new List<ConfigEntry>();
        foreach (var rawLine in content.Split('\n'))
        {
            var line = StripComment(rawLine).Trim();
            if (line.Length == 0)
                continue;

            var separator = line.IndexOfAny([' ', '\t', '=']);
            if (separator < 0)
                continue;

            var directive = line[..separator];
            var value = line[(separator + 1)..].TrimStart(' ', '\t', '=').Trim();
            IReadOnlyList<string>? patterns = directive.Equals("Host", StringComparison.OrdinalIgnoreCase)
                ? SplitArguments(value).ToList()
                : null;
            entries.Add(new ConfigEntry(directive, value, patterns));
        }
        return entries;
    }

    // Evaluate matching Host blocks in file order, including global and shared Host * options.
    // OpenSSH uses the first configured value; sessions currently select only the first key.
    private static ImportCandidate ResolveOptions(IReadOnlyList<ConfigEntry> entries, ImportCandidate candidate)
    {
        var matches = true;
        string? hostName = null, username = null, identity = null, proxyJump = null;
        string? kex = null, ciphers = null, macs = null, hostKeys = null;
        int? port = null;
        foreach (var entry in entries)
        {
            if (entry.HostPatterns is { } patterns)
            {
                matches = patterns.Any(pattern => !pattern.StartsWith('!') && Matches(pattern, candidate.Name))
                    && !patterns.Any(pattern => pattern.StartsWith('!') && Matches(pattern[1..], candidate.Name));
            }
            else if (entry.Directive.Equals("Match", StringComparison.OrdinalIgnoreCase))
                matches = false;
            else if (matches)
            {
                var directive = entry.Directive;
                var value = entry.Value;
                if (directive.Equals("HostName", StringComparison.OrdinalIgnoreCase) && hostName is null)
                    hostName = Unquote(value);
                else if (directive.Equals("User", StringComparison.OrdinalIgnoreCase) && username is null)
                    username = Unquote(value);
                else if (directive.Equals("Port", StringComparison.OrdinalIgnoreCase) && port is null
                    && int.TryParse(Unquote(value), out var parsedPort) && parsedPort is > 0 and <= 65535)
                    port = parsedPort;
                else if (directive.Equals("ProxyJump", StringComparison.OrdinalIgnoreCase) && proxyJump is null)
                    proxyJump = Unquote(value);
                else if (directive.Equals("IdentityFile", StringComparison.OrdinalIgnoreCase) && identity is null)
                    identity = Unquote(value);
                // First value wins, as for every other option.
                else if (directive.Equals("KexAlgorithms", StringComparison.OrdinalIgnoreCase))
                    kex ??= Unquote(value);
                else if (directive.Equals("Ciphers", StringComparison.OrdinalIgnoreCase))
                    ciphers ??= Unquote(value);
                else if (directive.Equals("MACs", StringComparison.OrdinalIgnoreCase))
                    macs ??= Unquote(value);
                else if (directive.Equals("HostKeyAlgorithms", StringComparison.OrdinalIgnoreCase))
                    hostKeys ??= Unquote(value);
            }
        }
        candidate = candidate with
        {
            Host = string.IsNullOrWhiteSpace(hostName) ? candidate.Name : ExpandHostName(hostName, candidate.Name),
            Username = string.IsNullOrWhiteSpace(username) ? Environment.UserName : username,
            Port = port ?? 22,
            JumpHostName = JumpAlias(proxyJump),
            Algorithms = Ssh.SshAlgorithmPolicy.FromOpenSsh(kex, ciphers, macs, hostKeys),
        };
        if (identity is not null)
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            identity = ExpandIdentity(identity, home, candidate.Host, candidate.Username, Environment.UserName);
            candidate = candidate with { PrivateKeyPath = ImportKeyPath.Resolve(identity) };
        }
        return candidate;
    }

    private static string ExpandHostName(string template, string alias)
    {
        if (!template.Contains('%'))
            return template;
        var builder = new StringBuilder(template.Length);
        for (var i = 0; i < template.Length; i++)
        {
            if (template[i] == '%' && i + 1 < template.Length)
            {
                var next = template[i + 1];
                if (next == '%')
                {
                    builder.Append('%');
                    i++;
                    continue;
                }
                if (next == 'h')
                {
                    builder.Append(alias);
                    i++;
                    continue;
                }
            }
            builder.Append(template[i]);
        }
        return builder.ToString();
    }

    private static string ExpandIdentity(string template, string home, string host, string remoteUser, string localUser)
    {
        if (!template.Contains('%'))
            return template;
        var builder = new StringBuilder(template.Length);
        for (var i = 0; i < template.Length; i++)
        {
            if (template[i] == '%' && i + 1 < template.Length)
            {
                var replacement = template[i + 1] switch
                {
                    '%' => "%",
                    'd' => home,
                    'h' => host,
                    'r' => remoteUser,
                    'u' => localUser,
                    _ => null,
                };
                if (replacement is not null)
                {
                    builder.Append(replacement);
                    i++;
                    continue;
                }
            }
            builder.Append(template[i]);
        }
        return builder.ToString();
    }

    /// <summary>The host a ProxyJump value reaches the target from: the last of a
    /// comma-separated chain, without its user or port, which the jump's own Host block
    /// supplies. Earlier hops are imported only when that block names them in turn.</summary>
    internal static string? JumpAlias(string? proxyJump)
    {
        var last = proxyJump?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        if (string.IsNullOrEmpty(last) || last.Equals("none", StringComparison.OrdinalIgnoreCase)
            || last.Contains("://", StringComparison.Ordinal))
            return null;
        return Models.HostPort.TrySplit(last[(last.LastIndexOf('@') + 1)..], out var host, out _) ? host : null;
    }

    /// <summary>Linear-time case-insensitive OpenSSH glob match (* and ?), avoiding regex backtracking.</summary>
    internal static bool Matches(string pattern, string host)
    {
        int p = 0, h = 0, star = -1, match = 0;
        while (h < host.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(host[h])))
            {
                p++;
                h++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                match = h;
            }
            else if (star >= 0)
            {
                p = star + 1;
                h = ++match;
            }
            else
            {
                return false;
            }
        }
        while (p < pattern.Length && pattern[p] == '*')
            p++;
        return p == pattern.Length;
    }

    private static ImportScanResult EmptyResult() => new()
    {
        Importable = [],
        Skipped = [],
    };

    private static bool IsConcreteAlias(string alias) =>
        alias.Length > 0 && alias[0] != '!' && alias.IndexOfAny(['*', '?', '[']) < 0;

    private static string StripComment(string line)
    {
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"')
                quoted = !quoted;
            else if (line[i] == '#' && !quoted)
                return line[..i];
        }
        return line;
    }

    private static IEnumerable<string> SplitArguments(string value)
    {
        var current = new StringBuilder();
        var quoted = false;
        foreach (var character in value)
        {
            if (character == '"')
            {
                quoted = !quoted;
                continue;
            }
            if (char.IsWhiteSpace(character) && !quoted)
            {
                if (current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }
                continue;
            }
            current.Append(character);
        }
        if (current.Length > 0)
            yield return current.ToString();
    }

    private static string Unquote(string value) =>
        value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value;
}
