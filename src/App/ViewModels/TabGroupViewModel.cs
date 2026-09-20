using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Resesh.App.ViewModels;

/// <summary>One tab group (SecureCRT-style): its tabs and selection.</summary>
public sealed class TabGroupViewModel : ObservableObject
{
    private TabViewModel? _selectedTab;

    public ObservableCollection<TabViewModel> Tabs { get; } = [];

    public void RemoveTab(TabViewModel tab)
    {
        var index = Tabs.IndexOf(tab);
        if (index < 0)
            return;
        var selection = SelectedTab;
        if (selection == tab)
            selection = index + 1 < Tabs.Count ? Tabs[index + 1] : index > 0 ? Tabs[index - 1] : null;
        Tabs.RemoveAt(index);
        // TabView can change selection during removal. Apply the intended neighbour
        // after its collection handler, including when an inactive tab was removed.
        SelectedTab = selection;
    }

    public TabViewModel? SelectedTab
    {
        get => _selectedTab;
        set
        {
            var previous = _selectedTab;
            if (SetProperty(ref _selectedTab, value))
            {
                if (previous is not null)
                    previous.IsActive = false;
                if (value is not null)
                    value.IsActive = true;
            }
        }
    }
}
