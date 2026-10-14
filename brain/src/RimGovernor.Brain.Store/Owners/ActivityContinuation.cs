using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using static RimGovernor.Brain.Policy.PolicyFunctions;

namespace RimGovernor.Brain.Store;

/// <summary>An existing Method's current assignment, not a second ownership store.</summary>
public sealed record ActivityMethod(PlanId Plan, MethodId Method, GenerationSnapshot Snapshot,
    Fact<bool> Needed, PawnActivities Batch);

public sealed partial class Store
{
    private static bool ActivityReleasePlan(PlanSpec plan) => plan.Actions.Count > 0
        && plan.Actions.All(a => a.PawnActivities is { } batch && batch.Orders.All(o => o.Release));

    public IReadOnlyList<ActivityMethod> ActivityMethods(GenerationSnapshot current, CancellationToken ct) => Run(tx =>
    {
        var result = new List<ActivityMethod>();
        foreach (var id in tx.Query("SELECT plan_id FROM plan_methods WHERE kind='standard' ORDER BY plan_id", [], r => new PlanId(r.GetString(0))))
        {
            var plan = LoadIn(tx, id);
            if (plan.Retired || plan.Progress.Count != 1) continue;
            var installed = InstalledActivityPlan(tx, plan);
            if (installed is null) continue;
            var progress = installed.Progress[0];
            if (progress.Action.PawnActivities is not { } batch || batch.Orders.All(o => o.Release)
                || !InstalledActivity(progress.View)
                || !progress.View.Snapshot.SameWorld(current) || progress.View.Snapshot.Native != current.Native) continue;
            var ownerId = tx.ScalarText("SELECT owner_id FROM plan_methods WHERE plan_id=$plan", ("$plan", id));
            var owner = LoadStandard(tx, new StandardId(ownerId!));
            result.Add(new(id, plan.Method, progress.View.Snapshot,
                installed.Spec.Id != id ? Fact.Known(false) : owner.Standard.Finding == Finding.Unclear ? default : Fact.Known(owner.Deficit), batch));
        }
        return (IReadOnlyList<ActivityMethod>)result;
    }, ct);

    /// <summary>Replace one installed activity batch under the same Method. The prior plan is retained as
    /// superseded history. Productive continuation still requires an unmet Concern; release does not.</summary>
    public void ContinueActivityMethod(PlanId previous, GenerationSnapshot current, Tick observedTick,
        IReadOnlyList<ActivityOwnership> observed, PlanSpec next, CancellationToken ct) => Run(tx =>
    {
        next.Validate();
        var prior = LoadIn(tx, previous);
        var installed = InstalledActivityPlan(tx, prior);
        if (prior.Retired || !prior.SupersededBy.IsEmpty || prior.Progress.Count != 1 || next.Actions.Count != 1
            || installed?.Progress[0].Action.PawnActivities is not { } old || next.Actions[0].PawnActivities is not { } batch)
            throw new ConflictException("continuation requires the current single-batch activity Method");
        var progress = installed.Progress[0].View;
        if (!InstalledActivity(progress) || !progress.Snapshot.SameWorld(current)
            || progress.Snapshot.Native != current.Native || batch.AuthorityGeneration != current.Native.Value)
            throw new ConflictException("activity installation or authority is unresolved/stale");
        var ownerId = tx.ScalarText("SELECT owner_id FROM plan_methods WHERE plan_id=$plan AND kind='standard'", ("$plan", previous));
        if (ownerId is null) throw new ConflictException("activity continuation has no Standard Method");
        var owner = LoadStandard(tx, new StandardId(ownerId));
        if (!owner.Standard.Snapshot.SameWorld(current) || observedTick < progress.Tick
            || observed.Select(r => r.Pawn).Distinct(StringComparer.Ordinal).Count() != observed.Count
            || old.Orders.Any(o => !observed.Any(r => r.Pawn == o.Pawn)))
            throw new ConflictException("activity continuation has stale or incomplete ownership evidence");
        tx.Floors.Guard(current, observedTick);
        foreach (var original in old.Orders)
        {
            var row = observed.Single(r => r.Pawn == original.Pawn);
            if (MatchesActivityOwner(original, row) && !batch.Orders.Any(o => o.Pawn == original.Pawn))
                throw new ConflictException("activity continuation abandons an owned participant");
        }
        foreach (var order in batch.Orders)
        {
            var original = old.Orders.SingleOrDefault(o => o.Pawn == order.Pawn);
            if (original is not null && PawnActivities.SameOrder(original, order)
                && MatchesActivityOwner(original, observed.Single(r => r.Pawn == original.Pawn))) continue;
            if (original is null || original.Release || order.Assignment != original.Assignment
                || order.Method != prior.Method.Value || order.ExpectedRevision != original.Revision)
                throw new ConflictException("activity continuation changes ownership or skips a revision");
            var row = observed.Single(r => r.Pawn == original.Pawn);
            if (!MatchesActivityOwner(original, row) || (!order.Release && row.State is not ("completed" or "failed")
                && !(original.Preparing && row.State == "holding")))
                throw new ConflictException("activity continuation requires exact observed ownership and a terminal prior step");
        }
        batch.Validate();
        var release = batch.Orders.All(o => o.Release);
        if (!release)
        {
            if (installed.Spec.Id != prior.Spec.Id) throw new ConflictException("refused continuation permits cleanup only");
            if (!owner.Deficit) throw new ConflictException("recovered Concern permits release only");
            AdmitRoundsSafeguards(tx, owner);
            AdmitRoundsReview(tx, SummarizeOwner(owner));
            var retained = batch.Orders.Where(o => old.Orders.Any(p => PawnActivities.SameOrder(p, o)))
                .Select(o => o.Pawn).ToHashSet(StringComparer.Ordinal);
            if (!WorkResourcesFit(WorkResourcesIn(tx, current, previous, observed), next, retained))
                throw new ConflictException("work_resource_conflict_or_unknown");
        }
        CreatePlanIn(tx, next);
        InsertPlanOwner(tx, next.Id, "standard");
        if (tx.Execute("UPDATE standard_methods SET plan_id=$next WHERE plan_id=$previous", ("$next", next.Id), ("$previous", previous)) != 1)
            throw new ConflictException("activity Method changed");
        _ = tx.Execute("UPDATE plans SET method_id=$method WHERE id=$next", ("$method", prior.Method), ("$next", next.Id));
        _ = tx.Execute("UPDATE plans SET retired=1,superseded_by=$next WHERE id=$previous", ("$next", next.Id), ("$previous", previous));
        BumpRevision(tx, owner);
    }, ct);

