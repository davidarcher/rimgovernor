using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    // A disposable coast, not a production operation. Only the initial state
    // is staged; catches, food consumption and population recovery are native.
    public sealed class FishingFixture
    {
        private static Map tracked;
        private static double catches;
        private static bool patched;

        [Tool("test/fishing_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Stage a two-colonist Odyssey coast with no soil or competing food sources, Fishing research and a small initial food buffer.")]
        public async Task<object> Prepare(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || !Find.TickManager.Paused || !ModsConfig.OdysseyActive) throw new InvalidOperationException("Paused Odyssey map required; set RIMGOVERNOR_ACCEPT_EXPANSIONS=odyssey.");
                var people = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead && !p.Downed && !p.WorkTypeIsDisabled(WorkTypeDefOf.Fishing)).Take(2).ToList();
                if (people.Count != 2) throw new InvalidOperationException("Need two capable fishers.");
                foreach (var zone in map.zoneManager.AllZones.ToList()) zone.Delete();
                foreach (var thing in map.listerThings.AllThings.ToList())
                    if (!(thing is Pawn p && people.Contains(p))) thing.Destroy(DestroyMode.Vanish);
                foreach (var pawn in people)
                {
                    pawn.jobs.StopAll(); pawn.drafter.Drafted = false;
                    pawn.inventory?.innerContainer.ClearAndDestroyContents(); pawn.carryTracker?.innerContainer.ClearAndDestroyContents();
                    foreach (var work in DefDatabase<WorkTypeDef>.AllDefsListForReading)
                        if (!pawn.WorkTypeIsDisabled(work)) pawn.workSettings.SetPriority(work, 0);
                    pawn.workSettings.SetPriority(WorkTypeDefOf.Fishing, 1);
                    pawn.skills.GetSkill(SkillDefOf.Animals).Level = 10;
                    for (int hour = 0; hour < 24; hour++) pawn.timetable.SetAssignment(hour, TimeAssignmentDefOf.Anything);
                    pawn.Position = new IntVec3(10 + people.IndexOf(pawn), 0, map.Size.z / 2); pawn.pather.StopDead();
                }
                var sea = DefDatabase<TerrainDef>.GetNamed("WaterOceanShallow");
                var ground = DefDatabase<TerrainDef>.GetNamed("Concrete");
                foreach (var cell in map.AllCells)
                {
                    map.roofGrid.SetRoof(cell, null);
                    map.terrainGrid.SetTerrain(cell, cell.x >= 15 ? sea : ground);
                    map.fogGrid.Unfog(cell);
                }
                map.waterBodyTracker.ConstructBodies();
                var body = map.waterBodyTracker.Bodies.OrderByDescending(b => b.Size).First();
                if (!body.HasFish || body.MaxPopulation < 500) throw new InvalidOperationException("Fixture needs a productive tropical coast.");
                Find.ResearchManager.FinishProject(DefDatabase<ResearchProjectDef>.GetNamed("Fishing"), false);
                var food = ThingMaker.MakeThing(ThingDefOf.MealSurvivalPack); food.stackCount = 8;
                GenSpawn.Spawn(food, new IntVec3(12, 0, map.Size.z / 2), map); food.SetForbidden(false, false);
                tracked = map; catches = 0;
                if (!patched)
                {
                    new Harmony("rimgovernor.test.fishing-catches").Patch(AccessTools.Method(typeof(WaterBodyTracker), "Notify_Fished"),
                        postfix: new HarmonyMethod(typeof(FishingFixture), nameof(Caught)));
                    patched = true;
                }
                return Audit(map);
            }, cancellationToken);
        }

        private static void Caught(WaterBodyTracker __instance, float amount)
        {
            if (tracked != null && tracked.waterBodyTracker == __instance) catches += amount;
        }

        [Tool("test/fishing_observe", Description = "Read native fishing population, catches, food stock, zone geometry and colonist nutrition; no mutations.")]
        public async Task<object> Observe(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => Audit(Find.CurrentMap ?? throw new InvalidOperationException("Map required.")), cancellationToken);
        }

        private static object Audit(Map map)
        {
            var people = map.mapPawns.FreeColonistsSpawned.ToList();
            var body = map.waterBodyTracker.Bodies.OrderByDescending(b => b.Size).First();
            var fish = map.listerThings.AllThings.Where(t => t.def.category == ThingCategory.Item && t.def.IsNutritionGivingIngestible).ToList();
            return new { success = true, tick = Find.TickManager.TicksGame, population = body.Population, maxPopulation = body.MaxPopulation,
                catches, foodNutrition = fish.Sum(t => (double)t.stackCount * t.GetStatValue(StatDefOf.Nutrition)),
                soilCells = map.AllCells.Count(c => c.GetTerrain(map).fertility >= 0.4f),
                competingSources = map.listerThings.AllThings.Count(t => t is Plant || t is Corpse || t is Pawn p && p.RaceProps.Animal),
                colonists = people.Count, malnutrition = people.Select(p => p.health.hediffSet.GetFirstHediffOfDef(HediffDefOf.Malnutrition)?.Severity ?? 0).DefaultIfEmpty(1).Max(),
                zones = map.zoneManager.AllZones.OfType<Zone_Fishing>().Select(z => new { id = z.GetUniqueLoadID(), cells = z.Cells.Select(c => new { x = c.x, z = c.z }).ToArray(), sameBody = z.Cells.All(c => c.GetWaterBody(map) == body), floor = z.targetPopulationPct, mode = z.repeatMode.ToString() }).ToArray() };
        }
    }
}
