using System.Text;

namespace Sessions.Core.Agents;

/// <summary>Where a piece of agent evidence came from, weakest to strongest.</summary>
public enum AgentSource
{
    /// <summary>Nothing has been observed yet.</summary>
    Unknown,

    /// <summary>A bell or generic notification — an attention hint, never an identity.</summary>
    Notification,

    /// <summary>The terminal title looked like an agent's.</summary>
    Title,

    /// <summary>A command run at a shell prompt.</summary>
    Command,

    /// <summary>A process running inside the tab's own job (local tabs).</summary>
    Process,

    /// <summary>An adapter reported a structured lifecycle event.</summary>
    Structured,

    /// <summary>The user said so (tab menu / session default).</summary>
    Manual,
}

/// <summary>One normalized agent event: whatever the source, this is all the rest of the
/// app sees. <paramref name="Label"/> is short, sanitized, non-sensitive display text.</summary>
public sealed record AgentEvent(
    string? Key,
    AgentAttention Attention,
    string? Label,
    AgentSource Source,
    bool Ended = false);

/// <summary>
/// The escape-sequence side of agent awareness: the Sessions structured event, plus the
/// generic notification sequences we accept as low-confidence fallbacks.
///
/// Structured form (what adapters emit):
/// <code>ESC ] 7377 ; agent ; id=claude ; state=needs-approval ; label=Run%20tests ST</code>
/// Values are percent-encoded; unknown keys are ignored so the format can grow.
/// </summary>
public static class AgentOsc
{
    /// <summary>Sessions' own OSC code ("SESS" on a phone keypad). Chosen clear of the
    /// sequences already in the wild: 0/1/2 title, 7 cwd, 8 links, 9 notify, 52 clipboard,
    /// 133 shell integration, 633 VS Code, 777 rxvt notify, 1337 iTerm2.</summary>
    public const int SessionsCode = 7377;

    /// <summary>iTerm2/ConEmu-style notification (also carries ConEmu progress, which we ignore).</summary>
    public const int NotifyCode = 9;

    /// <summary>rxvt-unicode style "notify;title;body".</summary>
    public const int RxvtNotifyCode = 777;

    public const int MaxLabelLength = 80;

    /// <summary>Parses any OSC payload we care about. Returns null when the sequence is
    /// not ours, is malformed, or is a form we deliberately ignore.</summary>
    public static AgentEvent? Parse(int code, string? payload) => code switch
    {
        SessionsCode => ParseStructured(payload),
        NotifyCode => ParseNotify(payload),
        RxvtNotifyCode => ParseRxvtNotify(payload),
        _ => null,
    };

    /// <summary>The Sessions structured event: <c>agent;id=…;state=…;label=…</c>.</summary>
    public static AgentEvent? ParseStructured(string? payload)
    {
        if (string.IsNullOrEmpty(payload))
            return null;
        var parts = payload.Split(';');
        // Namespaced so 7377 can carry other Sessions subprotocols later.
        if (!parts[0].Trim().Equals("agent", StringComparison.OrdinalIgnoreCase))
            return null;

        string? key = null, label = null, state = null;
        for (var i = 1; i < parts.Length; i++)
        {
            var split = parts[i].IndexOf('=');
            if (split <= 0)
                continue;
            var name = parts[i][..split].Trim().ToLowerInvariant();
            var value = PercentDecode(parts[i][(split + 1)..]);
            switch (name)
            {
                case "id" or "agent":
                    key = value.Trim().ToLowerInvariant();
                    break;
                case "state" or "status":
                    state = value.Trim().ToLowerInvariant();
                    break;
                case "label" or "message":
                    label = AgentText.Sanitize(value, MaxLabelLength);
                    break;
            }
        }

        if (state is null && key is null)
            return null;
        if (state == "exit" || state == "ended")
            return new AgentEvent(key, AgentAttention.None, null, AgentSource.Structured, Ended: true);

        // An adapter may report only a state; the tracker keeps the identity it already has.
        // An unknown id still counts as an agent — we just can't name it.
        if (key is not null && key.Length > 0 && !AgentIdentities.IsAgentKey(key) && key != AgentIdentities.Shell)
            key = AgentIdentities.Generic;

        return new AgentEvent(key, ParseState(state), label, AgentSource.Structured);
    }

