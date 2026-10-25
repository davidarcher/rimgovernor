using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Store;

/// <summary>An existing Method's current assignment, not a second ownership store.</summary>
public sealed record ActivityMethod(PlanId Plan, MethodId Method, GenerationSnapshot Snapshot,
    ActivityNeed Needed, PawnActivities Batch);

/// <summary>Expected shared-resource contention preserves the installed Method for a later observed review.</summary>
public enum ActivityContinuationAdmission { Admitted, ResourcesUnavailable }

public sealed partial class Store
{
    private static bool ActivityReleasePlan(PlanSpec plan) => plan.Actions.Count > 0
        && plan.Actions.All(a => a.PawnActivities is { } batch && batch.Orders.All(o => o.Release));

    /// <summary>
    /// The installed activity Methods in <paramref name="current"/>'s world, Need-owned (#3304, decision 13). A Method is
    /// needed while its Need's progress row in the current review says it is unmet or working a Method (decision 18),
    /// and unlisted without a row.
    /// </summary>
    public IReadOnlyList<ActivityMethod> ActivityMethods(GenerationSnapshot current, CancellationToken ct) => Run(tx =>
    {
        var result = new List<ActivityMethod>();
        foreach (var (id, ownerId) in tx.Query("SELECT plan_id,need_id FROM need_methods ORDER BY plan_id", [],
            r => (new PlanId(r.GetString(0)), r.GetString(1))))
        {
            var plan = LoadIn(tx, id);
            if (plan.Retired || plan.Progress.Count != 1) continue;
            var installed = InstalledActivityPlan(tx, plan);
            if (installed is null) continue;
            var progress = installed.Progress[0];
            if (progress.Action.PawnActivities is not { } batch || batch.Orders.All(o => o.Release)
                || !InstalledActivity(progress.View)
                || !progress.View.Snapshot.SameWorld(current) || progress.View.Snapshot.Native != current.Native) continue;
            var needed = installed.Spec.Id != id ? ActivityNeed.Ended
                : NeedOwedIn(tx, new NeedId(ownerId), current) ? ActivityNeed.Owed : ActivityNeed.Unlisted;
            result.Add(new(id, plan.Method, progress.View.Snapshot, needed, batch));
        }
        return (IReadOnlyList<ActivityMethod>)result;
    }, ct);

    /// <summary>
    /// Whether <paramref name="need"/> has a progress row in the current enabled review of <paramref name="current"/>'s
    /// world: the family's last run found it unmet or unknown, or working a Method (decision 18). A met Need without
    /// work has no row.
    /// </summary>
    private static bool NeedOwedIn(StoreTransaction tx, NeedId need, GenerationSnapshot current)
    {
        var review = LoadRounds(tx);
        return review.Enabled && review.Snapshot.SameWorld(current) && review.Progress.Any(p => p.Concern == need.FilingKey);
    }

