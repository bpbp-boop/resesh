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
        var rawUser = GetString(values, "UserName") ?? "";
        var protocol = (GetString(values, "Protocol") ?? "ssh").ToUpperInvariant();
        var port = GetInt(values, "PortNumber") ?? ImportCandidate.DefaultPort(protocol);
        var telnet = protocol == ImportCandidate.TelnetProtocol;
        var proxyMethod = values.TryGetValue("ProxyMethod", out var method) && method is int methodValue ? methodValue : 0;
        var proxyHost = (GetString(values, "ProxyHost") ?? "").Trim();

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

        if (protocol == ImportCandidate.SerialProtocol)
            return ParseSerial(decodedName, values);

        if (telnet)
            username = ""; // the server prompts; a saved name has nowhere to go
        else if (string.IsNullOrWhiteSpace(username))
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
            PrivateKeyPath = telnet ? null : ImportKeyPath.Resolve(GetString(values, "PublicKeyFile")),
            JumpHostName = telnet ? null : proxyMethod == PuttySshProxy && proxyHost.Length > 0 ? proxyHost : null,
            Proxy = telnet || proxyHost.Length == 0 ? null : proxyMethod switch
            {
                1 => PuttyProxy(Models.ProxyKind.Socks4, proxyHost, values),
                2 => PuttyProxy(Models.ProxyKind.Socks5, proxyHost, values),
                3 => PuttyProxy(Models.ProxyKind.Http, proxyHost, values),
                _ => null,
            },
        };
    }

    /// <summary>PuTTY's serial settings: SerialLine, SerialSpeed, SerialDataBits,
    /// SerialStopHalfbits (2, 3 or 4 half bits), SerialParity (none, odd, even, mark,
    /// space) and SerialFlowControl (none, XON/XOFF, RTS/CTS, DSR/DTR). DSR/DTR has no
    /// equivalent here and imports as none.</summary>
    private static ImportCandidate ParseSerial(string name, IReadOnlyDictionary<string, object?> values)
    {
        var portName = SerialTarget.NormalizePortName(GetString(values, "SerialLine"));
        var speed = values.TryGetValue("SerialSpeed", out var rawSpeed) && rawSpeed is int s && s > 0 ? s : 9600;
        var dataBits = values.TryGetValue("SerialDataBits", out var rawBits) && rawBits is int b and >= 5 and <= 8 ? b : 8;
        return new ImportCandidate
        {
            Name = name,
            FolderPath = "",
            RelativePath = $"PuTTY/{name}",
            Protocol = ImportCandidate.SerialProtocol,
            Port = 0,
            Serial = portName is null ? null : new SerialTarget
            {
                PortName = portName,
                BaudRate = speed,
                DataBits = dataBits,
                StopBits = values.TryGetValue("SerialStopHalfbits", out var stop) ? stop switch
                {
                    3 => SerialStopBits.OnePointFive,
                    4 => SerialStopBits.Two,
                    _ => SerialStopBits.One,
                } : SerialStopBits.One,
                Parity = values.TryGetValue("SerialParity", out var parity) ? parity switch
                {
                    1 => SerialParity.Odd,
                    2 => SerialParity.Even,
                    3 => SerialParity.Mark,
                    4 => SerialParity.Space,
                    _ => SerialParity.None,
                } : SerialParity.None,
                // PuTTY's default is XON/XOFF, so a missing value means that.
                FlowControl = values.TryGetValue("SerialFlowControl", out var flow) ? flow switch
                {
                    1 => SerialFlowControl.XonXoff,
                    2 => SerialFlowControl.RtsCts,
                    _ => SerialFlowControl.None,
                } : SerialFlowControl.XonXoff,
            },
        };
    }

    /// <summary>ProxyMethod 6: "SSH to proxy and use port forwarding", PuTTY's jump host.
    /// Its ProxyHost is a host name or another saved session's name.</summary>
    private const int PuttySshProxy = 6;

    private static Models.ProxySettings PuttyProxy(
        Models.ProxyKind kind, string host, IReadOnlyDictionary<string, object?> values) => new()
    {
        Kind = kind,
        Host = host,
        Port = GetInt(values, "ProxyPort") ?? Models.ProxySettings.DefaultPort(kind),
        Username = (GetString(values, "ProxyUsername") ?? "").Trim(),
    };

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
            return i is > 0 and <= 65535 ? i : null;

        if (int.TryParse(val.ToString(), out var parsed) && parsed is > 0 and <= 65535)
            return parsed;

        return null;
    }
}
