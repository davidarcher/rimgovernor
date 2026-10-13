using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;

namespace RimGovernor.Brain.Executor;

// Port of internal/executor (executor.go, haul_types.go, movement_types.go, trade_types.go).
// Dropped: the HaulJournal, MovementJournal and TradeJournal marker interfaces. They existed only to make Go
// type-assert a journal that also had the untyped Prepare; IJournal.Prepare is part of the one journal here.
// Awaiting (#3053): PlanState, BatchAttempt, BatchResult and BatchReceipt in RimGovernor.Brain.Store, and the
// store's IJournal implementation. They are named, never redefined, here.

/// <summary>Building authority changed or is disabled (Go <c>ErrAuthority</c>).</summary>
public sealed class AuthorityException : DomainException
{
    public AuthorityException()
        : base("building authority changed or disabled") { }

    public AuthorityException(string message)
        : base(message) { }

    public AuthorityException(Exception inner)
        : base("building authority changed or disabled: " + inner.Message) => Inner = inner;

    /// <summary>The refusal that revoked authority, when one did.</summary>
    public Exception? Inner { get; }
}

/// <summary>Building execution is held (Go <c>ErrHeld</c>).</summary>
public sealed class HeldException : DomainException
{
    public HeldException()
        : base("building execution held") { }

    public HeldException(string message)
        : base(message) { }

    public HeldException(Exception inner)
        : base("building execution held: " + inner.Message) { }
}

/// <summary>Invalid building evidence (Go <c>ErrEvidence</c>).</summary>
public sealed class EvidenceException : DomainException
{
    public EvidenceException()
        : base("invalid building evidence") { }

    public EvidenceException(string message)
        : base(message) { }

    public EvidenceException(Exception inner)
        : base("invalid building evidence: " + inner.Message) { }
}

/// <summary>The executor was stopped and takes no more work (Go <c>ErrStopped</c>).</summary>
public sealed class StoppedException : DomainException
{
    public StoppedException()
        : base("building executor stopped") { }
}

/// <summary>The executor's source of time (Go <c>Clock</c>).</summary>
public interface IClock
{
    DateTimeOffset Now();
}

/// <summary>Decides whether a non-root plan may dispatch under the root plan's authority (Go <c>RoundsScope</c>).</summary>
public interface IRoundsScope
{
    /// <summary>Throws when <paramref name="target"/> is not authorized under <paramref name="root"/>.</summary>
    Task AuthorizeRoundsPlanAsync(GenerationSnapshot root, GenerationSnapshot target, CancellationToken ct);
}

/// <summary>
/// The durable journal the executor writes through (Go <c>Journal</c>). The store implements it (awaiting #3053);
/// every method throws on a store failure.
/// </summary>
public interface IJournal
{
    Task<PlanState> LoadPlanAsync(PlanId plan, CancellationToken ct);

    /// <summary>The untyped preparation of an intent-mode action: native re-validates the intent itself, so nothing is admitted in the journal.</summary>
    Task<Progress> PrepareAsync(PlanId plan, ActionId action, GenerationSnapshot snapshot, Tick tick, CancellationToken ct);

    Task<Progress> DispatchAsync(PlanId plan, ActionId action, GenerationSnapshot snapshot, Tick tick, CancellationToken ct);

    Task<Progress> RecordReceiptAsync(PlanId plan, ActionId action, AttemptId attempt, Domain.Receipt receipt, CancellationToken ct);

    /// <summary>Records an applied zone_create's receipt with the zone identity its evidence named.</summary>
    Task<Progress> RecordZoneReceiptAsync(PlanId plan, ActionId action, AttemptId attempt, string zone, CancellationToken ct);

    Task<Progress> RecordStockpileReceiptAsync(PlanId plan, ActionId action, AttemptId attempt, IReadOnlyList<CreatedZone> zones, CancellationToken ct);

    /// <summary>Records an applied bill placement's receipt with the native bill id its evidence named.</summary>
    Task<Progress> RecordBillReceiptAsync(PlanId plan, ActionId action, AttemptId attempt, string bill, CancellationToken ct);

    Task<Progress> ObserveAsync(PlanId plan, Observation observation, GenerationSnapshot current, CancellationToken ct);

    Task<Progress> CancelAsync(PlanId plan, ActionId action, CancellationToken ct);

    Task<Progress> HoldAsync(PlanId plan, ActionId action, IReadOnlyList<HeldReason> reasons, Tick tick, CancellationToken ct);

    /// <summary>The batch forms advance many actions in one transaction; results are in input order, one per item.</summary>
    Task<IReadOnlyList<BatchResult>> PrepareBatchAsync(IReadOnlyList<BatchAttempt> attempts, CancellationToken ct);

    Task<IReadOnlyList<BatchResult>> DispatchBatchAsync(IReadOnlyList<BatchAttempt> attempts, CancellationToken ct);

    Task<IReadOnlyList<BatchResult>> RecordReceiptsAsync(IReadOnlyList<BatchReceipt> receipts, CancellationToken ct);
}

/// <summary>The executor's time bounds (Go <c>Limits</c>). The run and journal timeouts must be positive.</summary>
public sealed record Limits
{
    public TimeSpan MaxAge { get; init; }

