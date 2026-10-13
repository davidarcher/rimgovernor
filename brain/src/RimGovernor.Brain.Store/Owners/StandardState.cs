using System.Globalization;
using Microsoft.Data.Sqlite;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Store.Codec;
using static RimGovernor.Brain.Store.StorePackage;

namespace RimGovernor.Brain.Store;

/// <summary>
/// A <see cref="Standard"/> linked to the existing plan catalog (Go <c>StandardState</c>). <see cref="Revision"/> is
/// a local compare-and-swap token, not a native generation or permission to run a method.
/// </summary>
/// <param name="Methods">Active plans; retired methods remain in <see cref="Store.LoadMethod"/>.</param>
/// <param name="Admitted">
/// Every method ever committed for the standard, retired plans included: a monotonic salt for method
/// identities that must not collide with a retired plan's row.
/// </param>
/// <param name="History">
/// The current epoch's methods, retired plans included. Planners that number attempts count it, not
/// <paramref name="Methods"/>: a retired plan drops out of Methods, and re-deriving its id fails plans.id's unique
/// constraint.
/// </param>
public sealed record StandardState(Standard Standard, ulong Revision, EquatableArray<Method> Methods, int Admitted, EquatableArray<Method> History, bool Retired) : IMethodOwner
{
    /// <summary>An open standard whose review found a deficit: the state a planner may work.</summary>
    public bool Deficit => Standard.Status == StandardStatus.Open && Standard.Finding == Finding.Unmet;

    GenerationSnapshot IMethodOwner.OwnerSnapshot => Standard.Snapshot;

    int IMethodOwner.OwnerPriority => Standard.Priority;

    IReadOnlyList<PlanId> IMethodOwner.OwnerPlans => [.. Methods.Select(m => m.Plan)];

    string IMethodOwner.OwnerLabel => "standard " + Standard.Id.Value;

    bool IMethodOwner.OwnerNeed(Rounds rounds, out ConcernId need) => rounds.Need(Standard.Id, out need);

    PlanId? IMethodOwner.BoundPlan(StoreTransaction tx, MethodId method) =>
        tx.ScalarText(
            "SELECT plan_id FROM standard_methods WHERE standard_id=$id AND episode=$episode AND method_id=$method",
            ("$id", Standard.Id),
            ("$episode", Standard.Episode.ToString(CultureInfo.InvariantCulture)),
            ("$method", method)
        ) is { } plan
            ? new PlanId(plan)
            : null;

    void IMethodOwner.BindMethod(StoreTransaction tx, MethodId method, PlanId plan, string reason)
    {
        Store.InsertPlanOwner(tx, plan, "standard");
        _ = tx.Execute(
            "INSERT INTO standard_methods(standard_id,episode,method_id,plan_id,priority,reason) VALUES($id,$episode,$method,$plan,$priority,$reason)",
            ("$id", Standard.Id),
            ("$episode", Standard.Episode.ToString(CultureInfo.InvariantCulture)),
            ("$method", method),
            ("$plan", plan),
            ("$priority", Standard.Priority),
            ("$reason", reason.Length == 0 ? null : reason)
        );
    }

    long IMethodOwner.ServedCount(StoreTransaction tx) =>
        tx.Scalar("SELECT count(*) FROM standard_methods WHERE standard_id=$id AND episode=$episode", ("$id", Standard.Id), ("$episode", Standard.Episode.ToString(CultureInfo.InvariantCulture)));

    /// <summary>True for a standard that is active in the goal vocabulary.</summary>
    internal bool OwnerActive => Standard.Status == StandardStatus.Open;
}

/// <summary>The lifecycle view of a standard the HTTP API reports (Go <c>OwnerSummary</c>).</summary>
/// <param name="Kind">The Concern the standard serves.</param>
public sealed record OwnerSummary(
    string Id,
    ConcernId Kind,
    StandardStatus Status,
    Finding Finding,
    int Priority,
    ulong Episode,
    ulong Revision,
    GenerationSnapshot Snapshot,
    bool Retired
);

/// <summary>
/// What method admission reads of the standard or incident a methods row binds to (Go <c>methodOwner</c>): the
/// world it was last reviewed in, the routine need it serves and its open plans.
/// </summary>
internal interface IMethodOwner
{
    GenerationSnapshot OwnerSnapshot { get; }

    /// <summary>
    /// The routine need the owner serves under this review: a standard's review binding, an incident's kind.
    /// False when the review does not bind the owner.
    /// </summary>
    bool OwnerNeed(Rounds rounds, out ConcernId need);

