using System.Globalization;

namespace Resesh.Core.Recording;

public sealed record TerminalRecordingEvent(double Time, string Type, string Data)
{
    public bool IsOutput => Type == "o";
    public bool IsResize => Type == "r";
}

public sealed record TerminalKeyframe(double Time, int Columns, int Rows, ReadOnlyMemory<byte> State);

public sealed record TerminalRewindSlice(
    DateTimeOffset StartedAt,
    int InitialColumns,
    int InitialRows,
    double EarliestTime,
    double LatestTime,
    TerminalKeyframe? Keyframe,
    IReadOnlyList<TerminalRecordingEvent> Events);

public sealed record TerminalRecording(
    DateTimeOffset StartedAt,
    int Width,
    int Height,
    string? Title,
    IReadOnlyList<TerminalRecordingEvent> Events)
{
    public double Duration => Events.Count == 0 ? 0 : Events[^1].Time;
}

public sealed record TerminalRecordingPaths(string CastPath, string LogPath);

/// <summary>
/// The rewind caps' defaults and ranges. Settings defaults, the Settings page's ranges and
/// the capture all read them from here.
/// </summary>
public static class RewindLimits
{
    public const int DefaultMinutes = 30;
    public const int MinimumMinutes = 1;
    public const int MaximumMinutes = 24 * 60;
    public const int DefaultMegabytes = 32;
    public const int MinimumMegabytes = 1;
    public const int MaximumMegabytes = 1024;
    public const long BytesPerMegabyte = 1024L * 1024;

    /// <summary>The age cap for a setting, clamped to the range.</summary>
    public static TimeSpan Age(int minutes) =>
        TimeSpan.FromMinutes(Math.Clamp(minutes, MinimumMinutes, MaximumMinutes));

    /// <summary>The memory cap for a setting, clamped to the range.</summary>
    public static long Bytes(int megabytes) =>
        Math.Clamp(megabytes, MinimumMegabytes, MaximumMegabytes) * BytesPerMegabyte;
}

public static class TerminalRecordingFiles
{
    /// <summary>
    /// The folder a recording-directory setting names: environment variables expanded
    /// ("%USERPROFILE%\casts") and made absolute. The recorder writes there and the
    /// Recordings list and "Open Recordings Location" read there, so both see one folder.
    /// </summary>
    public static string ResolveDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("A recording directory is required.", nameof(directory));
        return Path.GetFullPath(Environment.ExpandEnvironmentVariables(directory.Trim()));
    }

    public static TerminalRecordingPaths CreatePaths(
        string directory,
        string sessionName,
        DateTimeOffset timestamp)
    {
        directory = ResolveDirectory(directory);
        Directory.CreateDirectory(directory);
        var invalid = Path.GetInvalidFileNameChars();
        var safeName = new string(sessionName.Trim().Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        if (string.IsNullOrWhiteSpace(safeName))
            safeName = "session";
        var stem = string.Create(CultureInfo.InvariantCulture, $"{safeName}-{timestamp:yyyyMMdd-HHmmss}");
        var suffix = 1;
        while (true)
        {
            var numberedStem = suffix == 1 ? stem : $"{stem}-{suffix}";
            var castPath = Path.Combine(directory, numberedStem + ".cast");
            var logPath = Path.Combine(directory, numberedStem + ".log");
            if (!File.Exists(castPath) && !File.Exists(logPath))
                return new TerminalRecordingPaths(castPath, logPath);
            suffix++;
        }
    }
}
