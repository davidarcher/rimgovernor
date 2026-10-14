using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Store;

public sealed partial class Store
{
    /// <summary>Bounded projection of committed batch actions; native readback decides which applied bills remain.</summary>
    public Fact<EquatableArray<ProductionBatch>> ProductionBatches(GenerationSnapshot current, Tick tick, CancellationToken cancellationToken)
    {
        current.Validate();
        DomainException.Require(tick.Value >= 0, "invalid production claim tick");
        return Run(tx =>
        {
            var links = OwnedPlanLinks(tx, """
                SELECT m.plan_id,m.owner_id FROM plan_methods m JOIN standards g ON g.id=m.owner_id AND m.kind='standard'
                WHERE json_extract(g.payload,'$.Snapshot.Colony')=$colony
                AND json_extract(g.payload,'$.Snapshot.Load')=$load AND json_extract(g.payload,'$.Snapshot.Map')=$map
                AND EXISTS(SELECT 1 FROM actions a WHERE a.plan_id=m.plan_id AND a.kind='production_bill'
                    AND json_extract(a.body,'$.Payload.Mode')='gear_batch')
                ORDER BY m.plan_id LIMIT 257
                """, current);
            if (links.Count > 256) return Fact.Unknown<EquatableArray<ProductionBatch>>();
            var result = new List<ProductionBatch>();
            foreach (var link in links)
            {
                foreach (var progress in LoadIn(tx, link.Plan).Progress)
                {
                    if (progress.Action.ProductionBill is not { Mode: BillMode.GearBatch } bill) continue;
                    if (progress.View.Tick > tick) return Fact.Unknown<EquatableArray<ProductionBatch>>();
                    result.Add(new(bill, progress.View));
                    if (result.Count > 256) return Fact.Unknown<EquatableArray<ProductionBatch>>();
                }
            }
            return Fact.Known(EquatableArray.From(result));
        }, cancellationToken);
    }

    public Task<Fact<EquatableArray<ProductionBatch>>> ProductionBatchesAsync(GenerationSnapshot current, Tick tick, CancellationToken cancellationToken) =>
        Task.Run(() => ProductionBatches(current, tick, cancellationToken), cancellationToken);
}
