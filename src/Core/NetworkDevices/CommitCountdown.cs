namespace Resesh.Core.NetworkDevices;

/// <summary>What a tab shows for its <see cref="CommitTimer"/>: a countdown while armed,
/// for a while after the time is up or the device rolled back, and nothing otherwise.</summary>
public sealed record CommitCountdown(string Text, string Tooltip, bool Urgent)
{
    /// <summary>Nothing to show, for bindings that must not see null.</summary>
    public static readonly CommitCountdown Hidden = new("", "", false);

    public bool IsShown => Text.Length > 0;

    /// <summary>How long a finished timer stays on the tab.</summary>
    public static readonly TimeSpan Linger = TimeSpan.FromMinutes(5);

    /// <summary>Under this, the countdown turns from caution to critical.</summary>
    public static readonly TimeSpan UrgentBelow = TimeSpan.FromMinutes(1);

    /// <summary>The countdown at <paramref name="now"/>; null when nothing shows.</summary>
    public static CommitCountdown? For(CommitTimer timer, DateTimeOffset now)
    {
        if (timer.Deadline is not { } deadline || now - deadline >= Linger)
            return null;
        if (timer.Status == CommitTimerStatus.RolledBack)
            return new("rolled back", "The confirmed commit was rolled back.", true);
        var left = deadline - now;
        if (left <= TimeSpan.Zero)
            return new("0:00", "The confirmed commit's time is up: the device rolls back unless it was confirmed.", true);
        // Rounded up, so the countdown reaches 0:00 exactly when the time is up.
        var seconds = (int)Math.Ceiling(left.TotalSeconds);
        var text = seconds >= 3600
            ? $"{seconds / 3600}:{seconds / 60 % 60:00}:{seconds % 60:00}"
            : $"{seconds / 60}:{seconds % 60:00}";
        return new(text, $"Commit confirmed: rolls back at {deadline.ToLocalTime():HH:mm:ss} unless confirmed.", left < UrgentBelow);
    }
}
