using RimGovernor.Brain.HttpChannel;
using RimGovernor.Brain.NativeFacade;

namespace RimGovernor.Brain.Host;

/// <summary>
/// The launch flag that asks the mod to host the brain in its own process (#3082). Without it the mod behaves as it
/// did before and no brain thread, database or telemetry file exists.
/// </summary>
public static class InProcLaunch
{
    /// <summary>The command-line flag (the game's own launch arguments).</summary>
    public const string Flag = "-rimgovernor-inproc";

    /// <summary>Whether <paramref name="arguments"/> carry <see cref="Flag"/>, compared ordinally.</summary>
    public static bool Requested(IEnumerable<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return arguments.Any(a => string.Equals(a, Flag, StringComparison.Ordinal));
    }
}

/// <summary>
/// The call bounds the host runs under. <see cref="Standard"/> carries the values the Go controller used
/// (<c>cmd/rimgovernor</c> serve_building.go and serve_clock.go); none of them is a watchdog threshold.
/// </summary>
public sealed record BrainTimings
{
    /// <summary>The bound of one native call made for control and the executor (Go <c>min(--timeout, 10s)</c>).</summary>
    public required TimeSpan CallTimeout { get; init; }

    /// <summary>The bound of one player request, which queues for the player gate and makes native calls (Go <c>serviceClockStepTimeout</c>).</summary>
    public required TimeSpan PlayerCallTimeout { get; init; }

    /// <summary>The bound of one journal write on the player and executor paths.</summary>
    public required TimeSpan JournalTimeout { get; init; }

    /// <summary>The executor's limits (Go <c>executor.Limits</c> of the serve composition).</summary>
    public required RimGovernor.Brain.Executor.Limits Executor { get; init; }

    /// <summary>The cadence of a scheduler step that no evidence woke (Go <c>ClockWorkerConfig.StepInterval</c>).</summary>
    public required TimeSpan StepInterval { get; init; }

    /// <summary>The bound of one scheduler step (Go <c>ClockWorkerConfig.StepTimeout</c>).</summary>
    public required TimeSpan StepTimeout { get; init; }

    /// <summary>The Worker's step cadence, its longest back-off, its step bound and its renew bounds (Go <c>WorkerConfig</c>).</summary>
    public required TimeSpan WorkerStepInterval { get; init; }

    public required TimeSpan WorkerMaxBackoff { get; init; }

    public required TimeSpan WorkerStepTimeout { get; init; }

    public required TimeSpan WorkerRenewInterval { get; init; }

    public required TimeSpan WorkerRenewTimeout { get; init; }

    /// <summary>How often the auto-resumer looks at the observed world (Go <c>serveRefresh</c>).</summary>
    public required TimeSpan RefreshInterval { get; init; }

    /// <summary>The bound of one attempt to drain the session at shutdown (Go <c>drainBuilding</c>).</summary>
    public required TimeSpan ShutdownAttemptTimeout { get; init; }

    /// <summary>How many drain attempts shutdown makes; Go retried without bound, which would hold the game's exit hostage.</summary>
    public required int ShutdownAttempts { get; init; }

    /// <summary>The bound of the final telemetry flush (Go: 5 s).</summary>
    public required TimeSpan TelemetryFlushTimeout { get; init; }

    /// <summary>The timings the Go controller ran with, for a native call bound of <paramref name="nativeTimeout"/> (Go <c>--timeout</c>, 1 s to 1 min).</summary>
    public static BrainTimings Standard(TimeSpan nativeTimeout)
    {
        if (nativeTimeout < TimeSpan.FromSeconds(1) || nativeTimeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(nativeTimeout), "the native call timeout is 1 s to 1 min");
        }
        return new BrainTimings
        {
            CallTimeout = TimeSpan.FromTicks(Math.Min(nativeTimeout.Ticks, TimeSpan.FromSeconds(10).Ticks)),
            PlayerCallTimeout = TimeSpan.FromMinutes(1),
            JournalTimeout = TimeSpan.FromSeconds(3),
            Executor = new RimGovernor.Brain.Executor.Limits
            {
                MaxAge = TimeSpan.FromSeconds(5),
                RunTimeout = TimeSpan.FromSeconds(8),
                JournalTimeout = TimeSpan.FromSeconds(3),
            },
            StepInterval = TimeSpan.FromSeconds(1),
            StepTimeout = TimeSpan.FromMinutes(1),
            WorkerStepInterval = TimeSpan.FromSeconds(1),
            WorkerMaxBackoff = TimeSpan.FromSeconds(10),
            WorkerStepTimeout = TimeSpan.FromTicks(Math.Min(nativeTimeout.Ticks, TimeSpan.FromSeconds(8).Ticks)),
            WorkerRenewInterval = TimeSpan.FromSeconds(5),
            WorkerRenewTimeout = TimeSpan.FromSeconds(5),
            RefreshInterval = TimeSpan.FromSeconds(3),
            ShutdownAttemptTimeout = TimeSpan.FromSeconds(10),
            ShutdownAttempts = 3,
            TelemetryFlushTimeout = TimeSpan.FromSeconds(5),
        };
    }

    internal void Validate()
    {
        static void Positive(TimeSpan value, string name)
        {
            if (value <= TimeSpan.Zero)
            {
                throw new ArgumentException($"{name} must be positive", nameof(value));
            }
        }

        Positive(CallTimeout, nameof(CallTimeout));
        Positive(PlayerCallTimeout, nameof(PlayerCallTimeout));
        Positive(JournalTimeout, nameof(JournalTimeout));
        Positive(StepInterval, nameof(StepInterval));
        Positive(StepTimeout, nameof(StepTimeout));
        Positive(RefreshInterval, nameof(RefreshInterval));
        Positive(ShutdownAttemptTimeout, nameof(ShutdownAttemptTimeout));
        Positive(TelemetryFlushTimeout, nameof(TelemetryFlushTimeout));
        if (ShutdownAttempts < 1)
        {
            throw new ArgumentException("ShutdownAttempts must be at least 1");
        }
    }
}

