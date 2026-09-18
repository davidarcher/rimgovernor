using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Disposable test setup only. Stages a shell the routine has already
    // sited: the named wall cells and the door are spawned as finished
    // player-owned wood buildings, so the controller's next review finds the
    // ring standing but for the cells left out and has to adopt it (issue
    // #174). Unforbidden WoodLog beside the door feeds the few walls the
    // builders still raise; take removes every WoodLog on the map and wood
    // drops it again, the material shortage of #175. Nothing here
    // designates or orders anything.
    public sealed class HutShellFixture
    {
        [Tool("test/hut_shell_fixture", Description = "UNSAFE FOR MODEL EXECUTION. Disposable shell-staging fixture: stage spawns finished player wood Walls at walls ('x,z;x,z') and a Door at door ('x,z', doorRotation north|east|south|west), moving pawns and items off those cells and keeping player walls and doors already standing there, clears plants and items off the gap cells gaps ('x,z;x,z') left to the builders, and drops wood WoodLog beside the door; clear removes the player walls at walls and clears those cells the same way; allow unforbids every forbidden item on the map; take removes every WoodLog item on the map and the wood delivered to frames on gaps; wood drops wood WoodLog beside door, outside the ring walls.")]
        public async Task<object> Run(IRimBridgeContext ctx, CancellationToken cancellationToken,
            string action = "stage", string walls = "", string door = "", string doorRotation = "north", int wood = 0, string gaps = "")
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null) throw new InvalidOperationException("Disposable map required.");
                if (!Find.TickManager.Paused) throw new InvalidOperationException("Paused map required.");
                if (action == "clear") return Clear(map, ParseCells(walls));
                if (action == "allow") return Allow(map);
                if (action == "take") return Take(map, ParseCells(gaps));
                if (action == "wood") {
                    var at = ParseCells(door);
                    if (at.Count != 1) throw new InvalidOperationException("door must name one cell");
                    var ring = new HashSet<IntVec3>(ParseCells(walls)) { at[0] };
                    var stacks = DropWood(map, at[0], ring, ring, wood);
                    return new { success = true, tick = Find.TickManager.TicksGame, wood, stacks };
                }
                if (action != "stage") throw new InvalidOperationException("Unknown action " + action);
                var doorCells = ParseCells(door);
                if (doorCells.Count != 1) throw new InvalidOperationException("door must name one cell");
                return Stage(map, ParseCells(walls), doorCells[0], ParseRotation(doorRotation), wood, ParseCells(gaps));
            }, cancellationToken);
        }

        // Allow unforbids every forbidden item on the map (the starting
        // supplies a fresh load drops forbidden), what the supply routine
        // would otherwise spend one worker dispatch per stack on (#193).
        private static object Allow(Map map)
        {
            var allowed = 0;
            foreach (var thing in map.listerThings.AllThings.Where(t => t.def.category == ThingCategory.Item && t.IsForbidden(Faction.OfPlayer)).ToList()) {
                thing.SetForbidden(false, false);
                allowed++;
            }
            return new { success = true, allowed };
        }

        private static List<IntVec3> ParseCells(string list)
        {
            var cells = new List<IntVec3>();
            foreach (var item in (list ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)) {
                var parts = item.Split(',');
                if (parts.Length != 2 || !int.TryParse(parts[0].Trim(), out var x) || !int.TryParse(parts[1].Trim(), out var z))
                    throw new InvalidOperationException("cells must be 'x,z;x,z': " + item);
                cells.Add(new IntVec3(x, 0, z));
            }
            return cells;
        }

        private static Rot4 ParseRotation(string name)
        {
            switch ((name ?? "").ToLowerInvariant()) {
                case "north": return Rot4.North;
                case "east": return Rot4.East;
                case "south": return Rot4.South;
                case "west": return Rot4.West;
            }
            throw new InvalidOperationException("doorRotation must be north, east, south or west: " + name);
        }

        // Vacate moves pawns and items off a cell (beside the ring, off the
        // gaps) and removes its plants, filth, blueprints and frames: a wall
        // cell under brambles waits on plant cutting the tribal baseline's
        // work priorities never assign (#193 run r2), so the gaps left to the
        // builders are cleared like the cells the fixture spawns on.
        private static int Vacate(Map map, IntVec3 cell, HashSet<IntVec3> avoid)
        {
            var moved = 0;
            foreach (var t in cell.GetThingList(map).Where(t => t is Pawn || t.def.category == ThingCategory.Item).ToList()) {
                // A pocket clearing (#175) has little open ground within a few
                // cells of the ring, so the search widens before giving up; a
                // chunk nothing can carry is destroyed rather than moved.
                var aside = default(IntVec3);
                foreach (var radius in new[] { 4.9f, 9.9f, 19.9f }) {
                    aside = GenRadial.RadialCellsAround(cell, radius, false)
                        .FirstOrDefault(n => n.InBounds(map) && n.Standable(map) && n.GetEdifice(map) == null && !avoid.Contains(n)
                            && !n.GetThingList(map).Any(o => o is Pawn || o is Blueprint || o is Frame || o.def.category == ThingCategory.Item));
                    if (aside != default(IntVec3)) break;
                }
                if (aside == default(IntVec3) && !(t is Pawn) && t.def.thingCategories != null && t.def.thingCategories.Contains(ThingCategoryDefOf.Chunks)) {
                    t.Destroy(DestroyMode.Vanish);
                    moved++;
                    continue;
                }
                if (aside == default(IntVec3)) throw new InvalidOperationException("Nowhere to move " + t.LabelShort + " off " + cell);
                if (t is Pawn pawn) { pawn.Position = aside; pawn.Notify_Teleported(true, true); moved++; continue; }
                t.DeSpawn();
                GenPlace.TryPlaceThing(t, aside, map, ThingPlaceMode.Near);
                moved++;
            }
            foreach (var t in cell.GetThingList(map).Where(t => t is Plant || t is Filth || t is Blueprint || t is Frame).ToList()) t.Destroy(DestroyMode.Vanish);
            return moved;
        }

        private static object Stage(Map map, List<IntVec3> walls, IntVec3 door, Rot4 rotation, int wood, List<IntVec3> gaps)
        {
            var player = Faction.OfPlayerSilentFail;
            var wallDef = ThingDefOf.Wall;
            var doorDef = ThingDefOf.Door;
            var ring = new HashSet<IntVec3>(walls) { door };
            var avoid = new HashSet<IntVec3>(ring);
            avoid.UnionWith(gaps);
            var spawned = new List<object>();
            var standing = 0;
            var moved = 0;
            Thing Spawn(ThingDef def, IntVec3 cell, Rot4 rot)
            {
                var thing = ThingMaker.MakeThing(def, ThingDefOf.WoodLog);
                thing.SetFaction(player);
                return GenSpawn.Spawn(thing, cell, map, rot);
            }
            foreach (var cell in gaps) {
                if (!cell.InBounds(map)) throw new InvalidOperationException("Cell out of bounds: " + cell);
                if (cell.GetEdifice(map) != null) throw new InvalidOperationException("Gap " + cell + " holds " + cell.GetEdifice(map).def.defName);
                moved += Vacate(map, cell, avoid);
            }
            foreach (var cell in ring) {
                if (!cell.InBounds(map)) throw new InvalidOperationException("Cell out of bounds: " + cell);
                // A second stage call (the case fills a gap it found opening
                // onto an enclosed pocket) keeps the ring cells already standing.
                if (cell.GetEdifice(map) is Building held && held.Faction == player && (held.def == wallDef || held.def == doorDef)) { standing++; continue; }
                if (cell.GetEdifice(map) != null) throw new InvalidOperationException("Cell " + cell + " already holds " + cell.GetEdifice(map).def.defName);
                moved += Vacate(map, cell, avoid);
                var isDoor = cell == door;
                var built = Spawn(isDoor ? doorDef : wallDef, cell, isDoor ? rotation : Rot4.North);
                spawned.Add(new { x = cell.x, z = cell.z, definition = built.def.defName, id = built.GetUniqueLoadID() });
            }
            map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
            var stacks = DropWood(map, door, ring, avoid, wood);
            return new { success = true, tick = Find.TickManager.TicksGame, spawned = spawned.Count, standing, moved, wood, stacks, cells = spawned };
        }

        // DropWood lays wood WoodLog in unforbidden stacks on open ground
        // within eight cells of the door, outside the ring's bounding box
        // and off the avoided cells.
        private static List<object> DropWood(Map map, IntVec3 door, HashSet<IntVec3> ring, HashSet<IntVec3> avoid, int wood)
        {
            var stacks = new List<object>();
            var remaining = wood;
            var bounds = CellRect.FromCellList(ring); // wood lies outside the shell
            if (remaining > 0) {
                foreach (var cell in GenRadial.RadialCellsAround(door, 7.9f, false)) {
                    if (remaining <= 0) break;
                    if (!cell.InBounds(map) || !cell.Standable(map) || cell.GetEdifice(map) != null || avoid.Contains(cell)) continue;
                    if (cell.x >= bounds.minX && cell.x <= bounds.maxX && cell.z >= bounds.minZ && cell.z <= bounds.maxZ) continue;
                    if (cell.GetThingList(map).Any(t => t.def.category == ThingCategory.Item || t is Blueprint || t is Frame)) continue;
                    var count = Math.Min(remaining, ThingDefOf.WoodLog.stackLimit);
                    var stack = ThingMaker.MakeThing(ThingDefOf.WoodLog);
                    stack.stackCount = count;
                    if (!GenPlace.TryPlaceThing(stack, cell, map, ThingPlaceMode.Direct)) continue;
                    stack.SetForbidden(false, false);
                    remaining -= count;
                    stacks.Add(new { x = cell.x, z = cell.z, count });
                }
                if (remaining > 0) throw new InvalidOperationException("Could not place " + remaining + " WoodLog beside the door");
            }
            return stacks;
        }

        // Take removes every WoodLog item on the map, whether on the ground,
        // carried or in storage, and the wood already delivered to the
        // frames on the gap cells, so the shell's remaining walls have
        // nothing to be built from until wood drops it again.
        private static object Take(Map map, List<IntVec3> gaps)
        {
            var taken = 0;
            var stacks = 0;
            foreach (var thing in map.listerThings.ThingsOfDef(ThingDefOf.WoodLog).ToList()) {
                taken += thing.stackCount;
                stacks++;
                thing.Destroy(DestroyMode.Vanish);
            }
            foreach (var pawn in map.mapPawns.AllPawnsSpawned.ToList()) {
                var carried = pawn.carryTracker?.CarriedThing;
                if (carried == null || carried.def != ThingDefOf.WoodLog) continue;
                taken += carried.stackCount;
                stacks++;
                carried.Destroy(DestroyMode.Vanish);
            }
            var emptied = 0;
            foreach (var cell in gaps) {
                if (!cell.InBounds(map)) throw new InvalidOperationException("Cell out of bounds: " + cell);
                foreach (var frame in cell.GetThingList(map).OfType<Frame>().ToList()) {
                    var held = frame.resourceContainer.TotalStackCount;
                    if (held == 0) continue;
                    frame.resourceContainer.ClearAndDestroyContents();
                    taken += held;
                    emptied++;
                }
            }
            return new { success = true, tick = Find.TickManager.TicksGame, taken, stacks, emptied };
        }

        // Clear removes the player walls standing at the named cells (a gap the
        // case moves to a cell whose outside is open ground) and vacates them
        // like a staged gap.
        private static object Clear(Map map, List<IntVec3> walls)
        {
            var player = Faction.OfPlayerSilentFail;
            var removed = 0;
            var avoid = new HashSet<IntVec3>(walls);
            foreach (var cell in walls) {
                if (!cell.InBounds(map)) throw new InvalidOperationException("Cell out of bounds: " + cell);
                var edifice = cell.GetEdifice(map);
                if (edifice != null) {
                    if (edifice.Faction != player || edifice.def != ThingDefOf.Wall) throw new InvalidOperationException("Cell " + cell + " holds " + edifice.def.defName + ", not a player wall");
                    edifice.Destroy(DestroyMode.Vanish);
                    removed++;
                }
                Vacate(map, cell, avoid);
            }
            map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
            return new { success = true, tick = Find.TickManager.TicksGame, removed };
        }
    }
}