    /// <summary>OSC 9. Bare text is a notification; ConEmu's <c>9;4;…</c> progress form is
    /// ignored on purpose — PowerShell 7 emits it for ordinary progress bars, and treating
    /// that as an agent alert would light up every tab running a long cmdlet.</summary>
    public static AgentEvent? ParseNotify(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return null;
        var text = payload.Trim();
        if (text.StartsWith("4;", StringComparison.Ordinal) || text == "4")
            return null;
        return Signal(text);
    }

    /// <summary>OSC 777: <c>notify;title;body</c>.</summary>
    public static AgentEvent? ParseRxvtNotify(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return null;
        var parts = payload.Split(';');
        if (!parts[0].Trim().Equals("notify", StringComparison.OrdinalIgnoreCase))
            return null;
        var text = string.Join(" — ", parts.Skip(1).Where(p => !string.IsNullOrWhiteSpace(p)));
        return Signal(text);
    }

    /// <summary>A bell: attention with no information at all.</summary>
    public static AgentEvent Bell() =>
        new(null, AgentAttention.Signal, null, AgentSource.Notification);

    private static AgentEvent Signal(string text) =>
        new(null, AgentAttention.Signal, AgentText.Sanitize(text, MaxLabelLength), AgentSource.Notification);

    private static AgentAttention ParseState(string? state) => state switch
    {
        "working" or "busy" or "running" or "start" or "resume" => AgentAttention.Working,
        "needs-approval" or "needs_approval" or "approval" or "permission" => AgentAttention.NeedsApproval,
        "needs-answer" or "needs_answer" or "question" or "input" or "waiting" => AgentAttention.NeedsAnswer,
        "complete" or "completed" or "done" or "ok" or "success" => AgentAttention.Complete,
        "failed" or "error" or "fail" => AgentAttention.Failed,
        "idle" => AgentAttention.Idle,
        _ => AgentAttention.Idle,
    };

    private static string PercentDecode(string value)
    {
        if (value.IndexOf('%') < 0)
            return value;
        var bytes = new List<byte>(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '%' && i + 2 < value.Length
                && Uri.IsHexDigit(value[i + 1]) && Uri.IsHexDigit(value[i + 2]))
            {
                bytes.Add((byte)((Uri.FromHex(value[i + 1]) << 4) + Uri.FromHex(value[i + 2])));
                i += 2;
            }
            else
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(value[i].ToString()));
            }
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }
}

/// <summary>Cleanup for text that came out of a terminal. Everything here is untrusted:
/// a remote host can put anything on the wire, so labels are stripped of control
/// characters, collapsed, and truncated before they reach any UI surface.</summary>
public static class AgentText
{
    public static string? Sanitize(string? text, int maxLength)
    {
        if (string.IsNullOrEmpty(text))
            return null;
        var builder = new StringBuilder(Math.Min(text.Length, maxLength + 1));
        var lastWasSpace = false;
        foreach (var ch in text)
        {
            // Control characters, line breaks and the private-use/format ranges agents like
            // to decorate output with all collapse to a single space.
            var isSpace = char.IsWhiteSpace(ch) || char.IsControl(ch);
            if (isSpace)
            {
                if (builder.Length > 0 && !lastWasSpace)
                    builder.Append(' ');
                lastWasSpace = true;
                continue;
            }
            if (char.GetUnicodeCategory(ch) is System.Globalization.UnicodeCategory.Format
                or System.Globalization.UnicodeCategory.PrivateUse
                or System.Globalization.UnicodeCategory.Surrogate)
            {
                continue;
            }
            builder.Append(ch);
            lastWasSpace = false;
            if (builder.Length > maxLength)
                break;
        }

        var result = builder.ToString().TrimEnd();
        if (result.Length == 0)
            return null;
        return result.Length > maxLength ? result[..maxLength].TrimEnd() + "…" : result;
    }
}
