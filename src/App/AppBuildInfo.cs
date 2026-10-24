using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Resesh.App;

/// <summary>What this build is and what it runs on, for the About page and bug reports.</summary>
internal static class AppBuildInfo
{
    public const string WebsiteUrl = "https://resesh.net/";
    public const string SourceUrl = "https://github.com/bpbp-boop/resesh";
    public const string ReleasesUrl = "https://github.com/bpbp-boop/resesh/releases";
    public const string IssuesUrl = "https://github.com/bpbp-boop/resesh/issues";
    public const string LicenseUrl = "https://github.com/bpbp-boop/resesh/blob/master/LICENSE";

    private static readonly Assembly AppAssembly = typeof(AppBuildInfo).Assembly;

    // Release builds stamp "1.2.3+<commit>"; local builds keep the SDK default "1.0.0+<commit>".
    private static readonly string InformationalVersion =
        AppAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";

    /// <summary>The release number, such as 0.21.0. Local builds have none.</summary>
    public static string? Version
    {
        get
        {
            var version = InformationalVersion.Split('+')[0];
            return version is "" or "1.0.0" ? null : version;
        }
    }

    /// <summary>The short commit the build came from, when the SDK recorded one.</summary>
    public static string? Commit
    {
        get
        {
            var plus = InformationalVersion.IndexOf('+');
            if (plus < 0)
                return null;
            var commit = InformationalVersion[(plus + 1)..];
            return commit.Length > 7 ? commit[..7] : commit;
        }
    }

    public static string VersionText => Version is { } version ? $"Version {version}" : "Development build";

    public static string Architecture => RuntimeInformation.ProcessArchitecture switch
    {
        System.Runtime.InteropServices.Architecture.X64 => "x64",
        System.Runtime.InteropServices.Architecture.Arm64 => "ARM64",
        var other => other.ToString(),
    };

    public static string WindowsText
    {
        get
        {
            var build = Environment.OSVersion.Version;
            var name = build.Build >= 22000 ? "Windows 11" : "Windows 10";
            var release = ReadWindowsRelease();
            var revision = ReadUbr() is int ubr ? $".{ubr}" : "";
            return release is null
                ? $"{name} (build {build.Build}{revision})"
                : $"{name} {release} (build {build.Build}{revision})";
        }
    }

    public static string DotNetText => RuntimeInformation.FrameworkDescription;

    public static string? WindowsAppSdkVersion => Metadata("WindowsAppSdkVersion");

    public static string? GhosttyVersion => Metadata("GhosttyVersion");

    /// <summary>Plain text for the clipboard, meant for pasting into an issue.</summary>
    public static string DetailsText()
    {
        var lines = new List<string>
        {
            $"resesh {Version ?? "development build"}" + (Commit is { } commit ? $" ({commit})" : ""),
            $"Architecture: {Architecture}",
            $"Windows: {WindowsText}",
            $"Runtime: {DotNetText}",
        };
        if (WindowsAppSdkVersion is { } sdk)
            lines.Add($"Windows App SDK: {sdk}");
        if (GhosttyVersion is { } ghostty)
            lines.Add($"libghostty-vt: {ghostty}");
        return string.Join(Environment.NewLine, lines);
    }

    private static string? Metadata(string key) =>
        AppAssembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == key)?.Value is { Length: > 0 } value ? value : null;

    private const string CurrentVersionKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";

    private static string? ReadWindowsRelease()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(CurrentVersionKey);
            return key?.GetValue("DisplayVersion") as string;
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static int? ReadUbr()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(CurrentVersionKey);
            return key?.GetValue("UBR") as int?;
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
