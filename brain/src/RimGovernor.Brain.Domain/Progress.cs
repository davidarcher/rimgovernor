using System.Collections.Immutable;

namespace RimGovernor.Brain.Domain;

public enum Stage
{
    Pending,
    Prepared,
    Dispatched,
    AwaitingObservation,
    Completed,
    Unsuccessful,
    Cancelled,
}

public enum Receipt
{
    Accepted,

    /// <summary>Trusted pre-admission no-effect proof, never a transport refusal.</summary>
    Refused,

    /// <summary>
    /// The transport's proof that the write never left the controller (it failed before the native call was
    /// issued), so nothing was admitted: no-effect, and the same authority may retry.
    /// </summary>
    Unsent,
    Unknown,
}

public enum Effect
{
    Unknown,
    Pending,
    Completed,

    /// <summary>
    /// Requires a complete inspection proving no order or effect remains. Partial or truncated reads must
    /// report <see cref="Unknown"/>.
    /// </summary>
    Absent,
    Unsuccessful,
}

public enum ObservationCausality
{
    [WireName("")]
    None,

    /// <summary>
    /// Asserts a native inspection correlated to this exact attempt, causally after dispatch. Equal game ticks
    /// alone cannot establish this ordering.
    /// </summary>
    AfterDispatch,
}

public enum UnsuccessfulReason
{
    [WireName("")]
    None,
    NativeFailure,

    [WireName("cancelled")]
    NativeCancelled,
    NativeInterrupted,
    NativeExpired,
    TargetDead,
    OutcomeNotAchieved,

    /// <summary>
    /// Names an intent settled Unsuccessful by a refused receipt (a stale source, a target changed since the
    /// census): no observation carries it, and the owning routine replans from live state. Read it through
    /// <see cref="ProgressView.UnsuccessfulCause"/>.
    /// </summary>
    [WireName("refused")]
    IntentRefused,
}

public static class UnsuccessfulReasonExtensions
{
    /// <summary>The reasons an observation may carry.</summary>
    public static bool IsObservable(this UnsuccessfulReason r) =>
        r is UnsuccessfulReason.NativeFailure
            or UnsuccessfulReason.NativeCancelled
            or UnsuccessfulReason.NativeInterrupted
            or UnsuccessfulReason.NativeExpired
            or UnsuccessfulReason.TargetDead
            or UnsuccessfulReason.OutcomeNotAchieved;
}

/// <summary>
/// Explains why a not-yet-dispatched action is currently stuck, mirroring (not importing, to avoid a
/// domain-to-policy cycle) every policy.Reason value across both the emergency subset and the ordinary
/// (non-emergency) admission-refusal reasons every action family's Admit-style check can return: geometry
/// conflicts, insufficient stock, dependency and spending blocks, per-family worker/resource unavailability,
/// and the rest. Declaration order is the fixed order <see cref="HoldEvidence.Reasons"/> decodes in, and the
/// bit position within the packed encoding.
/// </summary>
public enum HeldReason
{
    UnsafeThreat,
    CriticalMedical,
    StaleFacts,
    UnknownFacts,

    NotReady,
    AlreadyReserved,
    UnsafePlacement,

    [WireName("explicit_material_required")]
    MaterialRequired,

    [WireName("dependency_incomplete")]
    DependencyBlocked,

    [WireName("geometry_conflict")]
    GeometryBlocked,

    [WireName("spending_policy")]
    SpendingBlocked,
    InsufficientStock,

    [WireName("held_reservation_unverifiable")]
    InvalidHeld,
    ArithmeticOverflow,

    NativeIneligible,
    PlayerOrder,
    UnsuitableEquipment,
    UnsupportedThreat,
    WallRemovalGeometryChanged,
    WallRemovalTargetChanged,
    ExcavationUnsupported,
    ExcavationGeometryChanged,
    UnsafeRoute,
    RoofSupportRisk,

    [WireName("missing_storage")]
    StorageMissing,
    UrgentCompetingWork,
    UnsafeItem,
}

/// <summary>
/// Ties held-action reasons to the exact plan/revision and tick they were observed under. A projection must
/// re-verify this tie before surfacing the reasons, so a stale reason can never survive a subsequent
/// re-admission, resolution, or plan/action progression.
/// </summary>
public sealed record HoldEvidence
{
    // Packed so the record keeps value equality; bit i is (HeldReason)i. 64 bits cap the reason count.
    internal HoldEvidence(ulong reasons, PlanId plan, PlanRevision revision, Tick tick, Tick since)
    {
        ReasonBits = reasons;
        Plan = plan;
        Revision = revision;
        Tick = tick;
        Since = since;
    }

