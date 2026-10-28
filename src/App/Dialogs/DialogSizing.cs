using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Resesh.App.Dialogs;

internal static class DialogSizing
{
    /// <summary>
    /// Stock ContentDialog caps its width at 548, which leaves slightly less than 500 for
    /// content once padding and the scroll gutter are taken; a 500 minimum then clips on the
    /// right. Raise the cap instead, and shrink the minimum on small windows.
    /// </summary>
    public static void FitContent(ContentDialog dialog, FrameworkElement content, XamlRoot root)
    {
        const double contentWidth = 500;
        var available = Math.Max(280, root.Size.Width - 24);
        dialog.Resources["ContentDialogMaxWidth"] = Math.Min(contentWidth + 100, available);
        content.MinWidth = Math.Min(contentWidth, available - 100);
    }

    /// <summary>
    /// Sizes a tabbed form (section tabs over a fixed-height host whose sections scroll on
    /// their own) to the space the dialog really has. ContentDialog's own content scroller
    /// has scrolling turned off, so a host taller than that space is clipped with no way to
    /// reach its last fields. The chrome (title, padding, buttons) is measured from the
    /// template rather than assumed, so a notice bar or text scaling still fits.
    /// </summary>
    public static void FitSections(ContentDialog dialog, FrameworkElement form, FrameworkElement host,
        double preferredWidth, double preferredHeight)
    {
        void Update()
        {
            if (dialog.XamlRoot is not { } root)
                return;
            form.Width = Math.Min(preferredWidth, Math.Max(240, root.Size.Width - 96));

            if (FindDescendant(dialog, "BackgroundElement") is not Border { ActualHeight: > 0 } card
                || FindDescendant(dialog, "ContentScrollViewer") is not ScrollViewer { Content: Grid contentGrid }
                || FindDescendant(dialog, "CommandSpace") is not { } commands)
                return;
            // Above the form: the card border, the content padding and the title. Below it: the
            // content padding and the button row. The title sits inside the template's content
            // scroller, which has scrolling turned off, so anything that overflows is clipped.
            var above = form.TransformToVisual(card).TransformPoint(default).Y;
            var below = contentGrid.Padding.Bottom + contentGrid.BorderThickness.Bottom
                + commands.ActualHeight + card.BorderThickness.Bottom;
            var cardMax = dialog.Resources.TryGetValue("ContentDialogMaxHeight", out var max) && max is double m
                ? m
                : Application.Current.Resources.TryGetValue("ContentDialogMaxHeight", out var appMax) && appMax is double a
                    ? a
                    : 756;
            var rest = form.ActualHeight - host.ActualHeight; // tabs, notice bar, spacing
            var available = Math.Min(cardMax, root.Size.Height - WindowMargin) - above - below - rest;
            var height = Math.Floor(Math.Min(preferredHeight, Math.Max(MinSectionHeight, available)));
            if (Math.Abs(host.Height - height) >= 1)
                host.Height = height;
        }

        void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => Update();
        // Re-fit when something above the sections (a notice bar) appears; changing the host's
        // own height leaves `rest` unchanged, so this settles after one pass.
        void OnFormSizeChanged(object sender, SizeChangedEventArgs args) => Update();

        dialog.Opened += (_, _) =>
        {
            Update();
            dialog.XamlRoot.Changed += OnRootChanged;
            form.SizeChanged += OnFormSizeChanged;
        };
        dialog.Closed += (_, _) =>
        {
            dialog.XamlRoot.Changed -= OnRootChanged;
            form.SizeChanged -= OnFormSizeChanged;
        };
    }

    // Room left between the dialog card and the window edge, so its shadow and border show.
    private const double WindowMargin = 16;
    private const double MinSectionHeight = 160;

    private static FrameworkElement? FindDescendant(DependencyObject parent, string name)
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is FrameworkElement { Name: var childName } element && childName == name)
                return element;
            if (FindDescendant(child, name) is { } found)
                return found;
        }
        return null;
    }
}
