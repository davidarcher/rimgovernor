using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Facts;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.PlayerApi;
using RimGovernor.Brain.Store;
using RimGovernor.Brain.Telemetry;

namespace RimGovernor.Brain.Host;

// The seam between the host (lifecycle, thread, authority, fault isolation) and the parts of the brain that need a
// native client over the facade. The host asks for the finished parts instead of building them. Each member
// names what it hands back and
// who builds it.

/// <summary>What the session, the executor and the optional action families call natively.</summary>
public sealed record SessionParts
{
    /// <summary>The building boundary's native reads.</summary>
    public required IBoundaryNative Native { get; init; }

    /// <summary>The Actions/Apply writer every dispatch goes through.</summary>
    public required IActionsWriter Writer { get; init; }

    /// <summary>The clock capability (#3108); null composes a session that refuses to start while a clock command is owed.</summary>
    public ClockCapabilities? Clock { get; init; }

    /// <summary>What the player routes call natively: presentation, notifications, the render lease, the lifecycle and the colony census. Null registers none of them.</summary>
    public PlayerNatives? Player { get; init; }

    public HaulCapabilities? Haul { get; init; }

    public MovementCapabilities? Movement { get; init; }

    public TradeCapabilities? Trade { get; init; }

    /// <summary>
    /// Cancels native effects whose methods the per-world rebuild discarded (today an open
    /// trade session). Null sweeps nothing.
    /// </summary>
    public Action<IReadOnlyList<PlanState>, CancellationToken>? Orphans { get; init; }
}

/// <summary>
/// The native capabilities the player routes (#3083) call.
/// The host registers a route family for each one given.
/// </summary>
public sealed record PlayerNatives
{
    /// <summary>The tick and status reads behind the cached controller snapshot.</summary>
    public RimGovernor.Brain.Bridge.Contracts.IObservationSource? Source { get; init; }

    public IPresentationReader? Presentation { get; init; }

    public IPresentationMediaWriter? Media { get; init; }

    public INotificationReader? Notifications { get; init; }

    /// <summary>Save, load and new colony. The host wraps it so every save flushes the governor state first.</summary>
    public ILifecycleWriter? Lifecycle { get; init; }

    /// <summary>The colony census reads behind the colony route.</summary>
    public IColonyStatusNative? Colony { get; init; }
}

/// <summary>What the planner side is built over: the session it plans for and the signals it shares with the host.</summary>
public sealed class PlannerContext
{
    public required Player Player { get; init; }

    public required Session Session { get; init; }

    public required StoreJournal Journal { get; init; }

    public required INativeFacade Facade { get; init; }

    public required TelemetryEmitter Telemetry { get; init; }

    /// <summary>Committed clock evidence; the clock coordinator (#3108) notifies it and the host's scheduler loop waits on it.</summary>
    public required WakeSignal Wake { get; init; }

    /// <summary>The decoded state store the scheduler's reviews file their census sections into.</summary>
    public required FactStore Sections { get; init; }

    /// <summary>Rebuilds the store once per loaded world; the scheduler runs it first on each step.</summary>
    public required Func<RimGovernor.Contracts.Wire.Common.ObservationContext, CancellationToken, Task<bool>> WorldReady { get; init; }

    /// <summary>Where the goals Family files its latest statuses and the host's goals view reads them (#3497).</summary>
    public required GoalStatusBoard GoalStatuses { get; init; }

    /// <summary>
    /// The autosave a run start at a paused tick calls with the loaded world's identity and tick.
    /// It flushes the governor state before it saves. Null saves nothing.
    /// </summary>
    public Func<RimGovernor.Contracts.Wire.Common.Identity, long, CancellationToken, Task>? Autosave { get; init; }
}

/// <summary>What the planner side hands back to the host.</summary>
public sealed class PlannerParts
{
    /// <summary>The scheduler over the composed planners; the host steps it on its thread.</summary>
    public required ClockScheduler Scheduler { get; init; }

    /// <summary>The Worker's native reads for break responses and the arrival hold.</summary>
    public FacadeNativeClient? BreakSource { get; init; }

    public IArrivalPawns? Pawns { get; init; }

    /// <summary>The Worker's movement writer, which resends an idle drafted mover's order.</summary>
    public IActionsWriter? Moves { get; init; }

    /// <summary>Asks native to capture the frame a step's deferred writes need.</summary>
    public Func<CancellationToken, Task>? Flush { get; init; }

    /// <summary>
    /// Subscribes to frames in the shape the composed planners read (the facade's <c>OpenFramesAsync</c>). The
    /// host calls it at start and after every world rebuild; null composes planners that read without a stream.
    /// </summary>
    public Func<CancellationToken, Task>? OpenFrames { get; init; }

    /// <summary>
    /// Runs the native clock from a step's result: Run at the target speed, native hazard holds and the brain's own
    /// (coupled order, review, autosave), the heartbeat and the colony event consumer. Null steps the planners without ever
    /// running the clock.
    /// </summary>
    public ClockRunDriver? Clock { get; init; }

    /// <summary>The live target speed behind <c>POST /api/player/clock-speed</c>; null registers no such route.</summary>
    public ClockTargetSpeed? TargetSpeed { get; init; }

    /// <summary>The routine diagnostics behind <c>/api/routines</c>; null registers no such route.</summary>
    public IRoundsProvider? Rounds { get; init; }
}
