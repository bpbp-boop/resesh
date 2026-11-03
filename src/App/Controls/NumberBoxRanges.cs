using Microsoft.UI.Xaml.Controls;
using Resesh.Core.Storage;

namespace Resesh.App.Controls;

/// <summary>Number boxes take their limits from <see cref="SettingRanges"/> rather than XAML
/// literals, so the Settings page, the session editors and the saved-value clamps agree.</summary>
internal static class NumberBoxRanges
{
    public static void Limit(NumberBox box, IntRange range)
    {
        box.Minimum = range.Minimum;
        box.Maximum = range.Maximum;
    }
}
