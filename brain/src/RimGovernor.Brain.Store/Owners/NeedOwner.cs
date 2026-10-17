using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Store;

/// <summary>One Method bound to a Need: its MethodId, the attempt it was admitted as and the plan that executes it.</summary>
public sealed record NeedMethodBinding(MethodId Method, long Attempt, PlanId Plan);

/// <summary>
/// The journal owner a shared planner commits under (#3279) and that method admission reads (#3250): a Need in one world (colony, load, map), keyed by its
/// <see cref="NeedId"/>. A Need is derived, never stored, so the owner is only that key and its
/// <c>need_methods</c> rows. It has no episodes or revision: a MethodId whose plan is still open continues, and one
/// whose plan was released (retired, or its work closed) is admitted again as the next attempt.
/// </summary>
/// <param name="Snapshot">The world the Need was derived in; only its colony, load and map key the rows.</param>
/// <param name="Methods">The bound Methods whose plans are not retired, in admission order.</param>
public sealed record NeedOwner(NeedId Need, GenerationSnapshot Snapshot, EquatableArray<NeedMethodBinding> Methods)
{
    /// <summary>The owner's id in telemetry: the NeedId.</summary>
    public string OwnerKey => Need.Value;

    /// <summary>The owner's Methods whose plans are not retired, in admission order.</summary>
    public IReadOnlyList<OwnedMethod> OpenMethods => [.. Methods.Select(m => new OwnedMethod(m.Method, m.Plan))];

    internal IReadOnlyList<PlanId> OwnerPlans => [.. Methods.Select(m => m.Plan)];

    internal string OwnerLabel => "need " + Need.Value;

    /// <summary>The plan of <paramref name="method"/>'s latest attempt while it is still open work; null once released or never bound.</summary>
    internal PlanId? BoundPlan(StoreTransaction tx, MethodId method)
    {
        var (plan, found) = tx.QueryFirst(
            "SELECT plan_id FROM need_methods WHERE colony=$colony AND load_token=$load AND map_id=$map AND need_id=$need AND method_id=$method ORDER BY attempt DESC LIMIT 1",
            [("$colony", Snapshot.Colony), ("$load", Snapshot.Load), ("$map", Snapshot.Map), ("$need", Need.Value), ("$method", method)],
            r => new PlanId(r.GetString(0)));
        if (!found)
        {
            return null;
        }
        var held = Store.LoadIn(tx, plan);
        return !held.Retired && WorkProgress.Open(held.Progress) ? plan : null;
    }

    /// <summary>Writes the owner's methods row and the plan_owner row that keeps plan_id unique.</summary>
    internal void BindMethod(StoreTransaction tx, MethodId method, PlanId plan, string reason)
    {
        Store.InsertPlanOwner(tx, plan);
        var attempt = tx.Scalar(
            "SELECT COALESCE(MAX(attempt),0) FROM need_methods WHERE colony=$colony AND load_token=$load AND map_id=$map AND need_id=$need AND method_id=$method",
            ("$colony", Snapshot.Colony), ("$load", Snapshot.Load), ("$map", Snapshot.Map), ("$need", Need.Value), ("$method", method)) + 1;
        _ = tx.Execute(
            "INSERT INTO need_methods(colony,load_token,map_id,need_id,method_id,attempt,plan_id,reason) VALUES($colony,$load,$map,$need,$method,$attempt,$plan,$reason)",
            ("$colony", Snapshot.Colony),
            ("$load", Snapshot.Load),
            ("$map", Snapshot.Map),
            ("$need", Need.Value),
            ("$method", method),
            ("$attempt", attempt),
            ("$plan", plan),
            ("$reason", reason.Length == 0 ? null : reason)
        );
    }

    /// <summary>The owner's methods, retired plans included.</summary>
    internal long ServedCount(StoreTransaction tx) =>
        tx.Scalar(
            "SELECT count(*) FROM need_methods WHERE colony=$colony AND load_token=$load AND map_id=$map AND need_id=$need",
            ("$colony", Snapshot.Colony), ("$load", Snapshot.Load), ("$map", Snapshot.Map), ("$need", Need.Value));
}