    internal ulong ReasonBits { get; }

    public PlanId Plan { get; }

    public PlanRevision Revision { get; }

    public Tick Tick { get; }

    /// <summary>
    /// The tick this exact reason set was first recorded on this plan revision; a repeated identical hold
    /// keeps it, so planners can tell a long-stalled hold from a fresh one.
    /// </summary>
    public Tick Since { get; }

    /// <summary>Decodes the packed bits back into their canonical, deduplicated, deterministically ordered form.</summary>
    public IReadOnlyList<HeldReason> Reasons()
    {
        var result = new List<HeldReason>();
        foreach (var r in Enum.GetValues<HeldReason>())
        {
            if ((ReasonBits & Bit(r)) != 0)
            {
                result.Add(r);
            }
        }
        return result;
    }

    internal static ulong Bit(HeldReason r) => 1UL << (int)r;
}

/// <summary>
/// Comes from a complete attempt-correlated native completion inspection. It proves identity, not authority
/// for later upkeep.
/// </summary>
public readonly record struct ConstructionIdentity(string Origin, string Current)
{
    public void Validate() =>
        DomainException.Require(Identity.IsValidText(Origin) && Identity.IsValidText(Current), "invalid completed construction identity");
}

public sealed record Observation(
    ActionId Action,
    AttemptId Attempt,
    GenerationSnapshot Snapshot,
    Tick Tick,
    Effect Effect,
    ObservationCausality Causality = ObservationCausality.None,
    UnsuccessfulReason UnsuccessfulReason = UnsuccessfulReason.None
);

public sealed record ProgressView
{
    /// <summary>
    /// The native zone an applied zone_create receipt identifies (the zone's unique load id,
    /// ZoneEffect.zone_id). It is the identity later native censuses name the zone by, which is how a
    /// completed stockpile method owns its zone for MaintainHomeCoverage.
    /// </summary>
    public Fact<string> Zone { get; init; }

    public Fact<CreatedZones> Stockpiles { get; init; }

    public Fact<CombatResults> Combat { get; init; }

    /// <summary>
    /// The native bill an applied production_bill or surgery receipt identifies (BillEffect.bill,
    /// SurgeryEffect.bill): the id a later remove_production_bill names.
    /// </summary>
    public Fact<string> Bill { get; init; }

    public ActionId Action { get; init; }

    public AttemptId Attempt { get; init; }

    public PlanId Plan { get; init; }

    public PlanRevision Revision { get; init; }

    public Stage Stage { get; init; }

    public GenerationSnapshot Snapshot { get; init; }

    public Tick Tick { get; init; }

    public bool Unresolved { get; init; }

    public Fact<Receipt> Receipt { get; init; }

    public Fact<Effect> Effect { get; init; }

    public Fact<UnsuccessfulReason> UnsuccessfulReason { get; init; }

    public Fact<HoldEvidence> HeldReason { get; init; }

    /// <summary>Native's own account of a refused receipt: failure code, reason and class. Unknown unless the receipt was a native refusal.</summary>
    public Fact<NativeRefusal> Refusal { get; init; }

    /// <summary>
    /// The typed reason an Unsuccessful action ended: the observed reason, or
    /// <see cref="Domain.UnsuccessfulReason.IntentRefused"/> for an intent native refused. None for any other stage.
    /// </summary>
    public UnsuccessfulReason UnsuccessfulCause()
    {
        if (Stage != Stage.Unsuccessful)
        {
            return Domain.UnsuccessfulReason.None;
        }
        if (UnsuccessfulReason.TryGetValue(out var reason))
        {
            return reason;
        }
        if (Receipt.TryGetValue(out var receipt) && receipt == Domain.Receipt.Refused)
        {
            return Domain.UnsuccessfulReason.IntentRefused;
        }
        return Domain.UnsuccessfulReason.None;
    }

    /// <summary>
    /// Re-verifies HeldReason evidence against the view carrying it, rather than trusting the stored Fact
    /// alone. Any DTO projection must go through this instead of reading HeldReason directly.
    /// </summary>
    public bool TryGetFreshHeldReason(out IReadOnlyList<HeldReason> reasons)
    {
        if (!TryGetFreshHold(out var evidence))
        {
            reasons = [];
            return false;
        }
        reasons = evidence.Reasons();
        return true;
    }

