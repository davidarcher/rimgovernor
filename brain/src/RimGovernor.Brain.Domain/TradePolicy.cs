namespace RimGovernor.Brain.Domain;

/// <summary>
/// One priority-ordered economic target: the definition the player named, the stock level it should be kept
/// at, and the per-deal quantity and price limits either side of that level. It is the Go form of
/// colony_plan.py's TradeTarget, with its field bounds carried on <see cref="Validate"/>.
/// </summary>
public sealed record TradeTarget(string Item, long Stock, long MaxBuy, long MaxSell, double MinSellPrice)
{
    private const int MaximumName = 160;

    internal void Validate()
    {
        DomainException.Require(
            Item.Length != 0 && System.Text.Encoding.UTF8.GetByteCount(Item) <= MaximumName && !Item.StartsWith('#'),
            "economic target needs an observed definition, not a session row index"
        );
        DomainException.Require(Stock >= 0 && MaxBuy >= 0 && MaxSell >= 0, "economic target quantities out of range");
        DomainException.Require(double.IsFinite(MinSellPrice) && MinSellPrice >= 0, "economic target prices out of range");
    }
}

/// <summary>
/// colony_plan.py's TradePolicy: a priority-ordered list of case-insensitively unique targets plus the silver
/// the colony always keeps. Order is meaning, not presentation: it is the purchase priority SelectTrade spends
/// its budget in.
/// </summary>
public sealed record TradeEconomicPolicy(EquatableArray<TradeTarget> Targets, long SilverReserve)
{
    /// <summary>Mirrors TradePolicy's own pydantic bounds and its unique_targets validator exactly.</summary>
    public void Validate()
    {
        DomainException.Require(Targets.Count != 0, "economic policy needs at least one target");
        DomainException.Require(SilverReserve >= 0, "economic silver reserve out of range");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var target in Targets)
        {
            target.Validate();
            DomainException.Require(seen.Add(target.Item.ToLowerInvariant()), "use unique native definitions for economic targets");
        }
    }
}
