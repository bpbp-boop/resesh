namespace Resesh.Core.Layout;

/// <summary>Ordered Explorer-style selection with a stable range anchor.</summary>
public sealed class OrderedSelection<T> where T : class
{
    private readonly Action<T, bool> _setSelected;
    private readonly IEqualityComparer<T> _comparer;
    private readonly List<T> _items = [];
    private T? _anchor;

    public OrderedSelection(Action<T, bool> setSelected, IEqualityComparer<T>? comparer = null)
    {
        _setSelected = setSelected;
        _comparer = comparer ?? EqualityComparer<T>.Default;
    }

    public IReadOnlyList<T> Items => _items;

    public bool Contains(T item) => _items.Contains(item, _comparer);

    public void Clear()
    {
        foreach (var item in _items)
            _setSelected(item, false);
        _items.Clear();
        _anchor = null;
    }

    public void SelectOnly(T item)
    {
        Clear();
        Add(item);
        _anchor = item;
    }

    public void Toggle(T item)
    {
        var index = IndexOf(_items, item);
        if (index >= 0)
        {
            _setSelected(_items[index], false);
            _items.RemoveAt(index);
        }
        else
        {
            Add(item);
        }
        _anchor = item;
    }

    public void SelectRangeTo(T item, IReadOnlyList<T> visibleItems)
    {
        var anchor = _anchor;
        var from = anchor is null ? -1 : IndexOf(visibleItems, anchor);
        var to = IndexOf(visibleItems, item);
        if (from < 0 || to < 0)
        {
            SelectOnly(item);
            return;
        }

        foreach (var selected in _items)
            _setSelected(selected, false);
        _items.Clear();

        var (lo, hi) = from <= to ? (from, to) : (to, from);
        for (var index = lo; index <= hi; index++)
            Add(visibleItems[index]);
        _anchor = anchor;
    }

    /// <summary>Synchronizes custom selection when native keyboard navigation moves focus.</summary>
    public void SelectForKeyboardFocus(
        T item,
        IReadOnlyList<T> visibleItems,
        bool extendRange,
        bool preserveSelection)
    {
        if (preserveSelection)
            return;
        if (extendRange)
            SelectRangeTo(item, visibleItems);
        else
            SelectOnly(item);
    }

    /// <summary>Returns the custom selected set when dragging within it; otherwise selects the dragged item.</summary>
    public IReadOnlyList<T> BeginDrag(T item)
    {
        if (!Contains(item))
            SelectOnly(item);
        return _items.ToList();
    }

    private void Add(T item)
    {
        _setSelected(item, true);
        _items.Add(item);
    }

    private int IndexOf(IReadOnlyList<T> items, T item)
    {
        for (var index = 0; index < items.Count; index++)
            if (_comparer.Equals(items[index], item))
                return index;
        return -1;
    }
}
