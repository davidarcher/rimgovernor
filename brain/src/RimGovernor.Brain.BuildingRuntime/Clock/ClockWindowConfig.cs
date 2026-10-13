namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// What the window driver needs beyond the scheduler's own configuration: the profile whose native journal it polls
/// and the parameters every window it starts carries (the Go <c>ClockSchedulerConfig</c> fields the planning step
/// does not use: Profile, Start, FollowPlayerSpeed, PaceHorizonTicks, PlayerQuiet, Autosave) and the poll cadence (Go
/// <c>ClockWorkerConfig</c>).
/// </summary>
public sealed record ClockWindowConfig
{
    /// <summary>How long the player must have left the speed keys alone before the clock is re-taken from them (Go <c>DefaultPlayerQuiet</c>).</summary>
    public static readonly TimeSpan DefaultPlayerQuiet = TimeSpan.FromSeconds(3);

    /// <summary>The native profile whose journal the poll reads.</summary>
    public required string Profile { get; init; }

    /// <summary>The window every admission starts from; the admission fills the policy, the tick budget and the speed.</summary>
    public required ClockStart Start { get; init; }

    /// <summary>Starts each window at the speed the player last chose (Ultrafast when native reports none).</summary>
    public bool FollowPlayerSpeed { get; init; }

    /// <summary>The safe horizon player acceleration's backoff keeps its critical evidence inside; zero is <see cref="PaceBackoff.DefaultHorizonTicks"/>.</summary>
    public uint PaceHorizonTicks { get; init; }

    /// <summary>The quiet the player owes after a Manual speed change before the clock is re-taken; zero is <see cref="DefaultPlayerQuiet"/>.</summary>
    public TimeSpan PlayerQuiet { get; init; }

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

    internal TimeSpan EffectivePlayerQuiet => PlayerQuiet > TimeSpan.Zero ? PlayerQuiet : DefaultPlayerQuiet;
}
