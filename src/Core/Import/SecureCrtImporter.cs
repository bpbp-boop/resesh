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

    /// <summary>Only SSH sessions are importable; Telnet/serial/etc. are listed as skipped.</summary>
    public bool IsSupported => Protocol is "SSH2" or "SSH1";
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
/// imported sessions are marked "credential needed" instead.
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
            var candidate = Parse(File.ReadAllText(file), Path.GetFileNameWithoutExtension(fileName), folder, relative);
            if (personalSessionsDir is not null)
            {
                var personalFile = Path.Combine(personalSessionsDir, relative);
                if (File.Exists(personalFile)
                    && GetStringValue(File.ReadAllText(personalFile), "Username") is { } personalUsername)
                {
                    candidate = candidate with { Username = personalUsername.Trim() };
                }
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
    /// (D: values are 8-digit hex). Unknown keys are ignored; missing port falls back to 22.
    /// </summary>
    public static ImportCandidate Parse(string iniContent, string name, string folderPath, string relativePath)
    {
        string host = "", username = "", protocol = "";
        int? port = null;

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
            else if (kind == 'D' && port is null && key is "[SSH2] Port" or "[SSH1] Port" or "Port")
            {
                if (int.TryParse(value.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed)
                    && parsed is > 0 and <= 65535)
                {
                    port = parsed;
                }
            }
        }

        return new ImportCandidate
        {
            Name = name,
            FolderPath = folderPath,
            RelativePath = relativePath,
            Host = host.Trim(),
            Port = port ?? 22,
            Username = username.Trim(),
            Protocol = protocol,
        };
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
    /// Adds the selected candidates to the store. A candidate whose name+host+port already
    /// exists is skipped. Returns (imported, duplicatesSkipped).
    /// </summary>
    public static (int Imported, int Duplicates) Commit(SessionStore store, IEnumerable<ImportCandidate> selected)
    {
        var imported = 0;
        var duplicates = 0;

        foreach (var candidate in selected)
        {
            var isDuplicate = store.Sessions.Any(s =>
                s.Name.Equals(candidate.Name, StringComparison.OrdinalIgnoreCase)
                && s.Host.Equals(candidate.Host, StringComparison.OrdinalIgnoreCase)
                && s.Port == candidate.Port);
            if (isDuplicate)
            {
                duplicates++;
                continue;
            }

            store.Add(new Session
            {
                Name = candidate.Name,
                FolderPath = candidate.FolderPath,
                Host = candidate.Host,
                Port = candidate.Port,
                Username = candidate.Username,
                AuthMethod = AuthMethod.Password,
                CredentialNeeded = true,
            });
            imported++;
        }

        return (imported, duplicates);
    }
}
