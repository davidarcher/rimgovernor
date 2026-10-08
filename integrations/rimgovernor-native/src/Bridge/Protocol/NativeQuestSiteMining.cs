#nullable enable
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Obs = RimGovernor.Protocol.Observations;

namespace HomeBridge.BridgeTools
{
    internal static class NativeQuestSiteMining
    {
        internal static IEnumerable<Obs.QuestSiteMiningTarget> Read(Site site)
        {
            if (!site.HasMap) yield break;
            var defs = site.parts.Select(p => p.parms.preciousLumpResources).Where(d => d != null).Distinct().ToArray();
            if (defs.Length == 0) yield break;
            foreach (var thing in site.Map.listerThings.AllThings.OfType<Mineable>()
                .Where(t => defs.Contains(t.def) && !t.Destroyed && !t.Position.Fogged(site.Map)).OrderBy(t => t.GetUniqueLoadID()))
            {
                yield return new Obs.QuestSiteMiningTarget { Id = thing.GetUniqueLoadID(), Def = thing.def.defName,
                    MapId = site.Map.uniqueID, Cell = new Common.Cell { X = thing.Position.x, Z = thing.Position.z },
                    Designated = site.Map.designationManager.DesignationOn(thing, DesignationDefOf.Mine) != null };
            }
        }
    }
}
