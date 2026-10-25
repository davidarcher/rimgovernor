using O = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>What one pawn's live bill job has promised: the spawned ingredients it has queued or placed.</summary>
public sealed record IngredientReservation
{
    /// <summary>Native bill identity for reconciling this hold with a forecast promise.</summary>
    public required string Bill { get; init; }
    public required O.IngredientReservation Observed { get; init; }
    public PawnId Pawn => new(Observed.PawnId);
    public IReadOnlyList<Amount> Items => Observed.Items.Select(a => new Amount(new Resource(a.DefName), a.Units)).ToArray();
}

public static partial class MaterialBudgetRules
{
    /// <summary>
    /// What free stock already owes elsewhere: every standing blueprint's and frame's undelivered material, and
    /// every other pawn's live bill-job ingredients (worker's own job excluded; the empty id excludes none). A
    /// missing census fails at the observation boundary before this calculation.
    /// </summary>
    public static List<Amount> MaterialHolds(IReadOnlyDictionary<Resource, long> deficit, EquatableArray<IngredientReservation> reservations, PawnId worker)
    {
        var total = new Dictionary<Resource, long>();
        foreach (var (resource, n) in deficit)
        {
            total[resource] = total.GetValueOrDefault(resource) + n;
        }
        foreach (var r in reservations)
        {
            if (!worker.IsEmpty && r.Pawn == worker)
            {
                continue;
            }
            foreach (var item in r.Items)
            {
                total[item.Resource] = total.GetValueOrDefault(item.Resource) + item.Count;
            }
        }
        return [.. total.Where(kv => kv.Value > 0).Select(kv => new Amount(kv.Key, kv.Value)).OrderBy(a => a.Resource)];
    }

    /// <summary>
    /// Free stock per definition: the reachable, unforbidden item census less MaterialHolds. A held definition
    /// absent from the census goes negative: it is owed more than exists.
    /// </summary>
    public static IReadOnlyDictionary<Resource, long> MaterialBudget(
        EquatableArray<Amount> stock,
        IReadOnlyDictionary<Resource, long> deficit,
        EquatableArray<IngredientReservation> reservations,
        PawnId worker
    )
    {
        var budget = new Dictionary<Resource, long>();
        foreach (var row in stock)
        {
            budget[row.Resource] = budget.GetValueOrDefault(row.Resource) + row.Count;
        }
        foreach (var hold in MaterialHolds(deficit, reservations, worker))
        {
            budget[hold.Resource] = budget.GetValueOrDefault(hold.Resource) - hold.Count;
        }
        return budget;
    }
}
