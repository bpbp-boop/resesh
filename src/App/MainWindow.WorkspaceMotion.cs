using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace Resesh.App;

// Workspace switch motion. The workspace being left goes at once; the one shown slides a
// short way into place from the side of the rail it sits on, so a workspace lower in the
// rail rises from below and one higher drops from above. Only translation, with a clip that
// keeps the panes inside the tab area while they move: a terminal's swap chain composes
// near-black under partial opacity, so nothing fades (see MainWindow.PaneMotion.cs).
public sealed partial class MainWindow
{
    private const float WorkspaceSlideDistance = 24;

    /// <summary>Slides the shown workspace in from the direction of its rail button. A switch
    /// while one is sliding restarts it from the new direction.</summary>
    private void PlayWorkspaceSwitchMotion(Guid from, Guid to)
    {
        if (!PaneMotionEnabled || !GroupArea.IsLoaded)
            return;
        var direction = RailIndex(to).CompareTo(RailIndex(from));
        if (direction == 0)
            return;
        var offset = direction * WorkspaceSlideDistance;
        foreach (var element in new UIElement[] { GroupArea, PaneFocusFrame })
        {
            ElementCompositionPreview.SetIsTranslationEnabled(element, true);
            var visual = ElementCompositionPreview.GetElementVisual(element);
            var compositor = visual.Compositor;

            var slide = compositor.CreateVector3KeyFrameAnimation();
            slide.InsertKeyFrame(0f, new Vector3(0, offset, 0));
            slide.InsertKeyFrame(1f, Vector3.Zero, PaneEaseOut(compositor));
            slide.Duration = PaneMoveDuration;
            visual.StartAnimation("Translation", slide);

            // The clip moves with the visual, so it trims whatever has slid past the area's
            // edge and opens in step with the slide.
            var clip = compositor.CreateInsetClip();
            visual.Clip = clip;
            var reveal = compositor.CreateScalarKeyFrameAnimation();
            reveal.InsertKeyFrame(0f, Math.Abs(offset));
            reveal.InsertKeyFrame(1f, 0f, PaneEaseOut(compositor));
            reveal.Duration = PaneMoveDuration;
            clip.StartAnimation(offset > 0 ? "BottomInset" : "TopInset", reveal);
        }
    }

    /// <summary>Home first, then the saved workspaces in rail order.</summary>
    private int RailIndex(Guid id)
    {
        if (id == HomeWorkspace)
            return 0;
        for (var index = 0; index < RailWorkspaces.Count; index++)
        {
            if (RailWorkspaces[index].Id == id)
                return index + 1;
        }
        return RailWorkspaces.Count + 1;
    }
}
