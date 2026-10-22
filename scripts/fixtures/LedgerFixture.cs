using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using RimGovernor.Fixtures.Contracts;
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
        private static double catches, riceYield;
        private static int gathers;
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
                // Terrain edits initialize a one-cell body with no fish. ConstructBodies only
                // rebuilds existing bodies; initialize the completed coast at its native capacity.
                body.Initialize();
                if (!body.HasFish || body.MaxPopulation < 500) throw new InvalidOperationException($"Fixture needs a productive tropical coast: fish={body.HasFish}, population={body.Population}, max={body.MaxPopulation}, cells={body.Size}.");
                // Native fishing requires a zone. Finish one catch, then let the same crew
                // take its lower-priority harvesting and milking jobs without harness intervention.
                var fishing = new Zone_Fishing(map.zoneManager) { repeatMode = FishRepeatMode.RepeatCount, repeatCount = 1 };
                map.zoneManager.RegisterZone(fishing);
                for (int z = mid - 2; z <= mid + 2; z++) fishing.AddCell(new IntVec3(15, 0, z));
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
                    harmony.Patch(AccessTools.Method(typeof(GenPlace), nameof(GenPlace.TryPlaceThing), new[] {
                        typeof(Thing), typeof(IntVec3), typeof(Map), typeof(ThingPlaceMode), typeof(Thing).MakeByRefType(),
                        typeof(Action<Thing, int>), typeof(Predicate<IntVec3>), typeof(Rot4?), typeof(int) }),
                        prefix: new HarmonyMethod(typeof(LedgerFixture), nameof(HarvestPlacing)),
                        postfix: new HarmonyMethod(typeof(LedgerFixture), nameof(HarvestPlaced)));
                    harmony.Patch(AccessTools.Method(typeof(CompHasGatherableBodyResource), "Gathered"), prefix: new HarmonyMethod(typeof(LedgerFixture), nameof(Gathering)));
                    patched = true;
                }
                return FixtureKit.Reply(new FixtureDone { Success = true });
            }, cancellationToken);
        }

        private static void Caught(WaterBodyTracker __instance, float amount)
        {
            if (tracked != null && tracked.waterBodyTracker == __instance) catches += amount;
        }

        // PlantWork applies the pawn's harvest stat and random rounding after YieldNow.
        // Observe its actual stack at placement, independently of the ledger's quest hook.
        // Hook only the out-result overload: the other overload delegates to it.
        private static void HarvestPlacing(Thing thing, IntVec3 center, Map map, out int __state)
        {
            __state = 0;
            if (tracked == null || map != tracked || thing.def != Rice.plant.harvestedThingDef) return;
            if (!map.mapPawns.FreeColonistsSpawned.Any(p => p.Position == center
                && p.jobs.curDriver is JobDriver_PlantWork && p.CurJob.targetA.Thing is Plant plant && plant.def == Rice)) return;
            __state = thing.stackCount;
        }

        private static void HarvestPlaced(bool __result, int __state)
        {
            if (__result) riceYield += __state;
        }

        private static void Gathering(CompHasGatherableBodyResource __instance)
        {
            if (tracked != null && __instance.parent.Map == tracked) gathers++;
        }

        [Tool("test/ledger_observe", Description = "Read the fixture's independent catch, rice yield and milk gather tallies; no mutations.")]
        public async Task<object> Observe(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => FixtureKit.Reply(new LedgerTally { Catches = catches, RiceYield = riceYield, Gathers = gathers }), cancellationToken);
        }
    }
}
