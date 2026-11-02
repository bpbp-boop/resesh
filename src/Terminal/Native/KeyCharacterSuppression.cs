namespace Resesh.Terminal.Native;

/// <summary>
/// Keeps the character a key leaves behind out of the shell once the terminal has already
/// encoded that key. Handling the key does not stop TranslateMessage posting its WM_CHAR,
/// which can reach the hidden text box (Ctrl+left Alt combinations that a layout maps to
/// AltGr text, keys pressed while disconnected), so text arriving while the key is held is
/// filtered. Only the key's own characters are dropped, as the layout reports them for the
/// keyboard state at key-down: text from elsewhere in that window (dictation, the emoji
/// panel, a UI Automation SetValue) still goes through. When the layout cannot say what the
/// key types (a dead key), everything is dropped while it is held, as before.
/// </summary>
internal sealed class KeyCharacterSuppression
{
    private ushort _key;
    private string? _characters;

    public bool IsActive => _key != 0;

    /// <summary>Starts suppressing for <paramref name="key"/>. <paramref name="characters"/>
    /// is what the key types (empty: nothing), or null when that is unknown.</summary>
    public void Suppress(ushort key, string? characters)
    {
        _key = key;
        _characters = characters;
    }

    /// <summary>A key went down: a different key means the held one's key-up was missed, and
    /// its suppression must not swallow later typing.</summary>
    public void KeyDown(ushort key)
    {
        if (_key != 0 && _key != key)
            Clear();
    }

    public void KeyUp(ushort key)
    {
        if (_key == key)
            Clear();
    }

    public void Clear()
    {
        _key = 0;
        _characters = null;
    }

    /// <summary>The part of <paramref name="text"/> to send: without the held key's leading
    /// characters (one copy per auto-repeat).</summary>
    public string Filter(string text)
    {
        if (_key == 0)
            return text;
        if (_characters is null)
            return "";
        if (_characters.Length == 0)
            return text;
        var start = 0;
        while (text.AsSpan(start).StartsWith(_characters, StringComparison.Ordinal))
            start += _characters.Length;
        return text[start..];
    }
}
