using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    // A disposable coast with a rice field and a cow, not a production operation.
    // Only the initial state is staged; fishing, harvesting and milking are native.
    // The fixture counts the same work through other game methods than the delivery
    // ledger hooks, so the ledger is compared with an independent tally.
    public sealed class LedgerFixture
    {
        private static Map tracked;
        private static double catches, riceYield, gathers;
        private static bool patched;
        private static ThingDef Rice => DefDatabase<ThingDef>.GetNamed("Plant_Rice");

        [Tool("test/ledger_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Stage two colonists on an Odyssey coast with Fishing research, a mature rice field and a full-milk cow, and start counting catches, rice harvests and milk gathers.")]
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
                int mid = map.Size.z / 2;
                foreach (var pawn in people)
                {
                    pawn.jobs.StopAll(); pawn.drafter.Drafted = false;
                    pawn.inventory?.innerContainer.ClearAndDestroyContents(); pawn.carryTracker?.innerContainer.ClearAndDestroyContents();
                    foreach (var work in DefDatabase<WorkTypeDef>.AllDefsListForReading)
                        if (!pawn.WorkTypeIsDisabled(work)) pawn.workSettings.SetPriority(work, 0);
                    pawn.workSettings.SetPriority(WorkTypeDefOf.Fishing, 1);
                    foreach (var work in new[] { WorkTypeDefOf.Growing, WorkTypeDefOf.Handling })
                        if (!pawn.WorkTypeIsDisabled(work)) pawn.workSettings.SetPriority(work, 2);
                    for (int hour = 0; hour < 24; hour++) pawn.timetable.SetAssignment(hour, TimeAssignmentDefOf.Anything);
                    pawn.Position = new IntVec3(10 + people.IndexOf(pawn), 0, mid); pawn.pather.StopDead();
                }
                var sea = DefDatabase<TerrainDef>.GetNamed("WaterOceanShallow");
                var ground = DefDatabase<TerrainDef>.GetNamed("Concrete");
                var soil = DefDatabase<TerrainDef>.GetNamed("Soil");
                var field = new CellRect(2, mid + 3, 6, 6);
                foreach (var cell in map.AllCells)
                {
                    map.roofGrid.SetRoof(cell, null);
                    map.terrainGrid.SetTerrain(cell, cell.x >= 15 ? sea : field.Contains(cell) ? soil : ground);
                    map.fogGrid.Unfog(cell);
                }
                map.waterBodyTracker.ConstructBodies();
                var body = map.waterBodyTracker.Bodies.OrderByDescending(b => b.Size).First();
                if (!body.HasFish || body.MaxPopulation < 500) throw new InvalidOperationException("Fixture needs a productive tropical coast.");
                Find.ResearchManager.FinishProject(DefDatabase<ResearchProjectDef>.GetNamed("Fishing"), false);
                var food = ThingMaker.MakeThing(ThingDefOf.MealSurvivalPack); food.stackCount = 8;
                GenSpawn.Spawn(food, new IntVec3(12, 0, mid), map); food.SetForbidden(false, false);

                var growing = new Zone_Growing(map.zoneManager);
                map.zoneManager.RegisterZone(growing);
                foreach (var cell in field.Cells) growing.AddCell(cell);
                growing.SetPlantDefToGrow(Rice);
                foreach (var cell in field.Cells)
                {
                    var plant = (Plant)ThingMaker.MakeThing(Rice);
                    plant.Growth = 1f;
                    GenSpawn.Spawn(plant, cell, map);
                }

                var request = new PawnGenerationRequest(DefDatabase<PawnKindDef>.GetNamed("Cow"), Faction.OfPlayer, fixedGender: Gender.Female, fixedBiologicalAge: 4f);
                var cow = PawnGenerator.GeneratePawn(request);
                GenSpawn.Spawn(cow, new IntVec3(6, 0, mid - 4), map);
                AccessTools.Field(typeof(CompHasGatherableBodyResource), "fullness").SetValue(cow.GetComp<CompMilkable>(), 1f);

                tracked = map; catches = 0; riceYield = 0; gathers = 0;
                if (!patched)
                {
                    var harmony = new Harmony("rimgovernor.test.ledger-tally");
                    harmony.Patch(AccessTools.Method(typeof(WaterBodyTracker), "Notify_Fished"), postfix: new HarmonyMethod(typeof(LedgerFixture), nameof(Caught)));
                    harmony.Patch(AccessTools.Method(typeof(Plant), "PlantCollected"), prefix: new HarmonyMethod(typeof(LedgerFixture), nameof(Collecting)));
                    harmony.Patch(AccessTools.Method(typeof(CompHasGatherableBodyResource), "Gathered"), prefix: new HarmonyMethod(typeof(LedgerFixture), nameof(Gathering)));
                    patched = true;
                }
                return Audit();
            }, cancellationToken);
        }

        private static void Caught(WaterBodyTracker __instance, float amount)
        {
            if (tracked != null && tracked.waterBodyTracker == __instance) catches += amount;
        }

        // The rice a harvest is about to yield, read from the plant before it is collected.
        private static void Collecting(Plant __instance)
        {
            if (tracked != null && __instance.Map == tracked && __instance.def == Rice) riceYield += __instance.YieldNow();
        }

        private static void Gathering(CompHasGatherableBodyResource __instance)
        {
            if (tracked != null && __instance.parent.Map == tracked) gathers++;
        }

        [Tool("test/ledger_observe", Description = "Read the fixture's independent catch, rice yield and milk gather tallies; no mutations.")]
        public async Task<object> Observe(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => Audit(), cancellationToken);
        }

        private static object Audit() => new { success = true, tick = Find.TickManager.TicksGame, catches, riceYield, gathers };
    }
}
