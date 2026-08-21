namespace Resesh.Core.Sftp;

public enum Osc3008ContextAction
{
    Start,
    End,
}

/// <summary>A bounded, validated OSC 3008 context report.</summary>
public sealed record Osc3008Context(
    Osc3008ContextAction Action,
    string Id,
    string? Type,
    string? Hostname,
    string? WorkingDirectory,
    string? CommandLine,
    string? Exit,
    int? Status,
    string? Signal);

/// <summary>Parses the UAPI.15 OSC 3008 payload. Invalid and unknown metadata fields
/// are ignored as required by the specification; an invalid command or context ID
/// rejects the complete report.</summary>
public static class Osc3008ContextParser
{
    public const int MaxPayloadLength = 4096;
    private const int MaxTextLength = 255;

    private static readonly HashSet<string> ContextTypes =
    [
        "service", "session", "shell", "command", "vm", "container",
        "elevate", "chpriv", "subcontext", "remote", "boot", "app",
    ];

    private static readonly HashSet<string> ExitTypes =
        ["success", "failure", "crash", "interrupt"];

    public static bool TryParse(string? payload, out Osc3008Context? context)
    {
        context = null;
        if (string.IsNullOrEmpty(payload) || payload.Length > MaxPayloadLength || payload.Any(IsControl))
            return false;

        var fields = payload.Split(';');
        var command = fields[0];
        Osc3008ContextAction action;
        string encodedId;
        if (command.StartsWith("start=", StringComparison.Ordinal))
        {
            action = Osc3008ContextAction.Start;
            encodedId = command[6..];
        }
        else if (command.StartsWith("end=", StringComparison.Ordinal))
        {
            action = Osc3008ContextAction.End;
            encodedId = command[4..];
        }
        else
        {
            return false;
        }

        if (!TryDecode(encodedId, 64, allowEmpty: false, out var id) || id.Any(character => character > '~'))
            return false;

        string? type = null;
        string? hostname = null;
        string? cwd = null;
        string? commandLine = null;
        string? exit = null;
        int? status = null;
        string? signal = null;

        foreach (var field in fields.Skip(1))
        {
            var separator = field.IndexOf('=');
            if (separator <= 0)
                continue;

            var key = field[..separator];
            var value = field[(separator + 1)..];
            switch (key)
            {
                case "type" when action == Osc3008ContextAction.Start && ContextTypes.Contains(value):
                    type = value;
                    break;
                case "hostname" when action == Osc3008ContextAction.Start &&
                    TryDecode(value, MaxTextLength, allowEmpty: false, out var decodedHost) && ValidHost(decodedHost):
                    hostname = decodedHost;
                    break;
                case "cwd" when action == Osc3008ContextAction.Start &&
                    TryDecode(value, MaxTextLength, allowEmpty: false, out var decodedPath) && decodedPath.StartsWith('/'):
                    cwd = RemotePath.Normalize(decodedPath);
                    break;
                case "cmdline" when action == Osc3008ContextAction.Start &&
                    TryDecode(value, MaxTextLength, allowEmpty: true, out var decodedCommand):
                    commandLine = decodedCommand;
                    break;
                case "exit" when action == Osc3008ContextAction.End && ExitTypes.Contains(value):
                    exit = value;
                    break;
                case "status" when action == Osc3008ContextAction.End &&
                    value.Length > 0 && value.All(char.IsAsciiDigit) &&
                    int.TryParse(value, out var decodedStatus) && decodedStatus is >= 0 and <= 255:
                    status = decodedStatus;
                    break;
                case "signal" when action == Osc3008ContextAction.End &&
                    value.Length is > 3 and <= 32 && value.StartsWith("SIG", StringComparison.Ordinal) &&
                    value.All(character => character is >= 'A' and <= 'Z'):
                    signal = value;
                    break;
            }
        }

        context = new(action, id, type, hostname, cwd, commandLine, exit, status, signal);
        return true;
    }

    private static bool TryDecode(string value, int maximumLength, bool allowEmpty, out string decoded)
    {
        decoded = "";
        if (value.Length > maximumLength * 4 || (!allowEmpty && value.Length == 0))
            return false;

        var builder = new System.Text.StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character != '\\')
            {
                builder.Append(character);
                continue;
            }

            if (index + 3 >= value.Length || value[index + 1] != 'x')
                return false;
            var escape = value.Substring(index, 4);
            if (escape == "\\x3b")
                builder.Append(';');
            else if (escape == "\\x5c")
                builder.Append('\\');
            else
                return false;
            index += 3;
        }

        decoded = builder.ToString();
        return decoded.Length <= maximumLength && (allowEmpty || decoded.Length > 0) && !decoded.Any(IsControl);
    }

    private static bool ValidHost(string host) =>
        !host.Any(character => character is '@' or '/' or '\\');

    private static bool IsControl(char character) => character < ' ' || character == '\u007f';
}
