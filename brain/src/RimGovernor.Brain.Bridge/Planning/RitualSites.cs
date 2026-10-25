using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Facts;
using RimGovernor.Brain.GameAccess;
namespace RimGovernor.Brain.Bridge;

public static class RitualSitesReads
{
    /// <summary>
    /// The finished buildings of the frame's building table whose ThingDef a held ritual's pattern requires: the
    /// cells the game offers the begin command at. The required defs are the catalog's ritual defs, never a list
    /// here. None without the Ideology expansion; the building census is required.
    /// </summary>
    internal static EquatableArray<RimGovernor.Brain.GameFacts.Observations.BuildingState> RitualSitesOf(BuildingCensus census, IdeologyMode ideology)
    {
        if (ideology is not IdeologyMode.Installed { Primary: var ideo })
        {
            return [];
        }
        var wanted = new List<string>();
        foreach (var held in ideo.Facts.Rituals)
        {
            if (ideo.Defs.Rituals.TryGetValue(held.Pattern, out var pattern))
            {
                wanted.AddRange(pattern.RequiredBuildings);
            }
        }
        var output = new List<RimGovernor.Brain.GameFacts.Observations.BuildingState>();
        if (wanted.Count == 0)
        {
            return EquatableArray.From(output);
        }
        foreach (var row in census.Rows.Table.Values())
        {
            var def = row.Building.DefName;
            if (row.Status != RimGovernor.Brain.GameFacts.Observations.BuildingStatus.Built || !wanted.Contains(def))
            {
                continue;
            }
            output.Add(row);
        }
        return EquatableArray.From(output);
    }
}
