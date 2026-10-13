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
    public static string ListCommand(string agentPath) =>
        $"ZMX_DIR=\"{SocketDirectory}\" {agentPath} list --json";

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
