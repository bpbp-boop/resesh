using Sessions.Core.Ssh;

namespace Sessions.Core.Tests;

/// <summary>
/// set-titles is off by default in tmux, so a bootstrap that forgets it produces panes that
/// never report what they are running — silently, and identically to a host that simply has
/// no titles. Both branches must set it: attach re-asserts for servers an older build made.
/// </summary>
public sealed class TmuxTitleReportingTests
{
    private static readonly Guid Id = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Bootstrap_EnablesTitleReporting_InBothBranches(int slot)
    {
        var command = TmuxPersistence.BootstrapCommand(Id, slot);

        // Once for the attach branch, once for the new-session branch.
        Assert.Equal(2, Occurrences(command, "set -g set-titles on"));
        Assert.Equal(2, Occurrences(command, "set -g set-titles-string"));
    }

    [Fact]
    public void Bootstrap_PrefersPaneTitleOverCommand()
    {
        var command = TmuxPersistence.BootstrapCommand(Id, 0);

        // The command is the comm of the foreground process, so an interpreted tool reports
        // as "node" or "python3"; the pane title is what the program calls itself. Fall back
        // to the command only when the pane title is still tmux's hostname default.
        Assert.Contains(
            "'#{?#{==:#{pane_title},#{host}},#{pane_current_command},#{pane_title}}'",
            command);
    }

    [Fact]
    public void Bootstrap_SeparatesTitleOptionsAsTmuxCommands()
    {
        var command = TmuxPersistence.BootstrapCommand(Id, 0);

        // Backslash-semicolon: the shell must pass the ";" through to tmux as its own
        // command separator instead of ending the shell command.
        Assert.Contains(@"set -g set-titles on \; set -g set-titles-string", command);
    }

    private static int Occurrences(string haystack, string needle)
    {
        var count = 0;
        var at = 0;
        while ((at = haystack.IndexOf(needle, at, StringComparison.Ordinal)) >= 0)
        {
            count++;
            at += needle.Length;
        }
        return count;
    }
}
