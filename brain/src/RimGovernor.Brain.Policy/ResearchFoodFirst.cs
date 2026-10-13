using System.Collections.Immutable;

namespace RimGovernor.Brain.Policy;

public static partial class PolicyFunctions
{
    /// <summary>
    /// The stored-food runway under which a map with no soil researches the food chain first: Hydroponics
    /// must run within about a month of the starting stock, and the research and its basins take days.
    /// </summary>
    public const double FoodFirstRunwayDays = 45.0;

    /// <summary>
    /// The food chain a soilless colony walks first: Electricity and the Hydroponics it unlocks. It stays a
    /// judgment table like ArmorResearchRungs; the defs give the prerequisite, not when to want it.
    /// </summary>
    public static readonly ImmutableArray<string> FoodFirstResearchRungs = ["Electricity", "Hydroponics"];

    /// <summary>
    /// Whether the observed planning window holds no soil: every cell's fertility is known and none is above
    /// zero. Any soil cell is Known(false); a window with no cell, or an unknown fertility and no soil seen,
    /// is unknown.
    /// </summary>
    public static Fact<bool> NoSoil(IReadOnlyList<SiteCell> cells)
    {
        var unknown = false;
        var seen = false;
        foreach (var c in cells)
        {
            if (!c.Fertility.TryGetValue(out var fertility))
            {
                unknown = true;
                continue;
            }
            seen = true;
            if (fertility > 0)
            {
                return Known(false);
            }
        }
        if (!seen || unknown)
        {
            return Unknown<bool>();
        }
        return Known(true);
    }

    /// <summary>
    /// Whether the food chain leads the research ladder: no soil on the map and a stored-food runway under
    /// FoodFirstRunwayDays. Unknown either way is not owed.
    /// </summary>
    public static bool FoodFirstOwed(RoundsFacts f)
    {
        var soilKnown = f.NoSoil.TryGetValue(out var noSoil);
        var daysKnown = f.FoodDays.TryGetValue(out var days);
        return soilKnown && noSoil && daysKnown && days < FoodFirstRunwayDays;
    }

    /// <summary>
    /// ladder with FoodFirstResearchRungs at its head while foodFirst holds; rungs the ladder already names
    /// move up rather than repeat. An empty ladder (roadmap disabled) stays empty.
    /// </summary>
    public static IReadOnlyList<string> FoodFirstLadder(IReadOnlyList<string> ladder, bool foodFirst)
    {
        if (!foodFirst || ladder.Count == 0)
        {
            return ladder;
        }
        var output = new List<string>(FoodFirstResearchRungs);
        foreach (var rung in ladder)
        {
            if (rung != "Electricity" && rung != "Hydroponics")
            {
                output.Add(rung);
            }
        }
        return output;
    }

    /// <summary>
    /// p with its ResearchLadder extended for the colony: the armor rungs while a soldier role exists
    /// (ArmorResearchPolicy), then the food chain ahead of everything while a soilless colony's food runs short.
    /// </summary>
    public static RoundsPolicy ResearchPolicy(RoundsPolicy p, bool soldier, bool foodFirst)
    {
        p = ArmorResearchPolicy(p, soldier);
        return p with { ResearchLadder = EquatableArray.From(FoodFirstLadder(p.ResearchLadder, foodFirst)) };
    }
}
