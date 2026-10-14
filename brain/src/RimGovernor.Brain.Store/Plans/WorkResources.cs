using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Store;

public sealed partial class Store
{
    private const int MaxWorkActions = 1024;

    private static WorkActionCensus WorkActionsIn(StoreTransaction tx, GenerationSnapshot current, PlanId exclude = default)
    {
        var links = OwnedPlanLinks(tx, """
            SELECT m.plan_id,m.owner_id FROM plan_methods m
            JOIN (SELECT id,payload,'standard' AS kind FROM standards UNION ALL SELECT id,payload,'incident' AS kind FROM incidents) g
            ON g.id=m.owner_id AND g.kind=m.kind
            WHERE json_extract(g.payload,'$.Snapshot.Colony')=$colony
              AND json_extract(g.payload,'$.Snapshot.Load')=$load AND json_extract(g.payload,'$.Snapshot.Map')=$map
              AND EXISTS(SELECT 1 FROM actions a WHERE a.plan_id=m.plan_id AND a.kind IN ('building','pawn_activities','production_bill'))
            ORDER BY m.plan_id LIMIT 1025
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
                if (plan.Retired && action.Action.PawnActivities is not null) continue;
                rows.Add(new(link.Plan, action.Action, action.View));
            }
            if (!plan.Retired && plan.Progress.Count == 1 && plan.Progress[0].Action.PawnActivities is not null
                && !InstalledActivity(plan.Progress[0].View))
            {
                var predecessor = tx.ScalarText("SELECT id FROM plans WHERE superseded_by=$id AND retired=1", ("$id", link.Plan));
                if (predecessor is not null && InstalledActivityPlan(tx, LoadIn(tx, new PlanId(predecessor))) is { } installed)
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
        if (!PolicyFunctions.WorkResourcesFit(view, plan)) throw new ConflictException("work_resource_conflict_or_unknown");
    }
}