    int OwnerPriority { get; }

    IReadOnlyList<PlanId> OwnerPlans { get; }

    string OwnerLabel { get; }

    /// <summary>The plan the owner binds <paramref name="method"/> to in its current episode, retired plans included; null when none.</summary>
    PlanId? BoundPlan(StoreTransaction tx, MethodId method);

    /// <summary>Writes the owner's methods row and the plan_owner row that keeps plan_id unique across the owner tables.</summary>
    void BindMethod(StoreTransaction tx, MethodId method, PlanId plan, string reason);

    /// <summary>The owner's methods in its current episode, retired plans included.</summary>
    long ServedCount(StoreTransaction tx);
}

public sealed partial class Store
{
    /// <summary>The most standards that may be unretired at once.</summary>
    internal const int MaxActiveStandards = 512;

    private const int MaxStandardPayload = 8192;

    internal static OwnerSummary SummarizeOwner(StandardState o) =>
        new(o.Standard.Id.Value, o.Standard.Kind, o.Standard.Status, o.Standard.Finding, o.Standard.Priority, o.Standard.Episode, o.Revision, o.Standard.Snapshot, o.Retired);

    internal static void InsertPlanOwner(StoreTransaction tx, PlanId plan, string kind) =>
        tx.ExecuteOrConflict("INSERT INTO plan_owner(plan_id,kind) VALUES($plan,$kind)", ("$plan", plan), ("$kind", kind));

    internal static void CreateStandard(StoreTransaction tx, Standard g)
    {
        g.Validate();
        if (tx.Scalar("SELECT count(*) FROM standards WHERE retired=0") >= MaxActiveStandards)
        {
            throw new CapacityException();
        }
        tx.ExecuteOrConflict("INSERT INTO standards(id,revision,payload) VALUES($id,'0',$payload)", ("$id", g.Id), ("$payload", StoreJson.Serialize(g)));
    }

    internal static StandardState LoadStandard(StoreTransaction tx, StandardId id)
    {
        var (row, found) = tx.QueryFirst("SELECT revision,payload,retired FROM standards WHERE id=$id", [("$id", id)], r => (Revision: r.GetString(0), Payload: r.GetString(1), Retired: r.GetInt64(2) != 0));
        if (!found)
        {
            throw new NotFoundException();
        }
        if (!ulong.TryParse(row.Revision, NumberStyles.None, CultureInfo.InvariantCulture, out var revision) || revision.ToString(CultureInfo.InvariantCulture) != row.Revision)
        {
            throw new StoreCorruptException("invalid standard revision");
        }
        if (row.Payload.Length > MaxStandardPayload)
        {
            throw new StoreCorruptException("standard payload exceeds bound");
        }
        var standard = StoreJson.Deserialize<Standard>(row.Payload);
        try
        {
            standard.Validate();
        }
        catch (DomainException e)
        {
            throw new StoreCorruptException("standard: " + e.Message);
        }
        if (standard.Id != id)
        {
            throw new StoreCorruptException("standard identity mismatch");
        }
        if (row.Retired && standard.Status != StandardStatus.Voided)
        {
            throw new StoreCorruptException("invalid retired standard");
        }
        var admitted = (int)tx.Scalar("SELECT COUNT(*) FROM standard_methods WHERE standard_id=$id", ("$id", id));
        var history = tx.Query(
            "SELECT method_id,plan_id FROM standard_methods WHERE standard_id=$id AND episode=$episode ORDER BY method_id",
            [("$id", id), ("$episode", standard.Episode.ToString(CultureInfo.InvariantCulture))],
            r => new Method(id, standard.Episode, new MethodId(r.GetString(0)), new PlanId(r.GetString(1)))
        );
        var methods = new List<Method>();
        foreach (var (episodeText, methodId, planId) in tx.Query(
            "SELECT m.episode,m.method_id,m.plan_id FROM plans p INDEXED BY active_plans CROSS JOIN standard_methods m ON m.plan_id=p.id WHERE p.retired=0 AND m.standard_id=$id ORDER BY length(m.episode),m.episode,m.method_id",
            [("$id", id)],
            r => (r.GetString(0), r.GetString(1), r.GetString(2))
        ))
        {
            if (!ulong.TryParse(episodeText, NumberStyles.None, CultureInfo.InvariantCulture, out var episode) || episode.ToString(CultureInfo.InvariantCulture) != episodeText || episode > standard.Episode)
            {
                throw new StoreCorruptException("invalid standard method record");
            }
            var m = new Method(id, episode, new MethodId(methodId), new PlanId(planId));
            try
            {
                m.Validate();
            }
            catch (DomainException)
            {
                throw new StoreCorruptException("invalid standard method record");
            }
            methods.Add(m);
            if (methods.Count > 256)
            {
                throw new CapacityException();
            }
        }
        return new StandardState(standard, revision, EquatableArray.From(methods), admitted, EquatableArray.From(history), row.Retired);
    }

