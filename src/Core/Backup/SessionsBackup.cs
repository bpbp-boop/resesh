using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Resesh.Core.Credentials;
using Resesh.Core.Models;
using Resesh.Core.Ssh;
using Resesh.Core.Storage;

namespace Resesh.Core.Backup;

public enum BackupConflictResolution
{
    Keep,
    Replace,
    Duplicate,
}

public enum BackupConflictMatch
{
    SessionId,
    Endpoint,
}

public sealed record BackupScope(SessionKind Kind, string FolderPath);

public sealed record BackupExportOptions
{
    public BackupScope? Scope { get; init; }
    public bool IncludeSecrets { get; init; }
    public string? Passphrase { get; init; }
}

public sealed record BackupManifest
{
    public int SchemaVersion { get; init; }
    public DateTimeOffset CreatedUtc { get; init; }
    public bool IncludesSecrets { get; init; }
    public string? Scope { get; init; }
}

public sealed record BackupConflict
{
    // No required members: the WinUI XAML type-info generator needs a parameterless
    // activator for public types reachable from bound view models.
    public Session Imported { get; init; } = new();
    public Session Existing { get; init; } = new();
    public BackupConflictMatch Match { get; init; }
}

public sealed record BackupImportResult
{
    public int Imported { get; init; }
    public int Replaced { get; init; }
    public int Duplicated { get; init; }
    public int Kept { get; init; }
    public int SecretsImported { get; init; }
    public int KnownHostsAdded { get; init; }
}

public sealed record BackupPackage
{
    public required BackupManifest Manifest { get; init; }
    public required IReadOnlyList<Session> Sessions { get; init; }
    public required IReadOnlyList<string> Folders { get; init; }
    public required IReadOnlyList<string> LocalFolders { get; init; }
    public required AppSettings Settings { get; init; }
    public required IReadOnlyDictionary<string, KnownHostEntry> KnownHosts { get; init; }
    public required HighlightBackupData Highlights { get; init; }
    public required IReadOnlyDictionary<string, byte[]> Icons { get; init; }
    public required IReadOnlyDictionary<Guid, string> Secrets { get; init; }
    public required IReadOnlyList<SshKeyReference> SshKeys { get; init; }
    public required IReadOnlyDictionary<Guid, string> KeySecrets { get; init; }
    public byte[]? Workspaces { get; init; }
}

/// <summary>Creates and merges versioned .reseshbackup archives (pre-rename .sessionsbackup files import unchanged).</summary>
public static class SessionsBackup
{
    public const int CurrentSchemaVersion = 2;

    private const int Pbkdf2Iterations = 600_000;
    private const int MaxEntryCount = 10_000;
    private const long MaxEntryBytes = 50L * 1024 * 1024;
    private const long MaxArchiveBytes = 200L * 1024 * 1024;
    private static readonly byte[] EncryptedMagic = "SESSBKP1"u8.ToArray();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static bool IsEncrypted(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> magic = stackalloc byte[EncryptedMagic.Length];
        return stream.Read(magic) == magic.Length && magic.SequenceEqual(EncryptedMagic);
    }

