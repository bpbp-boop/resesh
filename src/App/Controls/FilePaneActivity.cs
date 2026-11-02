namespace Resesh.App.Controls;

/// <summary>What the file pane is doing. One thing runs at a time.</summary>
public enum FilePaneState
{
    /// <summary>Showing a listing (or nothing yet); ready for the next request.</summary>
    Idle,

    /// <summary>Connecting and/or listing a folder.</summary>
    Loading,

    /// <summary>A transfer, rename, delete or similar is running.</summary>
    Operating,

    /// <summary>The first listing failed; the pane shows the retry overlay. Accepts requests like Idle.</summary>
    Failed,

    /// <summary>The pane closed. Refuses everything.</summary>
    Disposed,
}

/// <summary>A folder to show: null path = the profile's home. The notice replaces the item count.</summary>
public readonly record struct FileNavigation(string? Path, string? Notice = null);

public enum NavigationRequest
{
    /// <summary>The caller runs this navigation now.</summary>
    Start,

    /// <summary>A load is running; this navigation replaces any earlier queued one and runs
    /// when that load ends. The caller cancels the running load so the latest wins.</summary>
    Queued,

    /// <summary>An operation is running, or the pane closed.</summary>
    Refused,
}

/// <summary>
/// The file pane's state, kept apart from the control so it is tested headless. It also
/// remembers whether any listing ever loaded: an empty folder is a loaded folder, so its
/// pane retries in place and reports later failures as a status line, not the full-pane
/// error meant for a pane that never connected.
/// </summary>
public sealed class FilePaneActivity
{
    private FileNavigation? _pending;

    public FilePaneState State { get; private set; }

    /// <summary>A listing has loaded at least once (even an empty one).</summary>
    public bool HasListing { get; private set; }

    public bool IsBusy => State is FilePaneState.Loading or FilePaneState.Operating;

    private bool AcceptsWork => State is FilePaneState.Idle or FilePaneState.Failed;

    public NavigationRequest RequestNavigation(FileNavigation navigation)
    {
        if (State == FilePaneState.Loading)
        {
            _pending = navigation;
            return NavigationRequest.Queued;
        }
        if (!AcceptsWork)
            return NavigationRequest.Refused;
        State = FilePaneState.Loading;
        return NavigationRequest.Start;
    }

    /// <summary>Ends a load. Returns the next queued navigation, which the caller runs
    /// straight away (the pane stays Loading), or null when the pane is free.</summary>
    public FileNavigation? EndNavigation(bool listed)
    {
        if (State != FilePaneState.Loading)
            return null;
        if (listed)
            HasListing = true;
        if (_pending is { } next)
        {
            _pending = null;
            return next;
        }
        State = HasListing ? FilePaneState.Idle : FilePaneState.Failed;
        return null;
    }

    public bool TryBeginOperation()
    {
        if (!AcceptsWork)
            return false;
        State = FilePaneState.Operating;
        return true;
    }

    public void EndOperation()
    {
        if (State == FilePaneState.Operating)
            State = HasListing ? FilePaneState.Idle : FilePaneState.Failed;
    }

    /// <summary>Where Retry goes: the current folder once anything loaded, else home.</summary>
    public FileNavigation RetryTarget(string currentPath) =>
        new(HasListing ? currentPath : null);

    public void Dispose()
    {
        State = FilePaneState.Disposed;
        _pending = null;
    }
}