    /// <summary>The standard with the id, with its methods.</summary>
    public StandardState LoadStandard(StandardId id, CancellationToken cancellationToken) => Run(tx => LoadStandard(tx, id), cancellationToken);

    public Task<StandardState> LoadStandardAsync(StandardId id, CancellationToken cancellationToken) => RunAsync(tx => LoadStandard(tx, id), cancellationToken);

    /// <summary>
    /// The open standards of <paramref name="kind"/>, including ones the rounds do not bind to an assessment (a
    /// trade mission). Their methods use the same Hands admission and journal.
    /// </summary>
    public IReadOnlyList<StandardState> OpenStandards(ConcernId kind, CancellationToken cancellationToken) =>
        Run(
            tx =>
            {
                var ids = tx.Query("SELECT id FROM standards WHERE retired=0 ORDER BY id", [], r => new StandardId(r.GetString(0)));
                var open = new List<StandardState>();
                foreach (var id in ids)
                {
                    var g = LoadStandard(tx, id);
                    if (g.Standard.Kind == kind && g.Standard.Status == StandardStatus.Open)
                    {
                        open.Add(g);
                    }
                }
                return (IReadOnlyList<StandardState>)open;
            },
            cancellationToken
        );

    public Task<IReadOnlyList<StandardState>> OpenStandardsAsync(ConcernId kind, CancellationToken cancellationToken) =>
        Task.Run(() => OpenStandards(kind, cancellationToken), cancellationToken);

    /// <summary>Writes a changed standard, bumping its revision; an unchanged one is returned as it was.</summary>
    internal static StandardState SaveStandard(StoreTransaction tx, StandardState previous, Standard g)
    {
        if (previous.Retired)
        {
            throw new DomainException("retired standard is read-only");
        }
        if (g == previous.Standard)
        {
            return previous;
        }
        if (previous.Revision == ulong.MaxValue)
        {
            throw new CapacityException();
        }
        g.Validate();
        var next = previous.Revision + 1;
        _ = tx.Execute(
            "UPDATE standards SET revision=$revision,payload=$payload WHERE id=$id",
            ("$revision", next.ToString(CultureInfo.InvariantCulture)),
            ("$payload", StoreJson.Serialize(g)),
            ("$id", g.Id)
        );
        return previous with { Standard = g, Revision = next };
    }

    internal static void BumpRevision(StoreTransaction tx, StandardState g) =>
        _ = tx.Execute("UPDATE standards SET revision=$revision WHERE id=$id", ("$revision", (g.Revision + 1).ToString(CultureInfo.InvariantCulture)), ("$id", g.Standard.Id));

    /// <summary>Whether any plan of the owner is still open work, or a fight of one holds (Go <c>standardOpenWork</c>).</summary>
    internal static bool StandardOpenWork(StoreTransaction tx, IMethodOwner owner)
    {
        var open = false;
        foreach (var plan in owner.OwnerPlans)
        {
            var p = LoadIn(tx, plan);
            open = open || (PlanOpen(p) && !RulesAttachOnly(p.Spec));
            if (!open)
            {
                // A fight's drafts are its open work.
                open = CombatFightHolds(tx, plan);
            }
        }
        return open;
    }

    /// <summary>
    /// A plan of native rule attachments alone: one in-memory write per Round that holds no pawn work, so it
    /// neither waits behind the owner's open work nor counts as open work for its other planners.
    /// </summary>
    internal static bool RulesAttachOnly(PlanSpec plan) => plan.Actions.Count > 0 && plan.Actions.All(a => a.Kind == ActionKind.RulesAttach);

    /// <summary><see cref="StandardOpenWork"/> without the fights: whether any of the owner's plans is still open.</summary>
    internal static bool PlanOpenWork(StoreTransaction tx, IMethodOwner owner) => owner.OwnerPlans.Any(plan => PlanOpen(LoadIn(tx, plan)));

