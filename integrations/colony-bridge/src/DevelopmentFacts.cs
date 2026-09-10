using System.Linq;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    internal static class DevelopmentFacts
    {
        internal static object Read(Map map)
        {
            var buildings = map.listerBuildings.allBuildingsColonist.ToList();
            var power = buildings.Select(b => b.TryGetComp<CompPowerTrader>()).Where(c => c != null).ToList();
            var current = Find.ResearchManager.GetProject();
            return new {
                power = power.Select(c => new { id = c.parent.GetUniqueLoadID(), defName = c.parent.def.defName,
                    x = c.parent.Position.x, z = c.parent.Position.z, outputW = c.PowerOutput,
                    baseW = -c.Props.PowerConsumption, powered = c.PowerOn,
                    net = c.PowerNet == null ? (int?)null : c.PowerNet.GetHashCode() }).ToArray(),
                furniture = buildings.Select(b => new { id = b.GetUniqueLoadID(), defName = b.def.defName,
                    x = b.Position.x, z = b.Position.z,
                    indoors = b.GetRoom() != null && b.GetRoom().ProperRoom && !b.GetRoom().PsychologicallyOutdoors,
                    slots = b is Building_Bed bed ? bed.SleepingSlotsCount : 0 }).ToArray(),
                research = new {
                    current = current?.defName, progress = current?.ProgressReal,
                    projects = DefDatabase<ResearchProjectDef>.AllDefsListForReading.Where(r => !r.IsHidden)
                        .Select(r => new { defName = r.defName,
                            prerequisites = r.prerequisites?.Select(p => p.defName).ToArray() ?? new string[0] }).ToArray(),
                    available = DefDatabase<ResearchProjectDef>.AllDefsListForReading
                        .Where(r => !r.IsFinished && r.CanStartNow && !r.IsHidden)
                        .OrderBy(r => r.Cost).ThenBy(r => r.defName)
                        .Select(r => new { defName = r.defName, cost = r.Cost }).ToArray(),
                    finished = DefDatabase<ResearchProjectDef>.AllDefsListForReading.Where(r => r.IsFinished)
                        .Select(r => r.defName).OrderBy(n => n).ToArray()
                }
            };
        }
    }
}
