using Resesh.Core.Backend;

namespace Resesh.Core.Tests;

/// <summary>
/// The subtitle's running-command name comes from a captured shell line, not a process
/// table — these pin down the guessing rules so they stay predictable: skip what merely
/// wraps the command, keep the first honest program name, and admit ignorance (null)
/// rather than display shell syntax.
/// </summary>
public sealed class CommandTitleTests
{
    [Fact]
    public void IsLocalExecutableTitle_MatchesExpandedPathWithoutCaseSensitivity()
    {
        var executable = @"%SystemRoot%\System32\cmd.exe";
        var title = Environment.ExpandEnvironmentVariables(executable).ToUpperInvariant();

        Assert.True(CommandTitle.IsLocalExecutableTitle(title, executable));
        Assert.True(CommandTitle.IsLocalExecutableTitle($"\"{title}\"", executable));
        Assert.False(CommandTitle.IsLocalExecutableTitle(@"C:\work", executable));
    }

    [Theory]
    [InlineData("htop", "htop")]
    [InlineData("  htop  -d 10 ", "htop")]
    [InlineData("/usr/bin/python3 script.py", "python3")]
    [InlineData("./deploy.sh prod", "deploy.sh")]
    [InlineData(@"\vim notes.txt", "vim")] // backslash alias bypass
    [InlineData("sudo tail -f /var/log/syslog", "tail")]
    [InlineData("env FOO=1 python3 x.py", "python3")]
    [InlineData("FOO=1 BAR=2 make all", "make")]
    [InlineData("time make -j8", "make")]
    [InlineData("show version", "show")] // network gear: first word is the best there is
    public void ProgramName_ExtractsTheProgram(string line, string expected) =>
        Assert.Equal(expected, CommandTitle.ProgramName(line));

    [Theory]
    [InlineData("htop;ls", "htop")]
    [InlineData("make&&echo done", "make")]
    [InlineData("sort<data.txt", "sort")]
    public void ProgramName_StopsAtShellOperators(string line, string expected) =>
        Assert.Equal(expected, CommandTitle.ProgramName(line));

    [Theory]
    [InlineData("sudo -u alice htop", "sudo")] // option arguments are not parsed
    [InlineData("time", "time")]
    [InlineData("sudo -i", "sudo")]
    public void ProgramName_KeepsTheWrapperWhenOptionsFollow(string line, string expected) =>
        Assert.Equal(expected, CommandTitle.ProgramName(line));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("FOO=1")]
    [InlineData("(cd /tmp && ls)")]
    public void ProgramName_ReturnsNullWhenNothingLegibleRuns(string? line) =>
        Assert.Null(CommandTitle.ProgramName(line));

    [Fact]
    public void ProgramName_CapsPathologicalLength()
    {
        var name = CommandTitle.ProgramName(new string('x', 500));
        Assert.Equal(48, name!.Length);
    }
}