    public static void Export(
        string destinationPath,
        string dataDirectory,
        SessionStore sessions,
        SettingsStore settings,
        KnownHostsStore knownHosts,
        HighlightsStore highlights,
        SshKeyStore sshKeys,
        ICredentialService credentials,
        BackupExportOptions options)
    {
        if (options.IncludeSecrets && string.IsNullOrEmpty(options.Passphrase))
            throw new ArgumentException("A passphrase is required when secrets are included.", nameof(options));

        var selected = SelectSessions(sessions.Sessions, options.Scope).ToList();
        var selectedIds = selected.Select(s => s.Id).ToHashSet();
        var selectedKeyIds = selected.Where(session => session.PrivateKeyId is not null)
            .Select(session => session.PrivateKeyId!.Value)
            .ToHashSet();
        var selectedKeys = sshKeys.Keys.Where(key => selectedKeyIds.Contains(key.Id)).ToList();
        var sessionData = new BackupSessionData
        {
            Sessions = selected,
            Folders = SelectFolders(sessions.Folders, options.Scope, SessionKind.Ssh),
            LocalFolders = SelectFolders(sessions.FoldersOf(SessionKind.Local), options.Scope, SessionKind.Local),
        };
        var manifest = new BackupManifest
        {
            SchemaVersion = CurrentSchemaVersion,
            CreatedUtc = DateTimeOffset.UtcNow,
            IncludesSecrets = options.IncludeSecrets,
            Scope = options.Scope is { } scope ? $"{scope.Kind}:{FolderPaths.Normalize(scope.FolderPath)}" : null,
        };

        using var zipBytes = new MemoryStream();
        using (var zip = new ZipArchive(zipBytes, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteJson(zip, "manifest.json", manifest);
            WriteJson(zip, "sessions.json", sessionData);
            WriteJson(zip, "settings.json", settings.Current);
            WriteJson(zip, "known_hosts.json", knownHosts.Entries);
            WriteJson(zip, "highlights.json", highlights.ExportBackup());
            WriteJson(zip, "ssh-keys.json", selectedKeys);

            var iconsDirectory = Path.Combine(dataDirectory, "icons");
            if (Directory.Exists(iconsDirectory))
            {
                foreach (var iconPath in Directory.EnumerateFiles(iconsDirectory)
                             .Where(IsSupportedIcon).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
                {
                    var entry = zip.CreateEntry("icons/" + Path.GetFileName(iconPath), CompressionLevel.Optimal);
                    using var output = entry.Open();
                    using var input = File.OpenRead(iconPath);
                    input.CopyTo(output);
                }
            }

            var workspacesPath = Path.Combine(dataDirectory, "workspaces.json");
            if (File.Exists(workspacesPath))
            {
                var entry = zip.CreateEntry("workspaces.json", CompressionLevel.Optimal);
                using var output = entry.Open();
                using var input = File.OpenRead(workspacesPath);
                input.CopyTo(output);
            }

            if (options.IncludeSecrets)
            {
                var secretData = selectedIds
                    .Select(id => (Id: id, Secret: credentials.Read(id)))
                    .Where(item => item.Secret is not null)
                    .ToDictionary(item => item.Id, item => item.Secret!);
                WriteJson(zip, "secrets.json", secretData);
                var keySecretData = selectedKeyIds
                    .Select(id => (Id: id, Secret: credentials.ReadKey(id)))
                    .Where(item => item.Secret is not null)
                    .ToDictionary(item => item.Id, item => item.Secret!);
                WriteJson(zip, "key-secrets.json", keySecretData);
            }
        }

        zipBytes.Position = 0;
        using (var validationZip = new ZipArchive(zipBytes, ZipArchiveMode.Read, leaveOpen: true))
            ValidateArchive(validationZip);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destinationPath))!);
        var tempPath = destinationPath + ".tmp";
        try
        {
            if (options.IncludeSecrets)
                WriteEncrypted(tempPath, zipBytes.ToArray(), options.Passphrase!);
            else
                File.WriteAllBytes(tempPath, zipBytes.ToArray());

            File.Move(tempPath, destinationPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
            if (options.IncludeSecrets && zipBytes.TryGetBuffer(out var buffer))
                CryptographicOperations.ZeroMemory(buffer.AsSpan(0, checked((int)zipBytes.Length)));
        }
    }

