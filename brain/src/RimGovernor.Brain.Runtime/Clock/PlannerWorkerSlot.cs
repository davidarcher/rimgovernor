namespace RimGovernor.Brain.Runtime;

// The worker itself (the event, renew and step loops) is the brain host's scheduler thread, the run driver and its colony event consumer; what stays is the slot
// the session stops before it stops its clock coordinator.

/// <summary>Holds at most one clock worker for a session and stops it once.</summary>
public sealed class PlannerWorkerSlot
{
    private readonly Lock _mu = new();
    private PlannerRunDriver? _worker;
    private bool _closing;

    /// <summary>Registers the worker; throws when the slot is closing or already holds one.</summary>
    public void Attach(PlannerRunDriver worker)
    {
        ArgumentNullException.ThrowIfNull(worker);
        lock (_mu)
        {
            if (_closing || _worker is not null)
            {
                throw new ControlException("attachClockWorker: slot.closing || slot.worker != nil");
            }
            _worker = worker;
        }
    }

    /// <summary>
    /// Closes the slot and stops its worker; whether there was one. Joining may need the coordinator and journal, so
    /// no lifecycle lock is held.
    /// </summary>
    public async Task<bool> Stop(CancellationToken ct)
    {
        PlannerRunDriver? worker;
        lock (_mu)
        {
            _closing = true;
            worker = _worker;
        }
        if (worker is null)
        {
            return false;
        }
        await worker.StopAsync(ct).ConfigureAwait(false);
        return true;
    }
}
