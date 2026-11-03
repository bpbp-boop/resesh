using System.Text;

namespace Resesh.App;

/// <summary>
/// Which data set a run uses, read once from its command line: the real profile, a
/// --data-dir folder, or the throwaway demo data. Everything that depends on the choice
/// (store paths, logs, credentials, the single-instance key, the taskbar relaunch command,
/// jump list and sign-in) derives from this one answer, so they cannot disagree.
/// </summary>
internal sealed record DataProfile
{
    public const string DemoOption = "--demo";
    public const string DataDirectoryOption = "--data-dir";

    /// <summary>The disposable screenshot data set. Takes precedence over --data-dir.</summary>
    public bool IsDemo { get; private init; }

    /// <summary>The --data-dir folder as a full path, so a relaunch from another working
    /// directory (the taskbar, a jump list) opens the same folder. Null otherwise.</summary>
    public string? DataDirectory { get; private init; }

    /// <summary>Why the command line could not be used, or null. A --data-dir without a usable
    /// folder must not fall back to the real profile: the user asked to keep it untouched.</summary>
    public string? Error { get; private init; }

    /// <summary>The user's real data: no demo, no --data-dir.</summary>
    public bool IsDefault => !IsDemo && DataDirectory is null && Error is null;

    /// <summary>Reads the options. <paramref name="args"/> starts with the program path, as
    /// <see cref="Environment.GetCommandLineArgs"/> returns it; options match case-insensitively
    /// and the first --data-dir wins.</summary>
    public static DataProfile Parse(IReadOnlyList<string> args, string? currentDirectory = null)
    {
        var demo = false;
        string? directory = null;
        string? error = null;
        for (var i = 1; i < args.Count; i++)
        {
            if (args[i].Equals(DemoOption, StringComparison.OrdinalIgnoreCase))
            {
                demo = true;
            }
            else if (args[i].Equals(DataDirectoryOption, StringComparison.OrdinalIgnoreCase))
            {
                var value = i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
                    ? args[++i]
                    : null; // a following option is read as an option, not as the folder
                if (directory is not null || error is not null)
                    continue;
                if (string.IsNullOrWhiteSpace(value))
                {
                    error = $"{DataDirectoryOption} needs a folder, for example {DataDirectoryOption} \"C:\\resesh-test\".";
                    continue;
                }
                try
                {
                    directory = currentDirectory is null
                        ? Path.GetFullPath(value)
                        : Path.GetFullPath(value, currentDirectory);
                }
                catch (Exception exception) when (exception is ArgumentException or NotSupportedException
                    or PathTooLongException)
                {
                    error = $"{DataDirectoryOption} \"{value}\" is not a usable folder: {exception.Message}";
                }
            }
        }
        return demo
            ? new DataProfile { IsDemo = true }
            : new DataProfile { DataDirectory = directory, Error = error };
    }

    /// <summary>The command line that reopens this profile, for the taskbar's relaunch entry.</summary>
    public string RelaunchCommand(string processPath)
    {
        var parts = new List<string> { Quote(processPath) };
        if (IsDemo)
            parts.Add(DemoOption);
        else if (DataDirectory is not null)
        {
            parts.Add(DataDirectoryOption);
            parts.Add(Quote(DataDirectory));
        }
        return string.Join(' ', parts);
    }

    /// <summary>Quotes one argument so the C runtime's splitting gives it back unchanged.</summary>
    internal static string Quote(string value)
    {
        var result = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var character in value)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            result.Append('\\', character == '"' ? (backslashes * 2) + 1 : backslashes);
            backslashes = 0;
            result.Append(character);
        }
        result.Append('\\', backslashes * 2);
        return result.Append('"').ToString();
    }
}
