using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;
using Verse.AI;
using HarmonyLib;

namespace HomeBridge.BridgeTools
{
    public sealed class StoreroomFixture
    {
        private static bool failConstruction, failureHook;
        [Tool("test/haul_quantity_contract", Description = "Exercise native split/merge/destruction tracking with disposable fixture stacks. This verifies identity accounting, not pawn labor.")]
        public async Task<object> QuantityContract(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                var pawn = map.mapPawns.FreeColonistsSpawned.First();
                var storage = map.AllCells.Where(c => c.Roofed(map) && c.GetSlotGroup(map) != null
                    && c.GetSlotGroup(map).Settings.filter.Allows(ThingDefOf.MedicineHerbal)
                    && !c.GetThingList(map).Any(t => t.def.category == ThingCategory.Item)).Take(2).ToList();
                if (storage.Count != 2) throw new System.InvalidOperationException("Two empty covered medicine cells required");
                var outside = GenRadial.RadialCellsAround(pawn.Position, 30, true).First(c => c.InBounds(map)
                    && !c.Roofed(map) && c.Standable(map) && !c.GetThingList(map).Any(t => t.def.category == ThingCategory.Item));
                System.Func<int, IntVec3, Thing> stack = (count, cell) => {
                    var t = ThingMaker.MakeThing(ThingDefOf.MedicineHerbal); t.stackCount = count;
                    return GenSpawn.Spawn(t, cell, map);
                };
                var source = stack(10, outside); var destination = stack(7, storage[0]);
                var id = HaulTracking.Begin(source, pawn); HaulTracking.Accept(id, true);
                var split = source.SplitOff(4); destination.TryAbsorbStack(split, true);
                var record = Current.Game.GetComponent<HaulTrackingState>().Records.Single(r => r.Id == id);
                bool partialWaited = !record.Complete && record.Blocker == null && record.RequiredCount == 17;
                source.DeSpawn(); GenSpawn.Spawn(source, storage[1], map);
                bool mergedDelivered = record.Complete && record.Blocker == null && record.RequiredCount == 17;
                // Consumption after the observed delivery must not erase its proof.
                source.Destroy(); destination.Destroy();
                bool proofRetained = record.Complete && record.Blocker == null;
                var lost = stack(10, outside);
                var lossId = HaulTracking.Begin(lost, pawn); HaulTracking.Accept(lossId, true);
                lost.SplitOff(4).Destroy(); lost.DeSpawn(); GenSpawn.Spawn(lost, storage[0], map);
                var loss = Current.Game.GetComponent<HaulTrackingState>().Records.Single(r => r.Id == lossId);
                bool lossBlocked = !loss.Complete && loss.Blocker != null;
                lost.Destroy();
                return new { success = partialWaited && mergedDelivered && proofRetained && lossBlocked,
                    partialWaited, mergedDelivered, proofRetained, lossBlocked, delivered = id, loss = lossId,
                    records = HaulTracking.Read(map) };
            }, cancellationToken).ConfigureAwait(false);
        }
        private static bool FailOnce(Frame __instance, Pawn worker)
        {
            if (!failConstruction || Current.Game.GetComponent<ConstructionLineageState>()?.Records
                    .Any(r => r.Current == __instance.GetUniqueLoadID()) != true) return true;
            failConstruction = false;
            __instance.FailConstruction(worker);
            return false;
        }
        [Tool("test/replace_lineage_wall", Description = "Replace one exact disposable completed wall with an identical independent wall, to test that coordinates cannot transfer construction ownership.")]
        public async Task<object> Replace(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Exact completed fixture wall ID.")] string target)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                var old = map.listerThings.AllThings.OfType<Building>().Single(b => b.GetUniqueLoadID() == target && b.def == ThingDefOf.Wall);
                if (RoofSupportSafety.Blocker(old, out _) != null) throw new System.InvalidOperationException("Fixture replacement requires supported roof");
                var next = ThingMaker.MakeThing(old.def, old.Stuff);
                next.SetFaction(old.Faction);
                var cell = old.Position; var rotation = old.Rotation;
                old.Destroy();
                GenSpawn.Spawn(next, cell, map, rotation);
                return new { success = true, previous = target, replacement = next.GetUniqueLoadID() };
            }, cancellationToken).ConfigureAwait(false);
        }

        [Tool("test/isolated_roof_holder", Description = "Prepare one disposable wall as the sole native holder for a small roof. No deconstruction is ordered.")]
        public async Task<object> RoofHolder(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                var center = map.mapPawns.FreeColonistsSpawned.First().Position;
                var cell = GenRadial.RadialCellsAround(center, 50, true).First(c => c.InBounds(map) && c.Standable(map)
                    && GenRadial.RadialCellsAround(c, RoofCollapseUtility.RoofMaxSupportDistance + 2, true)
                        .All(v => v.InBounds(map) && !v.Fogged(map) && v.GetEdifice(map)?.def.holdsRoof != true && !v.Roofed(map)));
                var wall = ThingMaker.MakeThing(ThingDefOf.Wall, ThingDefOf.WoodLog);
                wall.SetFaction(Faction.OfPlayerSilentFail);
                GenSpawn.Spawn(wall, cell, map);
                map.roofGrid.SetRoof(cell, RoofDefOf.RoofConstructed);
                map.roofGrid.SetRoof(cell + IntVec3.East, RoofDefOf.RoofConstructed);
                return new { success = true, wall = wall.GetUniqueLoadID(),
                    nativeSupported = RoofCollapseUtility.WithinRangeOfRoofHolder(cell + IntVec3.East, map) };
            }, cancellationToken).ConfigureAwait(false);
        }

        [Tool("test/storeroom_setup", Description = "Prepare disposable open ground, wood, exposed medicine and enabled builders. No room or roof is created.")]
        public async Task<object> Setup(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Inject one native construction fumble into a tracked frame.", DefaultValue = false)] bool constructionFailure = false)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                failConstruction = constructionFailure;
                if (constructionFailure && !failureHook) {
                    new Harmony("rimbot.fixture.construction-fumble").Patch(AccessTools.Method(typeof(Frame), nameof(Frame.CompleteConstruction)),
                        prefix: new HarmonyMethod(typeof(StoreroomFixture), nameof(FailOnce)));
                    failureHook = true;
                }
                var map = Find.CurrentMap;
                var people = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Downed && !p.Drafted && !p.InMentalState).ToList();
                var origin = GenRadial.RadialCellsAround(people.First().Position, 20, true).First(c =>
                    CellRect.FromLimits(c, c + new IntVec3(8, 0, 8)).Cells.All(v => v.InBounds(map)
                        && !v.Fogged(map) && v.Standable(map) && v.GetEdifice(map) == null && !v.Roofed(map)
                        && map.zoneManager.ZoneAt(v) == null && v.GetTerrain(map).affordances.Contains(TerrainAffordanceDefOf.Heavy)));
                foreach (var cell in CellRect.FromLimits(origin, origin + new IntVec3(8, 0, 8)))
                    foreach (var thing in cell.GetThingList(map).Where(t => t is Plant || t.def.category == ThingCategory.Item).ToList()) thing.Destroy();
                for (int i = 0; i < 4; i++) {
                    var wood = ThingMaker.MakeThing(ThingDefOf.WoodLog);
                    wood.stackCount = wood.def.stackLimit;
                    GenPlace.TryPlaceThing(wood, origin + new IntVec3(8, 0, i), map, ThingPlaceMode.Near);
                    wood.SetForbidden(false, false);
                }
                var medicine = ThingMaker.MakeThing(ThingDefOf.MedicineHerbal);
                medicine.stackCount = 5;
                GenPlace.TryPlaceThing(medicine, origin + new IntVec3(8, 0, 7), map, ThingPlaceMode.Near);
                medicine.SetForbidden(false, false);
                foreach (var p in people) {
                    p.playerSettings.AreaRestrictionInPawnCurrentMap = null;
                    p.needs.rest.CurLevelPercentage = .95f;
                    p.needs.food.CurLevelPercentage = .95f;
                    for (int i = 0; i < 24; i++) p.timetable.SetAssignment(i, TimeAssignmentDefOf.Anything);
                    foreach (var work in new[] { WorkTypeDefOf.Construction, WorkTypeDefOf.Hauling })
                        if (!p.WorkTypeIsDisabled(work)) p.workSettings.SetPriority(work, 1);
                    p.jobs.EndCurrentJob(JobCondition.InterruptForced);
                }
                return new { success = true, constructionFailure, medicine = medicine.GetUniqueLoadID(), origin = new { x = origin.x, z = origin.z } };
            }, cancellationToken).ConfigureAwait(false);
        }
    }
}
