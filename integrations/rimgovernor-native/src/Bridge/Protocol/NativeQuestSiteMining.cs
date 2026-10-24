#nullable enable
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Cell = RimGovernor.Brain.Domain.Cell;
using Obs = RimGovernor.Contracts.Wire.Observations;

namespace HomeBridge.BridgeTools
{
    internal static class NativeQuestSiteMining
    {
        // The site's precious-lump mineables that stand unfogged on its loaded map.
        internal static IEnumerable<Obs.QuestSiteMiningTarget> Read(Site site, Map map)
        {
            var defs = site.parts.Select(p => p.parms.preciousLumpResources).Where(d => d != null).Distinct().ToArray();
            if (defs.Length == 0) yield break;
            foreach (var thing in map.listerThings.AllThings.OfType<Mineable>()
                .Where(t => defs.Contains(t.def) && !t.Destroyed && !t.Position.Fogged(map)).OrderBy(t => t.GetUniqueLoadID()))
            {
                yield return new Obs.QuestSiteMiningTarget { Id = thing.GetUniqueLoadID(), Def = thing.def.defName,
                    Cell = new Cell(thing.Position.x, thing.Position.z),
                    Designated = map.designationManager.DesignationOn(thing, DesignationDefOf.Mine) != null };
            }
        }
    }
}
