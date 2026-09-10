using System.Security.Cryptography;
using System.Text;

namespace Resesh.Core.ShellIntegration;

/// <summary>Bundled scripts shared by local and SSH launchers.</summary>
public static class ShellIntegrationScripts
{
    public static IReadOnlyList<string> Files { get; } = ["bash/integration.bash", "pwsh/integration.ps1", "fish/vendor_conf.d/integration.fish", "zsh/.zshenv", "zsh/.zprofile", "zsh/.zshrc", "zsh/.zlogin", "LICENSE", "ATTRIBUTION.md"];

    public static string Read(string relativePath)
    {
        if (!Files.Contains(relativePath)) throw new ArgumentException("Unknown shell resource.", nameof(relativePath));
        var assembly = typeof(ShellIntegrationScripts).Assembly;
        var name = assembly.GetManifestResourceNames().Single(n => n.Replace('\\', '/') == "ShellIntegration/" + relativePath);
        using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!, Encoding.UTF8);
        return reader.ReadToEnd().Replace("\r\n", "\n");
    }

    public static string Materialize()
    {
        var contents = Files.ToDictionary(p => p, Read);
        var version = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Concat(contents.Values))))[..16];
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Resesh", "shell-integration", version);
        foreach (var (path, content) in contents)
        {
            var file = Path.Combine(root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            if (!File.Exists(file) || File.ReadAllText(file) != content)
            {
                // Several tabs/processes may start together. A shell must never source
                // another launch's partially written script.
                var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.WriteAllText(temporary, content, new UTF8Encoding(false));
                    File.Move(temporary, file, overwrite: true);
                }
                finally
                {
                    File.Delete(temporary);
                }
            }
        }
        return root;
    }
}