    /// <summary>The evidence form of <see cref="TryGetFreshHeldReason"/>, for callers that also need how long the current reason set has persisted (Since).</summary>
    public bool TryGetFreshHold([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out HoldEvidence? evidence)
    {
        evidence = null;
        if ((Stage != Stage.Pending && Stage != Stage.Prepared) || Unresolved)
        {
            return false;
        }
        if (!HeldReason.TryGetValue(out var held) || held.Plan != Plan || held.Revision != Revision || held.Tick < Tick)
        {
            return false;
        }
        evidence = held;
        return true;
    }
}

/// <summary>
/// Progress transitions return a new value; failed transitions throw and leave the original untouched. One
/// orchestrator owns each action and durably records Prepared and Dispatched before invoking a native
/// mutation. This package does not perform persistence.
/// </summary>
public sealed partial record Progress
{
    private Progress(ProgressView view, DomainAction action)
    {
        View = view;
        Action = action;
    }

    /// <summary>Starts an action of <paramref name="plan"/> as Pending (Go <c>NewProgress</c>).</summary>
    public Progress(PlanSpec plan, ActionId action)
    {
        var found = plan.Action(action) ?? throw new DomainException("action is not in plan");
        View = new ProgressView { Action = action, Plan = plan.Id, Revision = plan.Revision, Stage = Stage.Pending };
        Action = found;
    }

    public ProgressView View { get; private init; }

    public DomainAction Action { get; private init; }

    internal Progress With(Func<ProgressView, ProgressView> change) => new(change(View), Action);

    /// <summary>
    /// Binds a not-yet-dispatched action to the authority snapshot it will dispatch under. Dispatch is recorded
    /// durably before any native write, so a Prepared action never has a write outstanding: when its authority
    /// moves (a native generation advance after a cancelled dispatch) it is prepared again under the current
    /// snapshot rather than left behind with one it can never dispatch against.
    /// </summary>
    public Progress Prepare(GenerationSnapshot snapshot, Tick tick)
    {
        DomainException.Require((View.Stage is Stage.Pending or Stage.Prepared) && !View.Unresolved, "action is not ready");
        snapshot.Validate();
        DomainException.Require(snapshot.Plan == View.Plan && snapshot.Revision == View.Revision && tick >= View.Tick, "stale plan or tick");
        return With(v => v with { Stage = Stage.Prepared, Snapshot = snapshot, Tick = tick, HeldReason = Unknown<HoldEvidence>() });
    }

    /// <summary>
    /// Records why a Pending/Prepared, non-dispatched action is currently stuck, without changing Stage. The
    /// evidence is pinned to this exact plan revision and tick; any later successful transition (Prepare,
    /// dispatch, receipt, observation, cancellation) clears it, so a hold can never outlive the exact
    /// admission attempt it was computed against.
    /// </summary>
    public Progress Hold(IReadOnlyCollection<HeldReason> reasons, Tick tick)
    {
        DomainException.Require(View.Stage is Stage.Pending or Stage.Prepared, "hold requires a not-yet-dispatched action");
        DomainException.Require(!View.Unresolved, "hold requires no outstanding dispatch");
        DomainException.Require(tick >= View.Tick, "stale hold tick");
        DomainException.Require(reasons.Count != 0 && reasons.Count <= Enum.GetValues<HeldReason>().Length, "invalid hold reasons");
        ulong bits = 0;
        foreach (var r in reasons)
        {
            DomainException.Require(Wire.IsDefined(r), "invalid hold reason");
            var bit = HoldEvidence.Bit(r);
            DomainException.Require((bits & bit) == 0, "duplicate hold reason");
            bits |= bit;
        }
        var since = tick;
        if (View.HeldReason.TryGetValue(out var previous) && previous.Plan == View.Plan && previous.Revision == View.Revision && previous.ReasonBits == bits && previous.Since <= tick)
        {
            since = previous.Since;
        }
        return With(v => v with { HeldReason = Known(new HoldEvidence(bits, View.Plan, View.Revision, tick, since)) });
    }

    public Progress MarkDispatched(GenerationSnapshot current, Tick tick)
    {
        DomainException.Require(View.Stage == Stage.Prepared && View.Snapshot.Matches(current) && tick >= View.Tick, "dispatch requires current prepared authority");
        DomainException.Require(View.Attempt.Value != ulong.MaxValue, "dispatch attempt identity exhausted");
        return With(v =>
            v with
            {
                Attempt = new AttemptId(v.Attempt.Value + 1),
                Stage = Stage.Dispatched,
                Unresolved = true,
                Tick = tick,
                Receipt = Unknown<Receipt>(),
                Effect = Unknown<Effect>(),
                UnsuccessfulReason = Unknown<UnsuccessfulReason>(),
                HeldReason = Unknown<HoldEvidence>(),
                Zone = Unknown<string>(),
                Bill = Unknown<string>(),
            }
        );
    }

