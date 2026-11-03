using Resesh.App.Controls;
using Resesh.App.ViewModels;
using Resesh.Core.Models;

namespace Resesh.AppLogic.Tests;

public sealed class TabStripSizingTests
{
    [Fact]
    public void FullWidthTabsNeedNoFullRefresh()
    {
        Assert.False(TabStripSizing.HasShrunkTabs([220, 220, 219.6]));
        Assert.False(TabStripSizing.HasShrunkTabs([]));
    }

    [Fact]
    public void ANarrowerRealizedTabCountsAsShrunk()
    {
        Assert.True(TabStripSizing.HasShrunkTabs([220, 219.4]));
        Assert.True(TabStripSizing.HasShrunkTabs([100]));
    }

    [Fact]
    public void UnrealizedTabsAreIgnoredWhenCheckingForShrinking()
    {
        Assert.False(TabStripSizing.HasShrunkTabs([0, 220, 0]));
    }

    [Fact]
    public void ActionsKeepTheWidestMeasurementPlusPadding()
    {
        Assert.Equal(230, TabStripSizing.ReserveActionsWidth(224, visible: true, actualWidth: 224));
        Assert.Equal(224, TabStripSizing.ReserveActionsWidth(224, visible: true, actualWidth: 100));
    }

    [Fact]
    public void CollapsedOrUnmeasuredActionsKeepTheirReservedWidth()
    {
        Assert.Equal(224, TabStripSizing.ReserveActionsWidth(224, visible: false, actualWidth: 400));
        Assert.Equal(224, TabStripSizing.ReserveActionsWidth(224, visible: true, actualWidth: 0));
    }

    [Theory]
    [InlineData(523.9, 3, true)]
    [InlineData(524, 3, false)]
    [InlineData(1000, 3, false)]
    public void ActionsOverflowOnceMinimumWidthTabsAndActionsNoLongerFit(double strip, int tabs, bool overflow)
    {
        Assert.Equal(overflow, TabStripSizing.UseOverflow(strip, tabs, actionsWidth: 224));
    }

    [Fact]
    public void AnUnmeasuredOrEmptyStripNeverOverflows()
    {
        Assert.False(TabStripSizing.UseOverflow(0, 10, 224));
        Assert.False(TabStripSizing.UseOverflow(100, 0, 224));
    }

    [Fact]
    public void AnEmptyGroupDrawsNoDivider()
    {
        Assert.Equal(new TabDividerWidths(0, 0, false),
            TabStripSizing.DividerWidths(800, hasSelection: false, dragPreviewVisible: false, (10, 100)));
    }

    [Fact]
    public void ADragClosesTheActiveTabGap()
    {
        Assert.Equal(new TabDividerWidths(800, 0, false),
            TabStripSizing.DividerWidths(800, hasSelection: true, dragPreviewVisible: true, (10, 100)));
    }

    [Fact]
    public void TheDividerLeavesAGapUnderTheActiveTab()
    {
        Assert.Equal(new TabDividerWidths(200, 500, false),
            TabStripSizing.DividerWidths(800, true, false, (200, 100)));
    }

    [Fact]
    public void ADividerGapIsClippedToTheStrip()
    {
        Assert.Equal(new TabDividerWidths(0, 750, false),
            TabStripSizing.DividerWidths(800, true, false, (-50, 100)));
        Assert.Equal(new TabDividerWidths(800, 0, false),
            TabStripSizing.DividerWidths(800, true, false, (900, 100)));
    }

    [Fact]
    public void ANewTabWithoutBoundsHidesTheDividerUntilLayout()
    {
        Assert.Equal(new TabDividerWidths(0, 0, true), TabStripSizing.DividerWidths(800, true, false, null));
        Assert.Equal(new TabDividerWidths(0, 0, true), TabStripSizing.DividerWidths(800, true, false, (100, 0)));
    }

    [Fact]
    public void TheDividerSlidesFromWhereTheActiveTabWasDrawn()
    {
        var slide = TabStripSizing.DividerSlide(800, origin: (0, 100), layoutLeft: 200, width: 100);
        Assert.Equal(new TabDividerSlide(0, 200, 700, 500), slide);
        Assert.True(slide.Moves);
    }

