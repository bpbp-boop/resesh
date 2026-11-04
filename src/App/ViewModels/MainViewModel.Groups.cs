using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace Resesh.App.ViewModels;

public sealed partial class MainViewModel
{
    internal void ObserveTab(TabViewModel tab)
    {
        tab.PropertyChanged += Tab_PropertyChanged;
        tab.IsGroupFocused = tab.Group == _focusedGroup;
    }

    internal void UnobserveTab(TabViewModel tab) => tab.PropertyChanged -= Tab_PropertyChanged;

    /// <summary>Only the focused group's selection is the active tab; the others change
    /// nothing the status bar or tab commands show.</summary>
    internal void OnGroupSelectionChanged(TabGroupViewModel group)
    {
        if (ReferenceEquals(group, _focusedGroup))
            NotifyActiveTabChanged();
    }

    private sealed partial class GroupCollection(MainViewModel window, bool background) : ObservableCollection<TabGroupViewModel>
    {
        protected override void InsertItem(int index, TabGroupViewModel item)
        {
            ArgumentNullException.ThrowIfNull(item);
            if (index < 0 || index > Count)
                throw new ArgumentOutOfRangeException(nameof(index));
            if (item.Window is not null)
                throw new InvalidOperationException("Remove the group from its window before attaching it.");
            CheckReentrancy();
            item.SetWindow(window, background);
            base.InsertItem(index, item);
        }

        protected override void RemoveItem(int index)
        {
            CheckReentrancy();
            this[index].SetWindow(null, background: false);
            base.RemoveItem(index);
        }

        protected override void ClearItems()
        {
            CheckReentrancy();
            foreach (var group in this)
                group.SetWindow(null, background: false);
            base.ClearItems();
        }

        protected override void SetItem(int index, TabGroupViewModel item) =>
            throw new NotSupportedException("Remove the old group before inserting its replacement.");

        protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
        {
            if (!background && (window._focusedGroup is null || !Contains(window._focusedGroup)))
            {
                window._focusedGroup = this.FirstOrDefault();
                // Empty only while a workspace switch swaps the shown groups. FocusedGroup
                // throws then, so notify when the next group arrives instead.
                if (window._focusedGroup is not null)
                {
                    window.OnPropertyChanged(nameof(FocusedGroup));
                    window.SyncGroupFocus();
                    window.NotifyActiveTabChanged();
                }
            }
            base.OnCollectionChanged(e);
        }
    }
}