    public void ResolveActivityMethod(PlanId previous, GenerationSnapshot current, Tick observedTick,
        IReadOnlyList<ActivityOwnership> observed, CancellationToken ct) => Run(tx =>
    {
        var prior = LoadIn(tx, previous);
        var installed = InstalledActivityPlan(tx, prior);
        if (prior.Retired || installed?.Progress[0].Action.PawnActivities is not { } batch
            || !installed.Progress[0].View.Snapshot.SameWorld(current)
            || installed.Progress[0].View.Snapshot.Native != current.Native || observedTick < installed.Progress[0].View.Tick
            || !ReviewActivityOwnership(batch, Fact.Known(false), observedTick.Value, observed).OwnershipEnded)
            throw new ConflictException("activity ownership is not known ended");
        tx.Floors.Guard(current, observedTick);
        _ = tx.Execute("UPDATE plans SET retired=1 WHERE id=$id", ("$id", previous));
    }, ct);

    private static bool InstalledActivity(ProgressView progress) => !progress.Unresolved
        && progress.Stage is Stage.Completed or Stage.Cancelled
        && progress.Receipt.TryGetValue(out var receipt) && receipt == Receipt.Accepted;

    // A refused replacement did not erase the previous native assignment. Retain its cleanup obligation
    // through predecessor history; uncertain receipts must not take this path.
    private static PlanState? InstalledActivityPlan(StoreTransaction tx, PlanState plan)
    {
        for (var depth = 0; depth < 8; depth++)
        {
            if (plan.Progress.Count != 1) return null;
            var progress = plan.Progress[0].View;
            if (InstalledActivity(progress)) return plan;
            var noEffect = progress.Receipt.TryGetValue(out var receipt)
                ? receipt == Receipt.Refused || (progress.Stage == Stage.Cancelled && receipt == Receipt.Unsent)
                : progress.Stage == Stage.Cancelled;
            if (progress.Unresolved || !noEffect) return null;
            var predecessor = tx.ScalarText("SELECT id FROM plans WHERE superseded_by=$id AND retired=1", ("$id", plan.Spec.Id));
            if (predecessor is null) return null;
            plan = LoadIn(tx, new PlanId(predecessor));
        }
        return null;
    }

    // Only a release with a verified predecessor can outlive Concern admission. A free-standing release
    // action cannot use this exception. Native still checks exact assignment revisions before changing ownership.
    internal static bool ActivityReleaseContinuation(StoreTransaction tx, PlanId id, GenerationSnapshot current)
    {
        var next = LoadIn(tx, id);
        if (next.Retired || next.Spec.Actions.Count != 1 || next.Spec.Actions[0].PawnActivities is not { } batch
            || !batch.Orders.All(o => o.Release) || batch.AuthorityGeneration != current.Native.Value) return false;
        var previous = tx.ScalarText("SELECT id FROM plans WHERE superseded_by=$next AND retired=1", ("$next", id));
        if (previous is null) return false;
        var prior = InstalledActivityPlan(tx, LoadIn(tx, new PlanId(previous)));
        return prior is not null && prior.Progress[0].View.Snapshot.SameWorld(current)
            && prior.Progress[0].View.Snapshot.Native == current.Native;
    }
}
