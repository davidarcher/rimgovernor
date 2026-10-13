namespace RimGovernor.Brain.Policy;

/// <summary>What one pawn's live bill job has promised: the spawned ingredients it has queued or placed.</summary>
public sealed record IngredientReservation
{
    public PawnId Pawn { get; init; }
    public IReadOnlyList<Amount> Items { get; init; } = [];
}

/// <summary>Ports <c>material_budget.go</c>.</summary>
public static partial class PolicyFunctions
{
    /// <summary>
    /// What free stock already owes elsewhere: every standing blueprint's and frame's undelivered material, and
    /// every other pawn's live bill-job ingredients (worker's own job excluded; the empty id excludes none). An
    /// unknown census contributes nothing.
    /// </summary>
    public static List<Amount> MaterialHolds(Fact<IReadOnlyDictionary<Resource, long>> deficit, Fact<IReadOnlyList<IngredientReservation>> reservations, PawnId worker)
    {
        var total = new Dictionary<Resource, long>();
        if (deficit.TryGetValue(out var deficitRows))
        {
            foreach (var (resource, n) in deficitRows)
            {
                total[resource] = total.GetValueOrDefault(resource) + n;
            }
        }
        if (reservations.TryGetValue(out var held))
        {
            foreach (var r in held)
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
        }
        return [.. total.Where(kv => kv.Value > 0).Select(kv => new Amount(kv.Key, kv.Value)).OrderBy(a => a.Resource)];
    }

    /// <summary>
    /// Free stock per definition: the reachable, unforbidden item census less MaterialHolds. A held definition
    /// absent from the census goes negative: it is owed more than exists. Unknown while the stock census is.
    /// </summary>
    public static Fact<IReadOnlyDictionary<Resource, long>> MaterialBudget(
        Fact<IReadOnlyList<Amount>> stock,
        Fact<IReadOnlyDictionary<Resource, long>> deficit,
        Fact<IReadOnlyList<IngredientReservation>> reservations,
        PawnId worker
    )
    {
        if (!stock.TryGetValue(out var rows))
        {
            return Unknown<IReadOnlyDictionary<Resource, long>>();
        }
        var budget = new Dictionary<Resource, long>();
        foreach (var row in rows)
        {
            budget[row.Resource] = budget.GetValueOrDefault(row.Resource) + row.Count;
        }
        foreach (var hold in MaterialHolds(deficit, reservations, worker))
        {
            budget[hold.Resource] = budget.GetValueOrDefault(hold.Resource) - hold.Count;
        }
        return Known<IReadOnlyDictionary<Resource, long>>(budget);
    }
}
