using Resesh.Core.Input;

namespace Resesh.Core.Tests;

public class KeyBindingsTests
{
    [Fact]
    public void IdsAreUniqueAndEveryBindingHasAChord()
    {
        Assert.Equal(KeyBindings.All.Count, KeyBindings.All.Select(binding => binding.Id).Distinct().Count());
        Assert.All(KeyBindings.All, binding => Assert.NotEmpty(binding.Chords));
        Assert.All(KeyBindings.All, binding => Assert.Contains(binding.Category, KeyBindings.Categories));
    }

    [Fact]
    public void NoTwoShortcutsShareAChordWhereBothAreActive()
    {
        // App, Window and Terminal chords are live together whenever a terminal or the window
        // has focus; session-tree chords only compete with the window-wide ones.
        ShortcutScope[][] overlapping =
        [
            [ShortcutScope.App, ShortcutScope.Window, ShortcutScope.Terminal],
            [ShortcutScope.App, ShortcutScope.Window, ShortcutScope.SessionTree],
        ];
        foreach (var scopes in overlapping)
        {
            var duplicates = KeyBindings.All
                .Where(binding => scopes.Contains(binding.Scope))
                .SelectMany(binding => binding.Chords.Select(chord => (binding.Id, chord.Modifiers, chord.VirtualKey)))
                .GroupBy(entry => (entry.Modifiers, entry.VirtualKey))
                .Where(group => group.Count() > 1)
                .Select(group => string.Join(", ", group.Select(entry => entry.Id)))
                .ToList();
            Assert.Empty(duplicates);
        }
    }

    [Fact]
    public void ShortcutsThatReachTheTerminalLeaveShellKeysAlone()
    {
        foreach (var binding in KeyBindings.All.Where(binding => binding.Scope is ShortcutScope.App or ShortcutScope.Terminal))
        {
            foreach (var chord in binding.Chords)
            {
                // Ctrl+letter is the shell's (Ctrl+W, Ctrl+R, tmux's Ctrl+B).
                var isLetter = chord.VirtualKey is >= 'A' and <= 'Z';
                Assert.False(chord.Modifiers == KeyModifiers.Ctrl && isLetter, $"{binding.Id} takes {chord.Label}");
                // Ctrl+Alt is AltGr on many European layouts.
                Assert.False(chord.Modifiers.HasFlag(KeyModifiers.Ctrl) && chord.Modifiers.HasFlag(KeyModifiers.Alt),
                    $"{binding.Id} takes {chord.Label}");
                // Alt+letter is Meta for readline and zsh.
                Assert.False(chord.Modifiers == KeyModifiers.Alt && isLetter, $"{binding.Id} takes {chord.Label}");
            }
        }
    }

    [Fact]
    public void AltArrowOnlyTakesTheKeyWhileSplit()
    {
        var altChords = KeyBindings.All.Where(binding =>
            binding.Scope != ShortcutScope.SessionTree
            && binding.Chords.Any(chord => chord.Modifiers == KeyModifiers.Alt));
        Assert.All(altChords, binding => Assert.Equal(ShortcutCondition.Split, binding.Condition));
    }

    [Fact]
    public void LabelsReadLikeWindowsShortcuts()
    {
        Assert.Equal("Ctrl+Shift+P", KeyBindings.Get(ShortcutIds.CommandPalette).Label);
        Assert.Equal("Ctrl+Shift+W", KeyBindings.Get(ShortcutIds.CloseTab).Label);
        Assert.Equal("Ctrl+F4", KeyBindings.Get(ShortcutIds.CloseTab).Chords[1].Label);
        Assert.Equal("Ctrl+Shift+\\", KeyBindings.Get(ShortcutIds.SplitRight).Label);
        Assert.Equal("Alt+←", KeyBindings.Get(ShortcutIds.FocusGroupLeft).Label);
        Assert.Equal("Ctrl+1 … Ctrl+8", KeyBindings.Get(ShortcutIds.GoToTab).Label);
    }

    [Fact]
    public void GoToTabChordsAreCtrlOneThroughEightInOrder()
    {
        var chords = KeyBindings.Get(ShortcutIds.GoToTab).Chords;
        Assert.Equal(Enumerable.Range('1', 8), chords.Select(chord => chord.VirtualKey));
        Assert.All(chords, chord => Assert.Equal(KeyModifiers.Ctrl, chord.Modifiers));
    }

    [Fact]
    public void FindMatchesExactModifiersWithinTheRequestedScopes()
    {
        var closeTab = KeyBindings.Find(VirtualKeys.F4, KeyModifiers.Ctrl, ShortcutScope.App);
        Assert.Equal((ShortcutIds.CloseTab, 1), (closeTab?.Binding.Id, closeTab?.ChordIndex));

        Assert.Null(KeyBindings.Find(VirtualKeys.F4, KeyModifiers.Ctrl | KeyModifiers.Shift, ShortcutScope.App));
        Assert.Null(KeyBindings.Find('F', KeyModifiers.Ctrl | KeyModifiers.Shift, ShortcutScope.App));
        Assert.Equal(ShortcutIds.Find,
            KeyBindings.Find('F', KeyModifiers.Ctrl | KeyModifiers.Shift, ShortcutScope.Terminal)?.Binding.Id);
        Assert.Equal(ShortcutIds.DeleteSelection,
            KeyBindings.Find(VirtualKeys.Delete, KeyModifiers.None, ShortcutScope.SessionTree)?.Binding.Id);
    }