public sealed partial class Store
{
    /// <summary>
    /// The capacity guard on a Need's unretired Methods. Settled plans retire, and the whole unretired plan catalog is
    /// already bounded at 256 by <see cref="RetireRoundsPlans"/>, so no Need legitimately holds more; its released and
    /// retired history is unbounded and read through <see cref="OwnerMethodHistory"/> and <see cref="OwnerMethodPlan"/>.
    /// </summary>
    private const int MaxNeedMethods = 256;

    /// <summary>The Need's owner in <paramref name="world"/>: its unretired Methods.</summary>
    public NeedOwner LoadNeed(NeedId need, GenerationSnapshot world, CancellationToken cancellationToken)
    {
        need.Validate();
        return Run(tx => LoadNeedIn(tx, need, world), cancellationToken);
    }

    internal static NeedOwner LoadNeedIn(StoreTransaction tx, NeedId need, GenerationSnapshot world)
    {
        var methods = tx.Query(
            "SELECT m.method_id,m.attempt,m.plan_id FROM need_methods m JOIN plans p ON p.id=m.plan_id WHERE p.retired=0 AND m.colony=$colony AND m.load_token=$load AND m.map_id=$map AND m.need_id=$need ORDER BY m.rowid LIMIT 257",
            [("$colony", world.Colony), ("$load", world.Load), ("$map", world.Map), ("$need", need.Value)],
            r => new NeedMethodBinding(new MethodId(r.GetString(0)), r.GetInt64(1), new PlanId(r.GetString(2))));
        return methods.Count > MaxNeedMethods ? throw new CapacityException() : new NeedOwner(need, world, EquatableArray.From(methods));
    }

    /// <summary>
    /// Every attempt's plan of <paramref name="method"/> under <paramref name="owner"/> in its world, retired plans
    /// included, oldest first: a refusal budget's history over one re-admitted MethodId. A Method is admitted again
    /// only after its last attempt was released, so the list grows by one per retry the budget allowed; it is capped
    /// at <see cref="MaxOwnerHistory"/>, past which a budget is long spent.
    /// </summary>
    public IReadOnlyList<PlanId> OwnerMethodAttempts(NeedOwner owner, MethodId method, CancellationToken cancellationToken) =>
        Run(tx => tx.Query(
            "SELECT plan_id FROM need_methods WHERE colony=$colony AND load_token=$load AND map_id=$map AND need_id=$need AND method_id=$method ORDER BY attempt LIMIT $limit",
            [("$colony", owner.Snapshot.Colony), ("$load", owner.Snapshot.Load), ("$map", owner.Snapshot.Map), ("$need", owner.Need.Value), ("$method", method), ("$limit", MaxOwnerHistory)],
            r => new PlanId(r.GetString(0))), cancellationToken);

    /// <summary>
    /// The guard a Need-owned plan's prepare and dispatch runs: the plan's world is the current one and no Safeguard
    /// vetoes routine work. Whether the Need is still unmet is the family's to judge on its next run.
    /// </summary>
    internal static void GuardNeedWork(StoreTransaction tx, PlanId plan, GenerationSnapshot current)
    {
        var (row, found) = tx.QueryFirst(
            "SELECT colony,load_token,map_id,need_id FROM need_methods WHERE plan_id=$plan",
            [("$plan", plan)],
            r => (Colony: r.GetString(0), Load: r.GetString(1), Map: r.GetInt64(2), Need: r.GetString(3)));
        if (!found)
        {
            throw new NotFoundException();
        }
        if (row.Colony != current.Colony.Value || row.Load != current.Load.Value || row.Map != current.Map.Value)
        {
            throw new DomainException("need does not admit work outside its world");
        }
        AdmitRoundsSafeguards(tx, LoadNeedIn(tx, new NeedId(row.Need), current));
    }
}
