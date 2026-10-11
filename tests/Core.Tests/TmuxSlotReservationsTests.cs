using Resesh.Core.Models;
using Resesh.Core.Ssh;

namespace Resesh.Core.Tests;

public sealed class TmuxSlotReservationsTests
{
    private static readonly Guid Id = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static TmuxSessionInfo Shell(int slot) =>
        new(slot, TmuxPersistence.SessionName(Id, slot), "/home/me", 0);

    [Fact]
    public void ClaimsConflictOnlyWithinTheSameSessionAndReleaseOnSwitchAndDispose()
    {
        var registry = new TmuxSlotReservations();
        using var first = registry.CreateOwner(Id);
        using var second = registry.CreateOwner(Id);
        using var unrelated = registry.CreateOwner(Guid.NewGuid());
        Assert.True(first.TryReserve(0));
        Assert.True(first.TryReserve(0));
        Assert.False(second.TryReserve(0));
        Assert.True(unrelated.TryReserve(0));
        Assert.True(second.TryReserve(1));
        Assert.False(first.TryReserve(1));
        Assert.Equal(0, first.Slot);
        Assert.True(first.TryReserve(2));
        Assert.True(second.TryReserve(0));
        first.Dispose();
        first.Dispose();
        Assert.True(second.TryReserve(2));
        Assert.Equal([2], registry.ReservedSlots(Id).Order());
        Assert.Throws<ObjectDisposedException>(() => first.TryReserve(3));
    }

    [Fact]
    public async Task ConcurrentExplicitClaimsHaveExactlyOneOwner()
    {
        var registry = new TmuxSlotReservations();
        var owners = Enumerable.Range(0, 16).Select(_ => registry.CreateOwner(Id)).ToArray();
        try
        {
            var claims = await Task.WhenAll(owners.Select(owner => Task.Run(() => owner.TryReserve(0))));
            Assert.Single(claims.Where(claimed => claimed));
            Assert.Single(owners.Where(owner => owner.Slot == 0));
        }
        finally
        {
            foreach (var owner in owners) owner.Dispose();
        }
        Assert.Empty(registry.ReservedSlots(Id));
    }

    [Fact]
    public async Task ConcurrentPlansAgainstTheSameRemoteListingReserveDifferentNewSlots()
    {
        var registry = new TmuxSlotReservations();
        var owners = Enumerable.Range(0, 16).Select(_ => registry.CreateOwner(Id)).ToArray();
        try
        {
            var plans = await Task.WhenAll(owners.Select(owner => Task.Run(() =>
                Assert.IsType<TmuxConnectPlan.StartNew>(owner.PlanConnect([], DetachedSessionAction.StartNew)))));
            Assert.Equal(Enumerable.Range(0, owners.Length), plans.Select(plan => plan.Slot).Order());
            Assert.Equal(owners.Length, registry.ReservedSlots(Id).Count);
        }
        finally
        {
            foreach (var owner in owners) owner.Dispose();
        }
    }

    [Fact]
    public void AutomaticResumePlansUseLiveReservationsRatherThanAnEarlierSnapshot()
    {
        var registry = new TmuxSlotReservations();
        using var first = registry.CreateOwner(Id);
        using var second = registry.CreateOwner(Id);
        TmuxSessionInfo[] listing = [Shell(0)];
        var oldSnapshot = registry.ReservedSlots(Id);
        Assert.Equal(new TmuxConnectPlan.Resume(0), first.PlanConnect(listing, DetachedSessionAction.Ask));
        Assert.Empty(oldSnapshot);
        var plan = Assert.IsType<TmuxConnectPlan.StartNew>(second.PlanConnect(listing, DetachedSessionAction.Ask));
        Assert.Equal(1, plan.Slot);
        Assert.Equal(0, first.Slot);
        Assert.Equal(1, second.Slot);
    }

    [Fact]
    public void PickerChoicesAreRecheckedAndNewSlotIsReplannedAfterAnotherConnect()
    {
        var registry = new TmuxSlotReservations();
        using var picker = registry.CreateOwner(Id);
        using var other = registry.CreateOwner(Id);
        TmuxSessionInfo[] listing = [Shell(0), Shell(1)];
        var choice = Assert.IsType<TmuxConnectPlan.Choose>(picker.PlanConnect(listing, DetachedSessionAction.Ask));
        Assert.Null(picker.Slot);
        Assert.True(other.TryReserve(0));
        Assert.False(picker.TryReserve(choice.Sessions.Single(shell => shell.Slot == 0).Slot));
        Assert.Equal(new TmuxConnectPlan.Resume(1), picker.PlanConnect(listing, DetachedSessionAction.Ask));
        picker.Release();
        other.Release();
        var otherNew = Assert.IsType<TmuxConnectPlan.StartNew>(other.PlanConnect(listing, DetachedSessionAction.StartNew));
        Assert.Equal(choice.NewSlot, otherNew.Slot);
        var pickerNew = Assert.IsType<TmuxConnectPlan.StartNew>(picker.PlanConnect(listing, DetachedSessionAction.StartNew));
        Assert.NotEqual(choice.NewSlot, pickerNew.Slot);
    }

    [Fact]
    public void FailedAttachReleasesClaimAndCancelledWorkerCannotReclaimIt()
    {
        var registry = new TmuxSlotReservations();
        using var failed = registry.CreateOwner(Id);
        using var retry = registry.CreateOwner(Id);
        using var cancellation = new CancellationTokenSource();
        Assert.Throws<IOException>(void () =>
        {
            try
            {
                failed.PlanConnect([Shell(0)], DetachedSessionAction.Ask, cancellation.Token);
                Assert.False(retry.TryReserve(0));
                throw new IOException("Shell startup failed after planning");
            }
            finally
            {
                failed.Release();
            }
        });
        Assert.Null(failed.Slot);
        Assert.True(retry.TryReserve(0));
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            failed.PlanConnect([], DetachedSessionAction.StartNew, cancellation.Token));
        Assert.Null(failed.Slot);
    }

    [Fact]
    public void EndingDetachedShellsRechecksClaimsAndHoldsEachDeletionUntilItCompletes()
    {
        var registry = new TmuxSlotReservations();
        using var connecting = registry.CreateOwner(Id);
        using var other = registry.CreateOwner(Id);
        using var ending = registry.CreateOwner(Id);
        var plan = Assert.IsType<TmuxConnectPlan.StartNew>(connecting.PlanConnect(
            [Shell(0), Shell(1)], DetachedSessionAction.EndDetachedAndStartNew));
        Assert.Equal([0, 1], plan.EndFirst);
        Assert.DoesNotContain(plan.Slot, plan.EndFirst);
        Assert.True(other.TryReserve(0));
        Assert.False(ending.TryReserve(0));
        Assert.True(ending.TryReserve(1));
        Assert.False(other.TryReserve(1));
        ending.Dispose();
        var afterDeletion = Assert.IsType<TmuxConnectPlan.StartNew>(connecting.PlanConnect(
            [Shell(0)], DetachedSessionAction.StartNew));
        Assert.Equal(1, afterDeletion.Slot);
        Assert.Equal([0, 1], registry.ReservedSlots(Id).Order());
    }
}
