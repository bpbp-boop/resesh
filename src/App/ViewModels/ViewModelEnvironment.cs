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

    public Func<IReadOnlyList<Guid>> RecentSessionIds { get; init; } = () => [];
    public Action<IReadOnlyList<Guid>> SaveRecentSessionIds { get; init; } = _ => { };
    public Func<string> RecordingDirectory { get; init; } = () => "";
    public Func<Guid?> DefaultLocalProfileId { get; init; } = () => null;
    public Action<Guid> SetDefaultLocalProfile { get; init; } = _ => { };
}
