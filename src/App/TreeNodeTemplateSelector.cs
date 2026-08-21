using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Resesh.App.ViewModels;

namespace Resesh.App;

public sealed partial class TreeNodeTemplateSelector : DataTemplateSelector
{
    public DataTemplate? FolderTemplate { get; set; }
    public DataTemplate? SessionTemplate { get; set; }
    public DataTemplate? LocalRootTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) => item switch
    {
        TreeNodeViewModel { IsLocalRoot: true } => LocalRootTemplate ?? FolderTemplate,
        TreeNodeViewModel { IsFolder: true } => FolderTemplate,
        _ => SessionTemplate,
    };

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) =>
        SelectTemplateCore(item);
}
