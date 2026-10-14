using System.Globalization;
using Resesh.Core.Models;
using Resesh.Core.Storage;

namespace Resesh.Core.Import;

/// <summary>One SecureCRT session found on disk.</summary>
public sealed record ImportCandidate
{
    // Defaulted rather than `required`: the WinUI XAML type-info generator needs a
    // parameterless activator for types reachable from x:DataType templates.
    public string Name { get; init; } = "";
    public string FolderPath { get; init; } = "";
    public string RelativePath { get; init; } = "";
    public string Host { get; init; } = "";
    public int Port { get; init; } = 22;
    public string Username { get; init; } = "";
    public string Protocol { get; init; } = "";
    public string? PrivateKeyPath { get; init; }

    /// <summary>The name of another candidate or saved session this one is reached through
    /// (OpenSSH ProxyJump, PuTTY's SSH proxy). Linked after import when a session has that name.</summary>
    public string? JumpHostName { get; init; }

    /// <summary>A SOCKS or HTTP proxy the source connects through. Its password is not imported.</summary>
    public ProxySettings? Proxy { get; init; }

    /// <summary>
    /// Opaque, in-memory-only token equal for sessions whose source stored the same password
    /// (see <see cref="SecureCrtImporter.PasswordFingerprint"/>). Never decrypted, never saved;
    /// null when no password is stored or the format can't be compared.
    /// </summary>
    public string? PasswordFingerprint { get; init; }

    /// <summary>Plain telnet (not SecureCRT's Telnet/SSL, which needs TLS we don't speak).</summary>
    public bool IsTelnet => Protocol == TelnetProtocol;

    /// <summary>SSH and plain telnet are importable; serial, rlogin, raw etc. are listed as skipped.</summary>
    public bool IsSupported => Protocol is "SSH2" or "SSH1" || IsTelnet;

    /// <summary>The normalized protocol name both importers use for telnet.</summary>
    public const string TelnetProtocol = "TELNET";

    /// <summary>The port to assume when the source stores none.</summary>
    public static int DefaultPort(string protocol) => protocol == TelnetProtocol ? 23 : 22;
}

public sealed record ImportScanResult
{
    public required IReadOnlyList<ImportCandidate> Importable { get; init; }
    public required IReadOnlyList<ImportCandidate> Skipped { get; init; }
}

internal readonly record struct SecureCrtConfigSettings(
    string? ConfigPath,
    bool StorePersonalDataSeparately,
    string? PersonalDataPath);

internal interface ISecureCrtConfigSource
{
    SecureCrtConfigSettings GetSettings();
}

internal sealed class WindowsSecureCrtConfigSource : ISecureCrtConfigSource
{
    private const string SecureCrtKeyPath = @"Software\VanDyke\SecureCRT";
    private const string ConfigPathValueName = "Config Path";
    private const string StorePersonalDataSeparatelyValueName = "Store Personal Data Separately";
    private const string PersonalDataPathValueName = "Personal Data Path";

    public SecureCrtConfigSettings GetSettings()
    {
        if (!OperatingSystem.IsWindows())
            return default;

        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(SecureCrtKeyPath);
            return new SecureCrtConfigSettings(
                key?.GetValue(ConfigPathValueName) as string,
                key?.GetValue(StorePersonalDataSeparatelyValueName) is int and not 0,
                key?.GetValue(PersonalDataPathValueName) as string);
        }
        catch (Exception)
        {
            return default;
        }
    }
}

/// <summary>
/// Reads SecureCRT's Config\Sessions directory and overlays usernames from the matching
/// personal-data session files when separate personal storage is enabled. The directory
/// structure is the folder tree. Passwords stay encrypted and are intentionally not imported;
/// password sessions are marked "credential needed"; key sessions use shared key references.
/// </summary>
public static class SecureCrtImporter
{
    private static readonly string[] SkippedFileNames = ["__FolderData__.ini", "Default.ini"];

    public static ImportScanResult ScanDefault() => ScanDefault(new WindowsSecureCrtConfigSource());

    internal static ImportScanResult ScanDefault(ISecureCrtConfigSource source)
    {
        var (configSessionsPath, personalSessionsPath) = GetSessionPaths(source);
        return Directory.Exists(configSessionsPath)
            ? Scan(configSessionsPath, personalSessionsPath)
            : new ImportScanResult { Importable = [], Skipped = [] };
    }

