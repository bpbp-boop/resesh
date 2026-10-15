namespace Resesh.Core.Layout;

/// <summary>Each pane's tabs (in layout order) and selection at the moment a tab was zoomed.</summary>
public sealed record TabZoomSnapshot<T>(
    IReadOnlyList<IReadOnlyList<T>> Groups,
    IReadOnlyList<T?> Selected,
    int FocusedGroup) where T : class;

/// <summary>Where each surviving tab goes back to when a zoom ends. Groups keep their snapshot
/// indices; a group whose tabs all closed comes back empty for the caller to drop.</summary>
public sealed record TabZoomRestore<T>(
    IReadOnlyList<IReadOnlyList<T>> Groups,
    IReadOnlyList<T?> Selected,
    int FocusedGroup) where T : class;

/// <summary>Tab zoom: every pane's tabs gathered into one strip, then put back where they were.</summary>
public static class TabZoom
{
    /// <summary>The single strip while zoomed: panes in layout order, pinned tabs first.</summary>
    public static IReadOnlyList<T> Merge<T>(IReadOnlyList<IReadOnlyList<T>> groups, Func<T, bool> isPinned)
        where T : class
    {
        var all = groups.SelectMany(group => group).ToList();
        return [.. all.Where(isPinned), .. all.Where(tab => !isPinned(tab))];
    }

    /// <summary>Puts the zoomed strip's tabs back into their panes. Tabs that closed drop out.
    /// A tab that arrived while zoomed joins the pane of the tab before it in the strip (or
    /// the one after it, or <paramref name="homeGroup"/>), right beside that tab. The tab
    /// active at the end stays selected and its pane takes focus.</summary>
    public static TabZoomRestore<T> Plan<T>(
        TabZoomSnapshot<T> snapshot,
        IReadOnlyList<T> current,
        T? active,
        int homeGroup,
        Func<T, bool> isPinned) where T : class
    {
        var present = new HashSet<T>(current, ReferenceEqualityComparer.Instance);
        var home = new Dictionary<T, int>(ReferenceEqualityComparer.Instance);
        var groups = new List<List<T>>(snapshot.Groups.Count);
        for (var index = 0; index < snapshot.Groups.Count; index++)
        {
            var survivors = snapshot.Groups[index].Where(present.Contains).ToList();
            foreach (var tab in survivors)
                home.TryAdd(tab, index);
            groups.Add(survivors);
        }

        T? previous = null;
        for (var position = 0; position < current.Count; position++)
        {
            var tab = current[position];
            if (!home.ContainsKey(tab))
            {
                if (previous is not null)
                {
                    var group = groups[home[previous]];
                    group.Insert(group.IndexOf(previous) + 1, tab);
                    home[tab] = home[previous];
                }
                else if (current.Skip(position + 1).FirstOrDefault(home.ContainsKey) is { } next)
                {
                    var group = groups[home[next]];
                    group.Insert(group.IndexOf(next), tab);
                    home[tab] = home[next];
                }
                else
                {
                    groups[homeGroup].Add(tab);
                    home[tab] = homeGroup;
                }
            }
            previous = tab;
        }

        // Pins may have changed while zoomed; pinned tabs still lead their pane.
        var ordered = groups
            .Select(group => (IReadOnlyList<T>)[.. group.Where(isPinned), .. group.Where(tab => !isPinned(tab))])
            .ToList();

        var selected = new T?[ordered.Count];
        for (var index = 0; index < ordered.Count; index++)
            selected[index] = SurvivingSelection(snapshot.Groups[index], snapshot.Selected[index], ordered[index], present);

        var focused = snapshot.FocusedGroup;
        if (active is not null && home.TryGetValue(active, out var activeGroup))
        {
            selected[activeGroup] = active;
            focused = activeGroup;
        }
        if (focused < 0 || focused >= ordered.Count || ordered[focused].Count == 0)
        {
            var firstWithTabs = ordered.ToList().FindIndex(group => group.Count > 0);
            focused = firstWithTabs >= 0 ? firstWithTabs : homeGroup;
        }

        return new TabZoomRestore<T>(ordered, selected, focused);
    }

    /// <summary>The pane's own selection when it survived, else its nearest survivor in the
    /// snapshot (the next tab first, as closing a selected tab does).</summary>
    private static T? SurvivingSelection<T>(
        IReadOnlyList<T> original, T? selection, IReadOnlyList<T> restored, HashSet<T> present)
        where T : class
    {
        if (restored.Count == 0)
            return null;
        if (selection is not null && present.Contains(selection))
            return selection;
        var index = selection is null ? -1 : original.ToList().IndexOf(selection);
        if (index >= 0)
        {
            for (var next = index + 1; next < original.Count; next++)
                if (present.Contains(original[next]))
                    return original[next];
            for (var before = index - 1; before >= 0; before--)
                if (present.Contains(original[before]))
                    return original[before];
        }
        return restored[0];
    }
}