    public static BackupPackage Read(string path, string? passphrase = null)
    {
        byte[] zipData;
        var encrypted = IsEncrypted(path);
        if (encrypted)
        {
            if (string.IsNullOrEmpty(passphrase))
                throw new InvalidDataException("This backup is encrypted. Enter its passphrase.");
            zipData = ReadEncrypted(path, passphrase);
        }
        else
        {
            var info = new FileInfo(path);
            if (info.Length > MaxArchiveBytes)
                throw new InvalidDataException("The backup is too large.");
            zipData = File.ReadAllBytes(path);
        }

        try
        {
            using var bytes = new MemoryStream(zipData, writable: false);
            using var zip = new ZipArchive(bytes, ZipArchiveMode.Read);
            ValidateArchive(zip);
            var manifest = ReadRequired<BackupManifest>(zip, "manifest.json");
            if (manifest.SchemaVersion < 1 || manifest.SchemaVersion > CurrentSchemaVersion)
                throw new InvalidDataException($"Backup schema {manifest.SchemaVersion} is not supported.");

            var sessionData = ReadRequired<BackupSessionData>(zip, "sessions.json");
            if (sessionData.Sessions.Select(s => s.Id).Distinct().Count() != sessionData.Sessions.Count)
                throw new InvalidDataException("The backup contains duplicate session ids.");
            var secrets = ReadOptional<Dictionary<Guid, string>>(zip, "secrets.json") ?? [];
            var keySecrets = ReadOptional<Dictionary<Guid, string>>(zip, "key-secrets.json") ?? [];
            var sshKeys = ReadOptional<List<SshKeyReference>>(zip, "ssh-keys.json") ?? [];
            if (sshKeys.Select(key => key.Id).Distinct().Count() != sshKeys.Count)
                throw new InvalidDataException("The backup contains duplicate SSH key ids.");
            var sshKeyIds = sshKeys.Select(key => key.Id).ToHashSet();
            if (keySecrets.Keys.Any(keyId => !sshKeyIds.Contains(keyId)))
                throw new InvalidDataException("The backup contains a passphrase for an unknown SSH key.");
            if (manifest.SchemaVersion >= 2 && sessionData.Sessions
                .Where(session => session.PrivateKeyId is not null)
                .Any(session => !sshKeyIds.Contains(session.PrivateKeyId!.Value)))
            {
                throw new InvalidDataException("The backup contains a session with an unknown SSH key.");
            }
            if (manifest.IncludesSecrets != (zip.GetEntry("secrets.json") is not null))
                throw new InvalidDataException("The backup secret metadata is inconsistent.");
            if ((!manifest.IncludesSecrets && zip.GetEntry("key-secrets.json") is not null)
                || (manifest.SchemaVersion >= 2 && manifest.IncludesSecrets
                    && zip.GetEntry("key-secrets.json") is null))
            {
                throw new InvalidDataException("The backup SSH key secret metadata is inconsistent.");
            }
            if (manifest.IncludesSecrets && !encrypted)
                throw new InvalidDataException("A backup that contains secrets must be encrypted.");

            var icons = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in zip.Entries.Where(e => e.FullName.StartsWith("icons/", StringComparison.Ordinal)))
            {
                var name = entry.FullName["icons/".Length..];
                if (name.Length == 0 || name != Path.GetFileName(name) || !IsSupportedIcon(name))
                    throw new InvalidDataException($"The backup contains an invalid icon path: {entry.FullName}");
                using var stream = entry.Open();
                using var content = new MemoryStream();
                stream.CopyTo(content);
                icons.Add(name, content.ToArray());
            }

            return new BackupPackage
            {
                Manifest = manifest,
                Sessions = sessionData.Sessions,
                Folders = sessionData.Folders,
                LocalFolders = sessionData.LocalFolders,
                Settings = ReadOptional<AppSettings>(zip, "settings.json") ?? new AppSettings(),
                KnownHosts = ReadOptional<Dictionary<string, KnownHostEntry>>(zip, "known_hosts.json")
                    ?? new Dictionary<string, KnownHostEntry>(StringComparer.OrdinalIgnoreCase),
                Highlights = ReadOptional<HighlightBackupData>(zip, "highlights.json") ?? new HighlightBackupData(),
                Icons = icons,
                Secrets = secrets,
                SshKeys = sshKeys,
                KeySecrets = keySecrets,
                Workspaces = ReadOptionalBytes(zip, "workspaces.json"),
            };
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException)
        {
            throw new InvalidDataException("The backup archive is invalid or damaged.", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(zipData);
        }
    }

