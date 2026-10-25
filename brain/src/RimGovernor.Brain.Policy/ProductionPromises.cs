namespace RimGovernor.Brain.Policy;

/// <summary>A journal-owned fixed batch, before the current definition catalog prices it (<see cref="BatchPricing"/>).</summary>
public sealed record ProductionBatch(ProductionBill Order, ProgressView Progress);

/// <summary>A batch joined with the current definition catalog: its slots come from the catalog, not a stock-floor estimate.</summary>
public abstract record BatchPricing(ProductionBatch Batch)
{
    /// <summary>
    /// The recipe's ingredient slots and whether it makes a reusable unfinished item; such a recipe needs native
    /// embedded-ingredient evidence before exact claims.
    /// </summary>
    public sealed record Priced(ProductionBatch Batch, EquatableArray<EquatableArray<Amount>> Slots, bool UsesUnfinishedItem) : BatchPricing(Batch);

    /// <summary>The catalog has no recipe of this batch's, so its material claims cannot be read.</summary>
    public sealed record Unpriced(ProductionBatch Batch) : BatchPricing(Batch);
}

/// <summary>
/// Current native bill lifecycle; disappearance is evidence only in a complete census. RepeatCount is the iterations
/// left, which bind only a Count bill.
/// </summary>
public sealed record ProductionBatchState(string Id, bool Unchanged, BillRepeat Repeat, int RepeatCount,
    bool Suspended, bool Finished, ProductionUnfinishedWork UnfinishedWork);

/// <summary>One initialized, bound iteration. Empty Item is observed absence; embedded inputs are never free stock.</summary>
public sealed record ProductionUnfinishedWork(string Item, EquatableArray<Amount> Ingredients);

