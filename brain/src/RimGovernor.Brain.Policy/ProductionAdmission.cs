namespace RimGovernor.Brain.Policy;

public sealed record ProductionRecipeInput(string Recipe, Fact<EquatableArray<EquatableArray<Amount>>> Slots, Fact<bool> UsesUnfinishedItem);
public sealed record ProductionMaterialPromise(string Id, EquatableArray<ForecastMaterialClaim> Claims);

/// <summary>Native/catalog inputs to the full review; no independently owned reservations.</summary>
public sealed record ProductionAdmissionObservation
{
    public Fact<EquatableArray<ProductionRecipeInput>> Recipes { get; init; }
    public Fact<EquatableArray<ProductionBatchState>> Bills { get; init; }
}

/// <summary>Frozen full-review evidence. Journal actions remain the sole owner of subsequent commitments.</summary>
public sealed record ProductionAdmissionEvidence
{
    public Fact<EquatableArray<Amount>> Stock { get; init; }
    public Fact<IReadOnlyDictionary<Resource, long>> Construction { get; init; }
    public Fact<EquatableArray<IngredientReservation>> Reservations { get; init; }
    public Fact<EquatableArray<ProductionRecipeInput>> Recipes { get; init; }
    public Fact<EquatableArray<ProductionMaterialPromise>> Promises { get; init; }
    public EquatableArray<ActionId> Reviewed { get; init; }

    public Fact<EquatableArray<ForecastCommitment>> ForecastPromises => Promises.TryGetValue(out var rows)
        ? Known(EquatableArray.From(rows.Select(p => new ForecastCommitment(p.Id, default) { MaterialClaims = p.Claims }))) : default;
}

public static partial class PolicyFunctions
{
    public static Fact<EquatableArray<ProductionBatch>> PriceProductionBatches(
        Fact<EquatableArray<ProductionBatch>> batches, Fact<EquatableArray<ProductionRecipeInput>> recipes)
    {
        if (!batches.TryGetValue(out var rows)) return default;
        var catalog = recipes.TryGetValue(out var inputs) ? inputs.ToDictionary(r => r.Recipe, StringComparer.Ordinal) : [];
        return Known(EquatableArray.From(rows.Select(b => catalog.TryGetValue(b.Order.Recipe, out var r)
            ? b with { Slots = r.Slots, UsesUnfinishedItem = r.UsesUnfinishedItem } : b)));
    }

    public static ProductionAdmissionEvidence ProductionAdmissionEvidenceOf(RoundsFacts facts,
        Fact<EquatableArray<ProductionBatch>> batches)
    {
        var priced = PriceProductionBatches(batches, facts.ProductionAdmission.Recipes);
        var promises = ProductionPromisesOf(priced, facts.ProductionAdmission.Bills);
        return new()
        {
            Stock = facts.Resources, Construction = facts.ConstructionDeficit, Reservations = facts.BillReservations,
            Recipes = facts.ProductionAdmission.Recipes,
            Promises = promises.TryGetValue(out var claims)
                ? Known(EquatableArray.From(claims.Select(c => new ProductionMaterialPromise(c.Id, c.MaterialClaims)))) : default,
            Reviewed = batches.TryGetValue(out var rows) ? EquatableArray.From(rows.Select(b => b.Progress.Action)) : [],
        };
    }

    /// <summary>Reconcile current journal claims against frozen evidence. Dispatch alone never releases stock.</summary>
    public static bool ProductionAdmissionFits(ProductionAdmissionEvidence evidence,
        Fact<EquatableArray<ProductionBatch>> committed, EquatableArray<ProductionBatch> proposed)
    {
        var free = ProductionAdmissionBudget(evidence, committed);
        var candidate = ProductionPromisesOf(PriceProductionBatches(Known(proposed), evidence.Recipes), default);
        if (!free.TryGetValue(out var budget) || !candidate.TryGetValue(out var candidateClaims)) return false;
        var required = new Dictionary<Resource, double>();
        foreach (var claim in candidateClaims.SelectMany(c => c.MaterialClaims))
        {
            if (!claim.Units.TryGetValue(out var units)) return false;
            required[claim.Resource] = required.GetValueOrDefault(claim.Resource) + units;
        }
        return required.All(p => p.Value == 0 || budget.GetValueOrDefault(p.Key, Known(0.0)).TryGetValue(out var available) && available >= p.Value);
    }

    public static Fact<IReadOnlyDictionary<Resource, Fact<double>>> ProductionAdmissionBudget(ProductionAdmissionEvidence evidence,
        Fact<EquatableArray<ProductionBatch>> committed)
    {
        if (!committed.TryGetValue(out var rows) || !evidence.ForecastPromises.TryGetValue(out var baseline)) return default;
        var reviewed = evidence.Reviewed.ToHashSet();
        var released = rows.Where(b => b.Progress.Attempt.Value == 0
            && b.Progress.Stage is Stage.Cancelled or Stage.Unsuccessful).Select(b => "production/" + b.Progress.Action.Value).ToHashSet(StringComparer.Ordinal);
        var held = baseline.Where(c => !released.Contains(c.Id)).ToList();
        // Actions admitted since this observation still owe their full input, even if Hands has since dispatched.
        // Only never-dispatched cancellation can release that claim before fresh native evidence arrives.
        var additions = rows.Where(b => !reviewed.Contains(b.Progress.Action) && !released.Contains("production/" + b.Progress.Action.Value))
            .Select(b => b with { Progress = new ProgressView { Action = b.Progress.Action, Stage = Stage.Pending } });
        var pending = ProductionPromisesOf(PriceProductionBatches(Known(EquatableArray.From(additions)), evidence.Recipes), default);
        if (!pending.TryGetValue(out var pendingClaims)) return default;
        held.AddRange(pendingClaims);
        return SharedMaterialBudgetOf(evidence.Stock, evidence.Construction,
            ProductionMaterialHoldsOf(Known(EquatableArray.From(held)), evidence.Reservations));
    }
}
