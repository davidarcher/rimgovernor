using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;
using Verse.AI;

namespace HomeBridge.BridgeTools
{
    // Disposable test setup only. It raises a solid granite block under a
    // thick rock roof beside the colonists, with only the near face visible;
    // every cell is dug by ordinary pawn mining under the controller's
    // excavation plans. The fixture never designates, mines, builds or
    // assigns anything itself.
    //
    // Staging (#129): setup can open the project's first cells itself --
    // the two-cell corridor on the block's centre line and the nearest
    // predig columns of the 7x7 room past it -- exactly as pawns would have
    // left them (rock gone, rock roof kept, the open cells and the rock
    // bordering them unfogged). The planner's sunk-work credit then binds
    // the same target a fresh run would dig, and the run's own stages cover
    // only the columns left standing.
    public sealed class MountainFixture
    {
        private const int BlockWidth = 13;  // across the face (z extent)
        private const int BlockDepth = 12;  // into the mountain (x extent)
        private const int FaceGap = 4;      // clear cells between the anchor and the face
        private const int CorridorLength = 2; // the planner's shortest corridor
        private const int RoomSize = 7;       // the planner's rectangular interior

        [Tool("test/mountain_fixture", Description = "UNSAFE FOR MODEL EXECUTION. Disposable mountain-base fixture: setup raises a fogged granite block beside the colonists (predig opens the corridor and that many room columns first); inspect reads one cell's rock, roof, fog, designation, collapse marks, room and sleeper state; tire exhausts every colonist so the next bed is slept in at once.")]
        public async Task<object> Run(IRimBridgeContext ctx, CancellationToken cancellationToken,
            string action = "setup", int x = 0, int z = 0, int predig = 0)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null) throw new InvalidOperationException("Disposable map required.");
                if (action == "inspect") return Inspect(map, new IntVec3(x, 0, z));
                if (action == "tire") return Tire(map);
                if (action != "setup") throw new InvalidOperationException("Unknown action " + action);
                if (!Find.TickManager.Paused) throw new InvalidOperationException("Paused map required for setup.");
                if (predig < 0 || predig > RoomSize) throw new InvalidOperationException("predig must be 0.." + RoomSize + " room columns.");
                return Setup(map, predig);
            }, cancellationToken);
        }

        // Rest is the only need the sleeping assertion waits on; at 5% every
        // colonist heads for a bed at the next chance instead of on their own
        // schedule, so the furnished room is used within the hour.
        private static object Tire(Map map)
        {
            var tired = new List<string>();
            foreach (var p in map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead && p.needs?.rest != null)) {
                p.needs.rest.CurLevel = 0.05f;
                tired.Add(p.ThingID);
            }
            return new { success = true, tick = Find.TickManager.TicksGame, tired };
        }

        private static object Inspect(Map map, IntVec3 cell)
        {
            if (!cell.InBounds(map)) return new { success = false, error = "Cell out of bounds" };
            var rock = cell.GetEdifice(map) as Mineable;
            var room = cell.GetRoom(map);
            var region = GenRadial.RadialCellsAround(cell, RoofCollapseUtility.RoofMaxSupportDistance, true).Where(c => c.InBounds(map)).ToList();
            var sleepers = new List<string>();
            var beds = 0;
            if (room != null && !room.PsychologicallyOutdoors) {
                foreach (var pawn in map.mapPawns.FreeColonistsSpawned)
                    if (pawn.GetRoom() == room && pawn.CurJob != null && pawn.CurJob.def == JobDefOf.LayDown && pawn.jobs.curDriver is JobDriver_LayDown driver && driver.asleep)
                        sleepers.Add(pawn.ThingID);
                beds = room.ContainedBeds.Count();
            }
            return new {
                success = true,
                tick = Find.TickManager.TicksGame,
                fogged = cell.Fogged(map),
                mineable = rock?.def.defName,
                hitPoints = rock?.HitPoints ?? 0,
                roof = cell.GetRoof(map)?.defName,
                walkable = cell.Walkable(map),
                designated = map.designationManager.DesignationAt(cell, DesignationDefOf.Mine) != null,
                door = cell.GetDoor(map) != null,
                collapsing = region.Count(c => map.roofCollapseBuffer.IsMarkedToCollapse(c)),
                roomId = room?.ID ?? -1,
                properRoom = room != null && room.ProperRoom,
                outdoors = room == null || room.PsychologicallyOutdoors,
                roomCells = room?.CellCount ?? 0,
                bedsInRoom = beds,
                sleepers = sleepers,
            };
        }

        private static object Setup(Map map, int predig)
        {
            var people = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead).ToList();
            if (people.Count < 1 || people.Count > 8) throw new InvalidOperationException("Require 1..8 colonists.");
            var miners = people.Where(p => !p.WorkTypeIsDisabled(WorkTypeDefOf.Mining)).ToList();
            var builders = people.Where(p => !p.WorkTypeIsDisabled(WorkTypeDefOf.Construction)).ToList();
            if (miners.Count == 0 || builders.Count == 0) throw new InvalidOperationException("Require a miner and a builder.");
            // Raids, hunting predators and mental breaks are outside this
            // scenario, and each of them holds the clock (unsafe_colony,
            // mental-risk) for as long as it lasts.
            QuietStoryteller.Apply(map);
            // Debug colonists carry random ailments; a chronic tendable one
            // (carcinoma, an addiction) keeps the medical emergency open and
            // every development goal suspended for the whole run.
            foreach (var p in people)
                foreach (var h in p.health.hediffSet.hediffs.Where(h => (h.def.tendable || h.def.isBad) && !(h is Hediff_MissingPart)).ToList())
                    p.health.RemoveHediff(h);
            var anchor = new IntVec3((int)people.Average(p => p.Position.x), 0, (int)people.Average(p => p.Position.z));
            var granite = DefDatabase<ThingDef>.GetNamed("Granite");
            // Try the four directions from the anchor; the block must sit on
            // buildable natural terrain away from the map edge, zones, homes
            // and anything that is not a plant, a natural rock or an item.
            var choices = new[] { new IntVec3(1, 0, 0), new IntVec3(-1, 0, 0), new IntVec3(0, 0, 1), new IntVec3(0, 0, -1) };
            var rejected = new List<string>();
            // Random maps put ruins, sand and water anywhere, so the block may
            // slide sideways and further out until it finds natural ground.
            var placements = new List<(IntVec3 dir, int gap, int side)>();
            foreach (var gap in new[] { FaceGap, FaceGap + 4, FaceGap + 8 })
                foreach (var side in new[] { 0, -5, 5, -10, 10 })
                    foreach (var dir in choices) placements.Add((dir, gap, side));
            foreach (var (dir, gap, side) in placements) {
                var block = BlockRect(anchor + new IntVec3(dir.z * side, 0, dir.x * side), dir, gap);
                var apron = block.ExpandedBy(3);
                if (!apron.InBounds(map) || block.minX < 2 || block.minZ < 2 || block.maxX > map.Size.x - 3 || block.maxZ > map.Size.z - 3) { rejected.Add(dir + ": map edge"); continue; }
                var bad = apron.Cells.Select(c => Unsuitable(map, c)).FirstOrDefault(r => r != null);
                if (bad != null) { rejected.Add(dir + "/" + gap + "/" + side + ": " + bad); continue; }
                if (block.Cells.Any(c => c.GetThingList(map).OfType<Pawn>().Any())) { rejected.Add(dir + ": pawn in block"); continue; }
                // Level the apron: no plants, rock or roof so the access side is
                // open ground and the face is the only rock the planner sees.
                foreach (var c in apron.Cells) {
                    foreach (var t in c.GetThingList(map).Where(t => t is Plant || t is Mineable || t is Filth).ToList()) t.Destroy(DestroyMode.Vanish);
                    if (!c.GetTerrain(map).affordances.Contains(TerrainAffordanceDefOf.Heavy)) map.terrainGrid.SetTerrain(c, TerrainDefOf.Gravel);
                    map.fogGrid.Unfog(c);
                    map.roofGrid.SetRoof(c, null);
                }
                foreach (var c in block.Cells) {
                    foreach (var t in c.GetThingList(map).Where(t => t.def.category == ThingCategory.Item).ToList()) t.Destroy(DestroyMode.Vanish);
                    GenSpawn.Spawn(ThingMaker.MakeThing(granite), c, map);
                    map.roofGrid.SetRoof(c, RoofDefOf.RoofRockThick);
                }
                // Natural rock nearer the colonists than the block is a better
                // site by the planner's own score; level it (and its rock roof,
                // before the rock so nothing is left to collapse) so the block
                // is the only face in the colony window.
                foreach (var c in GenRadial.RadialCellsAround(anchor, 24, true)) {
                    if (!c.InBounds(map) || block.Contains(c)) continue;
                    var roof = c.GetRoof(map);
                    if (roof != null && roof.isNatural) map.roofGrid.SetRoof(c, null);
                    foreach (var t in c.GetThingList(map).OfType<Mineable>().ToList()) t.Destroy(DestroyMode.Vanish);
                    map.fogGrid.Unfog(c);
                }
                // Fog everything behind the face so the interior is unknown
                // until pawns actually dig into it.
                var faceX = dir.x != 0 ? (dir.x > 0 ? block.minX : block.maxX) : 0;
                var faceZ = dir.z != 0 ? (dir.z > 0 ? block.minZ : block.maxZ) : 0;
                var setFog = FogSetter(map);
                var fogged = 0;
                foreach (var c in block.Cells) {
                    var face = dir.x != 0 ? c.x == faceX : c.z == faceZ;
                    if (face) continue;
                    setFog(c);
                    fogged++;
                }
                // Staged sunk work: the corridor on the centre line plus the
                // nearest predig room columns are opened the way finished
                // mining leaves them. The rock roof stays; the open cells and
                // every cell bordering them are unfogged, as the game reveals
                // them when a miner breaks through, so the next column is
                // visible and designatable while the rest stays unknown.
                var predug = new List<IntVec3>();
                if (predig > 0) {
                    var face = dir.x != 0 ? new IntVec3(faceX, 0, block.CenterCell.z) : new IntVec3(block.CenterCell.x, 0, faceZ);
                    var across = dir.x != 0 ? new IntVec3(0, 0, 1) : new IntVec3(1, 0, 0);
                    for (var depth = 0; depth < CorridorLength + predig; depth++) {
                        var line = face + dir * depth;
                        var half = depth < CorridorLength ? 0 : RoomSize / 2;
                        for (var offset = -half; offset <= half; offset++) predug.Add(line + across * offset);
                    }
                    foreach (var c in predug)
                        foreach (var t in c.GetThingList(map).OfType<Mineable>().ToList()) t.Destroy(DestroyMode.Vanish);
                    foreach (var c in predug)
                        foreach (var n in GenAdj.CellsAdjacent8Way(new TargetInfo(c, map)).Concat(new[] { c }))
                            if (n.InBounds(map)) map.fogGrid.Unfog(n);
                    fogged = block.Cells.Count(c => c.Fogged(map));
                }
                map.mapDrawer.WholeMapChanged(MapMeshFlagDefOf.FogOfWar);
                map.roofGrid.Drawer.SetDirty();
                foreach (var food in map.listerThings.AllThings.Where(t => t.def.category == ThingCategory.Item
                    && t.def.IsNutritionGivingIngestible && !t.def.IsDrug && t.Position.InHorDistOf(anchor, 50)))
                    food.SetForbidden(false, false);
                // Mining and construction lead; everything else keeps its default
                // so colonists still eat, sleep and haul the door's wood.
                foreach (var p in people) {
                    if (!p.WorkTypeIsDisabled(WorkTypeDefOf.Mining)) p.workSettings.SetPriority(WorkTypeDefOf.Mining, 1);
                    if (!p.WorkTypeIsDisabled(WorkTypeDefOf.Construction)) p.workSettings.SetPriority(WorkTypeDefOf.Construction, 1);
                }
                var spare = GenRadial.RadialCellsAround(anchor, 30, true).FirstOrDefault(c => c.InBounds(map) && !apron.Contains(c)
                    && c.Standable(map) && c.GetRoof(map) == null && map.zoneManager.ZoneAt(c) == null
                    && c.GetTerrain(map).affordances.Contains(TerrainAffordanceDefOf.Light) && c.GetThingList(map).Count == 0);
                // The door is wooden and debug colonies start with no logs;
                // one stack beside the spare cell covers it (the acquisition
                // family that would fell trees is not part of this scenario).
                var woodCell = GenRadial.RadialCellsAround(anchor, 30, true).FirstOrDefault(c => c.InBounds(map) && !apron.Contains(c) && c != spare
                    && c.Standable(map) && map.zoneManager.ZoneAt(c) == null && c.GetThingList(map).Count == 0);
                if (woodCell.IsValid) {
                    var wood = ThingMaker.MakeThing(ThingDefOf.WoodLog); wood.stackCount = 75;
                    GenSpawn.Spawn(wood, woodCell, map); wood.SetForbidden(false, false);
                }
                // A named faction and settlement never raise the naming
                // dialog that would otherwise stop the clock a few days in.
                if (!Faction.OfPlayer.HasName) Faction.OfPlayer.Name = "Trial Colony";
                if (map.Parent is RimWorld.Planet.Settlement home && !home.namedByPlayer) { home.Name = "Trial Base"; home.namedByPlayer = true; }
                return new {
                    success = true,
                    anchor = new { x = anchor.x, z = anchor.z },
                    direction = new { x = dir.x, z = dir.z },
                    block = new { minX = block.minX, minZ = block.minZ, maxX = block.maxX, maxZ = block.maxZ },
                    faceX, faceZ, fogged,
                    predig, predug = predug.Select(c => new { c.x, c.z }).ToList(),
                    miners = miners.Select(p => p.ThingID).ToList(),
                    builders = builders.Select(p => p.ThingID).ToList(),
                    colonists = people.Count,
                    spare = spare.IsValid ? new { x = spare.x, z = spare.z } : null,
                };
            }
            return new { success = false, error = "No clear natural site for the block beside the colonists", rejected, anchor = new { x = anchor.x, z = anchor.z } };
        }

        // Natural ground only: no water, zones, home area or structures (ruins
        // may seal ancient dangers, so they are never opened). Plants, rock and
        // filth are levelled and soft terrain gravelled, since this is
        // disposable setup on a disposable map.
        private static string Unsuitable(Map map, IntVec3 c)
        {
            var terrain = c.GetTerrain(map);
            if (terrain.IsWater || terrain.passability == Traversability.Impassable) return c + " terrain " + terrain.defName;
            if (map.zoneManager.ZoneAt(c) != null) return c + " zoned";
            if (map.areaManager.Home[c]) return c + " home area";
            foreach (var t in c.GetThingList(map)) {
                if (t is Pawn || t is Plant || t is Mineable || t is Filth) continue;
                if (t.def.category == ThingCategory.Item || t.def.category == ThingCategory.Mote || t.def.category == ThingCategory.Gas) continue;
                return c + " " + t.def.defName;
            }
            return null;
        }

        // Direct fog writes: the game only exposes Unfog, so the storage is
        // reached through whichever member this game version keeps it in
        // (a bool[] field/property or an indexable native array).
        private static Action<IntVec3> FogSetter(Map map)
        {
            var grid = map.fogGrid;
            object storage = null;
            foreach (var name in new[] { "FogGrid_Unsafe", "fogGridDirect", "fogGrid" }) {
                var prop = AccessTools.Property(typeof(FogGrid), name);
                if (prop != null) { storage = prop.GetValue(grid); if (storage != null) break; }
                var field = AccessTools.Field(typeof(FogGrid), name);
                if (field != null) { storage = field.GetValue(grid); if (storage != null) break; }
            }
            if (storage is bool[] array) return c => array[map.cellIndices.CellToIndex(c)] = true;
            // NativeBitArray/NativeArray copies still point at the same native
            // memory, so writing through a boxed copy updates the grid.
            var setter = storage?.GetType().GetMethod("Set", new[] { typeof(int), typeof(bool) })
                ?? storage?.GetType().GetMethod("set_Item", new[] { typeof(int), typeof(bool) });
            if (setter != null) return c => setter.Invoke(storage, new object[] { map.cellIndices.CellToIndex(c), true });
            throw new InvalidOperationException("FogGrid storage unavailable: " + (storage?.GetType().FullName ?? "none"));
        }

        // The block starts gap cells from the anchor along dir and is centred
        // across it.
        private static CellRect BlockRect(IntVec3 anchor, IntVec3 dir, int gap)
        {
            if (dir.x != 0) {
                var minX = dir.x > 0 ? anchor.x + gap + 1 : anchor.x - gap - BlockDepth;
                return new CellRect(minX, anchor.z - BlockWidth / 2, BlockDepth, BlockWidth);
            }
            var minZ = dir.z > 0 ? anchor.z + gap + 1 : anchor.z - gap - BlockDepth;
            return new CellRect(anchor.x - BlockWidth / 2, minZ, BlockWidth, BlockDepth);
        }
    }
}