    internal static (string ConfigSessionsPath, string? PersonalSessionsPath) GetSessionPaths(
        ISecureCrtConfigSource source)
    {
        var settings = source.GetSettings();
        var configPath = string.IsNullOrWhiteSpace(settings.ConfigPath)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "VanDyke",
                "Config")
            : settings.ConfigPath.Trim();
        var personalSessionsPath =
            settings.StorePersonalDataSeparately && !string.IsNullOrWhiteSpace(settings.PersonalDataPath)
                ? Path.Combine(settings.PersonalDataPath.Trim(), "Sessions")
                : null;
        return (Path.Combine(configPath, "Sessions"), personalSessionsPath);
    }

    public static ImportScanResult Scan(string sessionsDir) => Scan(sessionsDir, personalSessionsDir: null);

    private static ImportScanResult Scan(string sessionsDir, string? personalSessionsDir)
    {
        var importable = new List<ImportCandidate>();
        var skipped = new List<ImportCandidate>();

        foreach (var file in Directory.EnumerateFiles(sessionsDir, "*.ini", SearchOption.AllDirectories))
        {
            var fileName = Path.GetFileName(file);
            if (SkippedFileNames.Contains(fileName, StringComparer.OrdinalIgnoreCase))
                continue;

            var relative = Path.GetRelativePath(sessionsDir, file);
            var folder = FolderPaths.Normalize(Path.GetDirectoryName(relative) ?? "");
            var content = File.ReadAllText(file);
            var candidate = Parse(content, Path.GetFileNameWithoutExtension(fileName), folder, relative);
            if (personalSessionsDir is not null)
            {
                var personalFile = Path.Combine(personalSessionsDir, relative);
                if (File.Exists(personalFile))
                {
                    var personalContent = File.ReadAllText(personalFile);
                    content += "\n" + personalContent;
                    candidate = candidate with
                    {
                        Username = GetStringValue(personalContent, "Username")?.Trim() ?? candidate.Username,
                        PasswordFingerprint = PasswordFingerprint(GetStringValue(content, "Password V2")),
                        PrivateKeyPath = GetIdentity(personalContent) ?? candidate.PrivateKeyPath,
                    };
                }
            }
            var configRoot = Path.GetFullPath(Path.Combine(sessionsDir, ".."));
            var personalRoot = personalSessionsDir is null ? configRoot
                : Path.GetFullPath(Path.Combine(personalSessionsDir, ".."));
            if (!OffersPublicKey(content))
                candidate = candidate with { PrivateKeyPath = null };
            else if (UsesGlobalKey(content))
                candidate = candidate with { PrivateKeyPath = GlobalIdentity(configRoot, personalRoot) };
            if (candidate.PrivateKeyPath is { } identity)
            {
                identity = identity.Replace("${VDS_CONFIG_PATH}", configRoot)
                    .Replace("${VDS_USER_DATA_PATH}", personalRoot);
                candidate = candidate with { PrivateKeyPath = PrivateKeyFor(ImportKeyPath.Resolve(identity, configRoot)) };
            }
            (candidate.IsSupported ? importable : skipped).Add(candidate);
        }

        return new ImportScanResult
        {
            Importable = importable.OrderBy(c => c.RelativePath, StringComparer.OrdinalIgnoreCase).ToList(),
            Skipped = skipped.OrderBy(c => c.RelativePath, StringComparer.OrdinalIgnoreCase).ToList(),
        };
    }

    /// <summary>
    /// Parses one .ini. Lines look like S:"Hostname"=10.0.0.1 and D:"[SSH2] Port"=00000016
    /// (D: values are 8-digit hex). Unknown keys are ignored. A session file keeps settings
    /// for every protocol it was ever switched to, so the port is read from the key of its
    /// current protocol ("[SSH2] Port", "[SSH1] Port", or plain "Port" for telnet), never
    /// just the first port key in the file; a missing port falls back to the protocol default.
    /// </summary>
    private const int RdpPort = 3389;

    public static ImportCandidate Parse(string iniContent, string name, string folderPath, string relativePath)
    {
        string host = "", username = "", protocol = "";
        var ports = new Dictionary<string, int>();

        foreach (var rawLine in iniContent.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (!TryParseLine(line, out var kind, out var key, out var value))
                continue;

            if (kind == 'S')
            {
                switch (key)
                {
                    case "Hostname": host = value; break;
                    case "Username": username = value; break;
                    case "Protocol Name": protocol = value.Trim().ToUpperInvariant(); break;
                }
            }
            else if (kind == 'D' && key is "[SSH2] Port" or "[SSH1] Port" or "Port" && !ports.ContainsKey(key))
            {
                if (int.TryParse(value.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed)
                    && parsed is > 0 and <= 65535)
                {
                    ports[key] = parsed;
                }
            }
        }

        // An RDP session can keep a stale "SSH2" protocol name; its current-protocol port
        // (the plain key) then still says 3389. SSH sessions store theirs under "[SSH2] Port".
        if (protocol is "SSH2" or "SSH1" && ports.GetValueOrDefault("Port") == RdpPort
            && iniContent.Contains("\"RDP ", StringComparison.Ordinal))
        {
            protocol = "RDP";
        }

        // SSH sessions fall back to the plain key, which older SecureCRT versions wrote.
        var port = protocol switch
        {
            "SSH2" => ports.GetValueOrDefault("[SSH2] Port", ports.GetValueOrDefault("Port")),
            "SSH1" => ports.GetValueOrDefault("[SSH1] Port", ports.GetValueOrDefault("Port")),
            _ => ports.GetValueOrDefault("Port"),
        };

        return new ImportCandidate
        {
            Name = name,
            FolderPath = folderPath,
            RelativePath = relativePath,
            Host = host.Trim(),
            Port = port > 0 ? port : ImportCandidate.DefaultPort(protocol),
            Username = protocol == ImportCandidate.TelnetProtocol ? "" : username.Trim(),
            Protocol = protocol,
            PrivateKeyPath = GetIdentity(iniContent),
            PasswordFingerprint = protocol == ImportCandidate.TelnetProtocol
                ? null
                : PasswordFingerprint(GetStringValue(iniContent, "Password V2")),
        };
    }

    /// <summary>
    /// Lets sessions be grouped by saved password without decrypting anything. SecureCRT's
    /// "Password V2" is AES-CBC over [length][password][SHA-256 of it][random padding].
    /// "03:" prepends a salt used to derive its key and IV; "02:" derives its key from the
    /// config passphrase. Padding occupies 8–23 bytes, so omit the final two cipher blocks.
    /// All remaining blocks are deterministic and include the entire password plus at least
    /// eight checksum bytes. Comparing only two leading blocks would conflate long passwords
    /// of the same length and prefix. The token never contains ciphertext or plaintext.
    /// </summary>
    internal static string? PasswordFingerprint(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
            return null;
        stored = stored.Trim();
        var saltHex = stored.StartsWith("03:", StringComparison.Ordinal) ? 16 * 2
            : stored.StartsWith("02:", StringComparison.Ordinal) ? 0
            : -1;
        var ciphertextHex = stored.Length - 3 - saltHex;
        if (saltHex < 0 || ciphertextHex < 3 * 16 * 2 || ciphertextHex % (16 * 2) != 0
            || stored.AsSpan(3).ContainsAnyExcept("0123456789abcdefABCDEF"))
            return null;
        var deterministicHex = stored.Length - 2 * 16 * 2;
        var leading = System.Text.Encoding.ASCII.GetBytes(stored[..deterministicHex].ToLowerInvariant());
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(leading));
    }

    private static string? GetIdentity(string content) =>
        GetStringValue(content, "Identity Filename V2")
        ?? GetStringValue(content, "Identity Filename")
        ?? GetStringValue(content, "SSH1 Identity Filename V2");

    /// <summary>
    /// The key behind "Use global public key". SecureCRT keeps it in SSH2.ini (Global
    /// Options › SSH2); older versions kept it in Global.ini. Personal data overrides config.
    /// </summary>
    private static string? GlobalIdentity(string configRoot, string personalRoot)
    {
        foreach (var root in new[] { personalRoot, configRoot }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var file in new[] { "SSH2.ini", "Global.ini" })
            {
                var path = Path.Combine(root, file);
                if (File.Exists(path) && GetIdentity(File.ReadAllText(path)) is { Length: > 0 } identity)
                    return identity;
            }
        }
        return null;
    }

    private static readonly string[] PrivateKeyExtensions = ["", ".key", ".pem", ".ppk"];

    /// <summary>SecureCRT records the public key file (*.pub); the private key sits beside it,
    /// with no extension or as .key/.pem/.ppk. Falls back to the bare name when none exists.</summary>
    internal static string? PrivateKeyFor(string? path)
    {
        if (path is null || !path.EndsWith(".pub", StringComparison.OrdinalIgnoreCase))
            return path;
        var stem = path[..^4];
        return PrivateKeyExtensions.Select(extension => stem + extension).FirstOrDefault(File.Exists) ?? stem;
    }

    /// <summary>False when the session's SSH2 authentication list leaves out public keys.</summary>
    private static bool OffersPublicKey(string content) =>
        GetStringValue(content, "SSH2 Authentications V2") is not { Length: > 0 } methods
        || methods.Split(',').Any(method => method.Trim().Equals("publickey", StringComparison.OrdinalIgnoreCase));

    private static bool UsesGlobalKey(string content)
    {
        var enabled = false;
        foreach (var line in content.Split('\n'))
            if (TryParseLine(line.Trim(), out var kind, out var key, out var value)
                && kind == 'D' && key == "Use Global Public Key")
                enabled = int.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var flag)
                    && flag != 0;
        return enabled;
    }

    private static string? GetStringValue(string iniContent, string expectedKey)
    {
        string? result = null;
        foreach (var rawLine in iniContent.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (TryParseLine(line, out var kind, out var key, out var value)
                && kind == 'S'
                && key == expectedKey)
            {
                result = value;
            }
        }
        return result;
    }

    /// <summary>Matches lines of the shape X:"Key"=Value; anything else is ignored.</summary>
    private static bool TryParseLine(string line, out char kind, out string key, out string value)
    {
        kind = default;
        key = value = "";

        if (line.Length < 5 || line[1] != ':' || line[2] != '"')
            return false;

        var closingQuote = line.IndexOf('"', 3);
        if (closingQuote < 0 || closingQuote + 1 >= line.Length || line[closingQuote + 1] != '=')
            return false;

        kind = char.ToUpperInvariant(line[0]);
        key = line[3..closingQuote];
        value = line[(closingQuote + 2)..];
        return true;
    }

    /// <summary>
    /// Adds the selected candidates (SSH or telnet, from any importer) to the store. A
    /// candidate whose name+host+port already
    /// exists is skipped. Returns (imported, duplicatesSkipped).
    /// </summary>
    /// <summary>True when a session with the candidate's name, host, and port already exists.</summary>
    public static bool IsDuplicate(SessionStore store, ImportCandidate candidate) =>
        store.Sessions.Any(s =>
            s.Name.Equals(candidate.Name, StringComparison.OrdinalIgnoreCase)
            && s.Host.Equals(candidate.Host, StringComparison.OrdinalIgnoreCase)
            && s.Port == candidate.Port);

    /// <summary>Adds the selected candidates, skipping duplicates. With an identity plan,
    /// sessions sharing a login use one identity (created or reused) instead of their own.</summary>
    public static (int Imported, int Duplicates) Commit(SessionStore store, IEnumerable<ImportCandidate> selected,
        SshKeyStore? keys = null, Identities.ImportIdentityPlan? plan = null, IdentityStore? identities = null)
    {
        var imported = 0;
        var duplicates = 0;
        var identityIds = new Dictionary<string, Guid>();
        var added = new List<(Session Session, string? JumpHostName)>();

        void Add(ImportCandidate candidate, Session session)
        {
            session = session with { Proxy = session.Kind == SessionKind.Ssh ? candidate.Proxy : null };
            store.Add(session);
            added.Add((session, candidate.JumpHostName));
        }

        Guid IdentityFor(Identities.ImportLogin login)
        {
            if (identityIds.TryGetValue(login.Key, out var known))
                return known;
            var suggestion = plan!.Suggestions.First(s => s.Login.Key == login.Key);
            Guid id;
            if (suggestion.Existing is { } existing)
                id = existing.Id;
            else
            {
                var key = login.UsesKey
                    ? (keys ?? throw new InvalidOperationException("An SSH key store is required to import keys."))
                        .RegisterExternal(login.KeyPath, allowMissing: true)
                    : null;
                id = identities!.Add(new Identity
                {
                    Name = suggestion.Name,
                    Username = login.Username,
                    Proof = key is null ? IdentityProof.Password : IdentityProof.Key,
                    KeyId = key?.Id,
                }).Id;
            }
            identityIds[login.Key] = id;
            return id;
        }

        foreach (var candidate in selected)
        {
            if (IsDuplicate(store, candidate))
            {
                duplicates++;
                continue;
            }

            if (candidate.IsTelnet)
            {
                // Telnet carries no credentials: the server prompts in the terminal.
                Add(candidate, new Session
                {
                    Kind = SessionKind.Telnet,
                    Name = candidate.Name,
                    FolderPath = candidate.FolderPath,
                    Host = candidate.Host,
                    Port = candidate.Port,
                    AuthMethod = AuthMethod.None,
                });
                imported++;
                continue;
            }

            if (identities is not null && plan?.Choices.GetValueOrDefault(candidate) is
                { Kind: not Identities.ImportLoginKind.Own } choice)
            {
                Add(candidate, new Session
                {
                    Name = candidate.Name,
                    FolderPath = candidate.FolderPath,
                    Host = candidate.Host,
                    Port = candidate.Port,
                    InheritLogin = choice.Kind == Identities.ImportLoginKind.Inherit,
                    IdentityId = choice.Kind == Identities.ImportLoginKind.Identity ? IdentityFor(choice.Login!) : null,
                });
                if (choice.Kind == Identities.ImportLoginKind.Inherit && choice.Login is { } inherited
                    && plan.Suggestions.Any(s => s.Login.Key == inherited.Key))
                    IdentityFor(inherited);
                imported++;
                continue;
            }

            var key = !string.IsNullOrWhiteSpace(candidate.PrivateKeyPath)
                ? (keys ?? throw new InvalidOperationException("An SSH key store is required to import keys."))
                    .RegisterExternal(candidate.PrivateKeyPath, allowMissing: true)
                : null;

            Add(candidate, new Session
            {
                Name = candidate.Name,
                FolderPath = candidate.FolderPath,
                Host = candidate.Host,
                Port = candidate.Port,
                Username = candidate.Username,
                AuthMethod = key is null ? AuthMethod.Password : AuthMethod.PrivateKey,
                PrivateKeyId = key?.Id,
                CredentialNeeded = key is null,
            });
            imported++;
        }

        // A jump host is named by the source, so it can only be linked once every session
        // exists: one imported just now, or one saved earlier under that name.
        var linked = new List<Session>();
        foreach (var (session, jumpName) in added)
        {
            if (jumpName is null || session.Kind == SessionKind.Local)
                continue;
            var jump = added.Select(entry => entry.Session).Concat(store.Sessions).FirstOrDefault(other =>
                other.Kind == SessionKind.Ssh && other.Id != session.Id
                && (other.Name.Equals(jumpName, StringComparison.OrdinalIgnoreCase)
                    || other.Host.Equals(jumpName, StringComparison.OrdinalIgnoreCase)));
            if (jump is not null)
                linked.Add(session with { JumpHostId = jump.Id });
        }
        // Checked against each other as well: a source can name two hosts as each other's jump.
        var byId = store.Sessions.ToDictionary(session => session.Id);
        foreach (var session in linked)
        {
            if (!Ssh.JumpRoute.WouldLoop(session.Id, session.JumpHostId!.Value, byId.GetValueOrDefault))
                byId[session.Id] = session;
        }
        store.UpdateMany(linked.Where(session => byId[session.Id].JumpHostId is not null));

        // Attach only identities some imported session uses, and never over a folder's own.
        foreach (var (folder, login) in plan?.Attachments ?? [])
        {
            if (identityIds.TryGetValue(login.Key, out var id) && store.FolderIdentity(folder) is null)
                store.SetFolderIdentity(folder, id);
        }

        return (imported, duplicates);
    }
}
