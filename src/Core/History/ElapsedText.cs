using System.Globalization;

namespace Resesh.Core.History;

/// <summary>How long something took, as the history list and the recording player show it.</summary>
public static class ElapsedText
{
    /// <summary>"420 ms", "12.5 s", "3 min 4 s", "2 h 5 min". The value is rounded to what the
    /// unit shows before the unit is chosen, so 999.6 ms reads "1 s", not "1000 ms", and
    /// 59.96 s reads "1 min 0 s", not "60 s".</summary>
    public static string Short(TimeSpan duration)
    {
        var milliseconds = Math.Round(duration.TotalMilliseconds, MidpointRounding.AwayFromZero);
        if (milliseconds < 1000)
            return $"{milliseconds:0} ms";
        var tenths = Math.Round(duration.TotalSeconds, 1, MidpointRounding.AwayFromZero);
        if (tenths < 60)
            return $"{tenths:0.#} s";
        var seconds = (long)Math.Round(duration.TotalSeconds, MidpointRounding.AwayFromZero);
        return seconds < 3600
            ? $"{seconds / 60} min {seconds % 60} s"
            : $"{seconds / 3600} h {seconds % 3600 / 60} min";
    }

    /// <summary>A playback position: "mm:ss.fff", or "h:mm:ss.fff" from an hour on, with the
    /// hours counted in full (a 25-hour recording reads 25:00:00.000, not 01:00:00.000).</summary>
    public static string Clock(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
            elapsed = TimeSpan.Zero;
        var minutesAndSeconds = elapsed.ToString(@"mm\:ss\.fff", CultureInfo.InvariantCulture);
        return elapsed.TotalHours >= 1
            ? $"{(long)elapsed.TotalHours:00}:{minutesAndSeconds}"
            : minutesAndSeconds;
    }
}
