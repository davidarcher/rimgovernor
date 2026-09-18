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
    // builders still raise. Nothing here designates or orders anything.
    public sealed class HutShellFixture
    {
        [Tool("test/hut_shell_fixture", Description = "UNSAFE FOR MODEL EXECUTION. Disposable shell-staging fixture: stage spawns finished player wood Walls at walls ('x,z;x,z') and a Door at door ('x,z', doorRotation north|east|south|west), moving pawns and items off those cells, and drops wood WoodLog beside the door.")]
        public async Task<object> Run(IRimBridgeContext ctx, CancellationToken cancellationToken,
            string action = "stage", string walls = "", string door = "", string doorRotation = "north", int wood = 0)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null) throw new InvalidOperationException("Disposable map required.");
                if (action != "stage") throw new InvalidOperationException("Unknown action " + action);
                if (!Find.TickManager.Paused) throw new InvalidOperationException("Paused map required.");
                var doorCells = ParseCells(door);
                if (doorCells.Count != 1) throw new InvalidOperationException("door must name one cell");
                return Stage(map, ParseCells(walls), doorCells[0], ParseRotation(doorRotation), wood);
            }, cancellationToken);
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

        private static object Stage(Map map, List<IntVec3> walls, IntVec3 door, Rot4 rotation, int wood)
        {
            var player = Faction.OfPlayerSilentFail;
            var wallDef = ThingDefOf.Wall;
            var doorDef = ThingDefOf.Door;
            var ring = new HashSet<IntVec3>(walls) { door };
            var spawned = new List<object>();
            var moved = 0;
            Thing Spawn(ThingDef def, IntVec3 cell, Rot4 rot)
            {
                var thing = ThingMaker.MakeThing(def, ThingDefOf.WoodLog);
                thing.SetFaction(player);
                return GenSpawn.Spawn(thing, cell, map, rot);
            }
            foreach (var cell in ring) {
                if (!cell.InBounds(map)) throw new InvalidOperationException("Cell out of bounds: " + cell);
                if (cell.GetEdifice(map) != null) throw new InvalidOperationException("Cell " + cell + " already holds " + cell.GetEdifice(map).def.defName);
                // A pawn steps beside the ring; an item goes a few cells
                // off it; plants and filth go.
                foreach (var t in cell.GetThingList(map).Where(t => t is Pawn || t.def.category == ThingCategory.Item).ToList()) {
                    var aside = GenRadial.RadialCellsAround(cell, 4.9f, false)
                        .FirstOrDefault(n => n.InBounds(map) && n.Standable(map) && n.GetEdifice(map) == null && !ring.Contains(n)
                            && !n.GetThingList(map).Any(o => o is Blueprint || o is Frame || o.def.category == ThingCategory.Item));
                    if (aside == default(IntVec3)) throw new InvalidOperationException("Nowhere to move " + t.LabelShort + " off " + cell);
                    if (t is Pawn pawn) { pawn.Position = aside; pawn.Notify_Teleported(false, true); moved++; continue; }
                    t.DeSpawn();
                    GenPlace.TryPlaceThing(t, aside, map, ThingPlaceMode.Near);
                    moved++;
                }
                foreach (var t in cell.GetThingList(map).Where(t => t is Plant || t is Filth || t is Blueprint || t is Frame).ToList()) t.Destroy(DestroyMode.Vanish);
                var isDoor = cell == door;
                var built = Spawn(isDoor ? doorDef : wallDef, cell, isDoor ? rotation : Rot4.North);
                spawned.Add(new { x = cell.x, z = cell.z, definition = built.def.defName, id = built.GetUniqueLoadID() });
            }
            map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
            var stacks = new List<object>();
            var remaining = wood;
            var bounds = CellRect.FromCellList(ring); // wood lies outside the shell
            if (remaining > 0) {
                foreach (var cell in GenRadial.RadialCellsAround(door, 7.9f, false)) {
                    if (remaining <= 0) break;
                    if (!cell.InBounds(map) || !cell.Standable(map) || cell.GetEdifice(map) != null || ring.Contains(cell)) continue;
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
            return new { success = true, tick = Find.TickManager.TicksGame, spawned = spawned.Count, moved, wood, stacks, cells = spawned };
        }
    }
}
