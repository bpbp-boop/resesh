using System.Net;
using Resesh.Core.Models;
using Resesh.Core.Storage;

namespace Resesh.Core.Import;

public interface IPuttyRegistrySource
{
    IEnumerable<string> GetSessionNames();
    IReadOnlyDictionary<string, object?>? GetSessionValues(string sessionName);
}

public sealed class WindowsPuttyRegistrySource : IPuttyRegistrySource
{
    private const string PuttySessionsKeyPath = @"Software\SimonTatham\PuTTY\Sessions";

    public IEnumerable<string> GetSessionNames()
    {
        if (!OperatingSystem.IsWindows())
            return [];

        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(PuttySessionsKeyPath);
            return key?.GetSubKeyNames() ?? [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    public IReadOnlyDictionary<string, object?>? GetSessionValues(string sessionName)
    {
        if (!OperatingSystem.IsWindows())
            return null;

        try
        {
            var subKeyPath = $@"{PuttySessionsKeyPath}\{sessionName}";
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(subKeyPath);
            if (key is null)
                return null;

            var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var valueName in key.GetValueNames())
            {
                dict[valueName] = key.GetValue(valueName);
            }
            return dict;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>
/// Scans and parses saved PuTTY sessions from Windows registry key HKCU\Software\SimonTatham\PuTTY\Sessions.
/// Session subkey names use percent-encoding for special characters (e.g. %20 for spaces).
/// </summary>
public static class PuttyRegistryImporter
{
    public static ImportScanResult Scan(IPuttyRegistrySource? source = null)
    {
        source ??= new WindowsPuttyRegistrySource();

        var importable = new List<ImportCandidate>();
        var skipped = new List<ImportCandidate>();

        foreach (var encodedName in source.GetSessionNames())
        {
            if (string.Equals(encodedName, "Default%20Settings", StringComparison.OrdinalIgnoreCase))
                continue;

            var values = source.GetSessionValues(encodedName);
            if (values is null)
                continue;

            var candidate = ParseSession(encodedName, values);
            (candidate.IsSupported ? importable : skipped).Add(candidate);
        }

        return new ImportScanResult
        {
            Importable = importable.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            Skipped = skipped.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList(),
        };
    }

    public static ImportCandidate ParseSession(string encodedName, IReadOnlyDictionary<string, object?> values)
    {
        var decodedName = DecodeSessionName(encodedName);

        var rawHost = GetString(values, "HostName") ?? "";
        var port = GetInt(values, "PortNumber") ?? 22;
        var rawUser = GetString(values, "UserName") ?? "";
        var protocol = (GetString(values, "Protocol") ?? "ssh").ToUpperInvariant();

        // PuTTY allows HostName to be "user@host"
        var username = rawUser;
        var host = rawHost;
        var atIndex = rawHost.IndexOf('@');
        if (atIndex >= 0)
        {
            if (string.IsNullOrWhiteSpace(username))
                username = rawHost[..atIndex];
            host = rawHost[(atIndex + 1)..];
        }

        if (string.IsNullOrWhiteSpace(username))
            username = Environment.UserName;

        return new ImportCandidate
        {
            Name = decodedName,
            FolderPath = "",
            RelativePath = $"PuTTY/{decodedName}",
            Host = host.Trim(),
            Port = port,
            Username = username.Trim(),
            Protocol = protocol == "SSH" ? "SSH2" : protocol,
        };
    }

    public static string DecodeSessionName(string encoded)
    {
        if (string.IsNullOrEmpty(encoded))
            return "";

        // PuTTY encodes spaces as %20 and special characters as %XX
        try
        {
            return Uri.UnescapeDataString(encoded.Replace("+", "%2B"));
        }
        catch
        {
            return encoded;
        }
    }

    private static string? GetString(IReadOnlyDictionary<string, object?> values, string key) =>
        values.TryGetValue(key, out var val) && val is string s ? s : null;

    private static int? GetInt(IReadOnlyDictionary<string, object?> values, string key)
    {
        if (!values.TryGetValue(key, out var val) || val is null)
            return null;

        if (val is int i)
            return i;

        if (int.TryParse(val.ToString(), out var parsed) && parsed is > 0 and <= 65535)
            return parsed;

        return null;
    }
}
