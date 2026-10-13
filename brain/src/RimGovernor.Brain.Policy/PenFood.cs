using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.Policy;

// The pen food formula. The game scores a pen (PenFoodCalculator) as the sum, over the pen's cells that stand
// free of edifices, of what each cell's terrain grows for pasture animals per day in each quadrum
// (MapPastureNutritionCalculator), against the animals' summed daily consumption
// (SimplifiedPastureNutritionSimulator.NutritionConsumedPerDay); the pen feeds its animals in a quadrum where
// the sum of growth is at least the consumption. A pen that does not exist yet has no calculator, so native
// reads the per-cell term once (PastureTerrain, the worst of the four quadrums) and this file solves the same
// balance for the cell count.

/// <summary>
/// What one cell of each terrain def grows for grazing per day in the map's worst quadrum. A terrain absent
/// from it, or at zero, grows nothing.
/// </summary>
public sealed record PastureTable(IReadOnlyDictionary<string, double> Values)
{
    /// <summary>The worst-quadrum nutrition per day one cell of terrain grows; Ok is false for a terrain that grows none.</summary>
    public (double Value, bool Ok) PerCell(string terrain)
    {
        var found = Values.TryGetValue(terrain, out var v);
        return (v, found && v > 0 && !double.IsInfinity(v));
    }

    /// <summary>The table's terrain defs that grow grazing, sorted.</summary>
    public IReadOnlyList<string> Terrains() =>
        [.. Values.Keys.Where(name => PerCell(name).Ok).OrderBy(n => n, StringComparer.Ordinal)];

    /// <summary>
    /// The least nutrition per day a cell of any grazing terrain grows: the per-cell term a pen is sized with
    /// before its site, hence its terrain, is known, so a pen on any of them holds the herd. Found is false
    /// when no terrain grows.
    /// </summary>
    public (double Least, bool Found) Weakest()
    {
        double least = 0;
        var found = false;
        foreach (var name in Terrains())
        {
            var (v, _) = PerCell(name);
            if (!found || v < least)
            {
                (least, found) = (v, true);
            }
        }
        return (least, found);
    }
}

public static partial class PolicyConstants
{
    /// <summary>
    /// The fewest cells a pen is sized to, whatever the herd eats. Judgment: a pen of a few cells cannot be
    /// fenced with a flap and still leave the animals room to turn.
    /// </summary>
    internal const int PenCellsFloor = 16;

    /// <summary>
    /// Bounds a pen's cells where the worst quadrum grows (almost) nothing: grass cannot carry the herd then,
    /// and the hay fields (HayNutritionNeed) cover the gap, so a pen is never sized past what the plan can
    /// wall.
    /// </summary>
    internal const int PenCellsCap = 900;
}

public static partial class PolicyFunctions
{
    /// <summary>
    /// The nutrition per day the animals of ceilings eat: the adult feed of each race (the figure
    /// PenFoodCalculator's example animals use) times its count. Ok is false when a race's feed is unread.
    /// </summary>
    public static (double Total, bool Ok) PenDemand(AnimalRaceCatalog races, IReadOnlyDictionary<Resource, long> ceilings)
    {
        double total = 0;
        // Go ranged the map in random order; a fixed order keeps the float sum reproducible.
        foreach (var (def, n) in ceilings.OrderBy(kv => kv.Key.Value, StringComparer.Ordinal))
        {
            if (n <= 0)
            {
                continue;
            }
            if (!races.Race(def, out var race))
            {
                return (0, false);
            }
            if (!race.AdultFeedPerDay.TryGetValue(out var feed) || !FoodNumber(feed))
            {
                return (0, false);
            }
            total += feed * n;
        }
        return (total, true);
    }

    /// <summary>
    /// The cells of a terrain that grows perCell nutrition per day that balance demand per day (the game's pen
    /// food sum solved for the cell count), bounded by PenCellsFloor and PenCellsCap. perCell at or below
    /// zero gives the cap, the pen the plan can wall.
    /// </summary>
    public static int PenCells(double demandPerDay, double perCell)
    {
        if (!FoodNumber(demandPerDay) || !FoodNumber(perCell) || perCell <= 0)
        {
            return PolicyConstants.PenCellsCap;
        }
        return Math.Min(PolicyConstants.PenCellsCap, Math.Max(PolicyConstants.PenCellsFloor, (int)Math.Ceiling(demandPerDay / perCell)));
    }
}
