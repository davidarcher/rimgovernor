using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Bridge.Contracts;

public static class FoodSupplyRules
{
    /// <summary>
    /// Resolves the supply's stocks against <paramref name="things"/>, a frame's things table. Returns null when the
    /// table misses a stock: that frame breaks the contract.
    /// </summary>
    public static IReadOnlyDictionary<string, Thing> JoinFoodSupply(FoodSupplyFacts supply, Things things)
    {
        var rows = new Dictionary<string, Thing>(supply.Stocks.Count, StringComparer.Ordinal);
        foreach (var stock in supply.Stocks)
        {
            if (!things.TryRow(stock.Item, out var row))
            {
                throw new BridgeContractException($"food supply stock {stock.Item?.Id} is missing from the frame's things table");
            }
            rows[stock.Item?.Id ?? ""] = row;
        }
        return rows;
    }
}
