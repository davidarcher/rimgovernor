using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Obs = RimGovernor.Contracts.Wire.Observations;

namespace HomeBridge.BridgeTools
{
    internal static class NativeBlightCensus
    {
        // Reuse the colony read's visible spawned things; no planning-window or zone read is needed.
        internal static Obs.BlightSnapshot Read(Map map, IEnumerable<Thing> visibleThings)
        {
            var plants = new List<Obs.BlightedPlantState>();
            foreach (var plant in visibleThings.OfType<Plant>())
            {
                if (!plant.Blighted || !(map.zoneManager.ZoneAt(plant.Position) is Zone_Growing) && !map.areaManager.Home[plant.Position]) continue;
                plants.Add(new Obs.BlightedPlantState
                {
                    Thing = NativeRef.Thing(plant),
                    Designated = map.designationManager.AllDesignationsOn(plant).Any(),
                });
            }
            return new Obs.BlightSnapshot { Plants = [.. plants] };
        }
    }
}
