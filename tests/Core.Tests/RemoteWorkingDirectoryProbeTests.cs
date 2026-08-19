using System.Text;
using Sessions.Core.Ssh;

namespace Sessions.Core.Tests;

public sealed class RemoteWorkingDirectoryProbeTests
{
    [Fact]
    public void Command_uses_the_same_sshds_interactive_pty_without_terminal_input()
    {
        Assert.Contains("/proc/$p/task/$p/children", RemoteWorkingDirectoryProbe.Command, StringComparison.Ordinal);
        Assert.Contains("/proc/$fg/cwd", RemoteWorkingDirectoryProbe.Command, StringComparison.Ordinal);
        Assert.Contains("/dev/pts/*", RemoteWorkingDirectoryProbe.Command, StringComparison.Ordinal);
        Assert.DoesNotContain("PROMPT_COMMAND", RemoteWorkingDirectoryProbe.Command, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/home/boden/work", "/home/boden/work")]
    [InlineData("/var/log/my app", "/var/log/my app")]
    public void Parse_accepts_an_absolute_base64_path(string path, string expected)
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(path));
        var result = RemoteWorkingDirectoryProbe.Parse($"banner\nsessions-cwd-v1:path:{encoded}\n");
        Assert.Equal(RemoteWorkingDirectoryProbeStatus.Path, result.Status);
        Assert.Equal(expected, result.Path);
    }

    [Fact]
    public void Parse_uses_the_last_marker()
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes("/srv/current"));
        var result = RemoteWorkingDirectoryProbe.Parse(
            $"sessions-cwd-v1:unavailable\nsessions-cwd-v1:path:{encoded}\n");
        Assert.Equal("/srv/current", result.Path);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sessions-cwd-v1:unavailable")]
    [InlineData("sessions-cwd-v1:path:not-base64")]
    [InlineData("sessions-cwd-v1:path:cmVsYXRpdmU=")]
    public void Parse_rejects_unavailable_or_invalid_results(string? output) =>
        Assert.Equal(RemoteWorkingDirectoryProbeStatus.Unavailable, RemoteWorkingDirectoryProbe.Parse(output).Status);

    [Fact]
    public void Parse_reports_a_foreground_program_instead_of_using_a_stale_shell_path()
    {
        var result = RemoteWorkingDirectoryProbe.Parse("sessions-cwd-v1:not-shell:ssh\n");
        Assert.Equal(RemoteWorkingDirectoryProbeStatus.NotAtShell, result.Status);
        Assert.Equal("ssh", result.Process);
        Assert.Null(result.Path);
    }
}