    /// <summary>
    /// Replaces the standard's trade mission at <paramref name="revision"/> (Go <c>RecordStandard</c>, whose
    /// record was the mission's JSON text). The mission rides in the standard's own payload.
    /// </summary>
    public StandardState RecordStandard(StandardId id, ulong revision, TradeMission? mission, CancellationToken cancellationToken) =>
        Run(
            tx =>
            {
                var state = LoadStandard(tx, id);
                return state.Revision != revision ? throw new ConflictException() : WriteStandardRecord(tx, state, mission);
            },
            cancellationToken
        );

    public Task<StandardState> RecordStandardAsync(StandardId id, ulong revision, TradeMission? mission, CancellationToken cancellationToken) =>
        Task.Run(() => RecordStandard(id, revision, mission, cancellationToken), cancellationToken);

    /// <summary>
    /// <see cref="CommitMethodReason"/> that also replaces the standard's record in the same transaction, so a
    /// method and the intent it carries are saved together or not at all.
    /// </summary>
    public StandardState CommitMethodRecord(StandardId id, ulong revision, MethodId method, string reason, PlanSpec plan, TradeMission? mission, CancellationToken cancellationToken)
    {
        plan.Validate();
        return Run(tx => WriteStandardRecord(tx, CommitMethodIn(tx, id, revision, method, reason, plan), mission), cancellationToken);
    }

    public Task<StandardState> CommitMethodRecordAsync(StandardId id, ulong revision, MethodId method, string reason, PlanSpec plan, TradeMission? mission, CancellationToken cancellationToken) =>
        Task.Run(() => CommitMethodRecord(id, revision, method, reason, plan, mission, cancellationToken), cancellationToken);

    private static StandardState WriteStandardRecord(StoreTransaction tx, StandardState state, TradeMission? mission) =>
        SaveStandard(tx, state, state.Standard with { Mission = mission });

    /// <summary>
    /// Reviews a standard against the observed <paramref name="need"/>: voids it on a world change, cancels a
    /// voided standard's methods, and files the new finding and episode (Go <c>ReviewStandard</c>).
    /// </summary>
    public StandardState ReviewStandard(StandardId id, ulong revision, GenerationSnapshot current, Finding need, CancellationToken cancellationToken) =>
        Run(
            tx =>
            {
                var state = LoadStandard(tx, id);
                if (state.Revision != revision)
                {
                    throw new ConflictException();
                }
                var open = StandardOpenWork(tx, state);
                var g = Standard.Review(state.Standard, current, need, open);
                if (g.Status == StandardStatus.Voided)
                {
                    CancelMethods(tx, state);
                }
                return SaveStandard(tx, state, g);
            },
            cancellationToken
        );

    public Task<StandardState> ReviewStandardAsync(StandardId id, ulong revision, GenerationSnapshot current, Finding need, CancellationToken cancellationToken) =>
        Task.Run(() => ReviewStandard(id, revision, current, need, cancellationToken), cancellationToken);

    /// <summary>Stores the method and its shared plan atomically. Admission and dispatch still belong to existing policy/Hands; this grants no authority.</summary>
    public StandardState CommitMethod(StandardId id, ulong revision, MethodId method, PlanSpec plan, CancellationToken cancellationToken) =>
        CommitMethodReason(id, revision, method, "", plan, cancellationToken);

    /// <summary>
    /// <see cref="CommitMethod"/> with the planner's short reason for admitting it (runway, deficit, target); the
    /// dispatcher appends it to the operation intent. Empty stores none.
    /// </summary>
    public StandardState CommitMethodReason(StandardId id, ulong revision, MethodId method, string reason, PlanSpec plan, CancellationToken cancellationToken)
    {
        plan.Validate();
        return plan.Actions.Count == 0
            ? throw new DomainException("empty standard method")
            : Run(tx => CommitMethodIn(tx, id, revision, method, reason, plan), cancellationToken);
    }

    public Task<StandardState> CommitMethodReasonAsync(StandardId id, ulong revision, MethodId method, string reason, PlanSpec plan, CancellationToken cancellationToken) =>
        Task.Run(() => CommitMethodReason(id, revision, method, reason, plan, cancellationToken), cancellationToken);

