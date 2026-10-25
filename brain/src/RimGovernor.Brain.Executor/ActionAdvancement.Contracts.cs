using RimGovernor.Brain.Domain;
using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.Telemetry;

namespace RimGovernor.Brain.Executor;

/// <summary>Session authority and dispatch entry points. Hands owns action selection.</summary>
public interface IExecutionSession
{
    ControlState State();
    Task ObserveTargetAsync(GenerationSnapshot requested, CancellationToken ct);
    Task<IReadOnlyList<BatchItem>> RunBatchAsync(PlanId plan, IReadOnlyList<ActionId> actions, CancellationToken ct);
    Task<BatchItem> ReassertMovementAsync(PlanId plan, ActionId action, Tick observed, CancellationToken ct);
}

/// <summary>Inputs and effect notifications for advancing admitted actions.</summary>
public sealed class ActionAdvancementConfig
{
    public required Store.Store Journal { get; init; }
    public required IReviewScope ReviewScope { get; init; }
    public required IExecutionSession Session { get; init; }
    public required Func<TelemetryEmitter> Telemetry { get; init; }
    public required Action<ActionKind, ExecutorResult, Exception?> RecordOutcome { get; init; }
    public Action<ActionKind>? Written { get; init; }
    public Func<GameFacts.Common.Identity, CancellationToken, Task<EmergencyObservation>>? BreakSource { get; init; }
    public IArrivalPawns? Pawns { get; init; }
    public IArrivalPawns? BreakPawns { get; init; }
    public bool ReviewMethods { get; init; }
    public TimeSpan StepInterval { get; init; }
    public TimeSpan MaxBackoff { get; init; }
    public int MaxDispatches { get; init; }
    public Func<bool>? ClockRunning { get; init; }
    public Func<CancellationToken, Task>? Flush { get; init; }
}
