using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace HomeBridge.BridgeTools
{
    public sealed class DeepMiningFixture
    {
        private static IntVec3[] seededCells;
        [Tool("test/deep_mining_fixture", Description = "Disposable researched, supplied extraction scenario. Never installed for gameplay.")]
        public async Task<object> Setup(IRimBridgeContext ctx, CancellationToken cancellationToken, string action = "setup")
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (action == "seed") {
                    var sites = ExtractionDevelopment.Sites(map, "Plasteel");
                    if (sites.Count == 0) return new { success = false, error = "No admitted drill site for scenario deposits" };
                    var site = sites[0];
                    var cell = new IntVec3((int)site.GetType().GetProperty("x").GetValue(site), 0,
                        (int)site.GetType().GetProperty("z").GetValue(site));
                    DeepDrillUtility.GetNextResource(cell, map, out var seededResource, out var count, out var first);
                    foreach (var deposit in seededCells) map.deepResourceGrid.SetAt(deposit, seededResource,
                        deposit == first ? 1 : seededResource.deepCountPerPortion * 2);
                    return new { success = true, first = new { x = first.x, z = first.z } };
                }
                if (action != "setup") {
                    var enabled = action == "construct" ? WorkTypeDefOf.Construction : action == "haul" ? WorkTypeDefOf.Hauling
                        : action == "basic" ? ExtractionDevelopment.FlickWork : WorkTypeDefOf.Mining;
                    foreach (var worker in map.mapPawns.FreeColonistsSpawned.ToList())
                        foreach (var work in DefDatabase<WorkTypeDef>.AllDefs.ToList())
                            if (!worker.WorkTypeIsDisabled(work)) worker.workSettings.SetPriority(work, work == enabled ? 1 : 0);
                    return new { success = true };
                }
                var definitions = DefDatabase<ThingDef>.AllDefs.Where(d => d.CompDefFor<CompDeepDrill>() != null || d.CompDefFor<CompDeepScanner>() != null).ToList();
                var builder = map.mapPawns.FreeColonistsSpawned.Where(p => !p.WorkTypeIsDisabled(WorkTypeDefOf.Construction)
                    && p.skills.GetSkill(SkillDefOf.Construction).Level >= definitions.Where(d => d.CompDefFor<CompDeepDrill>() != null).Max(d => d.constructionSkillPrerequisite))
                    .OrderByDescending(p => p.skills.GetSkill(SkillDefOf.Construction).Level).FirstOrDefault();
                var pawn = map.mapPawns.FreeColonistsSpawned.Where(p => !p.WorkTypeIsDisabled(WorkTypeDefOf.Mining) && !p.WorkTypeIsDisabled(WorkTypeDefOf.Hauling))
                    .OrderByDescending(p => p.GetStatValue(StatDefOf.MiningSpeed)).FirstOrDefault();
                if (pawn == null || builder == null) return new { success = false, error = "Scenario needs an existing qualified builder and capable miner/hauler" };
                var centers = GenRadial.RadialCellsAround(pawn.Position, 45, true).Where(c => c.InBounds(map)
                    && GenRadial.RadialCellsAround(c, 9, true).All(q => q.InBounds(map)
                        && q.GetTerrain(map).affordances.Contains(TerrainAffordanceDefOf.Heavy)
                        && (q.GetEdifice(map) == null || q.GetEdifice(map) is Mineable)
                        && map.zoneManager.ZoneAt(q) == null)).Take(1).ToList();
                if (centers.Count == 0) return new { success = false, error = "Scenario needs nearby supported natural terrain" };
                var center = centers[0];
                foreach (var other in map.mapPawns.FreeColonistsSpawned.Where(p => p != pawn && p != builder).ToList()) {
                    other.DeSpawn(); Find.WorldPawns.PassToWorld(other, PawnDiscardDecideMode.KeepForever);
                }
                foreach (var c in GenRadial.RadialCellsAround(center, 9, true)) {
                    map.fogGrid.Unfog(c); map.roofGrid.SetRoof(c, null);
                    if (c.GetEdifice(map) is Mineable rock) rock.Destroy(DestroyMode.Vanish);
                }
                foreach (var research in definitions.SelectMany(d => d.researchPrerequisites ?? Enumerable.Empty<ResearchProjectDef>()).Distinct())
                    Find.ResearchManager.FinishProject(research, doCompletionDialog: false);
                var generator = GenSpawn.Spawn(ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("WoodFiredGenerator")), center, map);
                generator.SetFaction(Faction.OfPlayer); generator.TryGetComp<CompRefuelable>().Refuel(60);
                var scannerDef = definitions.First(d => d.CompDefFor<CompDeepScanner>() != null);
                var scanner = GenSpawn.Spawn(ThingMaker.MakeThing(scannerDef), center + new IntVec3(0,0,4), map);
                scanner.SetFaction(Faction.OfPlayer);
                foreach (var pair in new[] { Tuple.Create(ThingDefOf.Steel, 1500), Tuple.Create(ThingDefOf.ComponentIndustrial, 30) }) {
                    int remaining = pair.Item2;
                    while (remaining > 0) {
                        var stack = ThingMaker.MakeThing(pair.Item1); stack.stackCount = Math.Min(remaining, pair.Item1.stackLimit);
                        remaining -= stack.stackCount; GenPlace.TryPlaceThing(stack, center + new IntVec3(0,0,-5), map, ThingPlaceMode.Near);
                    }
                }
                var resource = DefDatabase<ThingDef>.GetNamed("Plasteel");
                var cells = new[] { center + new IntVec3(-5,0,0), center + new IntVec3(5,0,0) }
                    .OrderBy(c => c.DistanceToSquared(pawn.Position)).ThenBy(c => c.x).ToArray();
                map.deepResourceGrid.SetAt(cells[0], resource, 1);
                map.deepResourceGrid.SetAt(cells[1], resource, resource.deepCountPerPortion * 2);
                seededCells = cells;
                foreach (var food in map.listerThings.AllThings.Where(t => t.def.IsNutritionGivingIngestible && !t.def.IsDrug)) food.SetForbidden(false, false);
                foreach (var worker in map.mapPawns.FreeColonistsSpawned.ToList())
                    foreach (var work in DefDatabase<WorkTypeDef>.AllDefs.ToList())
                        if (!worker.WorkTypeIsDisabled(work)) worker.workSettings.SetPriority(work, work == WorkTypeDefOf.Construction ? 1 : 0);
                return new { success = true, pawn = pawn.ThingID, builder = builder.ThingID,
                    constructionSkill = builder.skills.GetSkill(SkillDefOf.Construction).Level, resource = resource.defName,
                    generator = generator.ThingID, scanner = scanner.ThingID,
                    cells = cells.Select(c => new { x = c.x, z = c.z }).ToList(),
                    definitions = definitions.Select(d => d.defName).ToList() };
            }, cancellationToken);
        }
    }
}