    public TimeSpan RunTimeout { get; init; }

    public TimeSpan JournalTimeout { get; init; }
}

/// <summary>The authority the executor dispatches under; a disabled authority permits observation but never dispatch.</summary>
public sealed record Authority
{
    public GenerationSnapshot Snapshot { get; init; }

    public bool Enabled { get; init; }
}

public sealed record Target(DomainAction Action, GenerationSnapshot Snapshot);

/// <summary>
/// A placement's composite identity is the native deduplication and precondition token. The native adapter must
/// verify colony, map and load and deduplicate Action and Attempt atomically with the placement. This package
/// never wires an unguarded bridge call.
/// </summary>
public sealed record Placement(DomainAction Action, AttemptId Attempt, GenerationSnapshot Snapshot, Tick Tick);

/// <summary>What native answered for one placement. The boundary fills it in; the executor checks it against the placement.</summary>
public sealed class Receipt
{
    public ActionId Action { get; set; }

    public AttemptId Attempt { get; set; }

    public GenerationSnapshot Snapshot { get; set; }

    public Domain.Receipt Kind { get; set; }

    public List<CombatResult> Combat { get; } = [];

    /// <summary>The zone an applied zone_create's evidence names.</summary>
    public string Zone { get; set; } = "";

    public List<CreatedZone> Stockpiles { get; } = [];

    /// <summary>The native bill an applied production_bill's or surgery's evidence names.</summary>
    public string Bill { get; set; } = "";

    /// <summary>Native's own account of a refused write; null when the receipt is not a native refusal.</summary>
    public NativeRefusal? Refusal { get; set; }
}

/// <summary>The native read a building intent's dispatch is anchored to. It admits nothing: native validates the intent when it applies it.</summary>
public sealed record IntentInspection
{
    public DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset ObservedAt { get; init; }

    public GenerationSnapshot Current { get; init; }

    public Tick Tick { get; init; }
}

public interface IBoundary
{
    /// <summary>Anchors a building intent's dispatch to the current native tick.</summary>
    Task<IntentInspection> InspectIntentAsync(Target target, CancellationToken ct);

    /// <summary>Sends the intents of one world through one Actions/Apply call and returns their receipts in input order.</summary>
    Task<IReadOnlyList<Receipt>> WriteIntentsAsync(IReadOnlyList<Placement> placements, CancellationToken ct);
}

/// <summary>The outcome of one action's dispatch (Go <c>Result</c>).</summary>
public sealed record ExecutorResult
{
    public Progress? Progress { get; init; }

    public IReadOnlyList<Refusal> Refused { get; init; } = [];

    public bool NativeCalled { get; init; }
}

/// <summary>One action's outcome of <see cref="Executor.RunBatchAsync"/>: its dispatch result and the error it ended with, if any.</summary>
public sealed record BatchItem(ActionId Action, ExecutorResult Result, Exception? Error);

/// <summary>The native read a haul dispatch is journaled at.</summary>
public sealed record HaulInspection
{
    public DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset ObservedAt { get; init; }

    public GenerationSnapshot Snapshot { get; init; }

    public Tick Tick { get; init; }
}

/// <summary>
/// Optionally composed like <see cref="ITradeBoundary"/>: the planner has already selected the pawn and the item.
/// Haul is an intent-mode kind, so its receipt is terminal and there is nothing to observe.
/// </summary>
public interface IHaulBoundary
{
    Task<HaulInspection> InspectHaulAsync(Target target, CancellationToken ct);

    Task<Receipt> WriteHaulAsync(Placement placement, CancellationToken ct);
}

/// <summary>
/// Sends one move intent. Its receipt is terminal: the order was given (or already matched) or native refused it.
/// Arrival is not an effect of the action; the worker gates the plan's final action on it.
/// </summary>
public interface IMovementBoundary
{
    Task<Receipt> WriteMovementAsync(Placement placement, CancellationToken ct);
}

public sealed record TradeInspection
{
    public DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset ObservedAt { get; init; }

    public TradeAdmissionFacts Facts { get; init; } = new();
}

/// <summary>Optionally composed: the routine upstream has already selected the trader, negotiator, lines and floors.</summary>
public interface ITradeBoundary
{
    Task<TradeInspection> InspectTradeAsync(Target target, CancellationToken ct);

    Task<Receipt> WriteTradeAsync(Placement placement, CancellationToken ct);
}

public sealed record CommsTradeInspection
{
    public DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset ObservedAt { get; init; }

    public GenerationSnapshot Snapshot { get; init; }

    public Tick Tick { get; init; }

    public bool Eligible { get; init; }

    public long LastRequestTick { get; init; }

    public bool RequestKnown { get; init; }

    public bool MatchingWork { get; init; }

    public bool MatchingArrival { get; init; }

    public bool MatchingSeller { get; init; }
}

public interface ICommsTradeBoundary
{
    Task<CommsTradeInspection> InspectCommsTradeRequestAsync(Target target, CancellationToken ct);

    Task<Receipt> WriteCommsTradeRequestAsync(Placement placement, CancellationToken ct);
}
