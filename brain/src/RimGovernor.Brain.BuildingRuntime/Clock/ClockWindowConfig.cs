namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// What the window driver needs beyond the scheduler's own configuration: the profile whose native journal it polls
/// and the parameters every window it starts carries (the Go <c>ClockSchedulerConfig</c> fields the planning step
/// does not use: Profile, Start, PaceHorizonTicks, Autosave) and the poll cadence (Go
/// <c>ClockWorkerConfig</c>).
/// </summary>
public sealed record ClockWindowConfig
{
    /// <summary>The native profile whose journal the poll reads.</summary>
    public required string Profile { get; init; }

    /// <summary>The window every admission starts from; the admission fills the policy, the tick budget and the speed.</summary>
    public required ClockStart Start { get; init; }

    /// <summary>The live target speed each admission starts its window at (player acceleration only while it is Ultrafast); null keeps <see cref="Start"/>'s speed.</summary>
    public ClockTargetSpeed? TargetSpeed { get; init; }

    /// <summary>The safe horizon player acceleration's backoff keeps its critical evidence inside; zero is <see cref="PaceBackoff.DefaultHorizonTicks"/>.</summary>
    public uint PaceHorizonTicks { get; init; }

    /// <summary>Saves the game before a window starts at a paused tick; null saves nothing.</summary>
    public Func<PbCommon.Identity, long, CancellationToken, Task>? Autosave { get; init; }

    /// <summary>The most events one poll reads; 1 through 128.</summary>
    public uint PageLimit { get; init; } = 128;

    /// <summary>The poll cadence between windows.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>The poll cadence while a window runs; zero uses <see cref="PollInterval"/>.</summary>
    public TimeSpan RunningPollInterval { get; init; }

    /// <summary>The bound of one native call the poll and the driver make.</summary>
    public TimeSpan CallTimeout { get; init; } = TimeSpan.FromSeconds(60);
}
