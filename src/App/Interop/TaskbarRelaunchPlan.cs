namespace Resesh.App.Interop;

/// <summary>
/// Which taskbar relaunch properties a window can carry. The shell rejects string values
/// longer than <see cref="MaxPropertyLength"/> characters for these properties, and a
/// deep install folder or a long --data-dir path easily exceeds it. An oversized command
/// is skipped rather than shortened: dropping --data-dir would pin a shortcut that opens
/// a different data folder, which is worse than no relaunch entry at all.
/// </summary>
internal readonly record struct TaskbarRelaunchPlan(bool SetRelaunchCommand, bool SetRelaunchIcon)
{
    /// <summary>Longest value (in UTF-16 chars, excluding the terminator) accepted by
    /// IPropertyStore.SetValue for the System.AppUserModel.Relaunch* window properties.
    /// Measured on Windows 11: 256 fails with E_INVALIDARG.</summary>
    public const int MaxPropertyLength = 255;

    public static bool Fits(string value) => value.Length <= MaxPropertyLength;

    /// <summary>The display name is fixed and short; it is set exactly when the command is,
    /// because the shell ignores a relaunch command without one. The icon is optional
    /// and only meaningful alongside the command.</summary>
    public static TaskbarRelaunchPlan For(string command, string iconResource)
    {
        var commandFits = Fits(command);
        return new(commandFits, commandFits && Fits(iconResource));
    }
}
