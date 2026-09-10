using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace HomeBridge.BridgeTools
{
    internal static class ExtractionDevelopment
    {
        internal static bool Worker(Pawn p, WorkTypeDef work) => !p.Downed && !p.Drafted && !p.InMentalState
            && !p.WorkTypeIsDisabled(work) && p.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation);

        internal static List<object> Sites(Map map, string resource)
        {
            var result = new List<object>();
            bool scanning = map.deepResourceGrid.AnyActiveDeepScannersOnMap();
            var definitions = DefDatabase<ThingDef>.AllDefs.Where(d => !d.MadeFromStuff
                && (scanning ? d.CompDefFor<CompDeepDrill>() != null : d.CompDefFor<CompDeepScanner>() != null)
                && (d.researchPrerequisites == null || d.researchPrerequisites.All(r => r.IsFinished)))
                .OrderBy(d => d.defName).ToList();
            var work = scanning ? WorkTypeDefOf.Mining : WorkTypeDefOf.Research;
            var workers = map.mapPawns.FreeColonistsSpawned.Where(p => Worker(p, work)).ToList();
            var builders = map.mapPawns.FreeColonistsSpawned.Where(p => Worker(p, WorkTypeDefOf.Construction)).ToList();
            if (workers.Count == 0 || builders.Count == 0) return result;
            var cells = workers.SelectMany(p => GenRadial.RadialCellsAround(p.Position, 35, true)).Distinct()
                .Where(c => c.InBounds(map) && !c.Fogged(map))
                .OrderBy(c => workers.Min(p => p.Position.DistanceToSquared(c))).ThenBy(c => c.z).ThenBy(c => c.x);
            foreach (var cell in cells)
            {
                if (MiningGuard.State().Drills.Any(r => r.MapId == map.uniqueID && r.X == cell.x && r.Z == cell.z)) continue;
                if (scanning && (!DeepDrillUtility.GetNextResource(cell, map, out var output, out var count, out var deposit)
                    || output.defName != resource || count <= 0 || deposit.Fogged(map))) continue;
                foreach (var def in definitions)
                {
                    var power = def.GetCompProperties<CompProperties_Power>();
                    var net = PowerConnectionMaker.BestTransmitterForConnector(cell, map)?.PowerNet;
                    if (power == null || power.shortCircuitInRain || net == null
                        || net.powerComps.Sum(p => p.PowerOutput) < power.PowerConsumption) continue;
                    var rect = GenAdj.OccupiedRect(cell, Rot4.North, def.Size);
                    if (rect.Any(c => !c.InBounds(map) || c.Fogged(map) || c.Roofed(map)
                        || map.zoneManager.ZoneAt(c) != null || c.GetThingList(map).Any(t => t is Building || t is Blueprint || t is Frame))) continue;
                    var interaction = ThingUtility.InteractionCellWhenAt(def, cell, Rot4.North, map);
                    if (!interaction.InBounds(map) || !interaction.Standable(map)
                        || !workers.Any(p => !interaction.IsForbidden(p) && p.CanReach(interaction, PathEndMode.OnCell, Danger.None))
                        || !builders.Any(p => p.skills != null
                            && p.skills.GetSkill(SkillDefOf.Construction).Level >= def.constructionSkillPrerequisite
                            && p.skills.GetSkill(SkillDefOf.Artistic).Level >= def.artisticSkillPrerequisite
                            && p.CanReach(cell, PathEndMode.Touch, Danger.None))
                        || !GenConstruct.CanPlaceBlueprintAt(def, cell, Rot4.North, map, false).Accepted) continue;
                    result.Add(new { defName = def.defName, x = cell.x, z = cell.z, rotation = "north", eligible = true,
                        resource = scanning ? resource : null, powerW = power.PowerConsumption,
                        workTypes = new[] { HomeBillsTools.WorkTypeMetadata(work), HomeBillsTools.WorkTypeMetadata(WorkTypeDefOf.Construction) } });
                    if (result.Count >= 8) return result;
                }
            }
            return result;
        }
    }
}
