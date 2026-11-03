using Resesh.Core.Models;

namespace Resesh.App.ViewModels;

/// <summary>App services supplied by the view layer; view models do not access WinUI globals.</summary>
public sealed class ViewModelEnvironment
{
    public required Func<string> CurrentTheme { get; init; }
    public required Func<string, string> ResolveTheme { get; init; }
    public required Func<bool> ShowAgentIcons { get; init; }
    public required Func<Session, bool> IsSessionVisible { get; init; }
    public required Action<TabViewModel> ApplySessionSettings { get; init; }
    public required Action<Exception> ReportError { get; init; }

    /// <summary>Runs an action on the window's UI thread: at once when already on it.
    /// Session store changes can be saved from any thread.</summary>
    public Action<Action> RunOnUiThread { get; init; } = action => action();

    /// <summary>Saved sessions were deleted; references kept elsewhere (saved workspaces) drop them.</summary>
    public Action<IReadOnlyCollection<Guid>> SessionsDeleted { get; init; } = _ => { };

    public Func<IReadOnlyList<Guid>> RecentSessionIds { get; init; } = () => [];
    public Action<IReadOnlyList<Guid>> SaveRecentSessionIds { get; init; } = _ => { };
    public Func<string> RecordingDirectory { get; init; } = () => "";
    public Func<Guid?> DefaultLocalProfileId { get; init; } = () => null;
    public Action<Guid> SetDefaultLocalProfile { get; init; } = _ => { };

    /// <summary>The username a remote session logs in as (its own, or its identity's).</summary>
    public Func<Session, string> LoginUsername { get; init; } = session => session.Username;
}
