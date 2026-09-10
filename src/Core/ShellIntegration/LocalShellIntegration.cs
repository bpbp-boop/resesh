using Resesh.Core.Models;

namespace Resesh.Core.ShellIntegration;

public sealed record LocalShellIntegrationPlan(IReadOnlyList<string> Arguments, IReadOnlyDictionary<string, string> Environment, string? Message);

public static class LocalShellIntegration
{
    public static LocalShellIntegrationPlan CreatePlan(string executable, IReadOnlyList<string> arguments,
        ShellIntegrationMode mode, IReadOnlyDictionary<string, string> environment, string? resourceDirectory = null)
    {
        LocalShellIntegrationPlan Skip(string? reason) => new(arguments, environment, reason);
        if (mode == ShellIntegrationMode.Disabled) return Skip(null);
        var name = Path.GetFileNameWithoutExtension(executable).ToLowerInvariant();
        var detected = name switch { "bash" => ShellIntegrationMode.Bash, "zsh" => ShellIntegrationMode.Zsh,
            "fish" => ShellIntegrationMode.Fish, "powershell" or "pwsh" => ShellIntegrationMode.PowerShell, _ => ShellIntegrationMode.Disabled };
        if (detected == ShellIntegrationMode.Disabled || (mode != ShellIntegrationMode.Automatic && mode != detected))
            return Skip("Shell integration skipped: this executable is unsupported or does not match the selected shell. WSL launchers are not supported.");
        var allowed = detected == ShellIntegrationMode.PowerShell
            ? new[] { "-nologo", "-noexit", "-noprofile" }
            : new[] { "-i", "--interactive", "-l", "--login", "-il", "-li", "--noprofile", "--norc" };
        var checkedArguments = arguments.ToList();
        if (detected == ShellIntegrationMode.PowerShell)
        {
            for (var i = checkedArguments.Count - 2; i >= 0; i--)
                if (checkedArguments[i].Equals("-ExecutionPolicy", StringComparison.OrdinalIgnoreCase) &&
                    new[] { "AllSigned", "Bypass", "Default", "RemoteSigned", "Restricted", "Undefined", "Unrestricted" }
                        .Contains(checkedArguments[i + 1], StringComparer.OrdinalIgnoreCase))
                    checkedArguments.RemoveRange(i, 2);
        }
        if (checkedArguments.Any(a => !allowed.Contains(a.ToLowerInvariant())) ||
            (detected != ShellIntegrationMode.Bash && arguments.Any(a => a is "--noprofile" or "--norc")))
            return Skip("Shell integration skipped: custom arguments or command/script launches are left unchanged.");
        string root;
        try { root = (resourceDirectory ?? ShellIntegrationScripts.Materialize()).Replace('\\', '/'); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { return Skip("Shell integration skipped: bundled scripts could not be prepared. " + e.Message); }
        var env = new Dictionary<string, string>(environment, StringComparer.OrdinalIgnoreCase);
        var args = arguments.ToList();
        switch (detected)
        {
            case ShellIntegrationMode.PowerShell:
                if (!args.Any(a => a.Equals("-NoExit", StringComparison.OrdinalIgnoreCase))) args.Add("-NoExit");
                args.AddRange(["-Command", PowerShellIntegrationLaunch.Command(root + "/pwsh/integration.ps1")]);
                break;
            case ShellIntegrationMode.Bash:
                if (env.TryGetValue("ENV", out var originalEnv)) env["RESESH_SHELL_BASH_ENV"] = originalEnv;
                env["ENV"] = root + "/bash/integration.bash";
                env["RESESH_SHELL_BASH_INJECT"] = string.Join(' ', arguments.Select(a => a switch {
                    "-l" or "--login" or "-il" or "-li" => "login", "--noprofile" => "noprofile", "--norc" => "norc", _ => "" }).Where(a => a.Length > 0));
                args = ["--posix", "-i"];
                if (arguments.Any(a => a is "-l" or "--login" or "-il" or "-li")) args = ["--posix", "--login", "-i"];
                break;
            case ShellIntegrationMode.Zsh:
                if (env.TryGetValue("ZDOTDIR", out var zdotdir)) env["RESESH_SHELL_ZDOTDIR"] = zdotdir;
                env["ZDOTDIR"] = root + "/zsh";
                env["RESESH_SHELL_RESOURCES"] = root;
                args.Add("-i");
                break;
            case ShellIntegrationMode.Fish:
                args.AddRange(["-i", "--init-command", "source '" + root.Replace("'", "\\'") + "/fish/vendor_conf.d/integration.fish'"]);
                break;
        }
        return new(args, env, null);
    }
}