    public Progress RecordReceipt(AttemptId attempt, Receipt receipt) => RecordReceiptCore(attempt, receipt);

    /// <summary>Records a refused receipt together with native's account of the refusal; a class outside the three is recorded unknown.</summary>
    public Progress RecordRefusal(AttemptId attempt, NativeRefusal refusal)
    {
        var next = RecordReceiptCore(attempt, Receipt.Refused);
        if (!refusal.Class.IsValid())
        {
            refusal = refusal with { Class = RefusalClass.Unknown };
        }
        return next.With(v => v with { Refusal = Known(refusal) });
    }

    /// <summary>Records an applied zone_create's receipt together with the native zone identity its evidence named, which zone claims read.</summary>
    public Progress RecordZoneReceipt(AttemptId attempt, Receipt receipt, string zone)
    {
        DomainException.Require(
            Action.ZoneCreate is { } create && create.Kind != ZoneKind.Stockpile && receipt == Receipt.Accepted && Identity.IsValidText(zone),
            "zone identity requires an applied zone create"
        );
        return RecordReceiptCore(attempt, receipt).With(v => v with { Zone = Known(zone) });
    }

    /// <summary>Records an applied bill-placing receipt together with the native bill id its evidence named.</summary>
    public Progress RecordBillReceipt(AttemptId attempt, Receipt receipt, string bill)
    {
        DomainException.Require(
            Action.Kind is ActionKind.ProductionBill or ActionKind.Surgery && receipt == Receipt.Accepted && Identity.IsValidText(bill),
            "bill identity requires an applied bill placement"
        );
        return RecordReceiptCore(attempt, receipt).With(v => v with { Bill = Known(bill) });
    }

    internal Progress RecordReceiptCore(AttemptId attempt, Receipt receipt)
    {
        DomainException.Require(attempt.Value != 0 && attempt == View.Attempt, "receipt belongs to a different dispatch attempt");
        DomainException.Require(View.Stage == Stage.Dispatched || (View.Stage == Stage.Cancelled && View.Unresolved), "receipt requires dispatched action");
        DomainException.Require(Wire.IsDefined(receipt), "invalid receipt");
        DomainException.Require(!View.Receipt.IsKnown, "receipt already recorded");
        var cancelled = View.Stage == Stage.Cancelled;
        var recorded = With(v => v with { Receipt = Known(receipt), HeldReason = Unknown<HoldEvidence>() });
        if (Action.Kind.IntentMode() && receipt != Receipt.Unsent)
        {
            // The receipt settles an intent's attempt: applied is done, refused is over (the owning routine
            // replans from live state), and an unknown outcome is sent again, which an idempotent intent allows.
            var stage = Stage.Pending;
            var effect = recorded.View.Effect;
            switch (receipt)
            {
                case Receipt.Accepted:
                    effect = Known(Effect.Completed);
                    stage = Stage.Completed;
                    break;
                case Receipt.Refused:
                    effect = Known(Effect.Absent);
                    stage = Stage.Unsuccessful;
                    break;
            }
            return recorded.With(v => v with { Unresolved = false, Effect = effect, Stage = cancelled ? v.Stage : stage });
        }
        if (Action.Kind is ActionKind.CommsTradeRequest or ActionKind.Gathering && receipt == Receipt.Refused)
        {
            return recorded.With(v => v with { Unresolved = false, Effect = Known(Effect.Absent), Stage = cancelled ? v.Stage : Stage.Unsuccessful });
        }
        if (receipt is Receipt.Refused or Receipt.Unsent)
        {
            return recorded.With(v => v with { Unresolved = false, Effect = Known(Effect.Absent), Stage = cancelled ? v.Stage : Stage.Pending });
        }
        return cancelled ? recorded : recorded.With(v => v with { Stage = Stage.AwaitingObservation });
    }

    /// <summary>
    /// Opens the attempt that withdraws a cancelled, still-pending dispatch natively (a harvest designation
    /// nobody took). The action stays Cancelled and unresolved under a fresh attempt id, whose receipt and
    /// observation settle it the ordinary way; a lost receipt resolves as absent like any other unadmitted attempt.
    /// </summary>
    public Progress Withdraw(GenerationSnapshot current, Tick tick)
    {
        DomainException.Require(View.Stage == Stage.Cancelled && View.Unresolved, "withdrawal requires a cancelled unresolved dispatch");
        DomainException.Require(View.Effect.TryGetValue(out var effect) && effect == Effect.Pending, "withdrawal requires a pending effect");
        current.Validate();
        DomainException.Require(View.Snapshot.SameWorld(current) && tick >= View.Tick, "stale withdrawal authority or tick");
        DomainException.Require(View.Attempt.Value != ulong.MaxValue, "dispatch attempt identity exhausted");
        return With(v =>
            v with
            {
                Attempt = new AttemptId(v.Attempt.Value + 1),
                Snapshot = current,
                Tick = tick,
                Receipt = Unknown<Receipt>(),
                Effect = Unknown<Effect>(),
                UnsuccessfulReason = Unknown<UnsuccessfulReason>(),
            }
        );
    }

