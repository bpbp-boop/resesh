using Resesh.App.Controls;

namespace Resesh.AppLogic.Tests;

public sealed class FilePaneActivityTests
{
    [Fact]
    public void FirstLoadFailureLeavesThePaneFailedAndRetryGoesHome()
    {
        var pane = new FilePaneActivity();
        Assert.Equal(NavigationRequest.Start, pane.RequestNavigation(new("/srv")));
        Assert.Equal(FilePaneState.Loading, pane.State);

        Assert.Null(pane.EndNavigation(listed: false));

        Assert.Equal(FilePaneState.Failed, pane.State);
        Assert.False(pane.HasListing);
        Assert.Null(pane.RetryTarget("/srv").Path);
        Assert.Equal(NavigationRequest.Start, pane.RequestNavigation(new(null)));
    }

    [Fact]
    public void AnEmptyFolderCountsAsLoaded()
    {
        var pane = new FilePaneActivity();
        pane.RequestNavigation(new("/empty"));
        pane.EndNavigation(listed: true); // zero entries is still a listing

        pane.RequestNavigation(new("/missing"));
        pane.EndNavigation(listed: false);

        Assert.True(pane.HasListing);
        Assert.Equal(FilePaneState.Idle, pane.State);
        Assert.Equal("/empty", pane.RetryTarget("/empty").Path);
    }

    [Fact]
    public void NavigationWhileLoadingQueuesOnlyTheLatest()
    {
        var pane = new FilePaneActivity();
        pane.RequestNavigation(new("/a"));

        Assert.Equal(NavigationRequest.Queued, pane.RequestNavigation(new("/b")));
        Assert.Equal(NavigationRequest.Queued, pane.RequestNavigation(new("/c", "note")));

        Assert.Equal(new FileNavigation("/c", "note"), pane.EndNavigation(listed: false));
        Assert.Equal(FilePaneState.Loading, pane.State);
        Assert.Null(pane.EndNavigation(listed: true));
        Assert.Equal(FilePaneState.Idle, pane.State);
    }

    [Fact]
    public void OperationsAndNavigationExcludeEachOther()
    {
        var pane = new FilePaneActivity();
        pane.RequestNavigation(new(null));
        Assert.False(pane.TryBeginOperation());
        pane.EndNavigation(listed: true);

        Assert.True(pane.TryBeginOperation());
        Assert.True(pane.IsBusy);
        Assert.False(pane.TryBeginOperation());
        Assert.Equal(NavigationRequest.Refused, pane.RequestNavigation(new("/x")));

        pane.EndOperation();
        Assert.Equal(FilePaneState.Idle, pane.State);
        Assert.Equal(NavigationRequest.Start, pane.RequestNavigation(new("/x")));
    }

    [Fact]
    public void DisposedPaneRefusesEverythingAndDropsTheQueue()
    {
        var pane = new FilePaneActivity();
        pane.RequestNavigation(new("/a"));
        pane.RequestNavigation(new("/b"));

        pane.Dispose();

        Assert.Null(pane.EndNavigation(listed: true));
        Assert.Equal(FilePaneState.Disposed, pane.State);
        Assert.Equal(NavigationRequest.Refused, pane.RequestNavigation(new("/c")));
        Assert.False(pane.TryBeginOperation());
        pane.EndOperation();
        Assert.Equal(FilePaneState.Disposed, pane.State);
    }
}