    public static IReadOnlyList<BackupConflict> FindConflicts(SessionStore store, BackupPackage package)
    {
        var existing = store.Sessions;
        var conflicts = new List<BackupConflict>();
        foreach (var imported in package.Sessions)
        {
            var match = existing.FirstOrDefault(s => s.Id == imported.Id);
            if (match is not null)
            {
                conflicts.Add(new BackupConflict
                {
                    Imported = imported,
                    Existing = match,
                    Match = BackupConflictMatch.SessionId,
                });
                continue;
            }

            if (imported.Kind == SessionKind.Ssh)
            {
                match = existing.FirstOrDefault(s => s.Kind == SessionKind.Ssh
                    && s.Port == imported.Port
                    && s.Host.Equals(imported.Host, StringComparison.OrdinalIgnoreCase)
                    && s.Username.Equals(imported.Username, StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                {
                    conflicts.Add(new BackupConflict
                    {
                        Imported = imported,
                        Existing = match,
                        Match = BackupConflictMatch.Endpoint,
                    });
                }
            }
        }
        return conflicts;
    }

    public static BackupImportResult Import(
        BackupPackage package,
        string dataDirectory,
        SessionStore sessionStore,
        SettingsStore settingsStore,
        KnownHostsStore knownHostsStore,
        HighlightsStore highlightsStore,
        SshKeyStore sshKeyStore,
        ICredentialService credentials,
        IReadOnlyDictionary<Guid, BackupConflictResolution> resolutions)
    {
        var keyIdMap = sshKeyStore.MergeImport(package.SshKeys);
        var mappedPackage = package with
        {
            Sessions = package.Sessions.Select(session => session.PrivateKeyId is { } keyId
                ? session with { PrivateKeyId = keyIdMap.GetValueOrDefault(keyId, keyId) }
                : session).ToList(),
        };
        var conflicts = FindConflicts(sessionStore, mappedPackage).ToDictionary(c => c.Imported.Id);
        var sessions = new List<Session>();
        var idMap = new Dictionary<Guid, Guid>();
        var importedCount = 0;
        var replaced = 0;
        var duplicated = 0;
        var kept = 0;
        var secretsImported = 0;

        foreach (var imported in mappedPackage.Sessions)
        {
            Session target;
            if (!conflicts.TryGetValue(imported.Id, out var conflict))
            {
                target = imported;
                importedCount++;
            }
            else
            {
                var resolution = resolutions.GetValueOrDefault(imported.Id, BackupConflictResolution.Keep);
                if (resolution == BackupConflictResolution.Keep)
                {
                    idMap[imported.Id] = conflict.Existing.Id;
                    kept++;
                    continue;
                }
                if (resolution == BackupConflictResolution.Replace)
                {
                    target = imported with { Id = conflict.Existing.Id };
                    replaced++;
                }
                else
                {
                    target = imported with { Id = Guid.NewGuid(), BuiltIn = false };
                    duplicated++;
                }
            }

            sessions.Add(target);
            idMap[imported.Id] = target.Id;
            if (package.Secrets.TryGetValue(imported.Id, out var secret))
            {
                credentials.Write(target.Id, secret);
                secretsImported++;
            }
        }

        sessionStore.ApplyImport(sessions, package.Folders, package.LocalFolders);

        foreach (var (sourceKeyId, secret) in package.KeySecrets)
        {
            credentials.WriteKey(keyIdMap.GetValueOrDefault(sourceKeyId, sourceKeyId), secret);
            secretsImported++;
        }

        var validSessionIds = sessionStore.Sessions.Select(s => s.Id).ToHashSet();
        var importedSettings = package.Settings with
        {
            PinnedSessionIds = package.Settings.PinnedSessionIds
                .Select(id => idMap.GetValueOrDefault(id, id))
                .Where(validSessionIds.Contains)
                .Distinct()
                .ToList(),
            DefaultLocalProfileId = package.Settings.DefaultLocalProfileId is { } defaultId
                && idMap.GetValueOrDefault(defaultId, defaultId) is var mappedDefault
                && validSessionIds.Contains(mappedDefault)
                    ? mappedDefault
                    : null,
        };
        settingsStore.Save(importedSettings);
        var knownHostsAdded = knownHostsStore.Merge(package.KnownHosts);
        highlightsStore.MergeBackup(package.Highlights);
        ImportFiles(dataDirectory, package);

        return new BackupImportResult
        {
            Imported = importedCount,
            Replaced = replaced,
            Duplicated = duplicated,
            Kept = kept,
            SecretsImported = secretsImported,
            KnownHostsAdded = knownHostsAdded,
        };
    }

    private static IEnumerable<Session> SelectSessions(IEnumerable<Session> sessions, BackupScope? scope) =>
        scope is null
            ? sessions
            : sessions.Where(s => s.Kind == scope.Kind
                && FolderPaths.IsSelfOrDescendant(s.FolderPath, scope.FolderPath));

    private static IReadOnlyList<string> SelectFolders(
        IEnumerable<string> folders, BackupScope? scope, SessionKind kind)
    {
        if (scope is null)
            return folders.ToList();
        if (scope.Kind != kind)
            return [];
        return folders.Where(f => FolderPaths.IsSelfOrDescendant(f, scope.FolderPath)).ToList();
    }

    private static void ImportFiles(string dataDirectory, BackupPackage package)
    {
        var iconsDirectory = Path.Combine(dataDirectory, "icons");
        foreach (var (name, content) in package.Icons)
        {
            Directory.CreateDirectory(iconsDirectory);
            WriteAtomic(Path.Combine(iconsDirectory, name), content);
        }
        if (package.Workspaces is not null)
            WriteAtomic(Path.Combine(dataDirectory, "workspaces.json"), package.Workspaces);
    }

    private static void WriteAtomic(string path, byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, content);
        File.Move(temp, path, overwrite: true);
    }

    private static void WriteJson<T>(ZipArchive archive, string name, T value)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        JsonSerializer.Serialize(stream, value, JsonOptions);
    }

