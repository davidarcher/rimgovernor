using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// A store haul's value in capability-days (#3624): <see cref="TravelSaved"/> by its stack's future uses, plus
/// <see cref="LossAvoided"/> (the market value it would lose where it lies and not in storage, over the horizon), less its
/// own <see cref="Labor"/> now. Hauling is never valued for its own sake: a stack with no use that neither deteriorates nor
/// rots is worth only the labor it costs.
/// </summary>
public sealed record StoreHaulValue(double TravelSaved, double LossAvoided, double Labor)
{
    public double Total => TravelSaved + LossAvoided - Labor;
}

// The store-haul term of the capability-days ledger (#3624). Every number is a native read or a game constant: the travel
// the stack's uses walk from either cell, the game's deterioration and rot at either cell, and the market value converted
// to work at StatWorker_MarketValue.ValuePerWork. The horizon is tier 2's (ColonyModelPolicy.HorizonDays).
public static partial class ForecastValue
{
    /// <summary>
    /// The value of carrying <paramref name="haul"/> as <paramref name="row"/> does. Travel saved: each use, the most saved
    /// first, takes what is left of the stack; its trips (units over the row's carry, rounded up) each walk the stack to its
    /// consumer and back from where it is stored instead of where it lies, so a stockpile farther from the work saves less,
    /// or costs. A use with unknown travel saves nothing. Loss avoided: the value the stack loses where it lies less what it
    /// loses stored, each the whole value when it rots inside the horizon and otherwise its deterioration over the horizon
    /// as a share of its hit points (all of it once that reaches them). Labor: the row's approach and delivery.
    /// </summary>
    public static StoreHaulValue StoreHaulValueOf(StoreHaul haul, ConstructionActivityOption row, double valuePerWork, double horizonDays)
    {
        DomainException.Require(valuePerWork > 0 && horizonDays > 0, "invalid store haul valuation");
        var left = haul.Count;
        var saved = 0.0;
        foreach (var use in haul.Uses.Where(u => u.FromOrigin.IsKnown && u.FromDestination.IsKnown)
            .OrderByDescending(u => u.FromOrigin.Value - u.FromDestination.Value))
        {
            var units = Math.Min(left, use.Use.Units);
            if (units <= 0) break;
            left -= units;
            var trips = Math.Ceiling(units / (double)Math.Max(1, row.Capacity));
            saved += 2 * trips * (use.FromOrigin.Value - use.FromDestination.Value);
        }
        var value = haul.UnitValue * haul.Count;
        double Lost(double perDay, Fact<double> rotTicks) =>
            rotTicks.TryGetValue(out var rot) && rot < horizonDays * GameTime.TicksPerDay ? value
            : perDay <= 0 || haul.HitPoints <= 0 ? 0 : value * Math.Min(1, perDay * horizonDays / haul.HitPoints);
        var avoided = Lost(haul.DeteriorationPerDay, haul.RotTicks) - Lost(haul.StoredDeteriorationPerDay, haul.StoredRotTicks);
        var labor = (row.ApproachTicks.IsKnown ? row.ApproachTicks.Value : 0) + (row.DeliveryTicks.IsKnown ? row.DeliveryTicks.Value : 0);
        return new(saved / GameTime.TicksPerDay, avoided / valuePerWork / GameTime.TicksPerDay, labor / GameTime.TicksPerDay);
    }
}
