using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Store;

public sealed partial class Store
{
    private const int MaxProductionClaims = 256;

    /// <summary>Read-only selection input. Commit rechecks the authoritative journal under its own transaction.</summary>
    public ProductionMaterialView ProductionMaterials(GenerationSnapshot current, CancellationToken cancellationToken) => Run(tx =>
    {
        var review = LoadReview(tx);
        if (!review.Enabled || review.Snapshot != current || review.WorkEvidence is not { } evidence) return new([], new MaterialAvailability.Blocked(MaterialBudgetBlock.AwaitingReview));
        return new ProductionMaterialView(evidence.Recipes, WorkResourcesIn(tx, current).Materials);
    }, cancellationToken);
    /// <summary>Bounded projection of committed batch actions; native readback decides which applied bills remain.</summary>
    public ProductionBatchCensus ProductionBatches(GenerationSnapshot current, Tick tick, CancellationToken cancellationToken)
    {
        current.Validate();
        DomainException.Require(tick.Value >= 0, "invalid production claim tick");
        return Run(tx => ProductionBatchesIn(tx, current, tick), cancellationToken);
    }

    private static ProductionBatchCensus ProductionBatchesIn(StoreTransaction tx, GenerationSnapshot current, Tick? tick)
    {
        var links = OwnedPlanLinks(tx, """
            SELECT n.plan_id,n.need_id FROM need_methods n
            WHERE n.colony=$colony AND n.load_token=$load AND n.map_id=$map
              AND EXISTS(SELECT 1 FROM actions a WHERE a.plan_id=n.plan_id AND a.kind='production_bill'
                AND json_extract(a.body,'$.Payload.Mode')='gear_batch')
            ORDER BY 1 LIMIT 257
            """, current);
        if (links.Count > MaxProductionClaims) return new ProductionBatchCensus.LimitExceeded();
        var result = new List<ProductionBatch>();
        foreach (var link in links)
        {
            foreach (var progress in LoadIn(tx, link.Plan).Progress)
            {
                if (progress.Action.ProductionBill is not { Mode: BillMode.GearBatch } bill) continue;
                if (tick is { } observed && progress.View.Tick > observed) return new ProductionBatchCensus.AheadOfObservation();
                result.Add(new(bill, progress.View));
                if (result.Count > MaxProductionClaims) return new ProductionBatchCensus.LimitExceeded();
            }
        }
        return new ProductionBatchCensus.Read(EquatableArray.From(result));
    }

    public Task<ProductionBatchCensus> ProductionBatchesAsync(GenerationSnapshot current, Tick tick, CancellationToken cancellationToken) =>
        Task.Run(() => ProductionBatches(current, tick, cancellationToken), cancellationToken);
}
