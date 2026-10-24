namespace RimGovernor.Brain.Policy;

/// <summary>A journal-owned fixed batch. Slots come from the current definition catalog, not a stock-floor estimate.</summary>
public sealed record ProductionBatch(ProductionBill Order, ProgressView Progress)
{
    public Fact<EquatableArray<EquatableArray<Amount>>> Slots { get; init; }
    /// <summary>Recipes with reusable unfinished items need native embedded-ingredient evidence before exact claims.</summary>
    public Fact<bool> UsesUnfinishedItem { get; init; }
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
    /// <summary>Project existing Method intent into the forecast's claims. No extra reservation owner or ledger.</summary>
    public static Fact<EquatableArray<ForecastCommitment>> ProductionPromisesOf(
        Fact<EquatableArray<ProductionBatch>> batches, Fact<EquatableArray<ProductionBatchState>> census)
    {
        if (!batches.TryGetValue(out var orders)) return default;
        var output = new List<ForecastCommitment>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var unfinishedItems = new HashSet<string>(StringComparer.Ordinal);
        foreach (var batch in orders.OrderBy(b => b.Progress.Action.Value, StringComparer.Ordinal))
        {
            var order = batch.Order;
            var progress = batch.Progress;
            if (order.Mode != BillMode.GearBatch) continue;
            var unplaced = progress.Attempt.Value == 0
                || !progress.Unresolved && progress.Effect.TryGetValue(out var effect) && effect == Effect.Absent;
            if (progress.Stage == Stage.Unsuccessful || progress.Stage == Stage.Cancelled && unplaced) continue;
            var remaining = Known(order.Target);
            ProductionUnfinishedWork? unfinishedWork = unplaced ? new ProductionUnfinishedWork("", default) : null;
            var link = unplaced ? Known("") : progress.Bill;
            if (!unplaced)
            {
                remaining = default;
                if (progress.Bill.TryGetValue(out var id) && census.TryGetValue(out var observed))
                {
                    if (!seen.Add(id)) throw new DomainException("multiple production actions own one native bill");
                    var matches = observed.Where(b => b.Id == id).ToArray();
                    if (matches.Length > 1) throw new DomainException("duplicate production bill observation");
                    if (matches.Length == 0) continue;
                    var native = matches[0];
                    var counted = native.Repeat == BillRepeat.Count;
                    // Cancellation in the journal does not remove an already placed native bill.
                    if (native.Finished || counted && native.RepeatCount == 0 || native.Suspended) continue;
                    // A player edit can change ingredient definitions as well as the count.
                    if (!native.Unchanged) return default;
                    if (counted) remaining = Known(native.RepeatCount);
                    unfinishedWork = native.UnfinishedWork;
                }
            }
            if (!batch.UsesUnfinishedItem.TryGetValue(out var usesUnfinished)) remaining = default;
            else if (usesUnfinished)
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
                    remaining = remaining.TryGetValue(out var count) && count > 0 ? Known(count - 1) : default;
                }
                else if (work.Ingredients.Count != 0) throw new DomainException("embedded ingredients without an unfinished item");
            }
            if (!batch.Slots.TryGetValue(out var slots)) return default;
            var amounts = new Dictionary<Resource, Fact<double>>();
            foreach (var slot in slots)
            {
                var alternatives = slot.Where(a => order.Ingredients.Count == 0 || order.Ingredients.Contains(a.Resource.Value)).ToArray();
                if (alternatives.Length == 0) return default;
                foreach (var amount in alternatives)
                {
                    if (!AdmissionRules.ValidResource(amount.Resource) || amount.Count <= 0 || remaining.TryGetValue(out var n) && n < 0)
                        throw new DomainException("invalid production ingredient amount");
                    var prior = amounts.GetValueOrDefault(amount.Resource, Known(0.0));
                    amounts[amount.Resource] = remaining.TryGetValue(out var fresh) && fresh == 0 ? Known(0.0)
                        : alternatives.Length == 1 && remaining.IsKnown && prior.IsKnown
                        ? Known(prior.Value + amount.Count * (double)remaining.Value) : default;
                }
            }
            output.Add(new("production/" + progress.Action.Value, default)
            {
                MaterialClaims = EquatableArray.From(amounts.OrderBy(a => a.Key)
                    .Select(a => new ForecastMaterialClaim(a.Key, a.Value, link))),
            });
        }
        return Known(EquatableArray.From(output));
    }

    /// <summary>Shared outstanding material demand: native holds and batch promises overlap by exact bill identity.</summary>
    public static Fact<IReadOnlyDictionary<Resource, Fact<double>>> ProductionMaterialHoldsOf(
        Fact<EquatableArray<ForecastCommitment>> promises, Fact<EquatableArray<IngredientReservation>> reservations)
    {
        if (!promises.TryGetValue(out var rows)) return default;
        var budget = ForecastClaims.ForecastMaterialBudgetOf(Known(default(EquatableArray<Amount>)), reservations, rows, 0);
        return budget.TryGetValue(out var free)
            ? Known<IReadOnlyDictionary<Resource, Fact<double>>>(free.ToDictionary(p => p.Key,
                p => p.Value.TryGetValue(out var n) ? Known(-n) : default)) : default;
    }

    /// <summary>Use the same reconciled holds as acquisition and trade, plus outstanding construction.</summary>
    public static Fact<IReadOnlyDictionary<Resource, Fact<double>>> SharedMaterialBudgetOf(
        Fact<EquatableArray<Amount>> stock, Fact<IReadOnlyDictionary<Resource, long>> construction,
        Fact<IReadOnlyDictionary<Resource, Fact<double>>> holds)
    {
        if (!stock.TryGetValue(out var items) || !construction.TryGetValue(out var owed) || !holds.TryGetValue(out var promised)) return default;
        var output = new Dictionary<Resource, Fact<double>>();
        foreach (var item in items)
            output[item.Resource] = Known(output.GetValueOrDefault(item.Resource, Known(0.0)).Value + item.Count);
        foreach (var (resource, units) in owed)
            output[resource] = Known(output.GetValueOrDefault(resource, Known(0.0)).Value - units);
        foreach (var (resource, amount) in promised)
            output[resource] = amount.TryGetValue(out var units)
                ? Known(output.GetValueOrDefault(resource, Known(0.0)).Value - units) : default;
        return Known<IReadOnlyDictionary<Resource, Fact<double>>>(output);
    }

    /// <summary>Unknown required material cannot fund a new construction proposal.</summary>
    public static bool MaterialBudgetCovers(IReadOnlyDictionary<Resource, Fact<double>> budget, IEnumerable<Amount> costs, long copies)
    {
        if (copies < 1) throw new DomainException("invalid material copies");
        var total = new Dictionary<Resource, double>();
        foreach (var cost in costs)
        {
            if (!AdmissionRules.ValidResource(cost.Resource) || cost.Count < 0) throw new DomainException("invalid material cost");
            total[cost.Resource] = total.GetValueOrDefault(cost.Resource) + cost.Count * (double)copies;
        }
        return total.All(p => p.Value == 0 || budget.GetValueOrDefault(p.Key, Known(0.0)).TryGetValue(out var free) && free >= p.Value);
    }
}
