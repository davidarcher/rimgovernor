using Microsoft.Data.Sqlite;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Store.Codec;

namespace RimGovernor.Brain.Store;

public sealed partial class Store
{
    /// <summary>The most unretired plans the catalog holds.</summary>
    private const int PlanCapacity = 256;

    /// <summary>The most dependency edges one plan may carry.</summary>
    private const int DependencyCapacity = 4096;

    /// <summary>Initializes every action of a plan atomically. Ids remain unique across plans.</summary>
    public void CreatePlan(PlanSpec plan, CancellationToken cancellationToken)
    {
        plan.Validate();
        Run(tx => CreatePlanIn(tx, plan), cancellationToken);
    }

    public Task CreatePlanAsync(PlanSpec plan, CancellationToken cancellationToken)
    {
        plan.Validate();
        return RunAsync(tx => CreatePlanIn(tx, plan), cancellationToken);
    }

    internal static void CreatePlanIn(StoreTransaction tx, PlanSpec plan)
    {
        plan.Validate();
        if (tx.Scalar("SELECT count(*) FROM plans WHERE retired=0") >= PlanCapacity)
        {
            throw new CapacityException();
        }
        tx.ExecuteConflict(plan.Id, "INSERT INTO plans(id,revision) VALUES($id,$revision)", ("$id", plan.Id), ("$revision", plan.Revision));
        for (var i = 0; i < plan.Actions.Count; i++)
        {
            InsertAction(tx, plan.Id, i, plan.Actions[i]);
        }
        foreach (var d in plan.Dependencies)
        {
            tx.ExecuteConflict(
                plan.Id,
                "INSERT INTO action_dependencies(plan_id,action_id,requires_id,coupled) VALUES($plan,$action,$requires,$coupled)",
                ("$plan", plan.Id),
                ("$action", d.Action),
                ("$requires", d.Requires),
                ("$coupled", d.Coupled ? 1 : 0)
            );
        }
    }

    /// <summary>Writes one action row: its identity, kind, combat fight and the JSON of its payload.</summary>
    internal static void InsertAction(StoreTransaction tx, PlanId plan, int ordinal, DomainAction a)
    {
        if (tx.Scalar("SELECT count(*) FROM clock_attempts WHERE native_action_id=$id", ("$id", a.Id)) != 0)
        {
            throw new ConflictException($"action {a.Id} already reserved by a clock attempt");
        }
        tx.ExecuteConflict(
            plan,
            "INSERT INTO actions(id,plan_id,ordinal,kind,fight,body) VALUES($id,$plan,$ordinal,$kind,$fight,$body)",
            ("$id", a.Id),
            ("$plan", plan),
            ("$ordinal", ordinal),
            ("$kind", Wire.Name(a.Kind)),
            ("$fight", a.CombatBatch is { } batch ? batch.Fight : null),
            ("$body", StoreJson.Serialize(a))
        );
    }

    /// <summary>The plan with the id: its action rows, dependencies and every transition replayed.</summary>
    public PlanState LoadPlan(PlanId id, CancellationToken cancellationToken) => Run(tx => LoadIn(tx, id), cancellationToken);

    public Task<PlanState> LoadPlanAsync(PlanId id, CancellationToken cancellationToken) => RunAsync(tx => LoadIn(tx, id), cancellationToken);

    /// <summary>
    /// The plan with the id, from the cache when it holds it (Go <c>load</c>). The state is immutable, so the
    /// cached instance is handed to every reader. A retired plan is read-only and never cached.
    /// </summary>
    internal static PlanState LoadIn(StoreTransaction tx, PlanId id)
    {
        tx.Plans.SyncVersion(tx);
        if (tx.Plans.TryGet(id, out var held))
        {
            return held;
        }
        var state = DecodePlan(tx, id);
        if (!state.Retired)
        {
            tx.Plans.Put(id, state);
        }
        return state;
    }

