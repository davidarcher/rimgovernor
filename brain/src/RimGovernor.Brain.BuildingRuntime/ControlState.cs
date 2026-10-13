namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// Current scope and local permission, never lease secrets. Unknown observation scope has a default snapshot; it
/// cannot authorize inspection.
/// </summary>
public readonly record struct ControlState(GenerationSnapshot Snapshot, bool ObservationKnown, bool Enabled);

public sealed partial class Control
{
    /// <summary>
    /// Whether the control's private cleanup target is <paramref name="world"/>: the world this process last
    /// acquired or observed, kept while observation is unknown. A status read that failed or was cancelled
    /// leaves the process's own Auto grant standing natively; the player's next Resume or Pause revokes it
    /// through this target before acquiring again. The target itself stays private: this authorizes nothing but
    /// that cleanup.
    /// </summary>
    public bool TargetsWorld(World world)
    {
        lock (_mu)
        {
            return !_closing && !_closed && _haveTarget && Player.PlayerWorld(_snapshot) == world;
        }
    }

    /// <summary>The current state; closed controls expose no current authority.</summary>
    public ControlState State()
    {
        lock (_mu)
        {
            return StateLocked();
        }
    }

    private ControlState StateLocked()
    {
        if (_closing || _closed)
        {
            return default;
        }
        return new ControlState(_observationKnown ? _snapshot : default, _observationKnown, LiveLocked());
    }

    /// <summary>A read from before acquisition cannot revoke the replacement permission.</summary>
    internal void DisableObserved(ControlState expected)
    {
        lock (_mu)
        {
            if (StateLocked() != expected)
            {
                throw new ConflictException();
            }
            if (_closing || _closed)
            {
                throw new ControlException("disableObserved: control.closing || control.closed");
            }
            var err = InvalidateLocked();
            if (err is not null)
            {
                throw err;
            }
        }
    }

    /// <summary>
    /// Synchronously invalidates local writes before the player coordinator persists control intent. It performs
    /// no native operation and preserves known read context. Native cleanup remains the responsibility of
    /// Manual and Close.
    /// </summary>
    public void Disable()
    {
        lock (_mu)
        {
            if (_closing || _closed)
            {
                throw new ControlException("Disable: control.closing || control.closed");
            }
            var err = InvalidateLocked();
            if (err is not null)
            {
                throw err;
            }
        }
    }
}