public static partial class ProductionRules
{
    public static ProductionPromiseSet ProductionPromisesOf(ProductionBatchCensus census, EquatableArray<ProductionRecipeInput> recipes,
        EquatableArray<ProductionBatchState> bills)
    {
        switch (census)
        {
            case ProductionBatchCensus.Read(var batches): return ProductionPromisesOf(PriceProductionBatches(batches, recipes), bills);
            case ProductionBatchCensus.LimitExceeded: return new ProductionPromiseSet.Blocked(ProductionPromiseBlock.BatchLimit);
            case ProductionBatchCensus.AheadOfObservation: return new ProductionPromiseSet.Blocked(ProductionPromiseBlock.FutureAction);
        }
        throw new DomainException("undefined production batch census");
    }
    /// <summary>Project existing Method intent into the forecast's claims. No extra reservation owner or ledger.</summary>
    public static ProductionPromiseSet ProductionPromisesOf(
        EquatableArray<BatchPricing> batches, EquatableArray<ProductionBatchState> census)
    {
        var orders = batches;
        var output = new List<ForecastCommitment>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var unfinishedItems = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pricing in orders.OrderBy(b => b.Batch.Progress.Action.Value, StringComparer.Ordinal))
        {
            var order = pricing.Batch.Order;
            var progress = pricing.Batch.Progress;
            if (order.Mode != BillMode.GearBatch)
                continue;
            var unplaced =
                progress.Attempt.Value == 0
                || !progress.Unresolved && progress.Observes(Effect.Absent);
            if (
                progress.Stage == Stage.Unsuccessful
                || progress.Stage == Stage.Cancelled && unplaced
            )
                continue;
            int? remaining = order.Target;
            ProductionUnfinishedWork? unfinishedWork = unplaced
                ? new ProductionUnfinishedWork("", default)
                : null;
            var link = unplaced ? "" : progress.Bill ?? "";
            if (!unplaced)
            {
                remaining = default;
                if (progress.Bill is { } id)
                {
                    if (!seen.Add(id)) throw new DomainException("multiple production actions own one native bill");
                    var matches = census.Where(b => b.Id == id).ToArray();
                    if (matches.Length > 1) throw new DomainException("duplicate production bill observation");
                    if (matches.Length == 0) continue;
                    var native = matches[0];
                    var counted = native.Repeat == BillRepeat.Count;
                    // Cancellation in the journal does not remove an already placed native bill.
                    if (native.Finished || counted && native.RepeatCount == 0 || native.Suspended) continue;
                    // A player edit can change ingredient definitions as well as the count.
                    if (!native.Unchanged) return new ProductionPromiseSet.Blocked(ProductionPromiseBlock.EditedBill);
                    if (counted) remaining = native.RepeatCount;
                    unfinishedWork = native.UnfinishedWork;
                }
            }
            // A batch whose recipe the catalog does not price leaves every promise unread.
            if (pricing is not BatchPricing.Priced batch) return new ProductionPromiseSet.Blocked(ProductionPromiseBlock.UnpricedRecipe);
            if (batch.UsesUnfinishedItem)
            {
                if (unfinishedWork is not { } work) remaining = default;
                else if (work.Item.Length != 0)
                {
                    if (!unfinishedItems.Add(work.Item)) throw new DomainException("unfinished item linked to multiple production promises");
                    if (work.Ingredients.Any(a => !AdmissionRules.ValidResource(a.Resource) || a.Count <= 0)
                        || work.Ingredients.Select(a => a.Resource).Distinct().Count() != work.Ingredients.Count)
                        throw new DomainException("invalid embedded ingredients");
                    // Native creates an initialized unfinished item only after collecting the whole iteration.
                    // Credit the iteration, not its units: alternate stuff cannot discount later iterations.
                    remaining = remaining is { } count && count > 0 ? count - 1 : null;
                }
                else if (work.Ingredients.Count != 0) throw new DomainException("embedded ingredients without an unfinished item");
            }
            var amounts = new Dictionary<Resource, MaterialUnits>();
            foreach (var slot in batch.Slots)
            {
                var alternatives = slot.Where(a => order.Ingredients.Count == 0 || order.Ingredients.Contains(a.Resource.Value)).ToArray();
                if (alternatives.Length == 0) return new ProductionPromiseSet.Blocked(ProductionPromiseBlock.NoIngredientAlternative);
                foreach (var amount in alternatives)
                {
                    if (!AdmissionRules.ValidResource(amount.Resource) || amount.Count <= 0 || remaining is < 0)
                        throw new DomainException("invalid production ingredient amount");
                    var prior = amounts.GetValueOrDefault(amount.Resource, new MaterialUnits.Exact(0));
                    amounts[amount.Resource] = remaining == 0 ? new MaterialUnits.Exact(0)
                        : alternatives.Length == 1 && remaining is { } iterations && prior is MaterialUnits.Exact(var priorUnits)
                        ? new MaterialUnits.Exact(priorUnits + amount.Count * (double)iterations) : new MaterialUnits.Open();
                }
            }
            output.Add(new("production/" + progress.Action.Value, default)
            {
                MaterialClaims = EquatableArray.From(amounts.OrderBy(a => a.Key)
                    .Select(a => new ForecastMaterialClaim(a.Key, a.Value, link))),
            });
        }
        return new ProductionPromiseSet.Assessed(EquatableArray.From(output));
    }

    /// <summary>Native holds overlap batch promises only by their exact bill identity.</summary>
    public static MaterialAvailability ProductionMaterialHoldsOf(ProductionPromiseSet promises, EquatableArray<IngredientReservation> reservations)
    {
        if (promises is not ProductionPromiseSet.Assessed(var rows)) return new MaterialAvailability.Blocked(MaterialBudgetBlock.ProductionPromises);
        var budget = ForecastClaims.ForecastMaterialBudgetOf([], reservations, rows, 0);
        return new MaterialAvailability.Assessed(budget.ToDictionary(p => p.Key,
            p => p.Value is MaterialUnits.Exact(var n) ? (MaterialUnits)new MaterialUnits.Exact(-n) : new MaterialUnits.Open()));
    }

    /// <summary>Construction, native holds and promised work draw from one material budget.</summary>
    public static MaterialAvailability SharedMaterialBudgetOf(EquatableArray<Amount> stock,
        IReadOnlyDictionary<Resource, long>? construction, MaterialAvailability holds)
    {
        if (construction is not { } owed) return new MaterialAvailability.Blocked(MaterialBudgetBlock.AwaitingReview);
        if (holds is not MaterialAvailability.Assessed(var promised)) return holds;
        var output = new Dictionary<Resource, MaterialUnits>();
        foreach (var item in stock)
            output[item.Resource] = new MaterialUnits.Exact(((MaterialUnits.Exact)output.GetValueOrDefault(item.Resource, new MaterialUnits.Exact(0))).Count + item.Count);
        foreach (var (resource, units) in owed)
            output[resource] = new MaterialUnits.Exact(((MaterialUnits.Exact)output.GetValueOrDefault(resource, new MaterialUnits.Exact(0))).Count - units);
        foreach (var (resource, amount) in promised)
            output[resource] = amount is MaterialUnits.Exact(var units)
                ? new MaterialUnits.Exact(((MaterialUnits.Exact)output.GetValueOrDefault(resource, new MaterialUnits.Exact(0))).Count - units) : new MaterialUnits.Open();
        return new MaterialAvailability.Assessed(output);
    }

    public static bool MaterialBudgetCovers(IReadOnlyDictionary<Resource, MaterialUnits> budget, IEnumerable<Amount> costs, long copies)
    {
        if (copies < 1) throw new DomainException("invalid material copies");
        var total = new Dictionary<Resource, double>();
        foreach (var cost in costs)
        {
            if (!AdmissionRules.ValidResource(cost.Resource) || cost.Count < 0) throw new DomainException("invalid material cost");
            total[cost.Resource] = total.GetValueOrDefault(cost.Resource) + cost.Count * (double)copies;
        }
        return total.All(p => p.Value == 0 || budget.GetValueOrDefault(p.Key, new MaterialUnits.Exact(0)) is MaterialUnits.Exact(var free) && free >= p.Value);
    }
}