    private static PlanState DecodePlan(StoreTransaction tx, PlanId id)
    {
        var (header, found) = tx.QueryFirst(
            "SELECT revision,retired,method_id,superseded_by FROM plans WHERE id=$id",
            [("$id", id)],
            r => (Revision: r.UnsignedText(0), Retired: r.GetInt64(1) != 0, Method: r.Text(2) ?? "", Superseded: r.Text(3) ?? "")
        );
        if (!found)
        {
            throw new NotFoundException();
        }
        var actions = new List<DomainAction>();
        var ordinals = tx.Query(
            "SELECT body,ordinal FROM actions WHERE plan_id=$id ORDER BY ordinal",
            [("$id", id)],
            r => (Body: r.GetString(0), Ordinal: r.GetInt64(1))
        );
        foreach (var (body, ordinal) in ordinals)
        {
            if (ordinal != actions.Count)
            {
                throw new StoreCorruptException("noncontiguous action order");
            }
            actions.Add(StoreJson.Deserialize<DomainAction>(body));
        }
        var plan = new PlanSpec(id, new PlanRevision(header.Revision), actions, LoadDependencies(tx, id));
        var progress = new List<Progress>(actions.Count);
        var admissions = new List<ActionAdmission>();
        foreach (var a in actions)
        {
            var p = new Progress(plan, a.Id);
            try
            {
                foreach (var payload in tx.Query("SELECT payload FROM transitions WHERE action_id=$id ORDER BY sequence", [("$id", a.Id)], r => r.GetString(0)))
                {
                    p = StoreJson.Deserialize<Transition>(payload).Apply(p);
                }
            }
            catch (DomainException e)
            {
                throw new StoreCorruptException($"invalid action \"{a.Id}\" history: {e.Message}");
            }
            progress.Add(p);
            if (LoadAdmission(tx, a, p) is { } admission)
            {
                admissions.Add(new ActionAdmission(a.Id, admission));
            }
        }
        return new PlanState(header.Retired, new PlanId(header.Superseded), new MethodId(header.Method), plan, EquatableArray.From(progress), EquatableArray.From(admissions));
    }

    internal static List<ActionDependency> LoadDependencies(StoreTransaction tx, PlanId plan)
    {
        var rows = tx.Query(
            "SELECT action_id,requires_id,coupled FROM action_dependencies WHERE plan_id=$plan ORDER BY action_id,requires_id",
            [("$plan", plan)],
            r => new ActionDependency(new ActionId(r.GetString(0)), new ActionId(r.GetString(1)), r.GetInt64(2) != 0)
        );
        return rows.Count > DependencyCapacity ? throw new CapacityException() : rows;
    }

    /// <summary>The persisted dispatch footprint of every action of a plan, by action id.</summary>
    public IReadOnlyDictionary<ActionId, DispatchSite> DispatchSites(PlanId plan, CancellationToken cancellationToken) =>
        Run(
            tx =>
            {
                var sites = new Dictionary<ActionId, DispatchSite>();
                foreach (var body in tx.Query("SELECT body FROM actions WHERE plan_id=$plan", [("$plan", plan)], r => r.GetString(0)))
                {
                    var a = StoreJson.Deserialize<DomainAction>(body);
                    sites[a.Id] = ActionSites.Of(a);
                }
                return (IReadOnlyDictionary<ActionId, DispatchSite>)sites;
            },
            cancellationToken
        );

    public Task<IReadOnlyDictionary<ActionId, DispatchSite>> DispatchSitesAsync(PlanId plan, CancellationToken cancellationToken) =>
        Task.Run(() => DispatchSites(plan, cancellationToken), cancellationToken);

    /// <summary>Reads the zone footprint record of a zone action, validated against the progress it must agree with.</summary>
    private static Admission? LoadAdmission(StoreTransaction tx, DomainAction a, Progress p)
    {
        if (a.Kind != ActionKind.ZoneCreate)
        {
            return null;
        }
        var payload = tx.ScalarText("SELECT payload FROM admissions WHERE action_id=$id", ("$id", a.Id));
        if (payload is null)
        {
            return null;
        }
        var admission = StoreJson.Deserialize<Admission>(payload);
        try
        {
            ValidateAdmission(a, p, admission);
        }
        catch (DomainException e)
        {
            throw new StoreCorruptException($"invalid action \"{a.Id}\" admission: {e.Message}");
        }
        var v = p.View;
        // The footprint agrees with progress on the world, plan and revision rather than on the native
        // generation, which moves under it.
        var recorded = admission.Snapshot with { Native = default };
        var progressed = v.Snapshot with { Native = default };
        if ((v.Stage == Stage.Prepared || v.Attempt.Value > 0) && !recorded.Matches(progressed))
        {
            throw new StoreCorruptException("admission and progress authority disagree");
        }
        if (v.Unresolved && admission.Tick > v.Tick)
        {
            throw new StoreCorruptException("admission is newer than dispatched progress");
        }
        return admission;
    }

