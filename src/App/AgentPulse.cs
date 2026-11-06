using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace Resesh.App;

/// <summary>
/// Fades an element in and out while set: the tab's "agent working" dot, so a dot that is
/// still on screen when work stops reads as a change, not as more of the same. The fade runs
/// on the compositor, not the UI thread. With Windows animation effects off the element just
/// stays fully visible.
/// </summary>
public static class AgentPulse
{
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.RegisterAttached(
        "IsActive", typeof(bool), typeof(AgentPulse), new PropertyMetadata(false, OnChanged));

    public static void SetIsActive(DependencyObject target, bool value) => target.SetValue(IsActiveProperty, value);
    public static bool GetIsActive(DependencyObject target) => (bool)target.GetValue(IsActiveProperty);

    private const string Property = nameof(Visual.Opacity);

    private static void OnChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not UIElement element)
            return;
        var visual = ElementCompositionPreview.GetElementVisual(element);
        visual.StopAnimation(Property);
        visual.Opacity = 1;
        if ((bool)args.NewValue && AppMotion.Enabled)
            visual.StartAnimation(Property, Animation(visual.Compositor));
    }

    // One animation per compositor (one per window thread), shared by every pulsing dot.
    [ThreadStatic] private static ScalarKeyFrameAnimation? t_animation;

    private static ScalarKeyFrameAnimation Animation(Compositor compositor)
    {
        if (t_animation is { } cached && cached.Compositor == compositor)
            return cached;
        // A slow sine-like breath that never fades out entirely, so the dot stays findable.
        var ease = compositor.CreateCubicBezierEasingFunction(new Vector2(0.37f, 0), new Vector2(0.63f, 1));
        var animation = compositor.CreateScalarKeyFrameAnimation();
        animation.InsertKeyFrame(0, 1);
        animation.InsertKeyFrame(0.5f, 0.3f, ease);
        animation.InsertKeyFrame(1, 1, ease);
        animation.Duration = TimeSpan.FromMilliseconds(1600);
        animation.IterationBehavior = AnimationIterationBehavior.Forever;
        return t_animation = animation;
    }
}
