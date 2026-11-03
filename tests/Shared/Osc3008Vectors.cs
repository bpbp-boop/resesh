namespace Resesh.Tests.Shared;

/// <summary>OSC 3008 payloads with the fields both parsers read: Core's
/// <c>Osc3008ContextParser</c> and the terminal's <c>CommandTracker.ParseOsc3008</c>.
/// The terminal library cannot reference Core, so each test project checks its own parser
/// against this one list (compiled into both) to keep them agreeing.</summary>
public static class Osc3008Vectors
{
    /// <summary>Payload; whether it parses; then action, id, type, exit and status when it does.</summary>
    public static TheoryData<string, bool, string?, string?, string?, string?, int?> All => new()
    {
        { "start=cmd-1;type=command", true, "start", "cmd-1", "command", null, null },
        { "start=cmd-1;type=bogus", true, "start", "cmd-1", null, null, null },
        { "end=cmd-1;exit=failure;status=127", true, "end", "cmd-1", null, "failure", 127 },
        { "end=cmd-1;exit=success", true, "end", "cmd-1", null, "success", null },
        // type belongs to start, exit and status to end.
        { "end=cmd-1;type=command;exit=success", true, "end", "cmd-1", null, "success", null },
        { "start=cmd-1;type=command;exit=failure;status=1", true, "start", "cmd-1", "command", null, null },
        // status is a decimal 0-255, however it is padded.
        { "end=cmd-1;status=256", true, "end", "cmd-1", null, null, null },
        { "end=cmd-1;status=-1", true, "end", "cmd-1", null, null, null },
        { "end=cmd-1;status=" + new string('0', 30) + "7", true, "end", "cmd-1", null, null, 7 },
        { "end=cmd-1;status=99999999999999999999999", true, "end", "cmd-1", null, null, null },
        // IDs: the two escapes only, printable ASCII, 1-64 characters.
        { @"start=a\x3bb\x5cc;type=command", true, "start", @"a;b\c", "command", null, null },
        { @"start=a\x41;type=command", false, null, null, null, null, null },
        { "start=;type=command", false, null, null, null, null, null },
        { "start=" + new string('a', 64), true, "start", new string('a', 64), null, null, null },
        { "start=" + new string('a', 65), false, null, null, null, null, null },
        { "start=café", false, null, null, null, null, null },
        { "begin=cmd-1", false, null, null, null, null, null },
        // Control characters anywhere reject the report: C0, DEL and C1 (U+009B is CSI).
        { "start=cmd-1;type=command\u0007", false, null, null, null, null, null },
        { "start=cmd-1;cmdline=a\u007fb", false, null, null, null, null, null },
        { "start=cmd-1;cmdline=a\u009b31m", false, null, null, null, null, null },
        { "start=cmd-1;cmdline=a\u0085b", false, null, null, null, null, null },
        { "start=cmd-1;" + new string('x', 4096), false, null, null, null, null, null },
    };
}
