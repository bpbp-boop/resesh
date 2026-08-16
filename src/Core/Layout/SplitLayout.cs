namespace Sessions.Core.Layout;

public enum SplitDirection
{
    Left,
    Right,
    Up,
    Down,
}

public enum SplitOrientation
{
    Columns,
    Rows,
}

public abstract class SplitLayoutNode<T> where T : notnull
{
    internal SplitLayoutBranch<T>? Parent { get; set; }

    public abstract IEnumerable<T> Values { get; }
}

public sealed class SplitLayoutLeaf<T>(T value) : SplitLayoutNode<T> where T : notnull
{
    public T Value { get; } = value;

    public override IEnumerable<T> Values
    {
        get { yield return Value; }
    }
}

public sealed class SplitLayoutBranch<T> : SplitLayoutNode<T> where T : notnull
{
    private readonly List<SplitLayoutNode<T>> _children;

    internal SplitLayoutBranch(SplitOrientation orientation, IEnumerable<SplitLayoutNode<T>> children)
    {
        Orientation = orientation;
        _children = [.. children];
        foreach (var child in _children)
            child.Parent = this;
    }

    public SplitOrientation Orientation { get; }
    public IReadOnlyList<SplitLayoutNode<T>> Children => _children;
    internal List<SplitLayoutNode<T>> MutableChildren => _children;
    public override IEnumerable<T> Values => _children.SelectMany(child => child.Values);
}

/// <summary>A recursive layout of equal-size column and row siblings.</summary>
public sealed class SplitLayout<T> where T : notnull
{
    private readonly Dictionary<T, SplitLayoutLeaf<T>> _leaves = [];

    public SplitLayout(T initialValue)
    {
        Root = new SplitLayoutLeaf<T>(initialValue);
        _leaves.Add(initialValue, (SplitLayoutLeaf<T>)Root);
    }

    public SplitLayoutNode<T> Root { get; private set; }
    public IReadOnlyList<T> Values => [.. Root.Values];

    public void Split(T target, T added, SplitDirection direction)
    {
        if (_leaves.ContainsKey(added))
            throw new ArgumentException("The added value is already in the layout.", nameof(added));
        if (!_leaves.TryGetValue(target, out var targetLeaf))
            throw new ArgumentException("The target value is not in the layout.", nameof(target));

        var orientation = direction is SplitDirection.Left or SplitDirection.Right
            ? SplitOrientation.Columns
            : SplitOrientation.Rows;
        var insertBefore = direction is SplitDirection.Left or SplitDirection.Up;
        var addedLeaf = new SplitLayoutLeaf<T>(added);
        _leaves.Add(added, addedLeaf);

        if (targetLeaf.Parent is { Orientation: var parentOrientation } parent && parentOrientation == orientation)
        {
            var targetIndex = parent.MutableChildren.IndexOf(targetLeaf);
            parent.MutableChildren.Insert(targetIndex + (insertBefore ? 0 : 1), addedLeaf);
            addedLeaf.Parent = parent;
            return;
        }

        var originalParent = targetLeaf.Parent;
        SplitLayoutNode<T>[] children = insertBefore ? [addedLeaf, targetLeaf] : [targetLeaf, addedLeaf];
        var branch = new SplitLayoutBranch<T>(orientation, children);
        ReplaceNode(targetLeaf, branch, originalParent);
    }

    /// <summary>Removes a leaf and collapses its redundant one-child branch.</summary>
    public bool Remove(T value)
    {
        if (!_leaves.TryGetValue(value, out var leaf) || leaf.Parent is not { } parent)
            return false; // The root is the one group that must remain.

        parent.MutableChildren.Remove(leaf);
        _leaves.Remove(value);

        if (parent.MutableChildren.Count == 1)
        {
            var survivor = parent.MutableChildren[0];
            ReplaceNode(parent, survivor);
            FlattenIntoParentWhenPossible(survivor);
        }
        return true;
    }

    private void ReplaceNode(SplitLayoutNode<T> current, SplitLayoutNode<T> replacement)
        => ReplaceNode(current, replacement, current.Parent);

    private void ReplaceNode(
        SplitLayoutNode<T> current,
        SplitLayoutNode<T> replacement,
        SplitLayoutBranch<T>? parent)
    {
        if (parent is null)
        {
            Root = replacement;
            replacement.Parent = null;
            return;
        }

        var index = parent.MutableChildren.IndexOf(current);
        parent.MutableChildren[index] = replacement;
        replacement.Parent = parent;
    }

    private static void FlattenIntoParentWhenPossible(SplitLayoutNode<T> node)
    {
        if (node is not SplitLayoutBranch<T> branch
            || branch.Parent is not { } parent
            || parent.Orientation != branch.Orientation)
        {
            return;
        }

        var index = parent.MutableChildren.IndexOf(branch);
        parent.MutableChildren.RemoveAt(index);
        parent.MutableChildren.InsertRange(index, branch.MutableChildren);
        foreach (var child in branch.MutableChildren)
            child.Parent = parent;
    }
}

/// <summary>Maps a pointer to the nearest edge of a rectangular drop surface.</summary>
public static class SplitDropTarget
{
    public static SplitDirection Resolve(double x, double y, double width, double height)
    {
        if (width <= 0 || height <= 0)
            return SplitDirection.Right;

        var horizontal = Math.Clamp(x / width, 0, 1);
        var vertical = Math.Clamp(y / height, 0, 1);
        var distances = new[]
        {
            (Direction: SplitDirection.Left, Distance: horizontal),
            (Direction: SplitDirection.Right, Distance: 1 - horizontal),
            (Direction: SplitDirection.Up, Distance: vertical),
            (Direction: SplitDirection.Down, Distance: 1 - vertical),
        };
        return distances.MinBy(item => item.Distance).Direction;
    }
}