/// <summary>
/// What the watchdog tolerates before it acts. No property has a default; <see cref="Standard"/> is the starting set
/// (#3113), which the launcher that hosts the brain may tune once measured against a live game.
/// </summary>
public sealed record BrainWatchdog
{
    /// <summary>A scheduler step still running after this long is cancelled and counted a stall.</summary>
    public required TimeSpan StallAfter { get; init; }

    /// <summary>
    /// A stalled step that has not returned this long after its cancellation is abandoned: the host stops waiting
    /// for it and suspends authority, so a step that ignores its token cannot keep writes live.
    /// </summary>
    public required TimeSpan AbandonAfter { get; init; }

    /// <summary>Consecutive steps that faulted or stalled before authority is suspended.</summary>
    public required int FaultLimit { get; init; }

    /// <summary>The pause after a faulted or stalled step before the next one.</summary>
    public required TimeSpan FaultBackoff { get; init; }

    /// <summary>Consecutive steps a planner may miss the wave cutoff before the host reports it stalled.</summary>
    public required int PlannerMissLimit { get; init; }

    /// <summary>
    /// The thresholds a launcher should start from. Go had no watchdog (a stuck step blocked <c>ClockWorker</c> until
    /// its step timeout), so these are chosen, not ported: a step may use its whole <see cref="BrainTimings.StepTimeout"/>
    /// (1 min) plus half again before it is a stall; a step that ignores its token is abandoned 30 s after that; five
    /// consecutive faults is a pattern rather than a blip; the backoff matches the worker's maximum idle delay (10 s) at
    /// half; a planner that misses three consecutive wave cutoffs is reported. See <c>brain-host.md</c>, Watchdog.
    /// </summary>
    public static BrainWatchdog Standard() => new()
    {
        StallAfter = TimeSpan.FromSeconds(90),
        AbandonAfter = TimeSpan.FromSeconds(30),
        FaultLimit = 5,
        FaultBackoff = TimeSpan.FromSeconds(5),
        PlannerMissLimit = 3,
    };

    internal void Validate()
    {
        if (StallAfter <= TimeSpan.Zero || AbandonAfter <= TimeSpan.Zero || FaultBackoff < TimeSpan.Zero)
        {
            throw new ArgumentException("watchdog durations must be positive");
        }
        if (FaultLimit < 1 || PlannerMissLimit < 1)
        {
            throw new ArgumentException("watchdog limits must be at least 1");
        }
    }
}

/// <summary>What starting the host takes. Everything the brain cannot know on its own is named here.</summary>
public sealed record BrainHostOptions
{
    /// <summary>The state database (Go <c>--state</c>). A database from another schema version is moved aside and replaced.</summary>
    public required string StatePath { get; init; }

    /// <summary>The shared game profile directory the clock review reads its player-owned files from (Go <c>--profile</c>).</summary>
    public required string ProfileDirectory { get; init; }

    /// <summary>The directory the OTLP/JSON files are written to (the Bridge's <c>RIMGOVERNOR_OTLP_DIR</c>).</summary>
    public required string OtlpDirectory { get; init; }

    /// <summary>The run id that tells launches sharing the ring apart (the Bridge's <c>RIMGOVERNOR_RUN_ID</c>).</summary>
    public required string RunId { get; init; }

    /// <summary>The OTLP service.name the brain's rows carry.</summary>
    public string OtlpService { get; init; } = "rimgovernor-brain";

    public required BrainTimings Timings { get; init; }

    public required BrainWatchdog Watchdog { get; init; }

    /// <summary>Builds everything that needs a native client over the facade (see <see cref="IBrainComposition"/>).</summary>
    public required IBrainComposition Composition { get; init; }

    /// <summary>Whether the rounds methods run (Go <c>serveConfig.roundsMethods</c>).</summary>
    public bool RoundsMethods { get; init; } = true;

    /// <summary>Runs the bot for every fresh observed world without a player click (Go <c>--resume</c>).</summary>
    public bool AutoResume { get; init; }

    /// <summary>The facade to run over; the one the mod published to <see cref="NativeFacadeRegistry"/> when null.</summary>
    public INativeFacade? Facade { get; init; }

    /// <summary>
    /// The route table of the HTTP channel to register the player routes on. Null registers none: how the Bridge
    /// hands its table to the host is not settled (see docs/developers/architecture/brain-host.md).
    /// </summary>
    public RouteTable? Routes { get; init; }

    internal void Validate()
    {
        if (!Path.IsPathRooted(StatePath) || !Path.IsPathRooted(ProfileDirectory) || !Path.IsPathRooted(OtlpDirectory))
        {
            throw new ArgumentException("the state path, profile directory and OTLP directory must be absolute");
        }
        if (string.IsNullOrWhiteSpace(RunId) || string.IsNullOrWhiteSpace(OtlpService))
        {
            throw new ArgumentException("a run id and an OTLP service name are required");
        }
        ArgumentNullException.ThrowIfNull(Timings);
        ArgumentNullException.ThrowIfNull(Watchdog);
        ArgumentNullException.ThrowIfNull(Composition);
        Timings.Validate();
        Watchdog.Validate();
    }
}