    /// <summary>Stores a method for the standard like <see cref="CommitMethodReason"/>.</summary>
    public void CommitOwnerMethod(StandardState owner, MethodId method, string reason, PlanSpec plan, CancellationToken cancellationToken) =>
        _ = CommitMethodReason(owner.Standard.Id, owner.Revision, method, reason, plan, cancellationToken);

    /// <summary>The standard's binding of <paramref name="method"/> in its current epoch, including a retired plan's.</summary>
    public Method LoadOwnerMethod(StandardState owner, MethodId method, CancellationToken cancellationToken) =>
        LoadMethod(owner.Standard.Id, owner.Standard.Episode, method, cancellationToken);

    /// <summary>The standard's methods in its current epoch, retired bindings included.</summary>
    public IReadOnlyList<Method> LoadOwnerMethods(StandardState owner, CancellationToken cancellationToken) =>
        LoadMethods(owner.Standard.Id, owner.Standard.Episode, cancellationToken);

    internal static StandardState CommitMethodIn(StoreTransaction tx, StandardId id, ulong revision, MethodId method, string reason, PlanSpec plan)
    {
        var state = LoadStandard(tx, id);
        AdmitOwnerCommit(tx, state, revision, method, reason, plan);
        return LoadStandard(tx, id);
    }

    /// <summary>
    /// The commit a standard method shares with <see cref="CommitMethod"/>: the revision compare-and-swap, the
    /// open-deficit check, the Safeguards and current review, the open-work check with its exemptions, then the
    /// family admission, the methods row and the revision bump. The caller reloads the owner.
    /// </summary>
    private static void AdmitOwnerCommit(StoreTransaction tx, StandardState state, ulong revision, MethodId method, string reason, PlanSpec plan)
    {
        var summary = SummarizeOwner(state);
        if (state.Revision != revision)
        {
            throw new StaleOwnerException($"{((IMethodOwner)state).OwnerLabel} is at revision {state.Revision}, not {revision}");
        }
        if (summary.Status != StandardStatus.Open || summary.Finding != Finding.Unmet)
        {
            throw new DomainException("standard does not admit a method");
        }
        AdmitRoundsSafeguards(tx, state);
        AdmitRoundsReview(tx, summary);
        var open = StandardOpenWork(tx, state);
        if (open)
        {
            // An uncertain remote request earns no stock and cannot monopolize its supply owner. Ordinary local
            // methods may proceed while its separate Hands attempt remains unresolved.
            var onlyRequests = true;
            foreach (var linked in state.Methods)
            {
                var pending = LoadIn(tx, linked.Plan);
                foreach (var progress in pending.Progress)
                {
                    if (Standard.WorkOpen([progress]) && progress.Action.Kind != ActionKind.CommsTradeRequest)
                    {
                        onlyRequests = false;
                    }
                }
            }
            if (plan.Actions.Any(a => a.Kind == ActionKind.CommsTradeRequest))
            {
                onlyRequests = false;
            }
            if (onlyRequests)
            {
                open = false;
            }
        }
        if (open && !OpenWorkExempt(tx, state, method, plan))
        {
            throw new OpenMethodException();
        }
        var m = new Method(state.Standard.Id, summary.Episode, method, plan.Id);
        m.Validate();
        if (state.Revision == ulong.MaxValue)
        {
            throw new CapacityException();
        }
        BindOwnerMethod(tx, state, method, reason, plan);
        BumpRevision(tx, state);
    }

    /// <summary>Whether one of the open-work exemptions admits <paramref name="plan"/> beside the owner's open work.</summary>
    private static bool OpenWorkExempt(StoreTransaction tx, StandardState state, MethodId method, PlanSpec plan) =>
        AcquisitionOpenWorkExempt(tx, state, plan)
        || FieldOpenWorkExempt(tx, state, plan)
        || GrowerCropOpenWorkExempt(plan)
        || RulesAttachOnly(plan)
        || MealReplacementOpenWorkExempt(state, plan)
        || FoodFacilityOpenWorkExempt(tx, state, method, plan)
        || ReserveAccessOpenWorkExempt(tx, state, plan)
        || GearOpenWorkExempt(tx, state, plan)
        || CraftingSpotOpenWorkExempt(tx, state, plan)
        || ShelterOpenWorkExempt(tx, state, method, plan)
        || RoomShellOpenWorkExempt(tx, state, method, plan)
        || BuildingOpenWorkExempt(tx, state, plan);