    [Fact]
    public void ADividerThatMovesLessThanHalfAPixelDoesNotSlide()
    {
        Assert.False(TabStripSizing.DividerSlide(800, (200, 100), 200.4, 100).Moves);
        Assert.True(TabStripSizing.DividerSlide(800, (200, 100), 200, 100.5).Moves);
    }

    [Fact]
    public void ADividerSegmentScalesWithinItsLargerWidth()
    {
        Assert.Equal(new TabDividerScale(200, 0.5, 1), TabStripSizing.DividerScale(100, 200));
        Assert.Equal(new TabDividerScale(200, 1, 0), TabStripSizing.DividerScale(200, 0));
        Assert.Equal(0, TabStripSizing.DividerScale(0, 0).Width);
    }
}

public sealed class TabWidthHoldTests
{
    [Fact]
    public void ACloseOutsideTheStripRefreshesWidthsAtOnce()
    {
        var hold = new TabWidthHold();
        Assert.Equal(TabWidthAction.RefreshFull, hold.TabRemoved(anyShrunk: true, index: 0, remaining: 3));
        Assert.False(hold.HoldPending);
    }

    [Fact]
    public void FullWidthTabsNeedNothingAfterAClose()
    {
        var hold = new TabWidthHold();
        hold.PointerEntered();
        Assert.Equal(TabWidthAction.None, hold.TabRemoved(anyShrunk: false, index: 0, remaining: 3));
        Assert.False(hold.HoldPending);
    }

    [Fact]
    public void ACloseUnderThePointerHoldsWidthsUntilThePointerLeaves()
    {
        var hold = new TabWidthHold();
        hold.PointerEntered();
        Assert.Equal(TabWidthAction.Hold, hold.TabRemoved(true, index: 1, remaining: 3));
        Assert.True(hold.HoldPending);

        Assert.Equal(TabWidthAction.CaptureAndRefreshFull, hold.PointerExited());
        Assert.False(hold.HoldPending);
        Assert.False(hold.PointerInStrip);
        Assert.Equal(TabWidthAction.None, hold.PointerExited());
    }

    [Fact]
    public void ClosingTheLastTabUnderThePointerDoesNotHold()
    {
        var hold = new TabWidthHold();
        hold.PointerEntered();
        // Nothing slides under the pointer after the end of the strip.
        Assert.Equal(TabWidthAction.RefreshFull, hold.TabRemoved(true, index: 3, remaining: 3));
    }

    [Fact]
    public void LeavingTheStripWithoutAHoldDoesNothing()
    {
        var hold = new TabWidthHold();
        hold.PointerEntered();
        Assert.Equal(TabWidthAction.None, hold.PointerExited());
    }

    [Fact]
    public void AStripLeavingTheScreenReleasesItsHeldWidths()
    {
        var hold = new TabWidthHold();
        hold.PointerEntered();
        hold.TabRemoved(true, 0, 2);
        Assert.Equal(TabWidthAction.RefreshFull, hold.Release());
        Assert.False(hold.PointerInStrip);
        Assert.False(hold.HoldPending);
        Assert.Equal(TabWidthAction.None, hold.Release());
    }

    [Fact]
    public void AReleasedStripForgetsThePointer()
    {
        var hold = new TabWidthHold();
        hold.PointerEntered();
        hold.Release();
        Assert.Equal(TabWidthAction.RefreshFull, hold.TabRemoved(true, 0, 2));
    }
}

public sealed class TabStripMotionTests
{
    [Fact]
    public void ATabDrawnAwayFromItsSlotSlidesFromThere()
    {
        Assert.Equal(new TabMotionStep(TabMotionKind.Slide, -120), TabStripMotion.Plan(-120));
        Assert.Equal(TabMotionKind.Slide, TabStripMotion.Plan(0.5).Kind);
    }

    [Fact]
    public void ATabWithinHalfAPixelOfItsSlotSnaps()
    {
        Assert.Equal(TabMotionKind.Snap, TabStripMotion.Plan(0.49).Kind);
        Assert.Equal(TabMotionKind.Snap, TabStripMotion.Plan(-0.2).Kind);
    }

    [Fact]
    public void ATabNewSinceTheSnapshotFadesIn()
    {
        Assert.Equal(TabMotionKind.FadeIn, TabStripMotion.Plan(null).Kind);
    }

    [Fact]
    public void AnInFlightSlideEasesOutCubically()
    {
        Assert.Equal(100f, TabStripMotion.EasedOffset(100, 0));
        Assert.Equal(12.5f, TabStripMotion.EasedOffset(100, 0.5));
        Assert.Equal(-12.5f, TabStripMotion.EasedOffset(-100, 0.5));
    }

