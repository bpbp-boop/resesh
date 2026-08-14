using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Sessions.App.ViewModels;

/// <summary>One tab group (SecureCRT-style): its tabs and selection.</summary>
public sealed class TabGroupViewModel : ObservableObject
{
    private TabViewModel? _selectedTab;

    public ObservableCollection<TabViewModel> Tabs { get; } = [];

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
