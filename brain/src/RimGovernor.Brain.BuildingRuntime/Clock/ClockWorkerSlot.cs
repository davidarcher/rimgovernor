namespace RimGovernor.Brain.BuildingRuntime;

// Port of the clockWorkerSlot of internal/buildingruntime/clock_worker_session.go. The worker itself (the poll,
// renew and step goroutines) is the brain host's scheduler thread and the window driver; what stays is the slot
// the session stops before it stops its clock coordinator.

/// <summary>Whatever drives the clock windows of a session and must be joined before the session's clock stops (Go <c>clockWorkerStopper</c>).</summary>
public interface IClockWorkerStopper
{
    Task StopAsync(CancellationToken ct);
}

/// <summary>Holds at most one clock worker for a session and stops it once (Go <c>clockWorkerSlot</c>).</summary>
public sealed class ClockWorkerSlot
{
    private readonly Lock _mu = new();
    private IClockWorkerStopper? _worker;
    private bool _closing;

    /// <summary>Registers the worker; throws when the slot is closing or already holds one (Go <c>attachClockWorker</c>).</summary>
    public void Attach(IClockWorkerStopper worker)
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
    /// no lifecycle lock is held (Go <c>stop</c>).
    /// </summary>
    public async Task<bool> Stop(CancellationToken ct)
    {
        IClockWorkerStopper? worker;
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
