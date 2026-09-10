using System.Security.Cryptography;
using System.Text;
using Resesh.Core.Models;

namespace Resesh.Core.ShellIntegration;

/// <summary>Remote startup is selected explicitly. No command is generated for Disabled,
/// Automatic, or an unrecognized value; an SSH banner never enables probing.</summary>
internal static class RemoteShellIntegration
{
    public const string ReadyPrefix = "RESESH_SHELL_READY=";
    public static bool IsSupported(ShellIntegrationMode mode) => mode is
        ShellIntegrationMode.Bash or ShellIntegrationMode.Zsh or ShellIntegrationMode.Fish or ShellIntegrationMode.PowerShell;

    public static string[] ResourceFiles(ShellIntegrationMode mode) => mode switch
    {
        ShellIntegrationMode.Bash => ["bash/integration.bash"],
        ShellIntegrationMode.Zsh => ["zsh/.zshenv", "zsh/.zprofile", "zsh/.zshrc", "zsh/.zlogin"],
        ShellIntegrationMode.Fish => ["fish/vendor_conf.d/integration.fish"],
        ShellIntegrationMode.PowerShell => ["pwsh/integration.ps1"],
        _ => [],
    };

    public static string ShellName(ShellIntegrationMode mode) => mode switch
    {
        ShellIntegrationMode.Bash => "bash",
        ShellIntegrationMode.Zsh => "zsh",
        ShellIntegrationMode.Fish => "fish",
        ShellIntegrationMode.PowerShell => "powershell.exe",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    public static string QuotePosix(string text) => "'" + text.Replace("'", "'\"'\"'") + "'";
    // Fish interprets escaped backslashes even inside single-quoted strings.
    internal static string QuoteFish(string text) => "'" + text.Replace("\\", "\\\\").Replace("'", "\\'") + "'";
    private static string QuotePowerShell(string text) => "'" + text.Replace("'", "''") + "'";

    /// <summary>Code sent to a separate exec channel's stdin, never to the interactive PTY.
    /// Each installation has an exclusive directory; existing scripts are never overwritten.</summary>
    public static string BuildSetup(ShellIntegrationMode mode, string id)
    {
        if (!IsSupported(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (id.Length != 32 || id.Any(c => !Uri.IsHexDigit(c))) throw new ArgumentException("Invalid installation id.", nameof(id));
        var files = ResourceFiles(mode).Concat(["LICENSE", "ATTRIBUTION.md"]).ToDictionary(path => path, ShellIntegrationScripts.Read);
        var version = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", files.Values))))[..16].ToLowerInvariant();
        if (mode == ShellIntegrationMode.PowerShell)
        {
            var body = new StringBuilder("$ErrorActionPreference='Stop'\n");
            body.AppendLine("if ($env:OS -ne 'Windows_NT') { throw 'Windows PowerShell was selected for a non-Windows account.' }");
            body.AppendLine("$root=[Environment]::GetFolderPath('LocalApplicationData')");
            body.AppendLine("if (-not [IO.Path]::IsPathRooted($root)) { throw 'No user cache directory.' }");
            body.AppendLine("foreach ($part in @('Resesh','shell-integration')) { $root=Join-Path $root $part; if (Test-Path -LiteralPath $root) { if ((Get-Item -LiteralPath $root).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Cache is a link.' } }; [IO.Directory]::CreateDirectory($root) | Out-Null }");
            body.AppendLine($"$root=Join-Path $root '{version}-{id}'");
            body.AppendLine("if (Test-Path -LiteralPath $root) { throw 'Installation already exists.' }; [IO.Directory]::CreateDirectory($root) | Out-Null");
            foreach (var (file, content) in files)
            {
                var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(content));
                body.AppendLine($"$file=Join-Path $root {QuotePowerShell(file.Replace('/', '\\'))}; [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($file)) | Out-Null");
                body.AppendLine($"[IO.File]::WriteAllBytes($file,[Convert]::FromBase64String('{encoded}'))");
            }
            body.AppendLine($"[Console]::WriteLine('{ReadyPrefix}'+$root)");
            return body.ToString();
        }

        var script = new StringBuilder("set -eu\numask 077\n");
        script.AppendLine($"command -v {ShellName(mode)} >/dev/null 2>&1 || {{ printf 'Selected shell {ShellName(mode)} was not found in PATH.\\n' >&2; exit 1; }}");
        script.AppendLine("test -n \"${HOME:-}\" && test -d \"$HOME\" || { printf 'The account home directory is unavailable.\\n' >&2; exit 1; }");
        script.AppendLine("root=\"$HOME\"\nfor part in .cache resesh shell-integration; do\n  root=\"$root/$part\"\n  test ! -L \"$root\" || { printf 'The script cache contains a symbolic link.\\n' >&2; exit 1; }\n  if test ! -d \"$root\"; then mkdir -m 700 \"$root\"; fi\n  test -O \"$root\" || { printf 'The script cache directory is not owned by this account.\\n' >&2; exit 1; }\ndone");
        script.AppendLine($"root=\"$root/{version}-{id}\"\nmkdir -m 700 \"$root\"");
        foreach (var directory in files.Keys.Select(Path.GetDirectoryName).Where(d => !string.IsNullOrEmpty(d)).Distinct())
            script.AppendLine($"mkdir -p \"$root/\"{QuotePosix(directory!.Replace('\\', '/'))}");
        foreach (var (file, content) in files)
        {
            var octal = string.Concat(Encoding.UTF8.GetBytes(content).Select(b => "\\" + Convert.ToString(b, 8).PadLeft(3, '0')));
            script.AppendLine($"printf {QuotePosix(octal)} > \"$root/\"{QuotePosix(file)}");
        }
        script.AppendLine($"printf '{ReadyPrefix}%s\\n' \"$root\"");
        // AppendLine uses the client's OS newline. A Windows CR after "done"
        // is part of the token to Unix sh, so the entire setup fails to parse.
        return script.ToString().Replace("\r\n", "\n");
    }

    internal static string DescribeSetupFailure(int? exitStatus, string standardError)
    {
        var reason = exitStatus switch
        {
            0 => "remote setup did not report a valid script directory",
            null => "remote setup did not report an exit status",
            _ => $"remote setup exited with code {exitStatus}",
        };
        // Server diagnostics are useful here, but must not be able to emit terminal
        // controls or flood the terminal. Do not include the uploaded script/stdout.
        var detail = Agents.AgentText.Sanitize(standardError, 240);
        return detail is null ? reason : $"{reason}: {detail}";
    }

    public static string? ParseDirectory(ShellIntegrationMode mode, string output)
    {
        var line = output.Split('\n').Select(l => l.TrimEnd('\r')).LastOrDefault(l => l.StartsWith(ReadyPrefix, StringComparison.Ordinal));
        var path = line?[ReadyPrefix.Length..];
        if (string.IsNullOrEmpty(path) || path.Length > 2048 || path.Any(char.IsControl)) return null;
        return mode == ShellIntegrationMode.PowerShell
            ? path.Length > 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\' ? path : null
            : path.StartsWith('/') ? path : null;
    }

    public static string LaunchCommand(ShellIntegrationMode mode, string directory, bool tmux = false)
    {
        if (!IsSupported(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (mode == ShellIntegrationMode.PowerShell)
        {
            if (tmux) throw new NotSupportedException("PowerShell integration is not available with tmux persistence.");
            var script = directory.TrimEnd('\\') + "\\pwsh\\integration.ps1";
            // A real file is dot-sourced after profiles. Execution policy is not changed.
            var command = PowerShellIntegrationLaunch.Command(script);
            return "powershell.exe -NoLogo -NoExit -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
        }
        var root = QuotePosix(directory);
        var environment = $"export RESESH_SHELL_INTEGRATION=1 RESESH_SHELL_RESOURCES={root}; ";
        if (tmux) environment += "export RESESH_SHELL_TMUX=1; ";
        var launch = mode switch
        {
            ShellIntegrationMode.Bash => "if test \"${ENV+x}\" = x; then export RESESH_SHELL_BASH_ENV=\"$ENV\"; fi; " +
                $"export RESESH_SHELL_BASH_INJECT=login ENV={QuotePosix(directory + "/bash/integration.bash")}; exec bash --posix --login -i",
            ShellIntegrationMode.Zsh => "export RESESH_SHELL_ZDOTDIR=\"${ZDOTDIR:-$HOME}\"; " +
                $"export ZDOTDIR={QuotePosix(directory + "/zsh")}; exec zsh -l -i",
            ShellIntegrationMode.Fish => $"exec fish -l -i --init-command {QuotePosix("source " + QuoteFish(directory + "/fish/vendor_conf.d/integration.fish"))}",
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
        return "sh -c " + QuotePosix(environment + launch);
    }
}
