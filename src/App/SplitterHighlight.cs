using CommunityToolkit.WinUI.Controls;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Resesh.App;

/// <summary>
/// Decides when a splitter's divider is highlighted. Hovering highlights it only after the
/// pointer rests for <see cref="HoverDelay"/>, as VS Code's sash does, so moving the mouse
/// across a divider on the way somewhere else doesn't flash it. Pressing highlights it at
/// once and keeps it lit until the drag ends, even if the pointer outruns the strip.
/// </summary>
internal sealed class SplitterHighlight
{
    public static readonly TimeSpan HoverDelay = TimeSpan.FromMilliseconds(300);

    /// <summary>Divider thickness in DIPs while highlighted; the resting line is 1.</summary>
    public const double ActiveLineThickness = 3;

    private readonly DispatcherQueueTimer _hoverTimer;
    private readonly Action<bool> _apply;
    private bool _hovered;
    private bool _hoverShown;
    private bool _pressed;
    private bool _dragging;
    private bool _active;

    private SplitterHighlight(GridSplitter splitter, Action<bool> apply)
    {
        _apply = apply;
        // The toolkit fills the whole grab strip on hover and press, with no delay. Its
        // template resolves these keys before app resources, so they go on the splitter.
        var clear = new SolidColorBrush(Colors.Transparent);
        splitter.Resources["SizerBaseBackgroundPointerOver"] = clear;
        splitter.Resources["SizerBaseBackgroundPressed"] = clear;

        _hoverTimer = splitter.DispatcherQueue.CreateTimer();
        _hoverTimer.Interval = HoverDelay;
        _hoverTimer.IsRepeating = false;
        _hoverTimer.Tick += (_, _) =>
        {
            _hoverShown = _hovered;
            Update();
        };

        splitter.PointerEntered += (_, _) =>
        {
            _hovered = true;
            _hoverTimer.Start();
        };
        splitter.PointerExited += (_, _) => Leave();
        splitter.PointerCanceled += (_, _) => Leave();
        // The splitter marks pointer presses handled, so listen for handled ones too.
        splitter.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) =>
        {
            _pressed = true;
            // The user has found the strip, so it stays lit while hovered after release.
            _hoverShown = _hovered;
            _hoverTimer.Stop();
            Update();
        }), handledEventsToo: true);
        splitter.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, _) => Release()), handledEventsToo: true);
        splitter.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler((_, _) => Release()), handledEventsToo: true);
        splitter.ManipulationStarted += (_, _) =>
        {
            _dragging = true;
            Update();
        };
        // The splitter doesn't capture the pointer, so a release after the pointer has run
        // off the strip (past a pane's size limit, say) lands elsewhere and never reaches
        // it. The end of the drag ends the press too.
        splitter.ManipulationCompleted += (_, _) =>
        {
            _dragging = false;
            _pressed = false;
            Update();
        };
        // A splitter hidden under the pointer (the file pane closing, say) never sees the
        // pointer leave, and would come back lit.
        splitter.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, (_, _) =>
        {
            if (splitter.Visibility != Visibility.Collapsed)
                return;
            _pressed = false;
            _dragging = false;
            Leave();
        });
    }

    /// <summary>Calls <paramref name="apply"/> with true when the divider should be
    /// highlighted and false when it should rest. Lives as long as the splitter.</summary>
    public static void Attach(GridSplitter splitter, Action<bool> apply) => _ = new SplitterHighlight(splitter, apply);

    /// <summary>Thickens <paramref name="line"/> while highlighted. Negative margins keep
    /// the thicker line inside its 1px grid track, which would otherwise clip it back to
    /// one pixel (see <see cref="SplitterHitArea"/>).</summary>
    public static void SetLineThickness(Border line, bool columns, bool active)
    {
        var thickness = active ? ActiveLineThickness : 1;
        var overhang = (thickness - 1) / 2;
        if (columns)
        {
            line.Width = thickness;
            line.Margin = new Thickness(-overhang, 0, -overhang, 0);
        }
        else
        {
            line.Height = thickness;
            line.Margin = new Thickness(0, -overhang, 0, -overhang);
        }
    }

    private void Leave()
    {
        // Without a drag under way, a press that leaves the strip won't see its release.
        if (!_dragging)
            _pressed = false;
        _hovered = false;
        _hoverShown = false;
        _hoverTimer.Stop();
        Update();
    }

    private void Release()
    {
        _pressed = false;
        Update();
    }

    private void Update()
    {
        var active = _hoverShown || _pressed || _dragging;
        if (active == _active)
            return;
        _active = active;
        _apply(active);
    }
}
