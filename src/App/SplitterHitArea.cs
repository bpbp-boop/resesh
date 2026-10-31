using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;

namespace Resesh.App;

/// <summary>
/// Sizes the transparent grab strip of a <see cref="GridSplitter"/> that sits in a
/// one-pixel divider track. A grid clips each child to its cell, and the clip applies to
/// hit-testing too, so a wider splitter in a 1px track could only be grabbed on that one
/// pixel. Negative margins bring the splitter's layout size down to the track, so nothing
/// is clipped, while it still draws and hit-tests across the whole strip.
/// </summary>
internal static class SplitterHitArea
{
    /// <summary>Grab width in DIPs. Matches the toolkit's default MinWidth/MinHeight, which
    /// would silently override anything narrower.</summary>
    public const double Size = 8;

    // The strip covers 3 DIPs before the line, the line itself, and 4 after, so Size plus
    // both (negative) margins equals the 1px track.
    private const double Before = 3;
    private const double After = Size - 1 - Before;

    public static void Apply(GridSplitter splitter, bool columns)
    {
        if (columns)
        {
            splitter.Width = Size;
            splitter.Height = double.NaN;
            splitter.Margin = new Thickness(-Before, 0, -After, 0);
        }
        else
        {
            splitter.Width = double.NaN;
            splitter.Height = Size;
            splitter.Margin = new Thickness(0, -Before, 0, -After);
        }
    }
}
