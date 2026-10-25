using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimGovernor.Native
{
    // Test-only speed-matrix stage. No pawn stats, simulation ticks or resources are edited.
    public sealed class ThroughputFixture
    {
        // Speed-matrix stage: one staged colony the harness saves
        // once and reloads per clock speed, so every speed plays the same map,
        // pawns and stacks. Brain-valued work only (#3624): cloth deteriorating
        // outside a roofed stockpile, and the walls of rooms the brain plans;
        // no blueprint is placed. Needs are frozen by test/freeze_needs after
        // each reload (it is per game).
        [Tool("test/throughput_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Private disposable fixture: clear rock-chunk debris, register a legal roofed Cloth stockpile, spawn unforbidden Cloth stacks outside it and put every capable colonist on Construct/Haul with a Work timetable. No spawned resources beyond the Cloth, no skill or tick changes, no placement.")]
        public async Task<object> Prepare(INativeContext ctx, CancellationToken cancellationToken, int itemCount = 4)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap; var player = Faction.OfPlayerSilentFail;
                if (map == null || Current.Game == null || player == null || !Find.TickManager.Paused)
                    return Refuse("A paused disposable colony map is required.");
                if (itemCount < 1 || itemCount > 8) return Refuse("Use 1..8 items.");
                // Loose rock chunks trip the upkeep census row limit and starve
                // MaintainStorage of its items fact.
                var chunks = map.listerThings.AllThings
                    .Where(t => t.def.category == ThingCategory.Item && t.def.defName.StartsWith("Chunk")).ToList();
                foreach (var chunk in chunks) { try { chunk.Destroy(DestroyMode.Vanish); } catch { } }
                var people = map.mapPawns.FreeColonistsSpawned
                    .Where(p => !p.Dead && !p.Downed && !p.Drafted && !p.InMentalState && p.workSettings != null && p.timetable != null && p.skills != null
                        && !p.WorkTypeIsDisabled(WorkTypeDefOf.Construction) && !p.WorkTypeIsDisabled(WorkTypeDefOf.Hauling)
                        && p.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation) && p.health.capacities.CapableOf(PawnCapacityDefOf.Moving)
                        && p.skills.GetSkill(SkillDefOf.Construction).Level >= ThingDefOf.Wall.constructionSkillPrerequisite)
                    .OrderBy(p => p.thingIDNumber).ToList();
                if (people.Count < 3) return Refuse("At least three healthy colonists capable of construction and hauling are required (a debug start rolled before #152 may be cached in profile/Saves; delete its RimGovernor-debug-* save).");
                var anchor = people[0];
                bool ClearCell(IntVec3 c) => c.InBounds(map) && !c.Fogged(map) && c.Standable(map) && c.GetRoof(map) == null
                    && c.GetEdifice(map) == null && map.zoneManager.ZoneAt(c) == null
                    && c.GetThingList(map).All(t => t is Plant)
                    && anchor.CanReach(c, PathEndMode.Touch, Danger.None);
                // The stockpile and the stacks stay clear of each other; each
                // stack needs its own storage cell.
                var clear = GenRadial.RadialCellsAround(anchor.Position, 30, true).Where(ClearCell).ToList();
                var storageCells = clear.Take(itemCount).ToList();
                if (storageCells.Count != itemCount) return Refuse("Too few clear cells for the stockpile.");
                var itemCells = new List<IntVec3>();
                foreach (var c in clear) {
                    if (itemCells.Count == itemCount) break;
                    if (storageCells.Any(s => s.DistanceToSquared(c) < 16)) continue;
                    if (itemCells.Any(o => o.DistanceToSquared(c) < 4)) continue;
                    itemCells.Add(c);
                }
                if (itemCells.Count != itemCount) return Refuse("Too few well-separated clear cells for the Cloth stacks.");
                // A roof keeps the stored cloth from the unroofed and rain
                // deterioration the loose stacks take: the haul's value.
                foreach (var c in storageCells) map.roofGrid.SetRoof(c, RoofDefOf.RoofConstructed);
                var zone = new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, map.zoneManager);
                map.zoneManager.RegisterZone(zone);
                zone.GetStoreSettings().filter.SetDisallowAll();
                zone.GetStoreSettings().filter.SetAllow(ThingDefOf.Cloth, true);
                zone.GetStoreSettings().Priority = StoragePriority.Normal;
                foreach (var c in storageCells) zone.AddCell(c);
                var items = new List<object>();
                foreach (var cell in itemCells) {
                    var stack = ThingMaker.MakeThing(ThingDefOf.Cloth);
                    stack.stackCount = 25;
                    GenSpawn.Spawn(stack, cell, map);
                    stack.SetForbidden(false, false);
                    items.Add(new { id = stack.GetUniqueLoadID(), x = cell.x, z = cell.z, count = stack.stackCount });
                }
                foreach (var pawn in people) {
                    pawn.workSettings.SetPriority(WorkTypeDefOf.Construction, 1);
                    pawn.workSettings.SetPriority(WorkTypeDefOf.Hauling, 1);
                    for (var hour = 0; hour < 24; hour++) pawn.timetable.SetAssignment(hour, TimeAssignmentDefOf.Work);
                }
                map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
                var identity = Current.Game.GetComponent<ColonyIdentity>();
                return new {
                    success = true, colonyId = identity?.ColonyId, loadToken = identity?.LoadToken, mapId = map.uniqueID,
                    tick = Find.TickManager.TicksGame, chunksCleared = chunks.Count,
                    pawns = people.Select(p => new { id = p.GetUniqueLoadID(), name = p.LabelShort, construction = p.skills.GetSkill(SkillDefOf.Construction).Level }).ToArray(),
                    resourceDefName = ThingDefOf.Cloth.defName, stackSize = 25,
                    items = items.ToArray(),
                };
            }, cancellationToken).ConfigureAwait(false);
        }

        private static object Refuse(string reason) => new { success = false, reason };

    }
}