    /// <summary>Replace one installed activity batch under the same Need Method. The prior plan is retained as
    /// superseded history. Productive continuation still requires a Need with a progress row; release does not.</summary>
    public ActivityContinuationAdmission ContinueActivityMethod(PlanId previous, GenerationSnapshot current, Tick observedTick,
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
        var (link, linked) = tx.QueryFirst("SELECT need_id,colony,load_token,map_id FROM need_methods WHERE plan_id=$plan", [("$plan", previous)],
            r => (Need: r.GetString(0), Colony: r.GetString(1), Load: r.GetString(2), Map: r.GetInt64(3)));
        if (!linked) throw new ConflictException("activity continuation has no Need Method");
        var sameWorld = link.Colony == current.Colony.Value && link.Load == current.Load.Value && link.Map == current.Map.Value;
        var needOwner = LoadNeedIn(tx, new NeedId(link.Need), current);
        if (!sameWorld || observedTick < progress.Tick
            || observed.Select(r => r.Pawn).Distinct(StringComparer.Ordinal).Count() != observed.Count
            || old.Orders.Any(o => !observed.Any(r => r.Pawn == o.Pawn)))
            throw new ConflictException("activity continuation has stale or incomplete ownership evidence");
        tx.Floors.Guard(current, observedTick);
        foreach (var original in old.Orders)
        {
            var row = observed.Single(r => r.Pawn == original.Pawn);
            if (ActivityContinuationRules.MatchesActivityOwner(original, row) && !batch.Orders.Any(o => o.Pawn == original.Pawn))
                throw new ConflictException("activity continuation abandons an owned participant");
        }
        foreach (var order in batch.Orders)
        {
            var original = old.Orders.SingleOrDefault(o => o.Pawn == order.Pawn);
            if (original is not null && PawnActivities.SameOrder(original, order)
                && ActivityContinuationRules.MatchesActivityOwner(original, observed.Single(r => r.Pawn == original.Pawn))) continue;
            if (original is null || original.Release || order.Assignment != original.Assignment
                || order.Method != prior.Method.Value || order.ExpectedRevision != original.Revision)
                throw new ConflictException("activity continuation changes ownership or skips a revision");
            var row = observed.Single(r => r.Pawn == original.Pawn);
            if (!ActivityContinuationRules.MatchesActivityOwner(original, row) || (!order.Release && row.State is not ("completed" or "failed")
                && !(original.Preparing && row.State == "holding")))
                throw new ConflictException("activity continuation requires exact observed ownership and a terminal prior step");
        }
        batch.Validate();
        var release = batch.Orders.All(o => o.Release);
        if (!release && !ActivityCleanupDelta(old, batch))
        {
            if (installed.Spec.Id != prior.Spec.Id) throw new ConflictException("refused continuation permits cleanup only");
            if (!NeedOwedIn(tx, needOwner.Need, current)) throw new ConflictException("met Need permits release only");
            AdmitRoundsSafeguards(tx, needOwner);
            var retained = batch.Orders.Where(o => old.Orders.Any(p => PawnActivities.SameOrder(p, o)))
                .Select(o => o.Pawn).ToHashSet(StringComparer.Ordinal);
            if (!WorkRules.WorkResourcesFit(WorkResourcesIn(tx, current, previous, observed), next, retained))
                return ActivityContinuationAdmission.ResourcesUnavailable;
        }
        CreatePlanIn(tx, next);
        InsertPlanOwner(tx, next.Id);
        if (tx.Execute("UPDATE need_methods SET plan_id=$next WHERE plan_id=$previous", ("$next", next.Id), ("$previous", previous)) != 1)
            throw new ConflictException("activity Method changed");
        _ = tx.Execute("UPDATE plans SET method_id=$method WHERE id=$next", ("$method", prior.Method), ("$next", next.Id));
        _ = tx.Execute("UPDATE plans SET retired=1,superseded_by=$next WHERE id=$previous", ("$next", next.Id), ("$previous", previous));
        return ActivityContinuationAdmission.Admitted;
    }, ct);

    // Cleanup acquires no labor, targets or inputs. Exact ownership/revision checks
    // still apply; retained obligations must not prevent an unrelated participant release.
    private static bool ActivityCleanupDelta(PawnActivities previous, PawnActivities next)
    {
        var released = next.Orders.Where(o => o.Release).Select(o => o.Pawn).ToHashSet(StringComparer.Ordinal);
        if (released.Count == 0 || next.Orders.Any(o => !o.Release
            && !previous.Orders.Any(p => PawnActivities.SameOrder(p, o)))) return false;
        var removed = previous.Tasks.Where(t => released.Contains(t.Pawn)).Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        var expected = previous.Tasks.Where(t => !removed.Contains(t.Id)).Select(t =>
            t with { Prerequisites = EquatableArray.From(t.Prerequisites.Where(p => !removed.Contains(p))) });
        return next.Tasks.SequenceEqual(expected);
    }
    public void ResolveActivityMethod(PlanId previous, GenerationSnapshot current, Tick observedTick,
        IReadOnlyList<ActivityOwnership> observed, CancellationToken ct) => Run(tx =>
    {
        var prior = LoadIn(tx, previous);
        var installed = InstalledActivityPlan(tx, prior);
        if (prior.Retired || installed?.Progress[0].Action.PawnActivities is not { } batch
            || !installed.Progress[0].View.Snapshot.SameWorld(current)
            || installed.Progress[0].View.Snapshot.Native != current.Native || observedTick < installed.Progress[0].View.Tick
            || !ActivityContinuationRules.ReviewActivityOwnership(batch, ActivityNeed.Ended, observedTick.Value, observed).OwnershipEnded)
            throw new ConflictException("activity ownership is not known ended");
        tx.Floors.Guard(current, observedTick);
        _ = tx.Execute("UPDATE plans SET retired=1 WHERE id=$id", ("$id", previous));
    }, ct);

    private static bool InstalledActivity(ProgressView progress) => !progress.Unresolved
        && progress.Stage is Stage.Completed or Stage.Cancelled
        && progress.Received(Receipt.Accepted);

    // A refused replacement did not erase the previous native assignment. Retain its cleanup obligation
    // through predecessor history; uncertain receipts must not take this path.
    private static PlanState? InstalledActivityPlan(StoreTransaction tx, PlanState plan)
    {
        for (var depth = 0; depth < 8; depth++)
        {
            if (plan.Progress.Count != 1) return null;
            var progress = plan.Progress[0].View;
            if (InstalledActivity(progress))
                return plan;
            var noEffect = progress.Receipt is ReceiptState.Received received
                ? received.Kind == Receipt.Refused
                    || (progress.Stage == Stage.Cancelled && received.Kind == Receipt.Unsent)
                : progress.Stage == Stage.Cancelled;
            if (progress.Unresolved || !noEffect) return null;
            var predecessor = tx.ScalarText("SELECT id FROM plans WHERE superseded_by=$id AND retired=1", ("$id", plan.Spec.Id));
            if (predecessor is null) return null;
            plan = LoadIn(tx, new PlanId(predecessor));
        }
        return null;
    }

    // Only a release with a verified predecessor can outlive Need admission. A free-standing release
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
