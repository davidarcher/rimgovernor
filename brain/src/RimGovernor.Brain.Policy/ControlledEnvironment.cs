namespace RimGovernor.Brain.Policy;

public static class ControlledEnvironmentRules
{
    /// <summary>The headroom row for a native network id.</summary>
    public static PowerHeadroom? Network(this ControlledEnvironment environment, string id)
    {
        foreach (var n in environment.Networks)
        {
            if (n.Id == id)
            {
                return n;
            }
        }
        return null;
    }

    /// <summary>
    /// Maps every cell a currently lit lamp grows to that lamp's id; a cell under two lamps keeps the
    /// lexically smaller id so output is stable.
    /// </summary>
    public static Dictionary<Cell, string> LitCells(this ControlledEnvironment environment)
    {
        var result = new Dictionary<Cell, string>();
        foreach (var l in environment.Lights)
        {
            if (!l.LitNow)
            {
                continue;
            }
            foreach (var c in l.GrowthCells)
            {
                if (!result.TryGetValue(c, out var prev) || string.CompareOrdinal(l.Id, prev) < 0)
                {
                    result[c] = l.Id;
                }
            }
        }
        return result;
    }
    /// <summary>The network's surplus by day: generation less consumption.</summary>
    public static double DayHeadroomW(this PowerHeadroom network) => network.GenerationW - network.ConsumptionW;

    /// <summary>The network's surplus once solar output stops.</summary>
    public static double NightHeadroomW(this PowerHeadroom network) => network.GenerationW - network.SolarW - network.ConsumptionW;

    /// <summary>
    /// The surplus with neither sun nor wind: the margin a lamp or basin must fit in to keep growing through
    /// a still night.
    /// </summary>
    public static double CalmNightHeadroomW(this PowerHeadroom network) => network.NightHeadroomW() - network.WindW;

    /// <summary>Reports a crop whose native minimum glow is zero, such as cave fungus, so an unlit roofed room is eligible for it.</summary>
    public static bool GrowsInDark(CropChoice crop) =>
        FoodForecastRules.FoodNumber(crop.MinGlow) && crop.MinGlow <= 0;
}
