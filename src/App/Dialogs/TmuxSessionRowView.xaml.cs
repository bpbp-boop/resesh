using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Resesh.App.Dialogs;

/// <summary>Presents one <see cref="TmuxSessionItem"/> in the persistent-shell lists.</summary>
public sealed partial class TmuxSessionRowView : UserControl
{
    public static readonly DependencyProperty ItemProperty = DependencyProperty.Register(
        nameof(Item), typeof(TmuxSessionItem), typeof(TmuxSessionRowView),
        new PropertyMetadata(null, (view, _) => ((TmuxSessionRowView)view).Bindings.Update()));

    public TmuxSessionItem? Item
    {
        get => (TmuxSessionItem?)GetValue(ItemProperty);
        set => SetValue(ItemProperty, value);
    }

    public TmuxSessionRowView() => InitializeComponent();

    public Brush BadgeBorder(bool attachedElsewhere) =>
        (Brush)Application.Current.Resources[attachedElsewhere ? "SystemFillColorCautionBrush" : "SessionChromeFrameBrush"];
}
