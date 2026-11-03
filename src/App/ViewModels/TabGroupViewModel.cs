using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Resesh.App.ViewModels;

/// <summary>One tab group (SecureCRT-style): its tabs and selection.</summary>
public sealed partial class TabGroupViewModel : ObservableObject
{
    private TabViewModel? _selectedTab;

    internal MainViewModel? Window { get; private set; }
    private bool _background;

    public TabGroupViewModel() => Tabs = new TabCollection(this);

    public ObservableCollection<TabViewModel> Tabs { get; }

    public void RemoveTab(TabViewModel tab) => Tabs.Remove(tab);

    internal void SetWindow(MainViewModel? window, bool background)
    {
        foreach (var tab in Tabs)
            Window?.UnobserveTab(tab);
        Window = window;
        _background = background;
        foreach (var tab in Tabs)
        {
            if (window is null)
            {
                // A group moving between a window's shown and parked workspaces passes through
                // here; keep the flag until it lands so its tabs never seem to come on screen.
                tab.IsGroupFocused = false;
                continue;
            }
            tab.IsInBackgroundWorkspace = background;
            window.ObserveTab(tab);
        }
    }

    public TabViewModel? SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (value is not null && !Tabs.Contains(value))
                throw new ArgumentException("The selected tab must belong to this group.", nameof(value));
            var previous = _selectedTab;
            if (SetProperty(ref _selectedTab, value))
            {
                if (previous is not null)
                    previous.IsActive = false;
                if (value is not null)
                    value.IsActive = true;
                Window?.OnGroupSelectionChanged(this);
            }
        }
    }

    private void TabChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(TabViewModel.IsPinned) || sender is not TabViewModel tab)
            return;
        // Pin changes can come from restore, menus or another caller. Keep one boundary.
        var selected = SelectedTab;
        var target = Tabs.Count(item => item.IsPinned) - (tab.IsPinned ? 1 : 0);
        var index = Tabs.IndexOf(tab);
        if (index != target)
            Tabs.Move(index, target);
        SelectedTab = selected;
    }

    // Partial lets C#/WinRT generate the collection interfaces used by TabView, including AOT.
    private sealed partial class TabCollection(TabGroupViewModel group) : ObservableCollection<TabViewModel>
    {
        protected override void InsertItem(int index, TabViewModel item)
        {
            ArgumentNullException.ThrowIfNull(item);
            if (index < 0 || index > Count)
                throw new ArgumentOutOfRangeException(nameof(index));
            if (item.Group is not null)
                throw new InvalidOperationException("Detach the tab from its group before attaching it.");
            CheckReentrancy();
            var pins = this.Count(tab => tab.IsPinned);
            index = item.IsPinned ? Math.Min(index, pins) : Math.Max(index, pins);
            item.Group = group;
            item.PropertyChanged += group.TabChanged;
            item.IsInBackgroundWorkspace = group._background;
            group.Window?.ObserveTab(item);
            base.InsertItem(index, item);
        }

        protected override void RemoveItem(int index)
        {
            CheckReentrancy();
            var tab = this[index];
            var selected = group.SelectedTab;
            if (selected == tab)
                selected = index + 1 < Count ? this[index + 1] : index > 0 ? this[index - 1] : null;
            Release(tab);
            base.RemoveItem(index);
            // TabView can change selection during removal. Restore the intended neighbour.
            group.SelectedTab = selected is not null && Contains(selected) ? selected : null;
        }

        protected override void ClearItems()
        {
            CheckReentrancy();
            foreach (var tab in this)
                Release(tab);
            base.ClearItems();
            group.SelectedTab = null;
        }

        private void Release(TabViewModel tab)
        {
            group.Window?.UnobserveTab(tab);
            tab.PropertyChanged -= group.TabChanged;
            tab.Group = null;
            tab.IsActive = false;
            tab.IsGroupFocused = false;
        }

        protected override void MoveItem(int oldIndex, int newIndex)
        {
            if (newIndex < 0 || newIndex >= Count)
                throw new ArgumentOutOfRangeException(nameof(newIndex));
            var pins = this.Count(tab => tab.IsPinned);
            newIndex = this[oldIndex].IsPinned
                ? Math.Clamp(newIndex, 0, pins - 1)
                : Math.Clamp(newIndex, pins, Count - 1);
            base.MoveItem(oldIndex, newIndex);
        }

        protected override void SetItem(int index, TabViewModel item) =>
            throw new NotSupportedException("Remove the old tab before inserting its replacement.");
    }
}