    [Fact]
    public void ExistingShortcutsKeepTheirKeys()
    {
        (string Id, string Label)[] existing =
        [
            (ShortcutIds.CommandPalette, "Ctrl+Shift+P"),
            (ShortcutIds.QuickConnect, "Ctrl+Shift+K"),
            (ShortcutIds.NewLocalTab, "Ctrl+Shift+T"),
            (ShortcutIds.FilterSessions, "Ctrl+F"),
            (ShortcutIds.FilePane, "Ctrl+Shift+E"),
            (ShortcutIds.Find, "Ctrl+Shift+F"),
            (ShortcutIds.CommandsPanel, "Ctrl+Shift+O"),
            (ShortcutIds.Bookmark, "Ctrl+Shift+M"),
            (ShortcutIds.PreviousCommand, "Ctrl+Shift+↑"),
            (ShortcutIds.NextCommand, "Ctrl+Shift+↓"),
            (ShortcutIds.Copy, "Ctrl+Shift+C"),
            (ShortcutIds.Paste, "Ctrl+Shift+V"),
        ];
        foreach (var (id, label) in existing)
            Assert.Contains(label, KeyBindings.Get(id).Chords.Select(chord => chord.Label));
    }
}

public class ShortcutNavigationTests
{
    [Theory]
    [InlineData(0, 3, 1, 1)]
    [InlineData(2, 3, 1, 0)]
    [InlineData(0, 3, -1, 2)]
    [InlineData(-1, 3, 1, 0)]
    [InlineData(0, 0, 1, -1)]
    public void CycleWrapsAtBothEnds(int index, int count, int delta, int expected) =>
        Assert.Equal(expected, TabNavigation.Cycle(index, count, delta));

    [Theory]
    [InlineData(1, 3, 0)]
    [InlineData(3, 3, 2)]
    [InlineData(4, 3, -1)]
    [InlineData(9, 3, 2)]
    [InlineData(9, 12, 11)]
    [InlineData(8, 12, 7)]
    [InlineData(9, 0, -1)]
    public void GoToFollowsBrowserNumbering(int number, int count, int expected) =>
        Assert.Equal(expected, TabNavigation.GoTo(number, count));

    [Theory]
    // five tabs, the first two pinned
    [InlineData(3, 1, false, 4)]
    [InlineData(4, 1, false, -1)]
    [InlineData(2, -1, false, -1)]
    [InlineData(3, -1, false, 2)]
    [InlineData(0, 1, true, 1)]
    [InlineData(1, 1, true, -1)]
    [InlineData(0, -1, true, -1)]
    public void MoveKeepsPinnedAndUnpinnedTabsApart(int index, int delta, bool pinned, int expected) =>
        Assert.Equal(expected, TabNavigation.MoveTarget(index, delta, count: 5, pinnedCount: 2, pinned));

    //  +-----+-----+
    //  |  a  |  b  |
    //  |     +-----+
    //  |     |  c  |
    //  +-----+-----+
    private static readonly (string Item, LayoutBounds Bounds)[] Layout =
    [
        ("a", new LayoutBounds(0, 0, 500, 600)),
        ("b", new LayoutBounds(502, 0, 500, 300)),
        ("c", new LayoutBounds(502, 302, 500, 298)),
    ];

    [Theory]
    [InlineData("a", NavigationDirection.Right, "b")]
    [InlineData("b", NavigationDirection.Left, "a")]
    [InlineData("c", NavigationDirection.Left, "a")]
    [InlineData("b", NavigationDirection.Down, "c")]
    [InlineData("c", NavigationDirection.Up, "b")]
    [InlineData("a", NavigationDirection.Left, null)]
    [InlineData("a", NavigationDirection.Up, null)]
    [InlineData("b", NavigationDirection.Right, null)]
    public void FindNeighborPicksTheAdjacentGroup(string from, NavigationDirection direction, string? expected)
    {
        var origin = Layout.Single(entry => entry.Item == from).Bounds;
        Assert.Equal(expected, GroupNavigation.FindNeighbor(Layout, origin, direction));
    }

    [Fact]
    public void FindNeighborPrefersTheGroupAlignedWithTheCurrentOne()
    {
        //  +-----+-----+
        //  |  a  |  b  |
        //  +-----+--+--+
        //  |    c   |
        //  +--------+
        (string Item, LayoutBounds Bounds)[] layout =
        [
            ("a", new LayoutBounds(0, 0, 300, 200)),
            ("b", new LayoutBounds(302, 0, 300, 200)),
            ("c", new LayoutBounds(0, 202, 400, 200)),
        ];
        Assert.Equal("a", GroupNavigation.FindNeighbor(layout, layout[2].Bounds, NavigationDirection.Up));
        Assert.Equal("c", GroupNavigation.FindNeighbor(layout, layout[1].Bounds, NavigationDirection.Down));
    }
}
