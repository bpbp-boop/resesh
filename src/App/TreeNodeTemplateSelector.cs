using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Sessions.App.ViewModels;

namespace Sessions.App;

public sealed partial class TreeNodeTemplateSelector : DataTemplateSelector
{
    public DataTemplate? FolderTemplate { get; set; }
    public DataTemplate? SessionTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) =>
        item is TreeNodeViewModel { IsFolder: true } ? FolderTemplate : SessionTemplate;

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) =>
        SelectTemplateCore(item);
}
