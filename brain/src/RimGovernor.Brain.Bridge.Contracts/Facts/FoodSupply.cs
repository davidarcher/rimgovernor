using RimGovernor.Protocol.Observations;

namespace RimGovernor.Brain.Bridge.Contracts;

// Go food_supply.go.
public static partial class BridgeFunctions
{
    private static double? OptionalDouble(bool present, double value) => present ? value : null;

    /// <summary>
    /// Go's <c>ValidateFoodSupply</c>: food completeness counts consumer and stock rows together. Repeated eater ids
    /// are explicit native eligibility, never a default of "every consumer". A stock references its things table
    /// row; <see cref="JoinFoodSupply"/> checks the facts that need the row.
    /// </summary>
    /// <exception cref="BridgeContractException">The supply is malformed.</exception>
    public static void ValidateFoodSupply(FoodSupplyFacts? supply)
    {
        if (supply is null)
        {
            throw Contract("food supply exceeds bound");
        }
        BuildingUnknown(supply);
        var consumers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in supply.Consumers)
        {
            if (row is null || !IsValidId(row.PawnId) || consumers.Contains(row.PawnId) || !CombatNumber(OptionalDouble(row.HasNutritionPerDay, row.NutritionPerDay), true))
            {
                throw Contract("invalid food consumer");
            }
            consumers.Add(row.PawnId);
        }
        var stocks = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in supply.Stocks)
        {
            if (row is null || !UniqueRef(row.Item, stocks) || !CombatNumber(OptionalDouble(row.HasNutrition, row.Nutrition), true))
            {
                throw Contract("invalid food stock");
            }
            if (row.Eaters.Count > consumers.Count)
            {
                throw Contract("missing food eligibility");
            }
            var eaters = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in RefIds(row.Eaters))
            {
                if (!consumers.Contains(id) || !eaters.Add(id))
                {
                    throw Contract("invalid food eligibility");
                }
            }
            if (!OptionalRef(row.Holder)
                || (row.Holder is not null && (!consumers.Contains(row.Holder.Id) || row.Eaters.Count != 1 || row.Eaters[0].Id != row.Holder.Id)))
            {
                throw Contract("private food cannot be shared");
            }
        }
        if (supply.Larder is { } larder)
        {
            if (!CombatNumber(larder.RawMeatNutrition, true) || !CombatNumber(larder.CookDemandNutrition, true))
            {
                throw Contract("invalid food larder");
            }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in larder.Corpses)
            {
                if (row is null
                    || !stocks.Contains(row.StockId)
                    || seen.Contains(row.StockId)
                    || !TryProtoCell(row.Cell, out var cell)
                    || cell.X < 0
                    || cell.Z < 0
                    || (row.HasHaulerId && !IsValidId(row.HaulerId))
                    || (row.FrozenDestination && row.HaulerId.Length == 0))
                {
                    throw Contract("invalid corpse handling");
                }
                seen.Add(row.StockId);
            }
        }
    }

    /// <summary>
    /// Go's <c>JoinFoodSupply</c>: resolves the supply's stocks against <paramref name="things"/>, a frame's things
    /// table, and checks the facts that need a stock's row: eligibility for any food but a corpse or human meat, and
    /// that a larder corpse is a corpse with meat. Returns null, with nothing checked, when the table misses a
    /// stock: the supply waits for a later frame.
    /// </summary>
    /// <exception cref="BridgeContractException">The supply or a joined row is malformed.</exception>
    public static IReadOnlyDictionary<string, Thing>? JoinFoodSupply(FoodSupplyFacts supply, Things things)
    {
        ValidateFoodSupply(supply);
        var rows = new Dictionary<string, Thing>(supply.Stocks.Count, StringComparer.Ordinal);
        foreach (var stock in supply.Stocks)
        {
            if (!things.TryRow(stock.Item, out var row))
            {
                return null;
            }
            rows[stock.Item.Id] = row;
        }
        foreach (var stock in supply.Stocks)
        {
            var row = rows[stock.Item.Id];
            if (!IsValidId(row.Thing_?.DefName ?? ""))
            {
                throw Contract("food stock row without a definition");
            }
            if (stock.Eaters.Count == 0 && !row.Corpse && !row.IsHumanMeat)
            {
                throw Contract("missing food eligibility");
            }
            if (row.Corpse && row.MeatAmount <= 0)
            {
                throw Contract("corpse stock without meat");
            }
        }
        if (supply.Larder is { } larder)
        {
            foreach (var corpse in larder.Corpses)
            {
                if (!rows.TryGetValue(corpse.StockId, out var row) || !row.Corpse)
                {
                    throw Contract("larder corpse is no corpse stock");
                }
            }
        }
        return rows;
    }
}