    /// <summary>
    /// Cancels every open method of the owner that no step ever dispatched: each action is still Pending or
    /// Prepared on its first attempt. A review that observes the standard recovered runs it before counting open
    /// work, so a plan admitted for a deficit the colonists (or the player) cleared on their own settles here
    /// instead of holding the standard Active and never authorized. Work already dispatched keeps its own
    /// settlement path.
    /// </summary>
    internal static void CancelUndispatchedMethods(StoreTransaction tx, IMethodOwner owner)
    {
        foreach (var plan in owner.OwnerPlans)
        {
            var p = LoadIn(tx, plan);
            if (p.Retired || !Standard.WorkOpen(p.Progress))
            {
                continue;
            }
            var undispatched = p.Progress.All(progress =>
            {
                var v = progress.View;
                return v.Attempt.Value == 0 && v.Stage is Stage.Pending or Stage.Prepared or Stage.Cancelled;
            });
            if (!undispatched)
            {
                continue;
            }
            foreach (var progress in p.Progress)
            {
                var v = progress.View;
                if (v.Stage != Stage.Cancelled)
                {
                    _ = AdvanceIn(tx, plan, v.Action, new Transition(TransitionKind.Cancel));
                }
            }
        }
    }

    internal static void CancelMethods(StoreTransaction tx, IMethodOwner owner)
    {
        foreach (var plan in owner.OwnerPlans)
        {
            var p = LoadIn(tx, plan);
            foreach (var progress in p.Progress)
            {
                var v = progress.View;
                // A completed intent-mode order stands until cancelled; cancelling it releases the draft it holds.
                if ((v.Stage == Stage.Completed && !progress.Action.Kind.IntentMode()) || v.Stage is Stage.Unsuccessful or Stage.Cancelled)
                {
                    continue;
                }
                _ = AdvanceIn(tx, plan, v.Action, new Transition(TransitionKind.Cancel));
            }
        }
    }

    /// <summary>
    /// The guard every prepare and dispatch runs: the plan is live, its world is not retired, and its owning
    /// standard or incident still admits current work (Go <c>guardStandardWork</c>).
    /// </summary>
    internal static void GuardStandardWork(StoreTransaction tx, PlanId plan, GenerationSnapshot current, Tick tick)
    {
        var (retired, found) = tx.QueryFirst("SELECT retired FROM plans WHERE id=$id", [("$id", plan)], r => r.GetInt64(0) != 0);
        if (!found)
        {
            throw new NotFoundException();
        }
        if (retired)
        {
            throw new DomainException("retired plan does not admit work");
        }
        tx.Floors.Guard(current, tick);
        var (owner, ownerFound) = tx.QueryFirst("SELECT kind,owner_id,episode FROM plan_methods WHERE plan_id=$id", [("$id", plan)], r => (Kind: r.GetString(0), Owner: r.GetString(1), Episode: r.Text(2)));
        if (!ownerFound)
        {
            return;
        }
        if (owner.Kind == "incident")
        {
            GuardIncidentWork(tx, new IncidentId(owner.Owner), current, tick);
            return;
        }
        var state = LoadStandard(tx, new StandardId(owner.Owner));
        var g = state.Standard;
        var s = g.Snapshot;
        if (g.Status != StandardStatus.Open
            || g.Finding == Finding.Unclear
            || owner.Episode != g.Episode.ToString(CultureInfo.InvariantCulture)
            || s.Colony != current.Colony
            || s.Map != current.Map)
        {
            throw new DomainException("maintained standard does not admit current work");
        }
        // A prepared plan does not prepare or dispatch while a Safeguard vetoes its standard.
        AdmitRoundsSafeguards(tx, state);
    }

    /// <summary>
    /// Retirement retains identities, methods and progress. Only invalidated autopilot standards without
    /// observation or cleanup obligations leave capacity.
    /// </summary>
    internal static void RetireRoundsStandards(StoreTransaction tx, ISet<StandardId> retained)
    {
        var ids = tx.Query("SELECT id FROM standards WHERE retired=0 ORDER BY id LIMIT 257", [], r => new StandardId(r.GetString(0)));
        if (ids.Count > MaxActiveStandards)
        {
            throw new CapacityException();
        }
        foreach (var id in ids)
        {
            if (retained.Contains(id))
            {
                continue;
            }
            var g = LoadStandard(tx, id);
            if (g.Standard.Status != StandardStatus.Voided || StandardOpenWork(tx, g))
            {
                continue;
            }
            _ = tx.Execute("UPDATE standards SET retired=1 WHERE id=$id", ("$id", id));
        }
    }
}
