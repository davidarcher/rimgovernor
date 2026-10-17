using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Store;

public sealed partial class Store
{
    private const int MaxWorkActions = 1024;

    private static WorkActionCensus WorkActionsIn(StoreTransaction tx, GenerationSnapshot current, PlanId exclude = default)
    {
        var links = OwnedPlanLinks(tx, """
            SELECT n.plan_id,n.need_id FROM need_methods n
            WHERE n.colony=$colony AND n.load_token=$load AND n.map_id=$map
              AND EXISTS(SELECT 1 FROM actions a JOIN plans p ON p.id=a.plan_id WHERE a.plan_id=n.plan_id
                  AND (a.kind IN ('building','production_bill') OR a.kind='pawn_activities' AND p.retired=0))
            ORDER BY 1 LIMIT 1025
            """, current);
        if (links.Count > MaxWorkActions) return new([], true);
        var rows = new List<WorkAction>();
        foreach (var link in links)
        {
            if (link.Plan == exclude) continue;
            var plan = LoadIn(tx, link.Plan);
            foreach (var action in plan.Progress)
            {
                if (action.Action.Building is null && action.Action.PawnActivities is null && action.Action.ProductionBill is null) continue;
                // Native authority rollover ends all activity assignments, including pending old-generation intent.
                // Buildings and bills persist in the game and retain their independent material accounting.
                if (action.Action.PawnActivities is { } activity && (plan.Retired || activity.AuthorityGeneration != current.Native.Value)) continue;
                rows.Add(new(link.Plan, action.Action, action.View));
            }
            if (!plan.Retired && plan.Progress.Count == 1 && plan.Progress[0].Action.PawnActivities is not null
                && !InstalledActivity(plan.Progress[0].View))
            {
                var predecessor = tx.ScalarText("SELECT id FROM plans WHERE superseded_by=$id AND retired=1", ("$id", link.Plan));
                if (predecessor is not null && InstalledActivityPlan(tx, LoadIn(tx, new PlanId(predecessor))) is { } installed
                    && installed.Progress[0].Action.PawnActivities!.AuthorityGeneration == current.Native.Value)
                    rows.Add(new(installed.Spec.Id, installed.Progress[0].Action, installed.Progress[0].View));
            }
            if (rows.Count > MaxWorkActions) return new([], true);
        }
        return new(EquatableArray.From(rows));
    }

    private static WorkResourceView WorkResourcesIn(StoreTransaction tx, GenerationSnapshot current, PlanId exclude = default,
        IReadOnlyList<ActivityOwnership>? observed = null)
    {
        var review = LoadRounds(tx);
        if (!review.Enabled || review.Snapshot != current || review.WorkEvidence is not { } evidence)
            return new(new(), new([], [], [], ["work_resource_review_unavailable"]), default);
        if (!exclude.IsEmpty && LoadIn(tx, exclude).Spec.Actions.SingleOrDefault()?.PawnActivities is { } continuation)
            evidence = PolicyFunctions.WorkContinuationEvidence(evidence, continuation, observed);
        var actions = WorkActionsIn(tx, current, exclude);
        return PolicyFunctions.WorkResourcesOf(evidence, ProductionBatchesIn(tx, current, null), actions.Actions, actions.Truncated);
    }

    public WorkResourceView WorkResources(GenerationSnapshot current, CancellationToken ct, PlanId exclude = default,
        IReadOnlyList<ActivityOwnership>? observed = null) =>
        Run(tx => WorkResourcesIn(tx, current, exclude, observed), ct);

    public WorkActionCensus WorkActions(GenerationSnapshot current, CancellationToken ct) =>
        Run(tx => WorkActionsIn(tx, current), ct);

    private static void AdmitWorkResources(StoreTransaction tx, IMethodOwner owner, PlanSpec plan)
    {
        if (!plan.Actions.Any(a => a.Building is not null && a.ConstructionTarget.Length == 0
            || a.PawnActivities is { } batch && batch.Orders.Any(o => !o.Release)
            || a.ProductionBill is { Mode: BillMode.GearBatch })) return;
        var view = WorkResourcesIn(tx, owner.OwnerSnapshot);
        var proposed = plan.Actions.Where(a => a.ProductionBill is { Mode: BillMode.GearBatch })
            .Select(a => new ProductionBatch(a.ProductionBill!, new ProgressView { Action = a.Id, Stage = Stage.Pending })).ToArray();
        var committed = ProductionBatchesIn(tx, owner.OwnerSnapshot, null);
        if (proposed.Length != 0 && (!committed.TryGetValue(out var batches) || batches.Count + proposed.Length > MaxProductionClaims))
            throw new ProductionMaterialException();
        if (proposed.Length != 0 && !PolicyFunctions.ProductionAdmissionFits(view.Evidence,
            committed, EquatableArray.From(proposed), view.Claims.Materials))
            throw new ProductionMaterialException();
        if (PolicyFunctions.WorkResourcesRefusal(view, plan) is { } refusal) throw new NotAdmittedException("work_resource_conflict_or_unknown: " + refusal);
    }
}
