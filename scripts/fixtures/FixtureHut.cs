using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace HomeBridge.BridgeTools
{
    // The starter hut the ladder fixtures stage so a development rung is not
    // held behind a whole shell build: one square ring of finished player
    // wood walls with a single east door, roofed, a sleeping spot per
    // colonist along its south rows and every colonist moved inside. The
    // room is a proper enclosed Barracks/Room the facility catalog lets host
    // a workshop or laboratory bench, and the rows above the spots stay free
    // for whatever the case's ladder furnishes.
    public static class FixtureHut
    {
        public sealed class Result
        {
            public IntVec3 Origin;
            public IntVec3 Door;
            public Room Room;
            public List<IntVec3> Interior;
            public List<Pawn> People;
            public int SleepingSpots;

            public object Summary() => new { origin = Origin.ToString(), door = Door.ToString(), room = Room.ID, cells = Room.CellCount, sleepingSpots = SleepingSpots };
        }

        // Build sites a size x size ring (size-2 square inside) on heavy
        // affordance ground every housed colonist can reach, within 30 cells
        // of the first, and furnishes it as above. spots caps the sleeping
        // spots laid: negative is one per colonist, a smaller count leaves the
        // bed deficit a capacity goal then plans against. Throws when the
        // ruleset lacks the defs or the map has no room for it.
        public static Result Build(Map map, int size, int spots = -1)
        {
            var player = Faction.OfPlayerSilentFail;
            if (player == null) throw new InvalidOperationException("No player faction.");
            var people = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead && !p.Downed)
                .OrderBy(p => p.thingIDNumber).Take(8).ToList();
            if (people.Count < 1) throw new InvalidOperationException("No colonist to house.");
            var wallDef = DefDatabase<ThingDef>.GetNamedSilentFail("Wall");
            var doorDef = DefDatabase<ThingDef>.GetNamedSilentFail("Door");
            var spotDef = DefDatabase<ThingDef>.GetNamedSilentFail("SleepingSpot");
            if (wallDef == null || doorDef == null || spotDef == null)
                throw new InvalidOperationException("Wall, Door or SleepingSpot def unavailable in this ruleset.");

            var origin = FindSite(map, people, size);
            if (origin == default) throw new InvalidOperationException("No open reachable area for the fixture hut.");
            var door = new IntVec3(origin.x + size - 1, 0, origin.z + size / 2);
            var rect = new CellRect(origin.x, origin.z, size, size);
            foreach (var c in rect.Cells) {
                foreach (var t in c.GetThingList(map).Where(t => t is Plant || t.def.category == ThingCategory.Item).ToList()) t.Destroy(DestroyMode.Vanish);
                if (c == door) {
                    var d = (Building)ThingMaker.MakeThing(doorDef, ThingDefOf.WoodLog);
                    d.SetFaction(player); GenSpawn.Spawn(d, c, map);
                } else if (rect.IsOnEdge(c)) {
                    var w = (Building)ThingMaker.MakeThing(wallDef, ThingDefOf.WoodLog);
                    w.SetFaction(player); GenSpawn.Spawn(w, c, map);
                }
                map.roofGrid.SetRoof(c, RoofDefOf.RoofConstructed);
            }
            var interior = rect.ContractedBy(1).Cells.OrderBy(c => c.z).ThenBy(c => c.x).ToList();
            var width = size - 2;
            // A sleeping spot is a 1x2 footprint: one per cell of the south
            // row (rows 1-2), the overflow from the west end of row 3 (rows
            // 3-4), so none overlaps and the rows above stay free.
            var wanted = spots < 0 ? people.Count : Math.Max(0, Math.Min(spots, people.Count));
            var spotCells = interior.Take(width).Concat(interior.Skip(2 * width)).Take(wanted).ToList();
            foreach (var spotCell in spotCells) {
                var spot = ThingMaker.MakeThing(spotDef);
                spot.SetFaction(player); GenSpawn.Spawn(spot, spotCell, map, Rot4.North, WipeMode.Vanish);
                if (spot.Position != spotCell) throw new InvalidOperationException($"Sleeping spot landed at {spot.Position}, not {spotCell}.");
            }
            map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
            var room = interior[interior.Count / 2].GetRoom(map);
            if (room == null || !room.ProperRoom || room.TouchesMapEdge || room.OpenRoofCount > 0)
                throw new InvalidOperationException($"Fixture hut is not an enclosed roofed room: room={room?.ID} proper={room?.ProperRoom} edge={room?.TouchesMapEdge} openRoof={room?.OpenRoofCount} cells={room?.CellCount}");
            room.Temperature = 21f;
            var laid = map.listerBuildings.allBuildingsColonist.OfType<Building_Bed>().Count(b => b.GetRoom() == room);
            if (laid != wanted) throw new InvalidOperationException($"{laid} sleeping spots stand in the hut, not {wanted}.");
            var free = interior.Skip(interior.Count - people.Count).ToList();
            for (int i = 0; i < people.Count; i++) {
                var pawn = people[i];
                pawn.jobs?.StopAll();
                pawn.Position = free[i]; pawn.Notify_Teleported(true, true);
            }
            return new Result { Origin = origin, Door = door, Room = room, Interior = interior, People = people, SleepingSpots = laid };
        }

        // FindSite returns the south-west corner of the size x size square
        // nearest the first colonist whose cells are unfogged, in bounds,
        // clear of edifices and zones, walkable heavy-affordance terrain, and
        // hold nothing but plants and items (Build clears both: a chunk or a
        // tree is not a reason to refuse), reachable by every colonist. The
        // whole map is searched, nearest first (#674: the debug-200 start had
        // no such square within 30 cells under the old Standable test).
        // default when none exists.
        public static IntVec3 FindSite(Map map, List<Pawn> people, int size)
        {
            var anchor = people[0].Position;
            bool Open(IntVec3 cell) => cell.InBounds(map) && !cell.Fogged(map)
                && cell.GetEdifice(map) == null && cell.GetZone(map) == null
                && cell.GetTerrain(map).passability != Traversability.Impassable
                && cell.GetTerrain(map).affordances.Contains(TerrainAffordanceDefOf.Heavy)
                && cell.GetThingList(map).All(t => t is Plant || t.def.category == ThingCategory.Item);
            return map.AllCells
                .Where(c => c.x + size <= map.Size.x && c.z + size <= map.Size.z)
                .OrderBy(c => (c - anchor).LengthHorizontalSquared)
                .FirstOrDefault(c => new CellRect(c.x, c.z, size, size).Cells.All(Open)
                    && people.All(p => p.CanReach(c, PathEndMode.OnCell, Danger.Deadly)));
        }

        // DropOutside places count of def two cells east of the door,
        // unforbidden: the materials a rung costs that supply is not asked
        // to find on the tribal baseline.
        public static int DropOutside(Map map, Result hut, ThingDef def, int count)
        {
            var placed = 0;
            while (placed < count) {
                var stack = Math.Min(def.stackLimit, count - placed);
                var thing = ThingMaker.MakeThing(def); thing.stackCount = stack;
                if (!GenPlace.TryPlaceThing(thing, hut.Door + IntVec3.East * 2, map, ThingPlaceMode.Near)) throw new InvalidOperationException("No drop site for " + def.defName);
                thing.SetForbidden(false, false);
                placed += stack;
            }
            return placed;
        }

        // SpawnInside spawns a finished player building of def on the first
        // interior cell (south-west first, rows above the sleeping spots)
        // whose footprint is free and standable, facing north.
        public static Thing SpawnInside(Map map, Result hut, ThingDef def)
        {
            var player = Faction.OfPlayer;
            var sites = hut.Interior.Where(c => GenConstruct.CanPlaceBlueprintAt(def, c, Rot4.North, map).Accepted
                && GenAdj.OccupiedRect(c, Rot4.North, def.size).Cells.All(x => hut.Interior.Contains(x) && x.Standable(map) && x.GetFirstBuilding(map) == null && x.GetFirstPawn(map) == null)).ToList();
            if (sites.Count == 0) throw new InvalidOperationException("No site inside the fixture hut for " + def.defName);
            var cell = sites[0];
            var thing = ThingMaker.MakeThing(def, GenStuff.DefaultStuffFor(def));
            thing.SetFaction(player);
            GenSpawn.Spawn(thing, cell, map, Rot4.North);
            return thing;
        }
    }
}
