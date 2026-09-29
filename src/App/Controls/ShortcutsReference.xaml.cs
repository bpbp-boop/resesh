using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Resesh.Core.Input;

namespace Resesh.App.Controls;

/// <summary>A chord to press, or the word joining two chords.</summary>
public sealed class KeyToken
{
    public string Text { get; private init; } = "";
    public IReadOnlyList<string> Keys { get; private init; } = [];
    public bool IsSeparator => Keys.Count == 0;
    public bool IsChord => !IsSeparator;

    public static KeyToken Chord(KeyChord chord) => new() { Keys = AppShortcuts.Parts(chord).ToList() };

    public static KeyToken Word(string text) => new() { Text = text };
}

/// <summary>One shortcut row: what it does, and the keys that do it.</summary>
public sealed class ShortcutEntry
{
    public ShortcutEntry(KeyBinding binding)
    {
        Binding = binding;
        // A long run (Ctrl+1 to Ctrl+8) shows its ends; the rest list every chord.
        var chords = binding.Chords.Count > 2
            ? new[] { binding.Chords[0], binding.Chords[^1] }
            : binding.Chords.ToArray();
        var separator = binding.Chords.Count > 2 ? "to" : "or";
        Tokens = chords.SelectMany((chord, i) => i == 0
                ? [KeyToken.Chord(chord)]
                : new[] { KeyToken.Word(separator), KeyToken.Chord(chord) })
            .ToList();
        AutomationName = $"{binding.Title}: {string.Join($" {separator} ", chords.Select(AppShortcuts.Label))}";
    }

    public KeyBinding Binding { get; }
    public string Title => Binding.Title;
    public string Note => Binding.Note;
    public bool HasNote => Binding.Note.Length > 0;
    public IReadOnlyList<KeyToken> Tokens { get; }
    public string AutomationName { get; }
    public string AutomationId => "Shortcut_" + Binding.Id;
}

/// <summary>A titled category of shortcut rows.</summary>
public sealed class ShortcutGroup(string category, IReadOnlyList<ShortcutEntry> entries)
{
    public string Category { get; } = category;
    public IReadOnlyList<ShortcutEntry> Entries { get; } = entries;
}

/// <summary>Read-only reference of every shortcut in <see cref="KeyBindings"/>, grouped and searchable.</summary>
public sealed partial class ShortcutsReference : UserControl
{
    private readonly IReadOnlyList<ShortcutGroup> _all = KeyBindings.Categories
        .Select(category => new ShortcutGroup(category, KeyBindings.All
            .Where(binding => binding.Category == category)
            .Select(binding => new ShortcutEntry(binding))
            .ToList()))
        .ToList();

    /// <summary>The groups with at least one row matching the search.</summary>
    public ObservableCollection<ShortcutGroup> Groups { get; } = [];

    public ShortcutsReference()
    {
        InitializeComponent();
        ApplyFilter([]);
    }

    public void FocusSearch() => SearchBox.Focus(FocusState.Programmatic);

    /// <summary>Every search term must appear in the title, note, category or key names.</summary>
    internal static bool Matches(KeyBinding binding, IReadOnlyList<string> terms)
    {
        var keys = string.Join(" ", binding.Chords.Select(AppShortcuts.Label));
        return terms.All(term =>
            binding.Title.Contains(term, StringComparison.OrdinalIgnoreCase)
            || binding.Note.Contains(term, StringComparison.OrdinalIgnoreCase)
            || binding.Category.Contains(term, StringComparison.OrdinalIgnoreCase)
            || keys.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private void ApplyFilter(IReadOnlyList<string> terms)
    {
        Groups.Clear();
        foreach (var group in _all)
        {
            var entries = group.Entries.Where(entry => Matches(entry.Binding, terms)).ToList();
            if (entries.Count > 0)
                Groups.Add(entries.Count == group.Entries.Count ? group : new ShortcutGroup(group.Category, entries));
        }
        NoMatchesText.Visibility = Groups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) =>
        ApplyFilter(SearchBox.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries));
}
