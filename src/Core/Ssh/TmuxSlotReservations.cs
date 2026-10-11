using Resesh.Core.Models;

namespace Resesh.Core.Ssh;

/// <summary>Process-wide ownership of persistent shell slots, independent of windows.</summary>
public sealed class TmuxSlotReservations
{
    private readonly object _gate = new();
    private readonly Dictionary<(Guid SessionId, int Slot), Owner> _slots = [];

    public Owner CreateOwner(Guid sessionId) => new(this, sessionId);

    /// <summary>A snapshot for display only; claims must still use an owner atomically.</summary>
    public IReadOnlySet<int> ReservedSlots(Guid sessionId)
    {
        lock (_gate)
            return SlotsFor(sessionId, null);
    }

    private HashSet<int> SlotsFor(Guid sessionId, Owner? except)
    {
        HashSet<int> slots = [];
        foreach (var pair in _slots)
            if (pair.Key.SessionId == sessionId && pair.Value != except)
                slots.Add(pair.Key.Slot);
        return slots;
    }

    /// <summary>One tab's reservation. Disposal also prevents a late connect worker claiming a slot.</summary>
    public sealed class Owner : IDisposable
    {
        private readonly TmuxSlotReservations _registry;
        private readonly Guid _sessionId;
        private int? _slot;
        private bool _disposed;

        internal Owner(TmuxSlotReservations registry, Guid sessionId)
        {
            _registry = registry;
            _sessionId = sessionId;
        }

        public int? Slot
        {
            get { lock (_registry._gate) return _slot; }
        }

        /// <summary>Plan from live ownership and claim its target in the same critical section.</summary>
        public TmuxConnectPlan PlanConnect(IReadOnlyList<TmuxSessionInfo> remoteSessions,
            DetachedSessionAction action, CancellationToken token = default)
        {
            lock (_registry._gate)
            {
                ThrowIfUnavailable(token);
                var reserved = _registry.SlotsFor(_sessionId, this);
                var plan = TmuxPersistence.PlanConnect(remoteSessions, reserved, action);
                if (plan is TmuxConnectPlan.StartNew start)
                {
                    // Keep the pending shell outside the deletion targets. Each detached
                    // target is claimed separately while ending it, then survivors are re-listed.
                    if (start.EndFirst.Contains(start.Slot))
                    {
                        start = start with
                        {
                            Slot = TmuxPersistence.NextAvailableSlot(
                                remoteSessions.Select(remote => remote.Slot).Concat(reserved)),
                        };
                        plan = start;
                    }
                    Reserve(start.Slot);
                }
                else if (plan is TmuxConnectPlan.Resume resume)
                    Reserve(resume.Slot);
                return plan;
            }
        }

        /// <summary>Claim an explicit choice, atomically releasing the previous slot on success.</summary>
        public bool TryReserve(int slot, CancellationToken token = default)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(slot);
            lock (_registry._gate)
            {
                ThrowIfUnavailable(token);
                if (_registry._slots.TryGetValue((_sessionId, slot), out var other) && other != this)
                    return false;
                Reserve(slot);
                return true;
            }
        }

        public void Release()
        {
            lock (_registry._gate)
                ReleaseCore();
        }

        public void Dispose()
        {
            lock (_registry._gate)
            {
                _disposed = true;
                ReleaseCore();
            }
        }

        private void ThrowIfUnavailable(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
        }

        private void Reserve(int slot)
        {
            if (_slot == slot) return;
            ReleaseCore();
            _registry._slots.Add((_sessionId, slot), this);
            _slot = slot;
        }

        private void ReleaseCore()
        {
            if (_slot is not { } slot) return;
            _registry._slots.Remove((_sessionId, slot));
            _slot = null;
        }
    }
}
