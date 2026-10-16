using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Store;

/// <summary>One Method bound to a Need: its MethodId, the attempt it was admitted as and the plan that executes it.</summary>
public sealed record NeedMethodBinding(MethodId Method, long Attempt, PlanId Plan);

/// <summary>
/// The journal owner of Need-owned Methods (#3250): a Need in one world (colony, load, map), keyed by its
/// <see cref="NeedId"/>. A Need is derived, never stored, so the owner is only that key and its
/// <c>need_methods</c> rows. It has no episodes or revision: a MethodId whose plan is still open continues, and one
/// whose plan was released (retired, or its work closed) is admitted again as the next attempt.
/// </summary>
/// <param name="Snapshot">The world the Need was derived in; only its colony, load and map key the rows.</param>
/// <param name="Methods">The bound Methods whose plans are not retired, in admission order.</param>
public sealed record NeedOwner(NeedId Need, GenerationSnapshot Snapshot, EquatableArray<NeedMethodBinding> Methods) : IMethodOwner
{
    GenerationSnapshot IMethodOwner.OwnerSnapshot => Snapshot;

    IReadOnlyList<PlanId> IMethodOwner.OwnerPlans => [.. Methods.Select(m => m.Plan)];

    string IMethodOwner.OwnerLabel => "need " + Need.Value;

    /// <summary>
    /// A Need is routine work: the Safeguards and the shelter construction latch apply to it as to a Concern other
    /// than MaintainHousing, so it reports no Concern of its own.
    /// </summary>
    bool IMethodOwner.OwnerNeed(Rounds rounds, out ConcernId need)
    {
        need = default;
        return true;
    }

    /// <summary>The plan of <paramref name="method"/>'s latest attempt while it is still open work; null once released or never bound.</summary>
    PlanId? IMethodOwner.BoundPlan(StoreTransaction tx, MethodId method)
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
        return !held.Retired && Standard.WorkOpen(held.Progress) ? plan : null;
    }

    void IMethodOwner.BindMethod(StoreTransaction tx, MethodId method, PlanId plan, string reason)
    {
        Store.InsertPlanOwner(tx, plan, "need");
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

    long IMethodOwner.ServedCount(StoreTransaction tx) =>
        tx.Scalar(
            "SELECT count(*) FROM need_methods WHERE colony=$colony AND load_token=$load AND map_id=$map AND need_id=$need",
            ("$colony", Snapshot.Colony), ("$load", Snapshot.Load), ("$map", Snapshot.Map), ("$need", Need.Value));
}

public sealed partial class Store
{
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
