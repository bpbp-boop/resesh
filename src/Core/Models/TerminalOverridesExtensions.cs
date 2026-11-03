namespace Resesh.Core.Models;

public static class TerminalOverridesExtensions
{
    /// <summary>The theme a session's terminal and tab draw with: its override, else the app's.
    /// The one rule for it, used by the effective settings, the terminal and the tab header.</summary>
    public static string EffectiveTheme(this TerminalOverrides? overrides, string appTheme) =>
        overrides?.Theme ?? appTheme;
}