    internal static void ValidateAdmission(DomainAction a, Progress p, Admission admission)
    {
        admission.Snapshot.Validate();
        var v = p.View;
        DomainException.Require(
            admission.Snapshot.Plan == v.Plan && admission.Snapshot.Revision == v.Revision && admission.Tick.Value >= 0,
            "admission plan or tick mismatch"
        );
        var zone = a.ZoneCreate ?? throw new DomainException("unsupported admission action");
        DomainException.Require(admission.Footprint.Count == zone.Cells.Count, "zone admission footprint mismatch");
        var seen = new HashSet<Cell>();
        foreach (var cell in admission.Footprint)
        {
            DomainException.Require(cell.X >= 0 && cell.Z >= 0 && seen.Add(cell), "invalid or duplicate admission cell");
        }
        foreach (var cell in zone.Cells)
        {
            DomainException.Require(seen.Contains(cell), "zone admission footprint differs");
        }
    }

    private Progress Advance(PlanId plan, ActionId action, Transition ev, CancellationToken cancellationToken) =>
        Run(tx => AdvanceIn(tx, plan, action, ev), cancellationToken);

    private Task<Progress> AdvanceAsync(PlanId plan, ActionId action, Transition ev, CancellationToken cancellationToken) =>
        RunAsync(tx => AdvanceIn(tx, plan, action, ev), cancellationToken);

    /// <summary>
    /// Replays one transition onto the stored progress of an action and journals it, in the caller's transaction
    /// (Go <c>advanceInTransaction</c>). Nothing is written when any check or the domain's own transition refuses.
    /// </summary>
    internal static Progress AdvanceIn(StoreTransaction tx, PlanId plan, ActionId action, Transition ev)
    {
        var gated = ev.Kind is TransitionKind.Prepare or TransitionKind.Dispatch;
        if (gated)
        {
            GuardStandardWork(tx, plan, ev.Snapshot, ev.Tick);
        }
        var state = LoadIn(tx, plan);
        if (state.Retired)
        {
            throw new DomainException("retired plan is read-only");
        }
        if (gated)
        {
            CheckDependencies(tx, state, action, ev.Snapshot, ev.Tick);
        }
        var current = state.Progress.FirstOrDefault(p => p.View.Action == action) ?? throw new NotFoundException();
        if (ev.Kind == TransitionKind.Dispatch)
        {
            VetoAction(tx, current.Action);
        }
        // A zone_create prepares only under its method's footprint record, which keeps the footprint accounted
        // while the intent is in flight.
        if (current.Action.Kind == ActionKind.ZoneCreate && ev.Kind == TransitionKind.Prepare)
        {
            if (!state.Admissions.Any(record => record.Action == action && record.Admission.Tick <= ev.Tick))
            {
                throw new DomainException("zone requires shared footprint admission");
            }
        }
        foreach (var record in state.Admissions)
        {
            if (record.Action == action && ev.Kind == TransitionKind.Dispatch && ev.Tick < record.Admission.Tick)
            {
                throw new DomainException("dispatch predates latest admission observation");
            }
        }
        var next = ev.Apply(current);
        _ = tx.ExecuteForPlan(plan, "INSERT INTO transitions(action_id,payload) VALUES($action,$payload)", ("$action", action), ("$payload", StoreJson.Serialize(ev)));
        return next;
    }

    // ---- single transitions ------------------------------------------------------------------------------

    public Progress Prepare(PlanId plan, ActionId action, GenerationSnapshot snapshot, Tick tick, CancellationToken cancellationToken) =>
        Advance(plan, action, new Transition(TransitionKind.Prepare) { Snapshot = snapshot, Tick = tick }, cancellationToken);

    public Task<Progress> PrepareAsync(PlanId plan, ActionId action, GenerationSnapshot snapshot, Tick tick, CancellationToken cancellationToken) =>
        AdvanceAsync(plan, action, new Transition(TransitionKind.Prepare) { Snapshot = snapshot, Tick = tick }, cancellationToken);

    /// <summary>
    /// Authorizes a native call only on success. Any persistence failure means the caller has no authorization to
    /// write; reopen and inspect before continuing.
    /// </summary>
    public Progress Dispatch(PlanId plan, ActionId action, GenerationSnapshot snapshot, Tick tick, CancellationToken cancellationToken) =>
        Advance(plan, action, new Transition(TransitionKind.Dispatch) { Snapshot = snapshot, Tick = tick }, cancellationToken);

    public Task<Progress> DispatchAsync(PlanId plan, ActionId action, GenerationSnapshot snapshot, Tick tick, CancellationToken cancellationToken) =>
        AdvanceAsync(plan, action, new Transition(TransitionKind.Dispatch) { Snapshot = snapshot, Tick = tick }, cancellationToken);

