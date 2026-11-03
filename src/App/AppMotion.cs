namespace Resesh.App;

/// <summary>
/// Whether the app animates: motion follows Windows' "Animation effects" setting (see
/// DECISIONS 2026-10-29). Every animation asks here, so the tab strip, panes, workspace
/// switches, the rail marker and the sessions pane all stop moving together.
/// </summary>
internal static class AppMotion
{
    private static readonly Windows.UI.ViewManagement.UISettings s_settings = new();

    public static bool Enabled => s_settings.AnimationsEnabled;
}
