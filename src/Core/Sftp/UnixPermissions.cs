namespace Sessions.Core.Sftp;

/// <summary>
/// Converts between the three-digit "octal-as-decimal" permission form SSH.NET uses
/// (e.g. 755 means rwxr-xr-x) and the ls-style display string.
/// </summary>
public static class UnixPermissions
{
    /// <summary>"drwxr-xr-x"-style string. <paramref name="mode"/> &lt; 0 renders as unknown.</summary>
    public static string Format(short mode, bool isDirectory, bool isSymlink)
    {
        var kind = isSymlink ? 'l' : isDirectory ? 'd' : '-';
        if (mode < 0)
            return kind + "---------";
        var digits = new[] { mode / 100 % 10, mode / 10 % 10, mode % 10 };
        var sb = new System.Text.StringBuilder(10);
        sb.Append(kind);
        foreach (var d in digits)
        {
            sb.Append((d & 4) != 0 ? 'r' : '-');
            sb.Append((d & 2) != 0 ? 'w' : '-');
            sb.Append((d & 1) != 0 ? 'x' : '-');
        }
        return sb.ToString();
    }

    /// <summary>Parses user input like "755" or "0644" into the SSH.NET mode form.</summary>
    public static bool TryParseOctal(string text, out short mode)
    {
        mode = 0;
        var input = text.Trim();
        if (input.Length == 0)
            return false;
        var trimmed = input.TrimStart('0');
        if (trimmed.Length == 0)
            trimmed = "0"; // "0" / "000" are valid (if unusual) modes
        if (trimmed.Length > 3)
            return false;
        foreach (var c in trimmed)
        {
            if (c is < '0' or > '7')
                return false;
            mode = (short)(mode * 10 + (c - '0'));
        }
        return true;
    }
}
