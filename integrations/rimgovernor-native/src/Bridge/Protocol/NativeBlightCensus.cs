using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace RimGovernor.Native
{
    internal static class NativeBlightCensus
    {
        // Reuse the colony read's visible spawned things; no planning-window or zone read is needed.
        internal static RimGovernor.Brain.GameFacts.Observations.BlightSnapshot Read(Map map, IEnumerable<Thing> visibleThings)
        {
            var plants = new List<RimGovernor.Brain.GameFacts.Observations.BlightedPlantState>();
            foreach (var plant in visibleThings.OfType<Plant>())
            {
                if (!plant.Blighted || !(map.zoneManager.ZoneAt(plant.Position) is Zone_Growing) && !map.areaManager.Home[plant.Position]) continue;
                plants.Add(new RimGovernor.Brain.GameFacts.Observations.BlightedPlantState
                {
                    Thing = NativeRef.Thing(plant),
                    Designated = map.designationManager.AllDesignationsOn(plant).Any(),
                });
            }
            return new RimGovernor.Brain.GameFacts.Observations.BlightSnapshot { Plants = [.. plants] };
        }
    }
}
