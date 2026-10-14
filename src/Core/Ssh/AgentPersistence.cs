using System.Text.Json;

namespace Resesh.Core.Ssh;

/// <summary>
/// The remote session agent (our build of zmx, see eng/build-agent.ps1) as the layer that
/// keeps a session alive, in tmux's place. One daemon per session holds the pty and
/// forwards bytes untouched; nothing here is a second terminal in the stream.
/// <para>
/// So far this is the listing only: what to run on the host to see its sessions, and how
/// to read the reply. Starting, attaching and getting the binary onto the host come next.
/// Session names and slots are the ones <see cref="TmuxPersistence"/> uses, and the result
/// is a <see cref="TmuxSessionInfo"/>, so <see cref="TmuxPersistence.PlanConnect"/> decides
/// the same way for both.
/// </para>
/// </summary>
public static class AgentPersistence
{
    /// <summary>
    /// Where the sessions' sockets live, under the home directory. Not the agent's default
    /// under /run/user: systemd removes that directory when the user's last login ends,
    /// which would leave running sessions that nothing can reach.
    /// </summary>
    public const string SocketDirectory = "$HOME/.local/share/resesh/sock";

    /// <summary>Lists every session as one JSON object per line. Prints nothing when there
    /// are none. <paramref name="agentPath"/> is used as given, so quote it if needed.</summary>
    public static string ListCommand(string agentPath) => $"{Agent(agentPath)} list --json";

    /// <summary>The agent with its environment. <c>env</c> rather than a shell assignment,
    /// so the same text works typed into any login shell. The detach key is off: Ctrl+\
    /// belongs to the program in the session, and the tab is how this app detaches.</summary>
    private static string Agent(string agentPath) =>
        $"env ZMX_DIR=\"{SocketDirectory}\" ZMX_NO_DETACH_KEY=1 {agentPath}";

    /// <summary>
    /// Typed into the login shell to start a session, or attach to it if it is already
    /// running. The leading space and the screen wipe are as in
    /// <see cref="TmuxPersistence.BootstrapCommand"/>. There is no replay here: on a
    /// reattach the agent sends the screen itself.
    /// </summary>
    public static string BootstrapCommand(string agentPath, Guid id, int slot,
        string? newShellCommand = null, string? fallbackCommand = null)
    {
        var name = TmuxPersistence.SessionName(id, slot);
        var exec = fallbackCommand is null ? "exec " : "";
        return " printf '\\033[2J\\033[3J\\033[H'; "
            + $"{exec}{Agent(agentPath)} attach {name}"
            + (newShellCommand is not null ? " sh -c " + ShellIntegration.RemoteShellIntegration.QuotePosix(newShellCommand) : "")
            + (fallbackCommand is not null
                ? "; resesh_result=$?; if test \"$resesh_result\" -eq 0; then exit 0; fi; "
                  + "printf '\\n[resesh] Persistent startup failed; opening a non-persistent shell.\\n'; exec " + fallbackCommand
                : "")
            + " # resesh-agent-bootstrap";
    }

    /// <summary>Explicit resume never recreates a shell that ended after discovery: attach
    /// would start a new one under the same name, so the name is checked first.</summary>
    public static string ResumeCommand(string agentPath, Guid id, int slot)
    {
        var name = TmuxPersistence.SessionName(id, slot);
        return " printf '\\033[2J\\033[3J\\033[H'; "
            + $"if {Agent(agentPath)} list --short | grep -qx {name}; then exec {Agent(agentPath)} attach {name}; "
            + "else printf '[resesh] That session has ended.\\n'; exit 1; fi";
    }

    public static string KillCommand(string agentPath, Guid id, int slot) =>
        $"{Agent(agentPath)} kill {TmuxPersistence.SessionName(id, slot)}";

    /// <summary>Each kill is its own command, so one vanished session cannot stop the rest.</summary>
    public static string KillCommand(string agentPath, Guid id, IEnumerable<int> slots) =>
        string.Join("; ", slots.Distinct().Order().Select(slot => KillCommand(agentPath, id, slot)));

    /// <summary>Reads the sessions that belong to one saved profile from
    /// <see cref="ListCommand"/> output. Lines that are not ours, did not answer, or do not
    /// parse are skipped.</summary>
    public static IReadOnlyList<TmuxSessionInfo> ParseSessions(string output, Guid id)
    {
        var primaryName = TmuxPersistence.SessionName(id, 0);
        var sessions = new Dictionary<int, TmuxSessionInfo>();
        foreach (var line in output.Split('\n'))
        {
            if (line.AsSpan().Trim().IsEmpty)
                continue;
            try
            {
                using var document = JsonDocument.Parse(line);
                var session = document.RootElement;
                if (session.ValueKind != JsonValueKind.Object
                    || session.TryGetProperty("error", out _)
                    || Text(session, "name") is not { } name
                    || !TmuxPersistence.TryParseSlot(name, primaryName, out var slot)
                    || name != TmuxPersistence.SessionName(id, slot)
                    || !session.TryGetProperty("clients", out var clientsValue)
                    || !clientsValue.TryGetInt32(out var clients) || clients < 0)
                    continue;

                DateTimeOffset? created = null;
                if (session.TryGetProperty("created", out var createdValue)
                    && createdValue.TryGetInt64(out var seconds) && seconds is > 0 and <= 253402300799)
                    created = DateTimeOffset.FromUnixTimeSeconds(seconds);

                sessions[slot] = new TmuxSessionInfo(slot, name,
                    Text(session, "cwd") ?? PathFromUrl(Text(session, "pwd")) ?? "",
                    clients, created, Text(session, "fg") ?? "");
            }
            catch (JsonException)
            {
            }
        }
        return sessions.Values.OrderBy(session => session.Slot).ToList();
    }

    private static string? Text(JsonElement session, string name) =>
        session.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>The path of a file:// URL, as a program reports its directory (OSC 7). The
    /// fallback for a daemon that does not report the directory itself.</summary>
    private static string? PathFromUrl(string? url)
    {
        if (url is null || !url.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            return null;
        var slash = url.IndexOf('/', "file://".Length);
        if (slash < 0)
            return null;
        try
        {
            return Uri.UnescapeDataString(url[slash..]);
        }
        catch (UriFormatException)
        {
            return null;
        }
    }
}
