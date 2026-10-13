namespace RimGovernor.Brain.Policy;

// Go: trade_pawn.go.

public static partial class PolicyFunctions
{
    /// <summary>The share of the colony's current silver one pawn purchase may spend.</summary>
    public const double PawnPurchaseFraction = 0.5;

    /// <summary>Scores an offered pawn from its skills and passions: each usable skill's level weighted by how fast its passion grows it. A disabled skill adds nothing.</summary>
    public static double PawnPurchaseValue(IReadOnlyList<ProfileSkill> skills)
    {
        var value = 0.0;
        foreach (var skill in skills)
        {
            if (!skill.Disabled)
            {
                value += skill.Level * (1 + skill.LearnFactor(new TraitEffects()));
            }
        }
        return value;
    }

    /// <summary>
    /// Picks the slave or prisoner a caravan offers that the colony buys: only while capacity (JoinerCapacity) is
    /// known true, only a violence-capable pawn with readable skills, at most min(PawnPurchaseFraction of colony
    /// silver, colony silver - reserve - spent) where spent is what the resource lines already selected will cost,
    /// and among those the best skill value per silver (line id on ties). It returns false when no pawn qualifies.
    /// </summary>
    public static (TradeSelectionLine Line, bool Ok) SelectPawnPurchase(bool capacity, IReadOnlyList<TradeSheetRowFact> rows, long colonySilver, long reserve, IReadOnlyList<TradeSelectionLine> selected)
    {
        if (!capacity)
        {
            return (new TradeSelectionLine("", "", 0), false);
        }
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
        var budget = Math.Min(PawnPurchaseFraction * colonySilver, (colonySilver - reserve) - spent);
        var best = new TradeSelectionLine("", "", 0);
        var bestScore = 0.0;
        var found = false;
        foreach (var row in rows)
        {
            if (!row.PawnKnown || !row.Pawn || !row.TraderWillTradeKnown || !row.TraderWillTrade || row.TraderCount < 1 || row.ColonyCount != 0)
            {
                continue;
            }
            if (!row.ViolenceCapableKnown || !row.ViolenceCapable || row.Skills.Count == 0)
            {
                continue;
            }
            if (!row.BuyPriceKnown || !Finite(row.BuyPrice) || row.BuyPrice <= 0 || row.BuyPrice > budget)
            {
                continue;
            }
            var score = PawnPurchaseValue(row.Skills) / row.BuyPrice;
            if (!found || score > bestScore || (score == bestScore && string.CompareOrdinal(row.LineID, best.LineID) < 0))
            {
                best = new TradeSelectionLine(row.LineID, row.DefName, 1);
                bestScore = score;
                found = true;
            }
        }
        return (best, found);
    }

    /// <summary>
    /// Adds the pawn-purchase need to a measured trade need: set only while capacity is known true, so an unknown
    /// capacity neither opens a caravan nor leaves the rest of the need unknown.
    /// </summary>
    public static Fact<TradeNeed> PopulationTradeNeed(Fact<TradeNeed> need, Fact<bool> capacity)
    {
        var known = need.TryGetValue(out var n);
        var roomKnown = capacity.TryGetValue(out var room);
        if (!known || !roomKnown || !room)
        {
            return need;
        }
        return Known(n! with { Population = true });
    }
}
