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
        var hostName = "";
        var username = "";
        var port = 22;

        void CommitBlock()
        {
            if (aliases is null)
                return;

            foreach (var alias in aliases.Where(IsConcreteAlias))
            {
                var resolvedHost = string.IsNullOrWhiteSpace(hostName) || hostName == "%h" ? alias : hostName;
                importable.Add(new ImportCandidate
                {
                    Name = alias,
                    RelativePath = sourcePath,
                    Host = resolvedHost,
                    Port = port,
                    Username = string.IsNullOrWhiteSpace(username) ? Environment.UserName : username,
                    Protocol = "SSH2",
                });
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
                hostName = "";
                username = "";
                port = 22;
                continue;
            }

            if (aliases is null)
                continue;

            if (key.Equals("HostName", StringComparison.OrdinalIgnoreCase) && hostName.Length == 0)
                hostName = Unquote(value);
            else if (key.Equals("User", StringComparison.OrdinalIgnoreCase) && username.Length == 0)
                username = Unquote(value);
            else if (key.Equals("Port", StringComparison.OrdinalIgnoreCase)
                     && int.TryParse(Unquote(value), out var parsedPort)
                     && parsedPort is > 0 and <= 65535)
                port = parsedPort;
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