    private static T ReadRequired<T>(ZipArchive archive, string name) =>
        ReadOptional<T>(archive, name)
        ?? throw new InvalidDataException($"The backup does not contain {name}.");

    private static T? ReadOptional<T>(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry(name);
        if (entry is null)
            return default;
        using var stream = entry.Open();
        return JsonSerializer.Deserialize<T>(stream, JsonOptions);
    }

    private static byte[]? ReadOptionalBytes(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry(name);
        if (entry is null)
            return null;
        using var stream = entry.Open();
        using var result = new MemoryStream();
        stream.CopyTo(result);
        return result.ToArray();
    }

    private static void ValidateArchive(ZipArchive archive)
    {
        if (archive.Entries.Count > MaxEntryCount)
            throw new InvalidDataException("The backup contains too many files.");
        long total = 0;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            if (!names.Add(entry.FullName))
                throw new InvalidDataException($"The backup contains duplicate file {entry.FullName}.");
            if (entry.Length > MaxEntryBytes || (total += entry.Length) > MaxArchiveBytes)
                throw new InvalidDataException("The expanded backup is too large.");
        }
    }

    private static bool IsSupportedIcon(string path) =>
        Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase)
        || Path.GetExtension(path).Equals(".svg", StringComparison.OrdinalIgnoreCase);

    private static void WriteEncrypted(string path, byte[] plaintext, string passphrase)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var ciphertext = new byte[plaintext.Length];
        var key = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(passphrase), salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, 32);
        try
        {
            using var aes = new AesGcm(key, tag.Length);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, EncryptedMagic);
            using var stream = File.Create(path);
            stream.Write(EncryptedMagic);
            Span<byte> iterations = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(iterations, Pbkdf2Iterations);
            stream.Write(iterations);
            stream.Write(salt);
            stream.Write(nonce);
            stream.Write(tag);
            stream.Write(ciphertext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(ciphertext);
        }
    }

    private static byte[] ReadEncrypted(string path, string passphrase)
    {
        var payload = File.ReadAllBytes(path);
        var headerLength = EncryptedMagic.Length + 4 + 16 + 12 + 16;
        if (payload.Length < headerLength || payload.Length > MaxArchiveBytes)
            throw new InvalidDataException("The encrypted backup is invalid or too large.");
        var offset = EncryptedMagic.Length;
        var iterations = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(offset, 4));
        offset += 4;
        if (iterations is < 100_000 or > 2_000_000)
            throw new InvalidDataException("The encrypted backup uses invalid key settings.");
        var salt = payload.AsSpan(offset, 16);
        offset += 16;
        var nonce = payload.AsSpan(offset, 12);
        offset += 12;
        var tag = payload.AsSpan(offset, 16);
        offset += 16;
        var ciphertext = payload.AsSpan(offset);
        var plaintext = new byte[ciphertext.Length];
        var key = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(passphrase), salt, iterations, HashAlgorithmName.SHA256, 32);
        try
        {
            using var aes = new AesGcm(key, tag.Length);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, EncryptedMagic);
            return plaintext;
        }
        catch (CryptographicException ex)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw new InvalidDataException("The passphrase is incorrect, or the backup is damaged.", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    private sealed record BackupSessionData
    {
        public IReadOnlyList<Session> Sessions { get; init; } = [];
        public IReadOnlyList<string> Folders { get; init; } = [];
        public IReadOnlyList<string> LocalFolders { get; init; } = [];
    }
}