    [Fact]
    public void AFinishedSlideLeavesNoOffset()
    {
        Assert.Equal(0f, TabStripMotion.EasedOffset(100, 1));
        Assert.Equal(0f, TabStripMotion.EasedOffset(100, 3));
    }

    [Fact]
    public void PendingMotionHoldsTheOldDividerGap()
    {
        Assert.Equal(TabDividerOwner.Motion, TabStripMotion.DividerOwner(true, false, false, true));
    }

    [Fact]
    public void WithoutMotionOrASlideLayoutSetsTheDivider()
    {
        Assert.Equal(TabDividerOwner.Layout, TabStripMotion.DividerOwner(false, false, true, false));
    }

    [Fact]
    public void TheDividerSlideKeepsTheDividerWhileItsTabStaysSelected()
    {
        Assert.Equal(TabDividerOwner.Motion, TabStripMotion.DividerOwner(false, true, true, false));
    }

    [Fact]
    public void ANewSelectionOrADragCancelsTheDividerSlide()
    {
        Assert.Equal(TabDividerOwner.CancelSlide, TabStripMotion.DividerOwner(false, true, false, false));
        Assert.Equal(TabDividerOwner.CancelSlide, TabStripMotion.DividerOwner(false, true, true, true));
    }
}

public sealed class TabStripActionStateTests
{
    private static TabViewModel Tab(Session? session = null) => new(
        session ?? new Session { Name = "test", Host = "server" },
        new ViewModelEnvironment
        {
            CurrentTheme = () => "dark", ResolveTheme = theme => theme,
            ShowAgentIcons = () => true, IsSessionVisible = _ => true,
            ApplySessionSettings = _ => { }, ReportError = _ => { },
        });

    [Fact]
    public void ActionButtonsDimOnlyInAnUnfocusedGroup()
    {
        Assert.Equal(0.55, TabStripActionState.Opacity(false));
        Assert.Equal(1.0, TabStripActionState.Opacity(true));
        Assert.Equal(1.0, TabStripActionState.Opacity(null));
    }

    [Fact]
    public void TheCurrentFolderActionNeedsALiveUnlockedConnectionWithFiles()
    {
        Assert.False(TabStripActionState.CurrentFolderEnabled(null));

        var tab = Tab();
        Assert.False(TabStripActionState.CurrentFolderEnabled(tab));
        tab.MarkConnected();
        Assert.True(TabStripActionState.CurrentFolderEnabled(tab));
        tab.Lock("secret");
        Assert.False(TabStripActionState.CurrentFolderEnabled(tab));
    }

    [Fact]
    public void TheCurrentFolderActionIsOffForSessionsWithoutAFilePane()
    {
        var telnet = Tab(new Session { Name = "switch", Host = "10.0.0.1", Kind = SessionKind.Telnet });
        telnet.MarkConnected();
        Assert.False(TabStripActionState.CurrentFolderEnabled(telnet));
    }

    [Fact]
    public void ActionLabelsDescribeWhatAClickWillDo()
    {
        Assert.Equal("Hide File Pane", TabStripActionState.FilePaneLabel(true));
        Assert.Equal("Show File Pane", TabStripActionState.FilePaneLabel(false));
        Assert.Equal("Hide commands", TabStripActionState.CommandsLabel(true));
        Assert.Equal("Show commands", TabStripActionState.CommandsLabel(false));
        Assert.Equal("Stop recording", TabStripActionState.RecordingLabel(true));
        Assert.Equal("Start recording", TabStripActionState.RecordingLabel(false));
        Assert.Equal("Return to live terminal", TabStripActionState.RewindLabel(true));
        Assert.Equal("Instant rewind", TabStripActionState.RewindLabel(false));
    }

    [Fact]
    public void TheCloseButtonNamesItsTabAndHasAStableId()
    {
        var tab = Tab();
        Assert.Equal($"Close {tab.Header}", tab.CloseAutomationName);
        Assert.StartsWith("TabClose_", tab.CloseAutomationId);
        Assert.Equal(tab.CloseAutomationId, tab.CloseAutomationId);
        Assert.NotEqual(tab.CloseAutomationId, Tab().CloseAutomationId);
    }
}
