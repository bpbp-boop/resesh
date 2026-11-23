using System.Text.RegularExpressions;

namespace Resesh.Terminal.Native;

/// <summary>
/// Recognizes idle shell and network-CLI prompts (Windows/PowerShell, Unix, Nokia MD-CLI,
/// Junos, Cisco IOS/IOS-XE/IOS-XR) from the terminal buffer to report location context and
/// working directory. Regexes carry a short match timeout because they run on untrusted
/// terminal output on the UI thread.
/// </summary>
internal sealed class PromptContextDetector
{
    internal static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(50);

    private static readonly Regex HistoryCompactPromptRe = new(
        @"^[^@\s:]{1,100}@[^\s:]{1,100}:([^\s$#%>]{1,512})[$#%]$",
        RegexOptions.CultureInvariant, RegexTimeout);
    internal static readonly Regex WindowsIdlePromptRe = new(
        @"^(?:PS )?((?:[A-Za-z]:[\\/]|\\\\)[^\r\n>]*)>\s*$",
        RegexOptions.CultureInvariant, RegexTimeout);
    // PowerShell's default prompt in a provider other than the file system ("PS HKLM:\>"):
    // a location but not a folder, reported so an older folder report is not taken for it.
    private static readonly Regex PowerShellProviderPromptRe = new(
        @"^PS ([A-Za-z][A-Za-z0-9_.-]+:[\\/]?[^\r\n>]*)>\s*$",
        RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex UnixSpacedIdlePromptRe = new(
        @"^[^@\s$#%>]{1,100}@[^\s$#%>]{1,100}\s+([^\r\n$#%>]{1,160}?)\s*[$#%>]\s*$",
        RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex UnixBracketedIdlePromptRe = new(
        @"^\[[^@\]\s]{1,100}@[^\]\s]{1,100}\s+([^\]\r\n]{1,160}?)\]\s*[$#%>]\s*$",
        RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex NokiaMdCliPromptRe = new(
        @"^[A-Z]:[^@\s]+@[^\s#]+#\s*$",
        RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex NokiaMdCliContextRe = new(
        @"^(\*)?(?:\((gl|ex|pr|ro)\)\s*)?\[((?:(gl|ex|pr|ro):)?[^\]\r\n]{0,500})\]$",
        RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Dictionary<string, string> NokiaMdCliModes = new()
    {
        ["gl"] = "global", ["ex"] = "exclusive", ["pr"] = "private", ["ro"] = "read-only",
    };
    private static readonly Regex JunosPromptRe = new(
        @"^[^@\s]+@[^\s#>]+([#>])\s*$",
        RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex JunosEditContextRe = new(
        @"^(?:\{([^}\r\n]{1,100})\}\s*)?\[edit(?:\s+([^\]\r\n]{1,400}))?\]\s*$",
        RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex JunosRoleRe = new(
        @"^\{((?:master|backup|primary|secondary)(?::[^}\s]+)?)\}\s*$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, RegexTimeout);
    private static readonly Regex JunosBannerRe = new(
        @"^---\s*JUNOS\b",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, RegexTimeout);
    private static readonly Regex CiscoIosPromptRe = new(
        @"^([A-Za-z0-9][A-Za-z0-9._-]{0,100})(?:\(([^()\r\n]{1,100})\))?([#>])\s*$",
        RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex CiscoXrPromptRe = new(
        @"^(RP/\d+/(?:RP|RSP)\d+/CPU\d+):([^()\s#>]+)(?:\(([^()\r\n]{1,100})\))?([#>])\s*$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, RegexTimeout);
    private static readonly Regex CiscoBannerRe = new(
        @"\b(?:Cisco IOS(?: XE| XR)? Software|Cisco Internetwork Operating System Software)\b",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, RegexTimeout);
    private static readonly Dictionary<string, string> CiscoSubmodeNames = new()
    {
        ["if"] = "interface", ["if-pre"] = "preconfigured interface", ["subif"] = "subinterface",
        ["router"] = "routing", ["line"] = "line", ["vlan"] = "VLAN", ["vrf"] = "VRF",
        ["std-nacl"] = "standard ACL", ["ext-nacl"] = "extended ACL", ["bgp"] = "BGP",
        ["bgp-af"] = "BGP address family", ["bgp-nbr"] = "BGP neighbor",
        ["bgp-nbr-af"] = "BGP neighbor address family",
    };

    private static readonly Regex[] DirectoryPromptPatterns =
    [
        WindowsIdlePromptRe,
        UnixSpacedIdlePromptRe,
        UnixBracketedIdlePromptRe,
        HistoryCompactPromptRe,
    ];

    private readonly ICommandBuffer _buffer;

    // What the screen proved (a banner, a mode prompt) and what the host suggested (the
    // session's icon, the SSH banner). Kept apart so the hint can change or be withdrawn
    // without erasing evidence.
    private string? _promptPlatform;
    private string? _promptHint;
    private string? _lastPromptSignature;

    public PromptContextDetector(ICommandBuffer buffer)
    {
        _buffer = buffer;
    }

    public void ResetSignature() => _lastPromptSignature = null;

    /// <summary>Updates the platform hint from a saved icon or SSH banner; returns true when
    /// the hint changed and prompt context should be re-evaluated.</summary>
    public bool SetPlatformHint(string? platform)
    {
        var hint = platform is "cisco" or "juniper" or "nokia" ? platform : null;
        if (hint == _promptHint)
            return false;
        _promptHint = hint;
        return true;
    }

    public static bool IsKnownIdlePrompt(string text) =>
        SafeIsMatch(WindowsIdlePromptRe, text)
        || SafeIsMatch(UnixSpacedIdlePromptRe, text)
        || SafeIsMatch(UnixBracketedIdlePromptRe, text)
        || SafeIsMatch(CiscoIosPromptRe, text)
        || SafeIsMatch(CiscoXrPromptRe, text)
        || SafeIsMatch(JunosPromptRe, text)
        || SafeIsMatch(NokiaMdCliPromptRe, text);

    public static string? ExtractDirectory(string prompt)
    {
        foreach (var pattern in DirectoryPromptPatterns)
        {
            var match = SafeMatch(pattern, prompt);
            if (match.Success)
            {
                var value = match.Groups[1].Value.Trim();
                return value.Length > 1024 ? value[..1024] : value;
            }
        }
        return null;
    }

    /// <summary>Reads the current location label from a completed known prompt. The cursor
    /// line is part of the signature, so returning to the same context after a command still
    /// reports that the command ended.</summary>
    public ((string Context, string? Platform) Value, bool Changed)? Detect(bool force)
    {
        var row = _buffer.CursorLine;
        var start = row;
        while (start > 0 && _buffer.IsWrapped(start))
            start--;
        var promptText = _buffer.LineText(start);
        if (promptText is null)
            return null;
        for (var next = start + 1; next <= row && promptText.Length < 512; next++)
        {
            var text = _buffer.LineText(next);
            if (text is null || !_buffer.IsWrapped(next))
                break;
            promptText += text;
        }

        string? label = null, platform = null;
        Match match;
        if ((match = SafeMatch(WindowsIdlePromptRe, promptText)).Success)
        {
            label = match.Groups[1].Value;
        }
        else if ((match = SafeMatch(PowerShellProviderPromptRe, promptText)).Success)
        {
            label = match.Groups[1].Value;
        }
        else if ((match = SafeMatch(UnixBracketedIdlePromptRe, promptText)).Success)
        {
            label = match.Groups[1].Value.Trim();
        }
        else if ((match = SafeMatch(UnixSpacedIdlePromptRe, promptText)).Success)
        {
            label = match.Groups[1].Value.Trim();
        }
        else if (SafeIsMatch(NokiaMdCliPromptRe, promptText) && start > 0)
        {
            var contextText = !_buffer.IsWrapped(start - 1) ? _buffer.LineText(start - 1) ?? "" : "";
            var context = SafeMatch(NokiaMdCliContextRe, contextText);
            if (context.Success)
            {
                var modeKey = context.Groups[4].Success ? context.Groups[4].Value : context.Groups[2].Value;
                var path = context.Groups[3].Value;
                if (context.Groups[4].Success)
                    path = path[(context.Groups[4].Value.Length + 1)..];
                label = path.Length > 0 ? path : "MD-CLI";
                if (modeKey.Length > 0)
                    label = NokiaMdCliModes[modeKey] + (context.Groups[1].Success ? "*" : "") + " · " + label;
                platform = "nokia";
                _promptPlatform = platform;
            }
        }
        else
        {
            var junos = SafeMatch(JunosPromptRe, promptText);
            if (junos.Success)
            {
                var previousText = start > 0 && !_buffer.IsWrapped(start - 1) ? _buffer.LineText(start - 1) ?? "" : "";
                var edit = SafeMatch(JunosEditContextRe, previousText);
                if (junos.Groups[1].Value == "#" && edit.Success)
                {
                    var hierarchy = edit.Groups[2].Success && edit.Groups[2].Length > 0 ? "/" + edit.Groups[2].Value : "/";
                    label = (edit.Groups[1].Success && edit.Groups[1].Length > 0 ? edit.Groups[1].Value + " · " : "")
                        + "configure · " + hierarchy;
                    platform = "juniper";
                    _promptPlatform = platform;
                }
                else if (junos.Groups[1].Value == ">")
                {
                    var role = SafeMatch(JunosRoleRe, previousText);
                    var seen = _promptPlatform == "juniper" || role.Success || RecentLineMatches(start, JunosBannerRe, 40);
                    if (seen || _promptHint == "juniper")
                    {
                        label = (role.Success ? role.Groups[1].Value + " · " : "") + "operational";
                        platform = "juniper";
                        if (seen)
                            _promptPlatform = platform; // only screen evidence outlives the hint
                    }
                }
            }
            if (label is null)
            {
                var xr = SafeMatch(CiscoXrPromptRe, promptText);
                var ios = xr.Success ? Match.Empty : SafeMatch(CiscoIosPromptRe, promptText);
                if (xr.Success || ios.Success)
                {
                    var isXr = xr.Success;
                    var location = isXr ? xr.Groups[1].Value : "";
                    var mode = isXr ? xr.Groups[3].Value : ios.Groups[2].Value;
                    var terminator = isXr ? xr.Groups[4].Value : ios.Groups[3].Value;
                    var seen = isXr || _promptPlatform == "cisco" || RecentLineMatches(start, CiscoBannerRe, 60);
                    var known = seen || _promptHint == "cisco";
                    // A mode suffix is safe network-CLI context; a bare EXEC prompt needs Cisco
                    // evidence so "server#" stays an ordinary Unix root prompt.
                    if (mode.Length > 0 || known)
                    {
                        var parts = CiscoModeParts(mode, terminator, isXr);
                        if (location.Length > 0)
                            parts.Insert(0, location);
                        label = string.Join(" · ", parts);
                        if (known)
                            platform = "cisco";
                        if (seen)
                            _promptPlatform = platform; // only screen evidence outlives the hint
                    }
                }
            }
        }
        if (label is null)
            return null;
        var signature = row + ":" + platform + ":" + label;
        if (!force && signature == _lastPromptSignature)
            return ((label, platform), false);
        _lastPromptSignature = signature;
        return ((label, platform), true);
    }

    private bool RecentLineMatches(int start, Regex pattern, int limit)
    {
        var first = Math.Max(0, start - limit);
        var count = start - first;
        if (count <= 0)
            return false;
        _buffer.Prefetch(first, count);
        for (var scan = start - 1; scan >= first; scan--)
        {
            if (_buffer.LineText(scan) is { } text && SafeIsMatch(pattern, text))
                return true;
        }
        return false;
    }

    private static List<string> CiscoModeParts(string mode, string terminator, bool isXr)
    {
        if (mode.Length == 0)
            return [terminator == ">" ? "user EXEC" : isXr ? "EXEC" : "privileged EXEC"];
        if (mode == "admin")
            return ["administration"];
        if (mode == "admin-config")
            return ["administration", "configure"];
        if (mode.StartsWith("admin-config-", StringComparison.Ordinal))
        {
            var sub = mode[13..];
            return ["administration", "configure", CiscoSubmodeNames.GetValueOrDefault(sub) ?? sub.Replace('-', ' ')];
        }
        if (mode == "config")
            return ["configure"];
        if (mode.StartsWith("config-", StringComparison.Ordinal))
        {
            var sub = mode[7..];
            return ["configure", CiscoSubmodeNames.GetValueOrDefault(sub) ?? sub.Replace('-', ' ')];
        }
        return [mode.Replace('-', ' ')];
    }

    internal static Match SafeMatch(Regex pattern, string text)
    {
        try
        {
            return pattern.Match(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return Match.Empty;
        }
    }

    internal static bool SafeIsMatch(Regex pattern, string text)
    {
        try
        {
            return pattern.IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}
