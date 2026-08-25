using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Resesh.App.Controls;

public sealed class WorkspaceLayoutIconNode
{
    public bool SplitIntoColumns { get; set; }
    public IReadOnlyList<WorkspaceLayoutIconNode> Children { get; set; } = [];
}

/// <summary>A fixed square thumbnail of a saved workspace split tree.</summary>
public sealed class WorkspaceLayoutIcon : Grid
{
    public static readonly DependencyProperty LayoutProperty = DependencyProperty.Register(
        nameof(Layout),
        typeof(WorkspaceLayoutIconNode),
        typeof(WorkspaceLayoutIcon),
        new PropertyMetadata(null, OnLayoutPropertyChanged));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke),
        typeof(Brush),
        typeof(WorkspaceLayoutIcon),
        new PropertyMetadata(null, OnLayoutPropertyChanged));

    public WorkspaceLayoutIconNode? Layout
    {
        get => (WorkspaceLayoutIconNode?)GetValue(LayoutProperty);
        set => SetValue(LayoutProperty, value);
    }

    public Brush? Stroke
    {
        get => (Brush?)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public WorkspaceLayoutIcon()
    {
        Width = 16;
        Height = 16;
        IsHitTestVisible = false;
        Loaded += (_, _) => Rebuild();
    }

    private static void OnLayoutPropertyChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((WorkspaceLayoutIcon)sender).Rebuild();

    private void Rebuild()
    {
        Children.Clear();
        Children.Add(new Border
        {
            BorderBrush = Stroke,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(1),
            Child = BuildPartition(Layout ?? new WorkspaceLayoutIconNode()),
        });
    }

    private FrameworkElement BuildPartition(WorkspaceLayoutIconNode node)
    {
        if (node.Children.Count == 0)
            return new Grid();

        var partition = new Grid();
        for (var index = 0; index < node.Children.Count; index++)
        {
            var childPosition = index * 2;
            if (node.SplitIntoColumns)
            {
                partition.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = new GridLength(1, GridUnitType.Star),
                });
                SetColumn(AddChild(partition, BuildPartition(node.Children[index])), childPosition);
                if (index < node.Children.Count - 1)
                {
                    partition.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
                    SetColumn(AddChild(partition, new Border { Background = Stroke }), childPosition + 1);
                }
            }
            else
            {
                partition.RowDefinitions.Add(new RowDefinition
                {
                    Height = new GridLength(1, GridUnitType.Star),
                });
                SetRow(AddChild(partition, BuildPartition(node.Children[index])), childPosition);
                if (index < node.Children.Count - 1)
                {
                    partition.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1) });
                    SetRow(AddChild(partition, new Border { Background = Stroke }), childPosition + 1);
                }
            }
        }
        return partition;
    }

    private static FrameworkElement AddChild(Grid parent, FrameworkElement child)
    {
        parent.Children.Add(child);
        return child;
    }
}
