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
    [InlineData("pi", "pi")]
    [InlineData("oh-my-pi", "omp")]
    [InlineData("omp", "omp")]
    [InlineData("/root/.local/bin/omp --continue", "omp")]
    [InlineData("grok", "grok")]
    [InlineData("opencode", "opencode")]
    [InlineData("npx opencode-ai", "opencode")]
    [InlineData("aider --model gpt", "aider")]
    [InlineData("copilot --resume=abc", "copilot")]
    [InlineData("npx @github/copilot", "copilot")]
    [InlineData("kimi --session 1", "kimi")]
    [InlineData("qwen --resume 1", "qwen")]
    [InlineData("npx @qwen-code/qwen-code", "qwen")]
    [InlineData("agy --conversation 1", "antigravity")]
    [InlineData("kiro-cli chat", "kiro")]
    [InlineData("C:\\tools\\devin.exe", "devin")]
    [InlineData("qodercli --resume 1", "qoder")]
    [InlineData("mastracode --thread 1", "mastracode")]
    [InlineData("cursor-agent", "cursor")]
    [InlineData("droid", "droid")]
    [InlineData("letta", "letta")]
    public void RecognizesAgentCommands(string command, string expected) =>
        Assert.Equal(expected, AgentDetection.FromCommand(command));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ls -la")]
    [InlineData("git commit -m 'claude helped'")]   // the word appears, but not as the command
    [InlineData("vim ~/claude/notes.md")]
    [InlineData("sudo systemctl restart nginx")]
    [InlineData("antigravity .")]                   // IDE launchers, not the CLI agents
    [InlineData("kiro .")]
    public void PlainCommandsAreNotAgents(string command) =>
        Assert.Null(AgentDetection.FromCommand(command));

    [Theory]
    [InlineData("Claude Code", "claude")]
    [InlineData("✳ claude — building", "claude")]
    [InlineData("codex", "codex")]
    [InlineData("bpg@host | gemini", "gemini")]
    [InlineData("π > tmp", "omp")]                   // oh-my-pi 18's default title
    [InlineData("π >", "omp")]                       // ...before it knows the directory
    [InlineData("π - resesh", "pi")]
    [InlineData("π", "pi")]
    public void RecognizesAgentTitles(string title, string expected) =>
        Assert.Equal(expected, AgentDetection.FromTitle(title));

    [Fact]
    public void RecognizesCodexDefaultAnimatedTitles()
    {
        foreach (var frame in "⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏")
            Assert.Equal("codex", AgentDetection.FromTitle($"{frame} resesh"));

        Assert.Null(AgentDetection.FromTitle("build ⠋ resesh"));
        Assert.Null(AgentDetection.FromTitle("⠋resesh"));
    }

    [Theory]
    [InlineData("bpg@host: ~/src/claude")]           // a directory, not a running agent
    [InlineData("C:\\tools\\gemini")]
    [InlineData("~/claude")]
    [InlineData("πr² notes")]                        // π as part of a word, not Pi's mark
    [InlineData("bash")]
    [InlineData("")]
    public void PathShapedTitlesAreNotAgents(string title) =>
        Assert.Null(AgentDetection.FromTitle(title));

    [Theory]
    [InlineData("bash")]
    [InlineData("-zsh")]
    [InlineData("fish")]
    [InlineData("root@rct-keep:/srv/rct-keep")]
    [InlineData("bpg@host: ~/src")]
    public void ShellTitlesAreExitSignals(string title) =>
        Assert.True(AgentDetection.IsShellTitle(title));

    [Theory]
    [InlineData("root@rct-keep:/srv/rct-keep", "/srv/rct-keep")]
    [InlineData("bpg@host: ~/src", "~/src")]
    public void ExtractsDirectoryFromShellPromptTitle(string title, string expected)
    {
        Assert.True(AgentDetection.TryGetShellPromptDirectory(title, out var directory));
        Assert.Equal(expected, directory);
    }

    [Theory]
    [InlineData("bash — server")]
    [InlineData("/usr/bin/bash")]
    [InlineData("my-bash")]
    public void ShellWordsInsideOtherTitlesAreNotExitSignals(string title) =>
        Assert.False(AgentDetection.IsShellTitle(title));

    [Fact]
    public void FindsTheAgentInAProcessList()
    {
        Assert.Equal("claude", AgentDetection.FromProcessNames(["conhost", "node", "claude"]));
        Assert.Equal("aider", AgentDetection.FromProcessNames(["powershell", "aider"]));
        Assert.Null(AgentDetection.FromProcessNames(["powershell", "conhost", "git"]));
        Assert.Null(AgentDetection.FromProcessNames(null));
    }

    [Fact]
    public void ProcessNamesIgnoreExtensionAndCase() =>
        Assert.Equal("claude", AgentDetection.FromProcessName("Claude.EXE"));

    [Fact]
    public void EveryAgentIdentityHasAnIcon()
    {
        var dir = FindAgentIconDirectory();
        foreach (var identity in AgentIdentities.All.Where(a => a.IsAgent))
        {
            var stem = Path.Combine(dir, identity.Key);
            Assert.True(File.Exists(stem + ".svg") || File.Exists(stem + ".png"), $"no icon for agent '{identity.Key}'");
        }
    }

    private static string FindAgentIconDirectory()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, "src", "App", "Assets", "AgentIcons");
            if (Directory.Exists(candidate))
                return candidate;
        }

        throw new DirectoryNotFoundException($"agent icon directory not found above {AppContext.BaseDirectory}");
    }
}
