namespace RimGovernor.Brain.Policy;

/// <summary>A race the herd plan lacks and the sexes it wants: a target with no animals wants either, a founder its missing sex. (Go <c>HerdWant</c>.)</summary>
public sealed record HerdWant(Resource Race, bool Male, bool Female)
{
    /// <summary>The zero value Go's struct literal starts from.</summary>
    public HerdWant() : this(default, false, false)
    {
    }
}

public static partial class PolicyFunctions
{
    /// <summary>Go <c>HerdWants</c>: lists what the plan lacks, best first: each job's target race in job order, then each founder's missing sex by race. A race is listed once.</summary>
    public static IReadOnlyList<HerdWant> HerdWants(HerdPlan plan)
    {
        var @out = new List<HerdWant>();
        var listed = new HashSet<Resource>();
        void Add(Resource race)
        {
            if (!plan.Roles.TryGetValue(race, out var role) || listed.Contains(race) || (!role.WantMale && !role.WantFemale))
            {
                return;
            }
            listed.Add(race);
            @out.Add(new HerdWant(race, role.WantMale, role.WantFemale));
        }
        foreach (var job in HerdWorkJobs)
        {
            if (plan.Jobs.TryGetValue(job, out var jp) && jp.Target.Value.Length != 0)
            {
                Add(jp.Target);
            }
        }
        var founders = plan.Roles.Where(kv => kv.Value.Founder).Select(kv => kv.Key).Order().ToList();
        foreach (var race in founders)
        {
            Add(race);
        }
        return @out;
    }

    /// <summary>Go <c>HerdOffers</c>: the catalog races a trader's pawn rows offer; the plan counts them as obtainable.</summary>
    public static IReadOnlyList<Resource> HerdOffers(IReadOnlyList<TradeSheetRowFact> rows, AnimalRaceCatalog races)
    {
        var @out = new List<Resource>();
        foreach (var row in rows)
        {
            var race = new Resource(row.DefName);
            var ok = races.Race(race, out _);
            if (ok && row.PawnKnown && row.Pawn && row.TraderCount >= 1 && !@out.Contains(race))
            {
                @out.Add(race);
            }
        }
        @out.Sort();
        return @out;
    }

    /// <summary>
    /// Go <c>SelectAnimalPurchase</c>: picks the live animal a trader offers that the herd plan wants: a pawn row
    /// the trader holds and will trade, with none on the colony side, of the first want that has an affordable
    /// row of its race and a sex it wants. The budget is SelectPawnPurchase's: min(PawnPurchaseFraction of colony
    /// silver, colony silver - reserve - spent). Cheapest row, then line id, within a want. It returns false when
    /// nothing is wanted or affordable.
    /// </summary>
    public static (TradeSelectionLine Line, bool Ok) SelectAnimalPurchase(
        IReadOnlyList<HerdWant> wants,
        IReadOnlyList<TradeSheetRowFact> rows,
        long colonySilver,
        long reserve,
        IReadOnlyList<TradeSelectionLine> selected
    )
    {
        var price = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            price[row.LineID] = row.BuyPrice;
        }
        var spent = 0.0;
        foreach (var line in selected)
        {
            if (line.Count > 0)
            {
                spent += line.Count * price.GetValueOrDefault(line.LineID);
            }
        }
        var budget = Math.Min(PawnPurchaseFraction * colonySilver, (double)(colonySilver - reserve) - spent);
        foreach (var want in wants)
        {
            TradeSheetRowFact? best = null;
            foreach (var row in rows)
            {
                if (!row.PawnKnown || !row.Pawn || !row.TraderWillTradeKnown || !row.TraderWillTrade || row.TraderCount < 1 || row.ColonyCount != 0 || new Resource(row.DefName) != want.Race)
                {
                    continue;
                }
                if (!((want.Male && row.PawnGender == "Male") || (want.Female && row.PawnGender == "Female")))
                {
                    continue;
                }
                if (!row.BuyPriceKnown || !Finite(row.BuyPrice) || row.BuyPrice <= 0 || row.BuyPrice > budget)
                {
                    continue;
                }
                if (best is null || row.BuyPrice < best.BuyPrice || (row.BuyPrice == best.BuyPrice && string.CompareOrdinal(row.LineID, best.LineID) < 0))
                {
                    best = row;
                }
            }
            if (best is not null)
            {
                return (new TradeSelectionLine { LineID = best.LineID, DefName = best.DefName, Count = 1 }, true);
            }
        }
        return (new TradeSelectionLine(), false);
    }
}
