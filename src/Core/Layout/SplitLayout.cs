namespace Resesh.Core.Layout;

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
    private List<double> _sizes = [];

    public SplitLayoutBranch(
        SplitOrientation orientation,
        IEnumerable<SplitLayoutNode<T>> children,
        IEnumerable<double>? sizes = null)
    {
        Orientation = orientation;
        _children = [.. children];
        foreach (var child in _children)
            child.Parent = this;
        SetSizes(sizes);
    }

    public SplitOrientation Orientation { get; }
    public IReadOnlyList<SplitLayoutNode<T>> Children => _children;
    internal List<SplitLayoutNode<T>> MutableChildren => _children;
    public override IEnumerable<T> Values => _children.SelectMany(child => child.Values);

    /// <summary>Each child's share of the branch as a relative weight, or empty while they
    /// share it equally. This is the only record of a resized pane: the layout keeps it in
    /// step as panes are split and closed, so a split nobody touched keeps its sizes.</summary>
    public IReadOnlyList<double> Sizes => _sizes;

    /// <summary>Sets each child's weight. Anything but one positive, finite weight per child
    /// means equal shares.</summary>
    public void SetSizes(IEnumerable<double>? sizes)
    {
        var list = sizes?.ToList() ?? [];
        _sizes = list.Count == _children.Count && list.All(size => double.IsFinite(size) && size > 0)
            ? list
            : [];
    }

    /// <summary>The weights with an equal share filled in for each child while there are none.</summary>
    internal List<double> ExplicitSizes() =>
        _sizes.Count == _children.Count ? [.. _sizes] : [.. Enumerable.Repeat(1.0, _children.Count)];
}

/// <summary>A recursive layout of column and row siblings, equal-size unless resized.</summary>
public sealed class SplitLayout<T> where T : notnull
{
    private readonly Dictionary<T, SplitLayoutLeaf<T>> _leaves = [];

    public SplitLayout(T initialValue)
        : this(new SplitLayoutLeaf<T>(initialValue))
    {
    }

    public SplitLayout(SplitLayoutNode<T> root)
    {
        Root = root ?? throw new ArgumentNullException(nameof(root));
        Root.Parent = null;
        IndexNode(Root);
    }

    private void IndexNode(SplitLayoutNode<T> node)
    {
        if (node is SplitLayoutLeaf<T> leaf)
        {
            if (!_leaves.TryAdd(leaf.Value, leaf))
                throw new ArgumentException("The layout contains duplicate values.", nameof(node));
            return;
        }

        if (node is not SplitLayoutBranch<T> branch
            || !Enum.IsDefined(branch.Orientation)
            || branch.Children.Count < 2)
        {
            throw new ArgumentException("The layout contains an invalid branch.", nameof(node));
        }

        foreach (var child in branch.Children)
            IndexNode(child);
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
            // The branch that gained a pane goes back to equal shares, as a new one starts;
            // halving the target instead would shrink each pane of a run of splits.
            parent.SetSizes(null);
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

        var index = parent.MutableChildren.IndexOf(leaf);
        var sizes = parent.Sizes.Count > 0 ? parent.ExplicitSizes() : null;
        parent.MutableChildren.RemoveAt(index);
        _leaves.Remove(value);
        // The panes left keep their shares of the branch and split the closed one's
        // between them in proportion.
        sizes?.RemoveAt(index);
        parent.SetSizes(sizes);

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
        // The branch's children take its share of the parent between them as they had it,
        // so no pane moves when the branch dissolves into its parent.
        var sizes = parent.ExplicitSizes();
        var share = sizes[index];
        var inner = branch.ExplicitSizes();
        var total = inner.Sum();
        sizes.RemoveAt(index);
        sizes.InsertRange(index, inner.Select(size => share * size / total));
        parent.MutableChildren.RemoveAt(index);
        parent.MutableChildren.InsertRange(index, branch.MutableChildren);
        foreach (var child in branch.MutableChildren)
            child.Parent = parent;
        parent.SetSizes(sizes);
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