    public Progress RecordReceipt(PlanId plan, ActionId action, AttemptId attempt, Receipt receipt, CancellationToken cancellationToken) =>
        Advance(plan, action, new Transition(TransitionKind.Receipt) { Attempt = attempt, Receipt = receipt }, cancellationToken);

    public Task<Progress> RecordReceiptAsync(PlanId plan, ActionId action, AttemptId attempt, Receipt receipt, CancellationToken cancellationToken) =>
        AdvanceAsync(plan, action, new Transition(TransitionKind.Receipt) { Attempt = attempt, Receipt = receipt }, cancellationToken);

    /// <summary>Records an applied zone_create's receipt with the zone identity native created, which zone claims read.</summary>
    public Progress RecordZoneReceipt(PlanId plan, ActionId action, AttemptId attempt, string zone, CancellationToken cancellationToken) =>
        Advance(plan, action, new Transition(TransitionKind.Receipt) { Attempt = attempt, Receipt = Receipt.Accepted, Zone = zone }, cancellationToken);

    public Task<Progress> RecordZoneReceiptAsync(PlanId plan, ActionId action, AttemptId attempt, string zone, CancellationToken cancellationToken) =>
        AdvanceAsync(plan, action, new Transition(TransitionKind.Receipt) { Attempt = attempt, Receipt = Receipt.Accepted, Zone = zone }, cancellationToken);

    public Progress RecordStockpileReceipt(PlanId plan, ActionId action, AttemptId attempt, IReadOnlyList<CreatedZone> zones, CancellationToken cancellationToken) =>
        Advance(plan, action, new Transition(TransitionKind.Receipt) { Attempt = attempt, Receipt = Receipt.Accepted, Stockpiles = EquatableArray.From(zones) }, cancellationToken);

    public Task<Progress> RecordStockpileReceiptAsync(PlanId plan, ActionId action, AttemptId attempt, IReadOnlyList<CreatedZone> zones, CancellationToken cancellationToken) =>
        AdvanceAsync(plan, action, new Transition(TransitionKind.Receipt) { Attempt = attempt, Receipt = Receipt.Accepted, Stockpiles = EquatableArray.From(zones) }, cancellationToken);

    /// <summary>
    /// Records an applied production_bill's or surgery's receipt with the native bill id its evidence named,
    /// which a later remove_production_bill names.
    /// </summary>
    public Progress RecordBillReceipt(PlanId plan, ActionId action, AttemptId attempt, string bill, CancellationToken cancellationToken) =>
        Advance(plan, action, new Transition(TransitionKind.Receipt) { Attempt = attempt, Receipt = Receipt.Accepted, Bill = bill }, cancellationToken);

    public Task<Progress> RecordBillReceiptAsync(PlanId plan, ActionId action, AttemptId attempt, string bill, CancellationToken cancellationToken) =>
        AdvanceAsync(plan, action, new Transition(TransitionKind.Receipt) { Attempt = attempt, Receipt = Receipt.Accepted, Bill = bill }, cancellationToken);

    public Progress Observe(PlanId plan, Observation observation, GenerationSnapshot current, CancellationToken cancellationToken) =>
        Advance(plan, observation.Action, new Transition(TransitionKind.Observe) { Snapshot = current, Observation = observation }, cancellationToken);

    public Task<Progress> ObserveAsync(PlanId plan, Observation observation, GenerationSnapshot current, CancellationToken cancellationToken) =>
        AdvanceAsync(plan, observation.Action, new Transition(TransitionKind.Observe) { Snapshot = current, Observation = observation }, cancellationToken);

    public Progress Cancel(PlanId plan, ActionId action, CancellationToken cancellationToken) =>
        Advance(plan, action, new Transition(TransitionKind.Cancel), cancellationToken);

    public Task<Progress> CancelAsync(PlanId plan, ActionId action, CancellationToken cancellationToken) =>
        AdvanceAsync(plan, action, new Transition(TransitionKind.Cancel), cancellationToken);

    /// <summary>
    /// Opens the native withdrawal attempt of a cancelled, still-pending dispatch (<see cref="Progress.Withdraw"/>);
    /// the executor writes the cancellation only after this is durable, as with <see cref="Dispatch"/>.
    /// </summary>
    public Progress Withdraw(PlanId plan, ActionId action, GenerationSnapshot snapshot, Tick tick, CancellationToken cancellationToken) =>
        Advance(plan, action, new Transition(TransitionKind.Withdraw) { Snapshot = snapshot, Tick = tick }, cancellationToken);

    public Task<Progress> WithdrawAsync(PlanId plan, ActionId action, GenerationSnapshot snapshot, Tick tick, CancellationToken cancellationToken) =>
        AdvanceAsync(plan, action, new Transition(TransitionKind.Withdraw) { Snapshot = snapshot, Tick = tick }, cancellationToken);

