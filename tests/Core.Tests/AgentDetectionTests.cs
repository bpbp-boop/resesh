using Resesh.Core.Agents;

namespace Resesh.Core.Tests;

public class AgentDetectionTests
{
    [Theory]
    [InlineData("claude", "claude")]
    [InlineData("  claude --resume  ", "claude")]
    [InlineData("/usr/local/bin/claude", "claude")]
    [InlineData("C:\\Users\\b\\AppData\\Local\\claude.exe --continue", "claude")]
    [InlineData("sudo -E claude", "claude")]
    [InlineData("FOO=bar BAZ=1 claude", "claude")]
    [InlineData("npx @anthropic-ai/claude-code", "claude")]
    [InlineData("codex exec 'fix the build'", "codex")]
    [InlineData("gemini", "gemini")]
    [InlineData("oh-my-pi", "pi")]
    [InlineData("grok", "grok")]
    [InlineData("aider --model gpt", "agent")]
    public void RecognizesAgentCommands(string command, string expected) =>
        Assert.Equal(expected, AgentDetection.FromCommand(command));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ls -la")]
    [InlineData("git commit -m 'claude helped'")]   // the word appears, but not as the command
    [InlineData("vim ~/claude/notes.md")]
    [InlineData("sudo systemctl restart nginx")]
    public void PlainCommandsAreNotAgents(string command) =>
        Assert.Null(AgentDetection.FromCommand(command));

    [Theory]
    [InlineData("Claude Code", "claude")]
    [InlineData("✳ claude — building", "claude")]
    [InlineData("codex", "codex")]
    [InlineData("bpg@host: gemini", "gemini")]
    public void RecognizesAgentTitles(string title, string expected) =>
        Assert.Equal(expected, AgentDetection.FromTitle(title));

    [Theory]
    [InlineData("bpg@host: ~/src/claude")]           // a directory, not a running agent
    [InlineData("C:\\tools\\gemini")]
    [InlineData("~/claude")]
    [InlineData("bash")]
    [InlineData("")]
    public void PathShapedTitlesAreNotAgents(string title) =>
        Assert.Null(AgentDetection.FromTitle(title));

    [Theory]
    [InlineData("bash")]
    [InlineData("-zsh")]
    [InlineData("fish")]
    public void ExactShellTitlesAreExitSignals(string title) =>
        Assert.True(AgentDetection.IsShellTitle(title));

    [Theory]
    [InlineData("bash — server")]
    [InlineData("/usr/bin/bash")]
    [InlineData("my-bash")]
    public void ShellWordsInsideOtherTitlesAreNotExitSignals(string title) =>
        Assert.False(AgentDetection.IsShellTitle(title));

    [Fact]
    public void NamedAgentBeatsGenericInAProcessList()
    {
        Assert.Equal("claude", AgentDetection.FromProcessNames(["conhost", "aider", "claude"]));
        Assert.Equal("agent", AgentDetection.FromProcessNames(["powershell", "aider"]));
        Assert.Null(AgentDetection.FromProcessNames(["powershell", "conhost", "git"]));
        Assert.Null(AgentDetection.FromProcessNames(null));
    }

    [Fact]
    public void ProcessNamesIgnoreExtensionAndCase() =>
        Assert.Equal("claude", AgentDetection.FromProcessName("Claude.EXE"));
}
