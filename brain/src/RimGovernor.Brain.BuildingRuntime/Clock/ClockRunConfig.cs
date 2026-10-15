namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// What the run driver needs beyond the scheduler's own configuration: the profile whose native journal it polls, the
/// speed it asks the clock to run at, the base watch policy every Run carries, the autosave and the poll cadence (Go
/// <c>ClockSchedulerConfig</c> fields the planning step does not use, and <c>ClockWorkerConfig</c>).
/// </summary>
public sealed record ClockRunConfig
{
    /// <summary>The native profile whose journal the poll reads.</summary>
    public required string Profile { get; init; }

    /// <summary>The speed the clock runs at; a change reaches the clock as a Run at the new speed (player acceleration only while it is Ultrafast).</summary>
    public required ClockTargetSpeed TargetSpeed { get; init; }

    /// <summary>The colony watch policy with the Go-authored hazard thresholds; each Run adds the acknowledgements the watch decision names.</summary>
    public required PbClock.WatchPolicy Policy { get; init; }

    /// <summary>Saves the game while the clock is held for it; null saves nothing.</summary>
    public Func<PbCommon.Identity, long, CancellationToken, Task>? Autosave { get; init; }

    /// <summary>The game ticks between the autosave holds: the autosaver's own interval, so a hold is never spent on a save it would skip.</summary>
    public long AutosaveEveryTicks { get; init; }

    /// <summary>The most events one poll reads; 1 through 128.</summary>
    public uint PageLimit { get; init; } = 128;

    /// <summary>The poll cadence between captures.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>The longest the clock may go without a heartbeat before its ceiling drops to Normal is five seconds; the driver beats this often.</summary>
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>The bound of one native call the poll and the driver make.</summary>
    public TimeSpan CallTimeout { get; init; } = TimeSpan.FromSeconds(60);
}
