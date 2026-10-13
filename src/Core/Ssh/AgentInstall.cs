using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Resesh.Core.Ssh;

/// <summary>Whether Resesh may put its session agent on a host. Kept per saved session.</summary>
public enum AgentInstallChoice
{
    /// <summary>Ask each time the agent is missing or out of date.</summary>
    Ask = 0,
    /// <summary>Upload without asking.</summary>
    Always = 1,
    /// <summary>Never upload; persistence falls back to tmux, then to a plain shell.</summary>
    Never = 2,
}

/// <summary>One agent binary the app carries, as listed in the manifest beside it.</summary>
public sealed record AgentBinary(string Path, string Sha256, long Size);

/// <summary>
/// The agent binaries shipped with the app (built by eng/build-agent.ps1), keyed by the
/// architecture names <see cref="AgentInstall.Architecture"/> returns.
/// </summary>
public sealed record AgentPackage(string Version, IReadOnlyDictionary<string, AgentBinary> Binaries)
{
    public const string ManifestName = "agent-manifest.json";

    /// <summary>Reads the manifest in <paramref name="directory"/>. Null when there is none
    /// or it does not hold up: a version or hash that is not plain enough to put in a shell
    /// command is refused here, so nothing later has to quote them.</summary>
    public static AgentPackage? Load(string directory)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(System.IO.Path.Combine(directory, ManifestName)));
            var root = document.RootElement;
            var version = root.GetProperty("version").GetString();
            if (version is null || !AgentInstall.IsPlainVersion(version))
                return null;
            var binaries = new Dictionary<string, AgentBinary>(StringComparer.Ordinal);
            foreach (var entry in root.GetProperty("binaries").EnumerateObject())
            {
                var sha = entry.Value.GetProperty("sha256").GetString();
                var size = entry.Value.GetProperty("size").GetInt64();
                var file = entry.Value.GetProperty("file").GetString();
                if (sha is null || !AgentInstall.IsSha256(sha) || size <= 0 || string.IsNullOrEmpty(file)
                    || file.Contains("..", StringComparison.Ordinal) || System.IO.Path.IsPathRooted(file))
                    return null;
                binaries[entry.Name] = new AgentBinary(System.IO.Path.Combine(directory, file), sha, size);
            }
            return new AgentPackage(version, binaries);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException
            or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    /// <summary>The bytes to upload, or null when the file is missing or is not the one the
    /// manifest describes.</summary>
    public static byte[]? ReadVerified(AgentBinary binary)
    {
        try
        {
            var bytes = File.ReadAllBytes(binary.Path);
            return bytes.LongLength == binary.Size
                && Convert.ToHexStringLower(SHA256.HashData(bytes)) == binary.Sha256
                ? bytes
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>What a host said about itself and about the agent it may already have.</summary>
/// <param name="Hash">SHA-256 of the installed agent; "" when there is none or the host
/// has no tool to hash with.</param>
/// <param name="Runs">The installed agent started and exited cleanly.</param>
public sealed record AgentProbe(string Os, string Machine, string Hash, bool Runs);

/// <summary>What to do about the agent on one host.</summary>
public abstract record AgentInstallPlan
{
    private AgentInstallPlan() { }

    /// <summary>The right agent is there and runs.</summary>
    public sealed record Ready(string RemotePath) : AgentInstallPlan;

    /// <summary>Upload this binary.</summary>
    public sealed record Upload(string Architecture, AgentBinary Binary) : AgentInstallPlan;

    /// <summary>Ask the user, then upload this binary if they agree.</summary>
    public sealed record Ask(string Architecture, AgentBinary Binary) : AgentInstallPlan;

    /// <summary>No agent on this host; <paramref name="Reason"/> says why, for the tab.</summary>
    public sealed record Unavailable(string Reason) : AgentInstallPlan;
}

public enum AgentInstallOutcome
{
    Installed,
    /// <summary>What arrived was not what was sent.</summary>
    Mismatch,
    /// <summary>The file arrived intact but the host would not run it: a home directory
    /// mounted noexec, or a libc-less corner we did not build for.</summary>
    CannotRun,
    Failed,
}

/// <summary>
/// Getting the session agent onto a host over the SSH connection that is already open: a
/// probe, a decision, and an upload that is checked before it replaces anything. The host
/// downloads nothing. Everything here is text; the caller runs it
/// (<see cref="SshTerminalSession.RunCommand(string, ReadOnlyMemory{byte}, TimeSpan)"/>).
/// <para>
/// The agent lives at <c>~/.local/share/resesh/&lt;version&gt;/resesh-agent</c>. The version
/// is in the path so that a new app never replaces the binary that sessions started by an
/// older app are still running from.
/// </para>
/// </summary>
public static partial class AgentInstall
{
    private const string ProbeMark = "resesh-agent-probe";
    private const string InstallMark = "resesh-agent-install";

    /// <summary>Hash with whichever tool the host has. Prints nothing when it has none.
    /// Inside <c>$( )</c> it needs a space after the opening: dash reads <c>$((</c> as
    /// the start of arithmetic.</summary>
    private const string HashOf =
        "(sha256sum \"$f\" || shasum -a 256 \"$f\" || openssl dgst -sha256 -r \"$f\") 2>/dev/null | cut -c1-64";

    public static bool IsPlainVersion(string version) => PlainVersion().IsMatch(version);

    public static bool IsSha256(string hash) => Sha256Hex().IsMatch(hash);

    /// <summary>The agent's directory for one version, for use inside double quotes.</summary>
    public static string RemoteDirectory(string version) =>
        IsPlainVersion(version)
            ? $"$HOME/.local/share/resesh/{version}"
            : throw new ArgumentException("Not a plain version.", nameof(version));

    /// <summary>The agent's path, already quoted for a POSIX shell command.</summary>
    public static string RemotePath(string version) => $"\"{RemoteDirectory(version)}/resesh-agent\"";

    /// <summary>The build that runs on a host, from its <c>uname -s</c> and <c>uname -m</c>.
    /// Null when there is none; only Linux is built so far.</summary>
    public static string? Architecture(string os, string machine) =>
        !os.Equals("Linux", StringComparison.Ordinal) ? null
        : machine is "x86_64" or "amd64" ? "x86_64"
        : machine is "aarch64" or "arm64" ? "aarch64"
        : null;

    /// <summary>A script for <c>sh -s</c> that prints one line for <see cref="ParseProbe"/>.
    /// It changes nothing on a host without the agent; where the agent is installed, the
    /// test run creates the socket directory it will use anyway.</summary>
    public static string ProbeScript(string version) =>
        $"f={RemotePath(version)}\n"
        + "h=; r=0\n"
        + "if test -f \"$f\"; then\n"
        + $"  h=$( {HashOf} )\n"
        + $"  if ZMX_DIR=\"{AgentPersistence.SocketDirectory}\" \"$f\" version >/dev/null 2>&1; then r=1; fi\n"
        + "fi\n"
        + $"printf '{ProbeMark} os=%s machine=%s hash=%s runs=%s\\n' \"$(uname -s)\" \"$(uname -m)\" \"$h\" \"$r\"\n";

    /// <summary>Reads the probe's line out of whatever else the host printed (a banner, a
    /// profile's chatter). Null when it is not there.</summary>
    public static AgentProbe? ParseProbe(string output)
    {
        foreach (var raw in output.Split('\n'))
        {
            var match = ProbeLine().Match(raw.TrimEnd('\r'));
            if (match.Success)
                return new AgentProbe(match.Groups[1].Value, match.Groups[2].Value,
                    match.Groups[3].Value, match.Groups[4].Value == "1");
        }
        return null;
    }

    public static AgentInstallPlan Plan(AgentProbe probe, AgentPackage package, AgentInstallChoice choice)
    {
        if (Architecture(probe.Os, probe.Machine) is not { } architecture
            || !package.Binaries.TryGetValue(architecture, out var binary))
            return new AgentInstallPlan.Unavailable($"there is no session agent for {probe.Os} {probe.Machine}");

        // A host with no hashing tool reports no hash. An agent that runs there is taken as
        // it is: the upload was checked by size when it was put there.
        if (probe.Runs && (probe.Hash == binary.Sha256 || probe.Hash.Length == 0))
            return new AgentInstallPlan.Ready(RemotePath(package.Version));

        return choice switch
        {
            AgentInstallChoice.Always => new AgentInstallPlan.Upload(architecture, binary),
            AgentInstallChoice.Never => new AgentInstallPlan.Unavailable("the session agent is not installed on this host"),
            _ => new AgentInstallPlan.Ask(architecture, binary),
        };
    }

    /// <summary>
    /// The command that receives the binary on its standard input. It writes to a private
    /// temporary file in the agent's own directory, checks the hash (or, on a host with no
    /// hashing tool, the size), runs it once, and only then moves it into place, so a
    /// dropped connection or a bad copy never leaves a half-written agent behind.
    /// <para>
    /// One line with no single quotes, so it travels as one single-quoted argument through
    /// whatever the account's login shell is.
    /// </para>
    /// </summary>
    public static string InstallCommand(string version, AgentBinary binary)
    {
        if (!IsSha256(binary.Sha256) || binary.Size <= 0)
            throw new ArgumentException("Not a plain hash and size.", nameof(binary));
        var script =
            $"set -e; umask 077; d=\"{RemoteDirectory(version)}\"; mkdir -p \"$d\"; f=\"$d/.resesh-agent.$$\"; "
            + "trap \"rm -f \\\"$f\\\"\" EXIT; cat > \"$f\"; "
            + $"h=$( {HashOf} ); "
            + $"if test -n \"$h\"; then test \"$h\" = \"{binary.Sha256}\" || {{ echo \"{InstallMark} mismatch\"; exit 3; }}; "
            + $"else test \"$(wc -c < \"$f\" | tr -d \" \")\" = \"{binary.Size}\" || {{ echo \"{InstallMark} mismatch\"; exit 3; }}; fi; "
            + $"chmod 700 \"$f\"; ZMX_DIR=\"{AgentPersistence.SocketDirectory}\" \"$f\" version >/dev/null 2>&1 || {{ echo \"{InstallMark} cannot-run\"; exit 4; }}; "
            + $"mv -f \"$f\" \"$d/resesh-agent\"; echo \"{InstallMark} ok\"";
        return "sh -c '" + script + "'";
    }

    /// <summary>What the install command reported. Anything it did not say plainly is a
    /// failure, including a command that never ran.</summary>
    public static AgentInstallOutcome ParseInstall(SshCommandResult? result)
    {
        if (result is null)
            return AgentInstallOutcome.Failed;
        foreach (var raw in result.Output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line == $"{InstallMark} ok")
                return result.Success ? AgentInstallOutcome.Installed : AgentInstallOutcome.Failed;
            if (line == $"{InstallMark} mismatch")
                return AgentInstallOutcome.Mismatch;
            if (line == $"{InstallMark} cannot-run")
                return AgentInstallOutcome.CannotRun;
        }
        return AgentInstallOutcome.Failed;
    }

    [GeneratedRegex(@"^[0-9A-Za-z][0-9A-Za-z._-]{0,63}$")]
    private static partial Regex PlainVersion();

    [GeneratedRegex(@"^[0-9a-f]{64}$")]
    private static partial Regex Sha256Hex();

    [GeneratedRegex(@"^resesh-agent-probe os=(\S+) machine=(\S+) hash=([0-9a-f]{64}|) runs=([01])$")]
    private static partial Regex ProbeLine();
}
