using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Input;
using Resesh.Core.Input;
using Resesh.Terminal;
using Windows.System;

namespace Resesh.App;

/// <summary>Adapts the shared <see cref="KeyBindings"/> table to WinUI and the terminal surfaces.</summary>
internal static class AppShortcuts
{
    /// <summary>Everything a focused terminal must recognize: its own actions, plus the window
    /// shortcuts it forwards because WebView2 does not deliver window accelerators.</summary>
    internal static IReadOnlyList<TerminalShortcut> ForTerminals() =>
        [.. KeyBindings.All
            .Where(binding => binding.Scope is ShortcutScope.App or ShortcutScope.Terminal)
            .Select(binding => new TerminalShortcut(
                binding.Id,
                Forward: binding.Scope == ShortcutScope.App,
                WhenSplit: binding.Condition == ShortcutCondition.Split,
                [.. binding.Chords.Select(chord => new TerminalKeyChord(
                    chord.VirtualKey,
                    chord.Modifiers.HasFlag(KeyModifiers.Ctrl),
                    chord.Modifiers.HasFlag(KeyModifiers.Shift),
                    chord.Modifiers.HasFlag(KeyModifiers.Alt)))]))];

    internal static KeyboardAccelerator Accelerator(KeyChord chord) => new()
    {
        Key = (VirtualKey)chord.VirtualKey,
        Modifiers = ToVirtualKeyModifiers(chord.Modifiers),
    };

    internal static VirtualKeyModifiers ToVirtualKeyModifiers(KeyModifiers modifiers)
    {
        var result = VirtualKeyModifiers.None;
        if (modifiers.HasFlag(KeyModifiers.Ctrl))
            result |= VirtualKeyModifiers.Control;
        if (modifiers.HasFlag(KeyModifiers.Shift))
            result |= VirtualKeyModifiers.Shift;
        if (modifiers.HasFlag(KeyModifiers.Alt))
            result |= VirtualKeyModifiers.Menu;
        return result;
    }

    internal static KeyModifiers CurrentModifiers()
    {
        var modifiers = KeyModifiers.None;
        if ((GetKeyState(0x11) & 0x8000) != 0)
            modifiers |= KeyModifiers.Ctrl;
        if ((GetKeyState(0x10) & 0x8000) != 0)
            modifiers |= KeyModifiers.Shift;
        if ((GetKeyState(0x12) & 0x8000) != 0)
            modifiers |= KeyModifiers.Alt;
        return modifiers;
    }

    /// <summary>The label menus and the palette show for a binding, in the user's keyboard layout.</summary>
    internal static string Label(string id)
    {
        var binding = KeyBindings.Get(id);
        return binding.DisplayLabel ?? Label(binding.Chords[0]);
    }

    internal static string Label(KeyChord chord) => string.Join("+", Parts(chord));

    /// <summary>The keycaps of a chord. Punctuation keys are named by what the current
    /// layout prints on them: VK_OEM_2 is "/" on US keyboards and "#" on German ones.</summary>
    internal static IReadOnlyList<string> Parts(KeyChord chord)
    {
        var parts = chord.Parts.ToArray();
        parts[^1] = KeyLabel(chord);
        return parts;
    }

    private static string KeyLabel(KeyChord chord)
    {
        if (chord.VirtualKey is < 0xBA or > 0xE2)
            return chord.KeyLabel;
        var character = MapVirtualKeyW((uint)chord.VirtualKey, MapVkToChar) & 0x7FFF;
        return character is > 0x20 and < 0xFFFF
            ? ((char)character).ToString().ToUpperInvariant()
            : chord.KeyLabel;
    }

    private const uint MapVkToChar = 2;

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKeyW(uint code, uint mapType);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int virtualKey);
}
