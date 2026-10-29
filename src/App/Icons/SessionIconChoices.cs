using Resesh.App.Controls;
using Resesh.Core.Models;

namespace Resesh.App.Icons;

/// <summary>The cells a session's icon picker offers, shared by the session and local profile
/// editors: Auto-detect (null, so the first connect may suggest one) and No icon (a sentinel
/// that blocks suggestion), the built-in one-colour icons by section, then the user's own
/// files, shown as they are.</summary>
public static class SessionIconChoices
{
    public const string GeneralGroup = "General";
    public const string CustomGroup = "Your icons";
    private const double CustomImageSize = 24;

    /// <param name="unsetName">What no choice (null) means here: Auto-detect for SSH, the
    /// default terminal glyph for a local shell.</param>
    public static List<IconPickerChoice> Build(
        string? currentKey,
        out IconPickerChoice selected,
        string unsetName = "Auto-detect (suggested from the server on first connect)",
        string unsetText = "Auto",
        string unsetGlyph = "")
    {
        var choices = new List<IconPickerChoice>
        {
            new(null, unsetName, GeneralGroup, "automatic suggest detect default", glyph: unsetGlyph, text: unsetText),
            new(SessionIcons.None, "No icon", GeneralGroup, "none blank", glyph: ""),
        };
        choices.AddRange(SessionIcons.TreeIcons.Select(icon => new IconPickerChoice(
            icon.Key, icon.Name, SessionIcons.GroupName(icon.Group), icon.Keywords, brandKey: icon.Key)));
        choices.AddRange(App.Icons.CustomIcons().Select(icon => new IconPickerChoice(
            icon.Key, icon.Name, CustomGroup, customImage: App.Icons.GetImage(icon.Key, CustomImageSize))));

        // A key whose file has gone missing (a deleted custom icon) still round-trips.
        if (!string.IsNullOrEmpty(currentKey)
            && !choices.Any(choice => string.Equals(choice.Key, currentKey, StringComparison.OrdinalIgnoreCase)))
        {
            choices.Add(new IconPickerChoice(currentKey, $"{currentKey} (missing)", CustomGroup, text: "?"));
        }

        var key = string.IsNullOrEmpty(currentKey) ? null : currentKey;
        selected = choices.First(choice => string.Equals(choice.Key, key, StringComparison.OrdinalIgnoreCase));
        return choices;
    }
}
