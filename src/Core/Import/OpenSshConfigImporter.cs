namespace Resesh.Core.Import;

/// <summary>Reads concrete Host entries from an OpenSSH client configuration file.</summary>
public static class OpenSshConfigImporter
{
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
        var importable = new List<ImportCandidate>();
        List<string>? aliases = null;

        void CommitBlock()
        {
            if (aliases is null)
                return;

            foreach (var alias in aliases.Where(IsConcreteAlias))
            {
                importable.Add(ResolveOptions(content, new ImportCandidate
                {
                    Name = alias,
                    RelativePath = sourcePath,
                    Host = alias,
                    Username = Environment.UserName,
                    Protocol = "SSH2",
                }));
            }
        }

        foreach (var rawLine in content.Split('\n'))
        {
            var line = StripComment(rawLine).Trim();
            if (line.Length == 0)
                continue;

            var separator = line.IndexOfAny([' ', '\t', '=']);
            if (separator < 0)
                continue;

            var key = line[..separator];
            var value = line[(separator + 1)..].TrimStart(' ', '\t', '=').Trim();
            if (key.Equals("Host", StringComparison.OrdinalIgnoreCase))
            {
                CommitBlock();
                aliases = SplitArguments(value).ToList();
                continue;
            }

            if (key.Equals("Match", StringComparison.OrdinalIgnoreCase))
            {
                CommitBlock();
                aliases = null;
                continue;
            }
        }

        CommitBlock();
        return new ImportScanResult
        {
            Importable = importable
                .GroupBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            Skipped = [],
        };
    }

    // Evaluate matching Host blocks in file order, including global and shared Host * options.
    // OpenSSH uses the first configured value; sessions currently select only the first key.
    private static ImportCandidate ResolveOptions(string content, ImportCandidate candidate)
    {
        var matches = true;
        string? hostName = null, username = null, identity = null, proxyJump = null;
        int? port = null;
        foreach (var rawLine in content.Split('\n'))
        {
            var line = StripComment(rawLine).Trim();
            var separator = line.IndexOfAny([' ', '\t', '=']);
            if (separator < 0)
                continue;
            var directive = line[..separator];
            var value = line[(separator + 1)..].TrimStart(' ', '\t', '=').Trim();
            if (directive.Equals("Host", StringComparison.OrdinalIgnoreCase))
            {
                var patterns = SplitArguments(value).ToList();
                matches = patterns.Any(pattern => !pattern.StartsWith('!') && Matches(pattern, candidate.Name))
                    && !patterns.Any(pattern => pattern.StartsWith('!') && Matches(pattern[1..], candidate.Name));
            }
            else if (directive.Equals("Match", StringComparison.OrdinalIgnoreCase))
                matches = false;
            else if (matches)
            {
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
            }
        }
        candidate = candidate with
        {
            Host = string.IsNullOrWhiteSpace(hostName) ? candidate.Name
                : System.Text.RegularExpressions.Regex.Replace(hostName, "%[%h]", match =>
                    match.Value == "%%" ? "%" : candidate.Name),
            Username = string.IsNullOrWhiteSpace(username) ? Environment.UserName : username,
            Port = port ?? 22,
            JumpHostName = JumpAlias(proxyJump),
        };
        if (identity is not null)
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            identity = System.Text.RegularExpressions.Regex.Replace(identity, "%[%dhru]", match => match.Value switch
            {
                "%%" => "%", "%d" => home, "%h" => candidate.Host,
                "%r" => candidate.Username, "%u" => Environment.UserName, _ => match.Value,
            });
            candidate = candidate with { PrivateKeyPath = ImportKeyPath.Resolve(identity) };
        }
        return candidate;
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
        last = last[(last.LastIndexOf('@') + 1)..];
        // A bracketed IPv6 literal keeps its colons; otherwise a single colon is the port.
        if (last.StartsWith('['))
            return last.IndexOf(']') is > 0 and var close ? last[1..close] : null;
        var colon = last.IndexOf(':');
        if (colon >= 0 && colon == last.LastIndexOf(':'))
            last = last[..colon];
        return last.Length == 0 ? null : last;
    }

    private static bool Matches(string pattern, string host) =>
        System.Text.RegularExpressions.Regex.IsMatch(host,
            "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

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
        var current = new System.Text.StringBuilder();
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
