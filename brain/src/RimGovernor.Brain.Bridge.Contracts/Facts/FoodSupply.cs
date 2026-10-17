using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Bridge.Contracts;

public static partial class BridgeFunctions
{
    private static double? OptionalDouble(bool present, double value) => present ? value : null;

    /// <summary>
    /// Resolves the supply's stocks against <paramref name="things"/>, a frame's things table. Returns null when the
    /// table misses a stock: the supply waits for a later frame.
    /// </summary>
    public static IReadOnlyDictionary<string, Thing>? JoinFoodSupply(FoodSupplyFacts supply, Things things)
    {
        var rows = new Dictionary<string, Thing>(supply.Stocks.Count, StringComparer.Ordinal);
        foreach (var stock in supply.Stocks)
        {
            if (!things.TryRow(stock.Item, out var row))
            {
                return null;
            }
            rows[stock.Item?.Id ?? ""] = row;
        }
        return rows;
    }
}