    /// <summary>
    /// A completed intent-mode order is a standing order: its owned draft stays held until the planner cancels
    /// it (the settled effect is kept, so the plan still retires).
    /// </summary>
    public Progress Cancel()
    {
        DomainException.Require(
            !(View.Stage == Stage.Completed && !Action.Kind.IntentMode()) && View.Stage != Stage.Unsuccessful,
            "cannot cancel this action"
        );
        return With(v => v with { Stage = Stage.Cancelled, HeldReason = Unknown<HoldEvidence>() });
    }

    /// <summary>
    /// Accepts read evidence under current authority, even after direction changes, but never attributes effects
    /// across colony/map/load changes. Terminal evidence must come from a complete native attempt-correlated
    /// inspection; the executor validates that boundary evidence regardless of tick distance.
    /// </summary>
    public Progress Observe(Observation observation, GenerationSnapshot current)
    {
        DomainException.Require(!Action.Kind.IntentMode(), "an intent's receipt is terminal; there is nothing to observe");
        return ObserveCore(observation, current);
    }

    private Progress ObserveCore(Observation observation, GenerationSnapshot current)
    {
        DomainException.Require(View.Unresolved, "no dispatched effect to observe");
        current.Validate();
        DomainException.Require(Wire.IsDefined(observation.Causality), "invalid observation causality");
        DomainException.Require(
            observation.Action == View.Action
                && observation.Attempt.Value != 0
                && observation.Attempt == View.Attempt
                && observation.Snapshot.Matches(current)
                && View.Snapshot.SameWorld(current)
                && observation.Tick >= View.Tick
                && !(observation.Tick == View.Tick && observation.Causality != ObservationCausality.AfterDispatch),
            "stale or differently scoped observation"
        );
        DomainException.Require(Wire.IsDefined(observation.Effect), "invalid observed effect");
        if (observation.Effect == Effect.Unsuccessful)
        {
            DomainException.Require(observation.UnsuccessfulReason.IsObservable(), "invalid unsuccessful reason");
        }
        else
        {
            DomainException.Require(observation.UnsuccessfulReason == UnsuccessfulReason.None, "unsuccessful reason requires unsuccessful effect");
        }
        var next = With(v => v with { Tick = observation.Tick, Effect = Known(observation.Effect) });
        if (observation.Effect == Effect.Unsuccessful)
        {
            next = next.With(v => v with { UnsuccessfulReason = Known(observation.UnsuccessfulReason) });
        }
        if (observation.Effect is Effect.Completed or Effect.Absent or Effect.Unsuccessful)
        {
            var sameSnapshot = View.Snapshot.Matches(current);
            next = next.With(v =>
            {
                var settled = v with { Unresolved = false };
                if (v.Stage == Stage.Cancelled)
                {
                    return settled;
                }
                var stage = observation.Effect switch
                {
                    Effect.Completed => Stage.Completed,
                    Effect.Unsuccessful => Stage.Unsuccessful,
                    _ => sameSnapshot ? Stage.Pending : Stage.Cancelled,
                };
                return settled with { Stage = stage };
            });
        }
        return next;
    }
}

public static class ActionKindExtensions
{
    // Written at startup registration, read from any thread: copy-on-write.
    private static ImmutableHashSet<ActionKind> _intentKinds = [];

    /// <summary>
    /// Reports an action kind whose actions are idempotent intents: native validates each against live state
    /// when it applies, so the receipt is terminal (applied completes, refused fails, unknown is sent again),
    /// with no observation phase, and the routine owning the kind reads its next phase from live facts rather
    /// than per-attempt progress. A kind opts in by registering an Actions/Apply builder in the bridge, which
    /// calls <see cref="RegisterIntentKind"/>.
    /// </summary>
    public static bool IntentMode(this ActionKind kind) => _intentKinds.Contains(kind);

    public static void RegisterIntentKind(ActionKind kind) => ImmutableInterlocked.Update(ref _intentKinds, set => set.Add(kind));
}
