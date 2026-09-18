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
    // predig columns of the room past it -- exactly as pawns would have
    // left them (rock gone, rock roof kept, the open cells and the rock
    // bordering them unfogged). The planner's sunk-work credit then binds
    // the same target a fresh run would dig, and the run's own stages cover
    // only the columns left standing. The room is the planner's 7x7
    // rectangle or, for a neolithic colony (#64), its radius-4 round room:
    // the 49 cells within four of the centre, nine columns deep, the
    // nearest and farthest a single cell on the corridor line.
    //
    // Mid-project changes (#63): setup can hide a hazard in the fogged room
    // (two ancient wall cells in its far column) or widen the block to two
    // staged lanes; the seal action walls the corridor mouth shut and the
    // breach action levels the rock around the room so its roof is no
    // longer held once the rest is gone. Each is what a player, a ruin or
    // careless surface mining does to a dig in progress.
    public sealed class MountainFixture
    {
        private const int BlockWidth = 13;  // across the face (z extent)
        private const int BlockDepth = 12;  // into the mountain (x extent)
        private const int FaceGap = 4;      // clear cells between the anchor and the face
        private const int CorridorLength = 2; // the planner's shortest corridor
        private const int RoomSize = 7;       // the planner's rectangular interior
        private const int RoundRadius = 4;    // the planner's round interior (policy.EllipseShape(4, 4))
        // LaneSpacing separates the centre lines of two staged lanes: one
        // rock column between the rooms and one beyond the outer room.
        private const int LaneSpacing = RoomSize + 1;

        [Tool("test/mountain_fixture", Description = "UNSAFE FOR MODEL EXECUTION. Disposable mountain-base fixture: setup raises a fogged granite block beside the colonists (predig opens the corridor and that many room columns first, of the 7x7 rectangle or the radius-4 round shape; lanes=2 widens it to two staged lanes; hazard hides two ancient wall cells in the room; wood is the log stack left beside the colonists, 75 by default); inspect reads one cell's rock, roof, fog, designation, collapse marks, room and sleeper state; tire exhausts every colonist so the next bed is slept in at once; seal walls the corridor mouth at face x,z (dx,dz into the rock) shut; breach levels the rock around that corridor's room.")]
        public async Task<object> Run(IRimBridgeContext ctx, CancellationToken cancellationToken,
            string action = "setup", int x = 0, int z = 0, int predig = 0, string shape = "rectangle", int lanes = 1, bool hazard = false, int dx = 0, int dz = 0, int wood = 75)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null) throw new InvalidOperationException("Disposable map required.");
                if (action == "inspect") return Inspect(map, new IntVec3(x, 0, z));
                if (action == "tire") return Tire(map);
                if (action == "seal" || action == "breach") {
                    var dir = new IntVec3(dx, 0, dz);
                    if (Math.Abs(dx) + Math.Abs(dz) != 1) throw new InvalidOperationException("A cardinal direction dx,dz is required.");
                    return action == "seal" ? Seal(map, new IntVec3(x, 0, z), dir) : Breach(map, new IntVec3(x, 0, z), dir);
                }
                if (action != "setup") throw new InvalidOperationException("Unknown action " + action);
                if (!Find.TickManager.Paused) throw new InvalidOperationException("Paused map required for setup.");
                if (shape != "rectangle" && shape != "round") throw new InvalidOperationException("shape must be rectangle or round.");
                var columns = shape == "round" ? 2 * RoundRadius + 1 : RoomSize;
                if (predig < 0 || predig > columns) throw new InvalidOperationException("predig must be 0.." + columns + " room columns.");
                if (lanes < 1 || lanes > 2) throw new InvalidOperationException("lanes must be 1 or 2.");
                if (wood < 0 || wood > 1500) throw new InvalidOperationException("wood must be 0..1500 logs.");
                return Setup(map, predig, shape, lanes, hazard, wood);
            }, cancellationToken);
        }

        // Seal walls the corridor mouth shut, the way a player closing off a
        // dig would: the access cell in front of the face, the two beside it
        // and the one behind, so no visible walkable cell touches the access
        // cell. Colonists inside the dig are moved out first and every Mine
        // designation near the face is cancelled, as the player would cancel
        // work nobody can reach.
        private static object Seal(Map map, IntVec3 face, IntVec3 dir)
        {
            var across = dir.x != 0 ? new IntVec3(0, 0, 1) : new IntVec3(1, 0, 0);
            var access = face - dir;
            var walls = new[] { access, access + across, access - across, access - dir };
            var pocket = new List<IntVec3>();
            for (var depth = 0; depth < CorridorLength + RoomSize; depth++)
                for (var offset = -RoomSize / 2; offset <= RoomSize / 2; offset++)
                    pocket.Add(face + dir * depth + across * offset);
            var moved = new List<string>();
            var refuge = GenRadial.RadialCellsAround(access - dir * 3, 6, true).FirstOrDefault(c => c.InBounds(map) && c.Standable(map) && !walls.Contains(c) && !pocket.Contains(c));
            if (!refuge.IsValid) throw new InvalidOperationException("No refuge cell outside the dig.");
            foreach (var p in map.mapPawns.AllPawnsSpawned.Where(p => walls.Contains(p.Position) || pocket.Contains(p.Position)).ToList()) {
                p.jobs?.StopAll();
                p.Position = refuge; p.Notify_Teleported(true, false);
                moved.Add(p.ThingID);
            }
            var cancelled = 0;
            foreach (var c in pocket) {
                var designation = map.designationManager.DesignationAt(c, DesignationDefOf.Mine);
                if (designation != null) { map.designationManager.RemoveDesignation(designation); cancelled++; }
            }
            var built = new List<object>();
            foreach (var c in walls) {
                if (!c.InBounds(map)) throw new InvalidOperationException("Seal cell " + c + " out of bounds.");
                foreach (var t in c.GetThingList(map).Where(t => t is Plant || t is Filth || t.def.category == ThingCategory.Item).ToList()) t.Destroy(DestroyMode.Vanish);
                if (c.GetEdifice(map) != null) continue;
                var wall = ThingMaker.MakeThing(ThingDefOf.Wall, ThingDefOf.BlocksGranite);
                wall.SetFaction(Faction.OfPlayer);
                GenSpawn.Spawn(wall, c, map);
                built.Add(new { c.x, c.z });
            }
            map.mapDrawer.WholeMapChanged(MapMeshFlagDefOf.Things);
            return new { success = true, tick = Find.TickManager.TicksGame, walls = built, moved, cancelled, refuge = new { refuge.x, refuge.z } };
        }

        // Breach levels the rock around the corridor's room -- roof first,
        // then rock, as the setup levelling does, so nothing collapses now --
        // leaving only the face row and the corridor-plus-room footprint
        // standing. Once the remaining room rock is gone nothing within the
        // roof support radius of its far cells holds the roof, which is the
        // "roof holder removed" case for the planner's whole-target read.
        private static object Breach(Map map, IntVec3 face, IntVec3 dir)
        {
            var across = dir.x != 0 ? new IntVec3(0, 0, 1) : new IntVec3(1, 0, 0);
            var keep = new HashSet<IntVec3>();
            for (var depth = 0; depth < CorridorLength + RoomSize; depth++) {
                var half = depth < CorridorLength ? 0 : RoomSize / 2;
                for (var offset = -half; offset <= half; offset++) keep.Add(face + dir * depth + across * offset);
            }
            var levelled = new List<object>();
            for (var depth = 1; depth < BlockDepth + 2; depth++)
                for (var offset = -BlockWidth; offset <= BlockWidth; offset++) {
                    var c = face + dir * depth + across * offset;
                    if (!c.InBounds(map) || keep.Contains(c)) continue;
                    var rock = c.GetEdifice(map) as Mineable;
                    var roof = c.GetRoof(map);
                    if (rock == null && (roof == null || !roof.isNatural)) continue;
                    if (roof != null && roof.isNatural) map.roofGrid.SetRoof(c, null);
                    if (rock != null) rock.Destroy(DestroyMode.Vanish);
                    map.fogGrid.Unfog(c);
                    levelled.Add(new { c.x, c.z });
                }
            map.mapDrawer.WholeMapChanged(MapMeshFlagDefOf.FogOfWar);
            map.roofGrid.Drawer.SetDirty();
            return new { success = true, tick = Find.TickManager.TicksGame, levelled = levelled.Count, kept = keep.Count, collapsing = map.roofCollapseBuffer.CellsMarkedToCollapse.Count };
        }

        // RoomOffsets lists the across-corridor offsets of the room column at
        // depth (0 is the column past the door) for the shape.
        private static IEnumerable<int> RoomOffsets(string shape, int depth)
        {
            if (shape == "round") {
                var dd = depth - RoundRadius;
                for (var offset = -RoundRadius; offset <= RoundRadius; offset++)
                    if (dd * dd + offset * offset <= RoundRadius * RoundRadius) yield return offset;
                yield break;
            }
            for (var offset = -(RoomSize / 2); offset <= RoomSize / 2; offset++) yield return offset;
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
            var edifice = cell.GetEdifice(map);
            var rock = edifice as Mineable;
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
                edifice = edifice?.def.defName,
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

        private static object Setup(Map map, int predig, string shape, int lanes, bool hazard, int wood)
        {
            var blockWidth = BlockWidth + (lanes - 1) * 2 * LaneSpacing;
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
                var block = BlockRect(anchor + new IntVec3(dir.z * side, 0, dir.x * side), dir, gap, blockWidth);
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
                // The hazard: two ancient wall cells in the far column of the
                // centre lane's room, either side of its centre line, hidden
                // by the fog until the pawns dig up to them.
                var faceX = dir.x != 0 ? (dir.x > 0 ? block.minX : block.maxX) : 0;
                var faceZ = dir.z != 0 ? (dir.z > 0 ? block.minZ : block.maxZ) : 0;
                var centreFace = dir.x != 0 ? new IntVec3(faceX, 0, block.CenterCell.z) : new IntVec3(block.CenterCell.x, 0, faceZ);
                var across = dir.x != 0 ? new IntVec3(0, 0, 1) : new IntVec3(1, 0, 0);
                var hazards = new List<IntVec3>();
                if (hazard) {
                    var far = centreFace + dir * (CorridorLength + RoomSize - 1);
                    hazards.Add(far + across); hazards.Add(far - across);
                }
                foreach (var c in block.Cells) {
                    foreach (var t in c.GetThingList(map).Where(t => t.def.category == ThingCategory.Item).ToList()) t.Destroy(DestroyMode.Vanish);
                    if (hazards.Contains(c)) {
                        var wall = ThingMaker.MakeThing(ThingDefOf.Wall, ThingDefOf.BlocksGranite);
                        GenSpawn.Spawn(wall, c, map);
                    } else GenSpawn.Spawn(ThingMaker.MakeThing(granite), c, map);
                    map.roofGrid.SetRoof(c, RoofDefOf.RoofRockThick);
                }
                // Natural rock nearer the colonists than the block is a better
                // site by the planner's own score; level it (and its rock roof,
                // before the rock so nothing is left to collapse) so the block
                // is the only face in the colony window. Rock that walls an
                // enclosed structure (an ancient danger sealed in the
                // mountain, as on the tribal8 baseline) stays: breaching it
                // wakes its mechanoids and the run holds on unsafe_threat.
                var sealing = 0;
                foreach (var c in GenRadial.RadialCellsAround(anchor, 24, true)) {
                    if (!c.InBounds(map) || block.Contains(c)) continue;
                    if (SealsStructure(map, c)) { sealing++; continue; }
                    var roof = c.GetRoof(map);
                    if (roof != null && roof.isNatural) map.roofGrid.SetRoof(c, null);
                    foreach (var t in c.GetThingList(map).OfType<Mineable>().ToList()) t.Destroy(DestroyMode.Vanish);
                    map.fogGrid.Unfog(c);
                }
                // Fog everything behind the face so the interior is unknown
                // until pawns actually dig into it.
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
                // A second lane is staged the same way beside the first, one
                // rock column apart, so a project that loses its way in has
                // a verified face to continue from.
                var predug = new List<IntVec3>();
                var laneFaces = new List<IntVec3> { centreFace };
                if (lanes == 2) laneFaces.Add(centreFace + across * LaneSpacing);
                if (predig > 0) {
                    foreach (var face in laneFaces)
                        for (var depth = 0; depth < CorridorLength + predig; depth++) {
                            var line = face + dir * depth;
                            var offsets = depth < CorridorLength ? new[] { 0 } : RoomOffsets(shape, depth - CorridorLength).ToArray();
                            foreach (var offset in offsets) predug.Add(line + across * offset);
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
                for (var left = wood; left > 0 && woodCell.IsValid; left -= 75) {
                    var logs = ThingMaker.MakeThing(ThingDefOf.WoodLog); logs.stackCount = Math.Min(75, left);
                    GenSpawn.Spawn(logs, woodCell, map); logs.SetForbidden(false, false);
                    woodCell = GenRadial.RadialCellsAround(anchor, 30, true).FirstOrDefault(c => c.InBounds(map) && !apron.Contains(c) && c != spare
                        && c.Standable(map) && map.zoneManager.ZoneAt(c) == null && c.GetThingList(map).Count == 0);
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
                    faceX, faceZ, fogged, sealing,
                    predig, shape, predug = predug.Select(c => new { c.x, c.z }).ToList(),
                    lanes = laneFaces.Select(c => new { c.x, c.z }).ToList(),
                    hazards = hazards.Select(c => new { c.x, c.z }).ToList(),
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

        // SealsStructure reports a cell inside an enclosed room or bordering
        // one: the interior and natural walls of a sealed structure, which
        // levelling or unfogging would breach. Open rock mass is no room at
        // all, so ordinary mountain is levelled.
        private static bool SealsStructure(Map map, IntVec3 c)
        {
            foreach (var n in GenAdj.CellsAdjacent8Way(new TargetInfo(c, map)).Concat(new[] { c })) {
                if (!n.InBounds(map)) continue;
                var room = n.GetRoom(map);
                if (room != null && !room.PsychologicallyOutdoors && room.ProperRoom) return true;
            }
            return false;
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
        private static CellRect BlockRect(IntVec3 anchor, IntVec3 dir, int gap, int width)
        {
            if (dir.x != 0) {
                var minX = dir.x > 0 ? anchor.x + gap + 1 : anchor.x - gap - BlockDepth;
                return new CellRect(minX, anchor.z - width / 2, BlockDepth, width);
            }
            var minZ = dir.z > 0 ? anchor.z + gap + 1 : anchor.z - gap - BlockDepth;
            return new CellRect(anchor.x - width / 2, minZ, width, BlockDepth);
        }
    }
}
