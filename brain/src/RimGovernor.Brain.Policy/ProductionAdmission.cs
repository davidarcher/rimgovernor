namespace RimGovernor.Brain.Policy;

/// <summary>
/// A recipe's definition facts by the catalog: its ingredient slots (RecipeIngredients), whether it makes an unfinished
/// item first, what one bill iteration makes, the work one unit takes (RecipeWorkAmount) and the crafter stat that work goes at
/// (<c>RecipeDef.workSpeedStat</c>, "" when it names none: speed 1, #3868).
/// </summary>
public sealed record ProductionRecipeInput(string Recipe, EquatableArray<EquatableArray<Amount>> Slots, bool UsesUnfinishedItem)
{
    public required EquatableArray<Amount> Products { get; init; }
    public required double WorkAmount { get; init; }
    public string WorkSpeedStat { get; init; } = "";
}
public sealed record ProductionMaterialPromise(string Id, EquatableArray<ForecastMaterialClaim> Claims);

/// <summary>Native/catalog inputs to the full review; no independently owned reservations.</summary>
public sealed record WorkAdmissionObservation
{
    public Fact<EquatableArray<WorkBuildingCost>> Buildings { get; init; }
    public Fact<EquatableArray<ProductionRecipeInput>> Recipes { get; init; }
    public Fact<EquatableArray<ProductionBatchState>> Bills { get; init; }
}

/// <summary>
/// The construction census as a review froze it: the buildings standing built or as blueprints and frames, and the
/// construction deficit admission budgets against.
/// </summary>
public sealed record StandingConstruction(EquatableArray<Building> Buildings, IReadOnlyDictionary<Resource, long> Owed);

/// <summary>Frozen full-review evidence. Journal actions remain the sole owner of subsequent commitments.</summary>
public sealed record WorkAdmissionEvidence
{
    public Tick Tick { get; init; }
    public Fact<EquatableArray<WorkBuildingCost>> Buildings { get; init; }
    /// <summary>The census when the review froze; null when no routine frame read it.</summary>
    public StandingConstruction? Standing { get; init; }
    public Fact<EquatableArray<WorkPawnAvailability>> Pawns { get; init; }
    public Fact<EquatableArray<RimGovernor.Contracts.Wire.Observations.WorkResourcePawn>> WorkResources { get; init; }
    public EquatableArray<Amount> Stock { get; init; }
    public EquatableArray<IngredientReservation> Reservations { get; init; }
    public Fact<EquatableArray<ProductionRecipeInput>> Recipes { get; init; }
    public Fact<EquatableArray<ProductionMaterialPromise>> Promises { get; init; }
    public EquatableArray<ActionId> Reviewed { get; init; }
    /// <summary>Observed need targets' claimant capacity and stack quantity, frozen with the review; absent targets read as capacity 1.</summary>
    public EquatableArray<WorkTargetCapacity> NeedTargets { get; init; }

    public Fact<EquatableArray<ForecastCommitment>> ForecastPromises => Promises.TryGetValue(out var rows)
        ? Known(EquatableArray.From(rows.Select(p => new ForecastCommitment(p.Id, default) { MaterialClaims = p.Claims }))) : default;
}

public static partial class ProductionRules
{
    public static Fact<EquatableArray<BatchPricing>> PriceProductionBatches(
        Fact<EquatableArray<ProductionBatch>> batches, Fact<EquatableArray<ProductionRecipeInput>> recipes)
    {
        if (!batches.TryGetValue(out var rows)) return default;
        var catalog = recipes.TryGetValue(out var inputs) ? inputs.ToDictionary(r => r.Recipe, StringComparer.Ordinal) : [];
        return Known(EquatableArray.From(rows.Select(b => catalog.TryGetValue(b.Order.Recipe, out var r) ? Priced(b, r) : new BatchPricing.Unpriced(b))));
    }

    /// <summary>The batch with the recipe's catalog slots and unfinished-item flag.</summary>
    public static BatchPricing Priced(ProductionBatch batch, ProductionRecipeInput recipe) =>
        new BatchPricing.Priced(batch, recipe.Slots, recipe.UsesUnfinishedItem);

    public static WorkAdmissionEvidence WorkAdmissionEvidenceOf(RoundsFacts facts,
        Fact<EquatableArray<ProductionBatch>> batches)
    {
        var priced = PriceProductionBatches(batches, facts.WorkAdmission.Recipes);
        var promises = WorkRules.CreditCarriedBillInputs(ProductionPromisesOf(priced, facts.WorkAdmission.Bills), facts.WorkResources);
        var construction = facts.CurrentConstruction;
        return new()
        {
            Tick = facts.Tick, Buildings = facts.WorkAdmission.Buildings, WorkResources = facts.WorkResources,
            Standing = new(EquatableArray.From(construction.Sites.Where(s => s.Building is not null).Select(s => s.Building!)
                    .Concat(construction.Buildings.Where(b => b.Building is not null).Select(b => b.Building!))),
                    // Standing blueprints are optional work, not reservations: admitted actions below own stock.
                    new Dictionary<Resource, long>()),
            Pawns = Known(EquatableArray.From(facts.ForecastObserved.WorkPawns.Pawns.Select(p => new WorkPawnAvailability(p.Id.Value, Known(p.Available), Known(p.Applies),
                    ForecastCurrentWork.ForecastCurrentWorkOf(p).Occupied, Known(p.Job.Target.Thing))
                    { Schedule = Known(p.Schedule), Preemptible = p.Job.Preemptible, LocalHour = facts.WorkResources.TryGetValue(out var native)
                        && native.SingleOrDefault(n => n.Pawn == p.Id.Value)?.LocalHour is { } hour ? Known(hour) : default }))),
            Stock = facts.Resources,
            Reservations = facts.BillReservations,
            Recipes = facts.WorkAdmission.Recipes,
            Promises = promises.TryGetValue(out var claims)
                ? Known(EquatableArray.From(claims.Select(c => new ProductionMaterialPromise(c.Id, c.MaterialClaims)))) : default,
            Reviewed = batches.TryGetValue(out var rows) ? EquatableArray.From(rows.Select(b => b.Progress.Action)) : [],
            NeedTargets = facts.ForecastObserved.Needs is { } needs
                ? EquatableArray.From(needs.Pawns.SelectMany(p => p.Targets).GroupBy(t => t.Id, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal)
                    .Select(g => new WorkTargetCapacity(g.Key, g.First().MaxPawns,
                        TargetQuantity.Of(g.First().Stock)))) : [],
        };
    }


    /// <summary>Reconcile current journal claims against frozen evidence. Dispatch alone never releases stock.</summary>
    public static bool ProductionAdmissionFits(WorkAdmissionEvidence evidence,
        Fact<EquatableArray<ProductionBatch>> committed, EquatableArray<ProductionBatch> proposed,
        EquatableArray<ForecastCommitment> work = default)
    {
        var free = ProductionAdmissionBudget(evidence, committed, work);
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

    public static Fact<IReadOnlyDictionary<Resource, Fact<double>>> ProductionAdmissionBudget(WorkAdmissionEvidence evidence,
        Fact<EquatableArray<ProductionBatch>> committed, EquatableArray<ForecastCommitment> work = default)
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
        held.AddRange(work);
        return SharedMaterialBudgetOf(evidence.Stock, evidence.Standing?.Owed,
            ProductionMaterialHoldsOf(Known(WorkRules.MergeWorkMaterialPromises(held)), evidence.Reservations));
    }
}
