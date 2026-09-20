using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI;

namespace HomeBridge.BridgeTools
{
    // Disposable test setup only. All extraction uses unchanged native pawn labor.
    public sealed class MiningFixture
    {
        private static Map remoteMap;
        private static List<Thing> remoteOre;
        private static Thing foreignOre;
        private static Zone_Stockpile remoteStore;

        [Tool("test/mining_remote_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Disposable flat-map steel shortage with three surface rocks near the far edge, a forbidden foreign Mine designation, accepting base storage and six armed hauling colonists.")]
        public async Task<object> RemotePrepare(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                var people = map.mapPawns.FreeColonistsSpawned.ToList();
                var anchor = people.First().Position;
                var baseCell = GenRadial.RadialCellsAround(anchor, 5, true).First(c => c.InBounds(map) && c.Standable(map) && c.GetEdifice(map) == null);
                var table = ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Table1x2c"), ThingDefOf.WoodLog);
                table.SetFaction(Faction.OfPlayer);
                GenSpawn.Spawn(table, baseCell, map);
                // Isolate Steel demand from the medical reserve derived for
                // every colony, even when only the resource family is enabled.
                var medicine = ThingMaker.MakeThing(ThingDefOf.MedicineHerbal);
                medicine.stackCount = 75;
                GenPlace.TryPlaceThing(medicine, anchor, map, ThingPlaceMode.Near);
                medicine.SetForbidden(false, false);
                while (people.Count(p => p.equipment != null && !p.WorkTagIsDisabled(WorkTags.Violent)) < 6) {
                    var pawn = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDefOf.Colonist, Faction.OfPlayer,
                        forceGenerateNewPawn: true, colonistRelationChanceFactor: 0f, allowDead: false, allowDowned: false,
                        canGeneratePawnRelations: false, mustBeCapableOfViolence: true, fixedBiologicalAge: 30f, fixedChronologicalAge: 30f));
                    GenSpawn.Spawn(pawn, CellFinder.RandomClosewalkCellNear(anchor, map, 6), map);
                    people.Add(pawn);
                }
                var miner = people.First(p => !p.WorkTypeIsDisabled(WorkTypeDefOf.Mining) && !p.WorkTypeIsDisabled(WorkTypeDefOf.Hauling));
                foreach (var pawn in people) {
                    if (pawn.equipment != null && !pawn.WorkTagIsDisabled(WorkTags.Violent) && pawn.equipment.Primary == null)
                        pawn.equipment.AddEquipment((ThingWithComps)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Gun_BoltActionRifle")));
                    if (!pawn.workSettings.Initialized) pawn.workSettings.EnableAndInitialize();
                    foreach (var work in DefDatabase<WorkTypeDef>.AllDefsListForReading)
                        if (!pawn.WorkTypeIsDisabled(work)) pawn.workSettings.SetPriority(work,
                            work == WorkTypeDefOf.Hauling ? 2 : work == WorkTypeDefOf.Mining && pawn == miner ? 1 : 0);
                    pawn.jobs.EndCurrentJob(JobCondition.InterruptForced);
                }
                miner.skills.GetSkill(SkillDefOf.Mining).Level = 20;
                foreach (var t in map.listerThings.AllThings.Where(t => t.def == ThingDefOf.Steel ||
                    t is Mineable && t.def.building.mineableThing == ThingDefOf.Steel).ToList()) t.Destroy(DestroyMode.Vanish);
                remoteStore = new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, map.zoneManager);
                map.zoneManager.RegisterZone(remoteStore);
                remoteStore.GetStoreSettings().filter.SetDisallowAll();
                remoteStore.GetStoreSettings().filter.SetAllow(ThingDefOf.Steel, true);
                foreach (var cell in GenRadial.RadialCellsAround(anchor, 8, true).Where(c => c.InBounds(map) && c.Standable(map)
                    && c.GetEdifice(map) == null && map.zoneManager.ZoneAt(c) == null && c.GetThingList(map).All(t => t is Plant)).Take(8))
                    remoteStore.AddCell(cell);
                int Edge(IntVec3 c) => Math.Min(Math.Min(c.x, map.Size.x - 1 - c.x), Math.Min(c.z, map.Size.z - 1 - c.z));
                var sites = map.AllCells.Where(c => Edge(c) >= 9 && Edge(c) <= 12 && c.DistanceTo(anchor) > 50
                    && !c.Fogged(map) && c.Standable(map) && c.GetEdifice(map) == null && miner.CanReach(c, PathEndMode.Touch, Danger.None)
                    && GenRadial.RadialCellsAround(c, 8, true).All(q => q.InBounds(map) && !q.Fogged(map) && !q.Roofed(map)
                        && !map.areaManager.Home[q] && map.zoneManager.ZoneAt(q) == null && q.GetEdifice(map) == null))
                    .OrderByDescending(c => c.DistanceToSquared(anchor)).ToList();
                var cells = new List<IntVec3>();
                // The foreign rock sits well clear of the demanded three: its
                // enclosure must not border them or any cell they need.
                foreach (var cell in sites) { if (cells.All(c => c.DistanceTo(cell) >= (cells.Count == 3 ? 8 : 3))) cells.Add(cell); if (cells.Count == 4) break; }
                if (cells.Count != 4 || remoteStore.Cells.Count < 4) return new { success = false, reason = "Insufficient clear remote ore or storage sites" };
                var def = DefDatabase<ThingDef>.AllDefs.First(d => d.building?.mineableThing == ThingDefOf.Steel && typeof(Mineable).IsAssignableFrom(d.thingClass));
                remoteOre = cells.Take(3).Select(c => GenSpawn.Spawn(ThingMaker.MakeThing(def), c, map)).ToList();
                foreignOre = GenSpawn.Spawn(ThingMaker.MakeThing(def), cells[3], map);
                new Designator_Mine().DesignateThing(foreignOre);
                foreignOre.SetForbidden(true, false);
                // Mineables need not have a forbiddable comp. Enclose the
                // foreign designation so ordinary player mining cannot run it.
                // Factionless walls: a player building marks Home around it
                // and would count as a colony facility next to the ore.
                foreach (var cell in GenAdj.CellsAdjacent8Way(foreignOre))
                    GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Wall, DefDatabase<ThingDef>.GetNamed("BlocksGranite")), cell, map);
                remoteMap = map;
                return new { success = true, distance = cells[0].DistanceTo(anchor), edge = Edge(cells[0]),
                    initialSteel = 0, rocks = remoteOre.Count, yield = def.building.mineableYield };
            }, cancellationToken);
        }

        [Tool("test/mining_remote_observe", Description = "UNSAFE FOR MODEL EXECUTION. Read only: remote ore removal, steel delivered to the base stockpile and preserved foreign mining designation.")]
        public async Task<object> RemoteObserve(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                if (remoteMap != Find.CurrentMap || remoteOre == null) return new { success = false };
                return new { success = true, removed = remoteOre.Count(t => t.Destroyed),
                    designated = remoteOre.Count(t => t.Spawned && remoteMap.designationManager.DesignationAt(t.Position, DesignationDefOf.Mine) != null),
                    storedSteel = remoteStore.Cells.SelectMany(c => c.GetThingList(remoteMap)).Where(t => t.def == ThingDefOf.Steel).Sum(t => t.stackCount),
                    foreignPreserved = foreignOre.Spawned && remoteMap.designationManager.DesignationAt(foreignOre.Position, DesignationDefOf.Mine) != null,
                    ownedRecords = Current.Game.GetComponent<MiningState>()?.Records.Count(r => r.MapId == remoteMap.uniqueID) ?? 0 };
            }, cancellationToken);
        }

        [Tool("test/mining_fixture", Description = "Disposable surface deposit fixture; never installed for gameplay.")]
        public async Task<object> Setup(IRimBridgeContext ctx, CancellationToken cancellationToken,
            string action = "setup", int x = 0, int z = 0)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (action == "inspect") {
                    var cell = new IntVec3(x, 0, z);
                    var region = GenRadial.RadialCellsAround(cell, RoofCollapseUtility.RoofMaxSupportDistance, true).ToList();
                    return new { success = true, standable = cell.InBounds(map) && cell.Standable(map),
                        unknown = region.Count(c => !c.InBounds(map) || c.Fogged(map)),
                        roofs = region.Count(c => c.InBounds(map) && c.Roofed(map)),
                        collapsing = region.Count(c => c.InBounds(map) && map.roofCollapseBuffer.IsMarkedToCollapse(c)) };
                }
                if (action == "roof") { map.roofGrid.SetRoof(new IntVec3(x, 0, z), RoofDefOf.RoofConstructed); return new { success = true }; }
                if (action == "unroof") { map.roofGrid.SetRoof(new IntVec3(x, 0, z), null); return new { success = true }; }
                if (action == "cancel") {
                    var designation = map.designationManager.DesignationAt(new IntVec3(x, 0, z), DesignationDefOf.Mine);
                    designation?.Delete(); return new { success = true };
                }
                if (action == "hauling" || action == "mining") {
                    var selectedWork = action == "hauling" ? WorkTypeDefOf.Hauling : WorkTypeDefOf.Mining;
                    foreach (var worker in map.mapPawns.FreeColonistsSpawned.ToList())
                        foreach (var work in DefDatabase<WorkTypeDef>.AllDefs.ToList())
                            if (!worker.WorkTypeIsDisabled(work)) worker.workSettings.SetPriority(work, work == selectedWork ? 1 : 0);
                    return new { success = true };
                }
                var pawn = map.mapPawns.FreeColonistsSpawned.Where(p => !p.WorkTypeIsDisabled(WorkTypeDefOf.Mining)
                    && !p.WorkTypeIsDisabled(WorkTypeDefOf.Hauling))
                    .OrderByDescending(p => p.GetStatValue(StatDefOf.MiningSpeed)).ThenBy(p => p.thingIDNumber).First();
                // A single-worker fixture isolates extraction from unrelated social fights.
                // Keep the other baseline pawns alive in world storage, never as outcomes.
                foreach (var other in map.mapPawns.FreeColonistsSpawned.Where(p => p != pawn).ToList()) {
                    other.DeSpawn();
                    Find.WorldPawns.PassToWorld(other, PawnDiscardDecideMode.KeepForever);
                }
                foreach (var food in map.listerThings.AllThings.Where(t => t.def.category == ThingCategory.Item
                    && t.def.IsNutritionGivingIngestible && !t.def.IsDrug && t.Position.InHorDistOf(pawn.Position, 50)))
                    food.SetForbidden(false, false);
                var cells = GenRadial.RadialCellsAround(pawn.Position, 35, true).Where(c => c.InBounds(map) && c.Standable(map)
                    && GenRadial.RadialCellsAround(c, 8, true).All(q => q.InBounds(map)
                        && !(q.GetEdifice(map) is Building b && !(b is Mineable))
                        && map.zoneManager.ZoneAt(q) == null && !map.areaManager.Home[q])).Take(3).ToList();
                if (cells.Count != 3) return new { success = false, error = "Fixture requires three clear surface cells" };
                var cleared = cells.SelectMany(c => GenRadial.RadialCellsAround(c, 8, true)).Distinct().ToList();
                foreach (var cell in cleared) map.roofGrid.SetRoof(cell, null);
                foreach (var cell in cleared) {
                    map.fogGrid.Unfog(cell);
                    if (cell.GetEdifice(map) is Mineable rock) rock.Destroy(DestroyMode.Vanish);
                }
                var def = DefDatabase<ThingDef>.AllDefs.First(d => d.building?.mineableThing == ThingDefOf.Steel
                    && typeof(Mineable).IsAssignableFrom(d.thingClass));
                var targets = cells.Select(c => GenSpawn.Spawn(ThingMaker.MakeThing(def), c, map)).ToList();
                foreach (var worker in map.mapPawns.FreeColonistsSpawned)
                    if (!worker.WorkTypeIsDisabled(WorkTypeDefOf.Mining)) {
                        foreach (var work in DefDatabase<WorkTypeDef>.AllDefs)
                            if (!worker.WorkTypeIsDisabled(work)) worker.workSettings.SetPriority(work, work == WorkTypeDefOf.Mining ? 1 : 0);
                    }
                return new { success = true, pawn = pawn.ThingID, targets = targets.Select(t => new {
                    thingId = t.ThingID, x = t.Position.x, z = t.Position.z, hp = t.HitPoints,
                    resource = t.def.building.mineableThing.defName }).ToList() };
            }, cancellationToken);
        }
    }
}
