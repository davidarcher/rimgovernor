namespace RimGovernor.Brain.Policy;


/// <summary>
/// One open production bill of the journal: the batch count, the bill's ingredient filter (the
/// loadout's stuff and the substitutes the planner treats as equivalent; empty allows every member) and the
/// recipe's catalog slots, one alternative list per ingredient slot with the count one unit of product takes.
/// </summary>
public sealed record OpenBill
{
    public int Count { get; init; }

    public EquatableArray<string> Filter { get; init; }

    public required EquatableArray<EquatableArray<Amount>> Slots { get; init; }

    /// <summary>
    /// The bill's ingredient demand: each slot's cheapest filtered member (lowest count, then name) at the
    /// count the whole batch takes. A slot the filter leaves no member of
    /// gives none for that slot.
    /// </summary>
    internal List<Amount> Picks()
    {
        var output = new List<Amount>();
        if (Count <= 0)
        {
            return output;
        }

        foreach (var slot in Slots.Items)
        {
            var pick = new Amount(default, 0);
            foreach (var a in slot.Items)
            {
                if (Filter.Count > 0 && !Filter.Items.Contains(a.Resource.Value))
                {
                    continue;
                }

                if (pick.Resource.IsEmpty || a.Count < pick.Count || (a.Count == pick.Count && a.Resource < pick.Resource))
                {
                    pick = a;
                }
            }

            if (!pick.Resource.IsEmpty && pick.Count > 0)
            {
                output.Add(new Amount(pick.Resource, pick.Count * Count));
            }
        }

        return output;
    }
}

public static class BillDemandRules
{
    /// <summary>
    /// The stock the economy/resource Need must reach for the open bills' ingredients, per resource, counted where the
    /// stock falls short of the bills' total. Each slot is demanded at its cheapest filtered member (lowest
    /// count, then name), whether or not native would run the bill now. A slot the filter leaves no member
    /// of contributes nothing; the demand is an absolute stock level like
    /// ConstructionDemand and merges into it by maximum.
    /// </summary>
    public static Dictionary<Resource, long> OpenBillDemand(IEnumerable<OpenBill> bills, StockReader stock)
    {
        var need = new Dictionary<Resource, long>();
        foreach (var bill in bills)
        {
            foreach (var pick in bill.Picks())
            {
                need[pick.Resource] = need.GetValueOrDefault(pick.Resource) + pick.Count;
            }
        }

        var output = new Dictionary<Resource, long>();
        foreach (var (resource, n) in need)
        {
            if (stock.Count(resource).TryGetValue(out var have) && n > have)
            {
                output[resource] = n;
            }
        }

        return output;
    }
}
