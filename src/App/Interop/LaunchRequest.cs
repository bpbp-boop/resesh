namespace Resesh.App.Interop;

/// <summary>
/// What a command line asks an open instance to do. The first launch applies it to its new
/// window; a later launch (a jump list item, a second shortcut click) is redirected to the
/// running instance, which applies it there.
/// </summary>
internal sealed record LaunchRequest(
    IReadOnlyList<Guid> SessionIds,
    IReadOnlyList<string> SessionNames,
    IReadOnlyList<string> RecordingPaths)
{
    /// <summary>Opens a saved session by id. The jump list uses this, so a renamed session still opens.</summary>
    public const string SessionOption = "--session";

    /// <summary>Opens a saved session by name. Used by the automated UI test rig.</summary>
    public const string OpenOption = "--open";

    public const string OpenRecordingOption = "--open-recording";

    /// <summary>True when the launch names something to open, rather than just a new window.</summary>
    public bool OpensSomething => SessionIds.Count + SessionNames.Count + RecordingPaths.Count > 0;

    /// <summary>Reads the options that open things. <paramref name="args"/> may start with the
    /// program path; unknown options (such as --data-dir) are skipped.</summary>
    public static LaunchRequest Parse(IReadOnlyList<string> args)
    {
        var ids = new List<Guid>();
        var names = new List<string>();
        var recordings = new List<string>();
        for (var i = 0; i < args.Count - 1; i++)
        {
            switch (args[i])
            {
                case SessionOption when Guid.TryParse(args[i + 1], out var id):
                    ids.Add(id);
                    i++;
                    break;
                case OpenOption:
                    names.Add(args[++i]);
                    break;
                case OpenRecordingOption:
                    recordings.Add(args[++i]);
                    break;
            }
        }
        return new(ids, names, recordings);
    }
}