    /// <summary>
    /// Durably records why a not-yet-dispatched action is currently stuck. It reuses the same transaction-guarded
    /// replay as every other transition, so a concurrent transition committed first is observed, never overwritten.
    /// </summary>
    public Progress Hold(PlanId plan, ActionId action, IReadOnlyList<HeldReason> reasons, Tick tick, CancellationToken cancellationToken) =>
        Advance(plan, action, new Transition(TransitionKind.Hold) { Tick = tick, HeldReasons = EquatableArray.From(reasons) }, cancellationToken);

    public Task<Progress> HoldAsync(PlanId plan, ActionId action, IReadOnlyList<HeldReason> reasons, Tick tick, CancellationToken cancellationToken) =>
        AdvanceAsync(plan, action, new Transition(TransitionKind.Hold) { Tick = tick, HeldReasons = EquatableArray.From(reasons) }, cancellationToken);

    // ---- batches -----------------------------------------------------------------------------------------

    /// <summary>Prepares every attempt in one transaction.</summary>
    public IReadOnlyList<BatchResult> PrepareBatch(IReadOnlyList<BatchAttempt> attempts, CancellationToken cancellationToken) =>
        AdvanceBatch(attempts.Count, i => (attempts[i].Plan, attempts[i].Action, new Transition(TransitionKind.Prepare) { Snapshot = attempts[i].Snapshot, Tick = attempts[i].Tick }), cancellationToken);

    public Task<IReadOnlyList<BatchResult>> PrepareBatchAsync(IReadOnlyList<BatchAttempt> attempts, CancellationToken cancellationToken) =>
        Task.Run(() => PrepareBatch(attempts, cancellationToken), cancellationToken);

    /// <summary>Authorizes every attempt in one transaction; as with <see cref="Dispatch"/>, only an item without an error may be sent.</summary>
    public IReadOnlyList<BatchResult> DispatchBatch(IReadOnlyList<BatchAttempt> attempts, CancellationToken cancellationToken) =>
        AdvanceBatch(attempts.Count, i => (attempts[i].Plan, attempts[i].Action, new Transition(TransitionKind.Dispatch) { Snapshot = attempts[i].Snapshot, Tick = attempts[i].Tick }), cancellationToken);

    public Task<IReadOnlyList<BatchResult>> DispatchBatchAsync(IReadOnlyList<BatchAttempt> attempts, CancellationToken cancellationToken) =>
        Task.Run(() => DispatchBatch(attempts, cancellationToken), cancellationToken);

    /// <summary>Writes every receipt in one transaction.</summary>
    public IReadOnlyList<BatchResult> RecordReceipts(IReadOnlyList<BatchReceipt> receipts, CancellationToken cancellationToken) =>
        AdvanceBatch(
            receipts.Count,
            i => (receipts[i].Plan, receipts[i].Action, new Transition(TransitionKind.Receipt) { Attempt = receipts[i].Attempt, Receipt = receipts[i].Receipt, Combat = receipts[i].Combat, Refusal = receipts[i].Refusal }),
            cancellationToken
        );

    public Task<IReadOnlyList<BatchResult>> RecordReceiptsAsync(IReadOnlyList<BatchReceipt> receipts, CancellationToken cancellationToken) =>
        Task.Run(() => RecordReceipts(receipts, cancellationToken), cancellationToken);

    /// <summary>
    /// Runs each item under its own savepoint, so a rejected item rolls back only its own writes. An exception
    /// from here means nothing committed and every result is void.
    /// </summary>
    private IReadOnlyList<BatchResult> AdvanceBatch(int count, Func<int, (PlanId Plan, ActionId Action, Transition Event)> item, CancellationToken cancellationToken)
    {
        if (count == 0)
        {
            return [];
        }
        return Run(
            tx =>
            {
                var results = new BatchResult[count];
                for (var i = 0; i < count; i++)
                {
                    tx.Savepoint("batch_item");
                    var (plan, action, ev) = item(i);
                    try
                    {
                        results[i] = new BatchResult(AdvanceIn(tx, plan, action, ev), null);
                    }
                    catch (Exception e) when (e is DomainException or SqliteException or StoreCorruptException)
                    {
                        tx.RollbackToSavepoint("batch_item");
                        results[i] = new BatchResult(null, Conflict(e));
                    }
                    tx.ReleaseSavepoint("batch_item");
                }
                return (IReadOnlyList<BatchResult>)results;
            },
            cancellationToken
        );
    }
}
