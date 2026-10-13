using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Facts;
using RimGovernor.Brain.NativeFacade;
using RimGovernor.Brain.Store;
using RimGovernor.Brain.Telemetry;

namespace RimGovernor.Brain.Host;

// The seam between the host (lifecycle, thread, authority, fault isolation) and the parts of the brain that need a
// native client over the facade. Go composed those in serve_building.go and serve_clock.go over one bridge.Client
// that implemented some forty Native/Source interfaces at once; the C# port has no such client yet and no child owns
// it, so the host asks for the finished parts instead of building them. Each member names what it hands back and
// who builds it.

/// <summary>What the session, the executor and the optional action families call natively (Go <c>buildingServiceBridge</c>).</summary>
public sealed record SessionParts
{
    /// <summary>The building boundary's native reads (Go <c>buildingServiceBridge.native</c>).</summary>
    public required IBoundaryNative Native { get; init; }

    /// <summary>The Actions/Apply writer every dispatch goes through (Go <c>buildingServiceBridge.writes</c>).</summary>
    public required IActionsWriter Writer { get; init; }

    /// <summary>The clock capability (#3108); null composes a session that refuses to start while a clock command is owed.</summary>
    public ClockCapabilities? Clock { get; init; }

    public HaulCapabilities? Haul { get; init; }

    public MovementCapabilities? Movement { get; init; }

    public TradeCapabilities? Trade { get; init; }

    /// <summary>
    /// Cancels native effects whose methods the per-world rebuild discarded (Go <c>orphanSweep</c>: today an open
    /// trade session). Null sweeps nothing.
    /// </summary>
    public Action<IReadOnlyList<PlanState>, CancellationToken>? Orphans { get; init; }
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

    /// <summary>Rebuilds the store once per loaded world; the scheduler runs it first on each step (Go <c>worldRebuild.workerGate</c>).</summary>
    public required Func<RimGovernor.Protocol.Common.ObservationContext, CancellationToken, Task<bool>> WorldReady { get; init; }

    /// <summary>Asks the scheduler loop to step now (the Worker's <c>Advanced</c>).</summary>
    public required Action Nudge { get; init; }
}

/// <summary>What the planner side hands back to the host.</summary>
public sealed class PlannerParts
{
    /// <summary>The scheduler over the composed planners; the host steps it on its thread.</summary>
    public required ClockScheduler Scheduler { get; init; }

    /// <summary>The Worker's native reads for break responses and the arrival hold (Go <c>natives.breaks</c>).</summary>
    public IBreakResponseSource? BreakSource { get; init; }

    public IArrivalPawns? Pawns { get; init; }

    /// <summary>The Worker's movement writer, which resends an idle drafted mover's order.</summary>
    public IActionsWriter? Moves { get; init; }

    /// <summary>Asks native to capture the frame a step's deferred writes need (Go <c>natives.flush.FlushSnapshot</c>).</summary>
    public Func<CancellationToken, Task>? Flush { get; init; }

    /// <summary>Told each dispatched action's progress (the Rounder's placement index).</summary>
    public Action<PlanId, DomainAction, ProgressView>? Placement { get; init; }

    /// <summary>
    /// Subscribes to frames in the shape the composed planners read (the facade's <c>OpenFramesAsync</c>). The
    /// host calls it at start and after every world rebuild; null composes planners that read without a stream.
    /// </summary>
    public Func<CancellationToken, Task>? OpenFrames { get; init; }

    /// <summary>
    /// Starts, renews and ends native-work windows from a step's result and runs the clock behaviours that belong
    /// to the host (combat stops, window sizing, pace back-off, player speed, latched reconcile, epoch
    /// maintenance). Owned by #3108; null steps the planners without ever lending a window.
    /// </summary>
    public IClockWindowDriver? Windows { get; init; }
}

/// <summary>The window side of the clock, which stays in the mod and is driven from the host after each step (#3108).</summary>
public interface IClockWindowDriver
{
    /// <summary>
    /// Acts on one finished step: admit and start a window for <see cref="ClockSchedulerResult.Window"/>, arm its
    /// stops, and reconcile what native latched. Returns true when it left the planners work to do at once (a window
    /// that just ran, a dispatch it reconciled), so the next step is a settled one rather than a timer's.
    /// </summary>
    Task<bool> AfterStepAsync(ClockSchedulerResult result, CancellationToken cancellationToken);

    /// <summary>Retires the epochs the host owns and stops the poll loop; called on shutdown after the last step.</summary>
    Task StopAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Builds the parts of the brain that need a native client over <see cref="INativeFacade"/>. The host owns when
/// they are built and torn down; the composition owns what they are made of.
/// </summary>
public interface IBrainComposition
{
    /// <summary>Called once, before the session exists.</summary>
    SessionParts ComposeSession(INativeFacade facade, TelemetryEmitter telemetry);

    /// <summary>Called once, after the player exists and before the Worker does.</summary>
    PlannerParts ComposePlanners(PlannerContext context);
}
