using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;
using Common = RimGovernor.Contracts.Wire.Common;
using DomainCell = RimGovernor.Brain.Domain.Cell;
using DomainWire = RimGovernor.Brain.Domain.Wire;
using Obs = RimGovernor.Contracts.Wire.Observations;

namespace HomeBridge.BridgeTools
{
    internal static class NativeRoomObservationTools
    {
        // On the game thread: the typed core of the tool, shared with the in-process facade. A refused request
        // or a census that cannot be read completely throws.
        internal static Obs.RoomsSnapshot HandleListRooms(Obs.ListRoomsRequest parsed)
        {
            if (!Validate(parsed, out var invalidRequest)) throw NativeFaults.Refused(invalidRequest);
            if (!ProtoBoundary.ValidateIdentity(parsed.Scope?.ExpectedIdentity, out var map, out var context, out var error))
                throw NativeFaults.Refused(error);
            if (parsed.Region != null && (!NativeCell(parsed.Region.Minimum).InBounds(map) || !NativeCell(parsed.Region.Maximum).InBounds(map)))
                throw NativeFaults.Refused(ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Room filter rectangle must be within the current map."));
            try { return Read(map, parsed, context); }
            catch (Exception readError)
            {
                ModLog.Error("observe", "Room census read failed: " + readError);
                throw NativeFaults.Unavailable(new Common.Unavailable { Reason = Common.UnavailableReason.ReadFailed,
                    Detail = PlacementPreviewOperation.Diagnostic("Room census, geometry or contents could not be read completely: " + readError.GetType().Name + ": " + readError.Message) });
            }
        }
        // On the main thread: the room census for a validated request; a census it cannot read completely throws.
        internal static Obs.RoomsSnapshot Read(Map map, Obs.ListRoomsRequest parsed, Common.ObservationContext context)
        {
            if (map.regionGrid?.AllRooms == null || map.regionAndRoomUpdater == null || map.zoneManager == null || map.mapPawns == null)
                throw new InvalidOperationException("Room, zone or pawn census is unavailable.");
            // AllRooms is the raw cache; resolve pending region changes
            // before taking a complete physical-room census.
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            if (map.regionAndRoomUpdater.AnythingToRebuild) throw new InvalidOperationException("Native room graph remains dirty.");
            var source = map.regionGrid.AllRooms.ToList();
            if (source.Any(r => r == null) || source.Select(r => r.ID).Distinct().Count() != source.Count)
                throw new InvalidOperationException("Null or duplicate native room census entry.");
            var physical = source.Where(HasPhysicalRegions).ToList();
            if (physical.Any(r => r.Map != map)) throw new InvalidOperationException("Foreign native room census entry.");
            var pawns = map.mapPawns.AllPawnsSpawned.ToList();
            var pawnRooms = new Dictionary<Room, List<Pawn>>();
            foreach (var pawn in pawns)
            {
                if (pawn == null || !pawn.Spawned || pawn.Map != map) throw new InvalidOperationException();
                var room = pawn.Position.GetRoom(map);
                if (room == null) continue; // Impassable/unregioned cells have no room.
                if (!pawnRooms.TryGetValue(room, out var list)) pawnRooms.Add(room, list = new List<Pawn>());
                list.Add(pawn);
            }
            var rows = new List<Obs.RoomState>();
            foreach (var room in physical.OrderBy(r => r.ID))
            {
                if (!Selected(parsed, Id(room.ID), room.PsychologicallyOutdoors, room.IsDoorway)) continue;
                var cells = new List<IntVec3>();
                foreach (var cell in room.Cells)
                {
                    if (!cell.InBounds(map)) throw new InvalidOperationException();
                    cells.Add(cell);
                }
                if (cells.Count == 0 || cells.Count != room.CellCount || cells.Distinct().Count() != cells.Count) throw new InvalidOperationException("Incomplete room footprint.");
                if (parsed.Region != null && !cells.Any(c => Inside(parsed.Region, c))) continue;
                rows.Add(Project(room, map, cells, pawnRooms.TryGetValue(room, out var members) ? members : new List<Pawn>(), parsed));
            }
            return new Obs.RoomsSnapshot { Context = context, Rooms = [.. rows] };
        }
        internal static bool Validate(Obs.ListRoomsRequest request, out Common.Failure failure)
        {
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Expected identity, unique room IDs, valid rectangle required.");
            return request?.Scope?.ExpectedIdentity != null && request.RoomIds.Count <= 256
                && request.RoomIds.All(ProtoBoundary.IsIdentifier) && request.RoomIds.Distinct(StringComparer.Ordinal).Count() == request.RoomIds.Count
                && (request.Region == null || request.Region.Minimum is { } lo && request.Region.Maximum is { } hi && CellPresent(lo) && CellPresent(hi)
                    && lo.X <= hi.X && lo.Z <= hi.Z);
        }
        internal static bool Selected(Obs.ListRoomsRequest request, string id, bool psychologicallyOutdoors, bool doorway)
            => !psychologicallyOutdoors && !doorway && (request.RoomIds.Count == 0 || request.RoomIds.Contains(id));
        internal static bool HasPhysicalRegions(Room room)
        {
            if (!room.Dereferenced) return true;
            // Native AllRooms can retain an empty district with no regions.
            // It contributes no physical room, but inconsistent geometry fails the read.
            if (room.CellCount != 0 || room.Cells.Any()) throw new InvalidOperationException("Regionless room has physical cells.");
            return false;
        }
        internal static bool Inside(Obs.Rectangle rectangle, IntVec3 cell) => cell.x >= (rectangle.Minimum?.X).GetValueOrDefault() && cell.x <= (rectangle.Maximum?.X).GetValueOrDefault() && cell.z >= (rectangle.Minimum?.Z).GetValueOrDefault() && cell.z <= (rectangle.Maximum?.Z).GetValueOrDefault();

        private static Obs.RoomState Project(Room room, Map map, List<IntVec3> cells, List<Pawn> pawns, Obs.ListRoomsRequest request)
        {
            // Room owns reusable region/thing buffers. Copy before any stat, label or
            // bed projection can read those getters again. Never enumerate Zone.Cells.
            var things = room.ContainedAndAdjacentThings.ToList();
            if (things.Any(t => t == null || !t.Spawned || t.Map != map) || things.Distinct().Count() != things.Count) throw new InvalidOperationException("Invalid or duplicate room thing membership.");
            var roof = room.OpenRoofCount;
            if (roof < 0 || roof > cells.Count) throw new InvalidOperationException("Invalid room open roof count.");
            var role = room.Role ?? throw new InvalidOperationException("Native room has no role.");
            var held = cells.Where(c => !c.Fogged(map)).ToList();
            var buildings = things.OfType<Building>().Where(t => request.IncludeBoundary || room.ContainsCell(t.Position)).ToList();
            var beds = buildings.OfType<Building_Bed>().ToList();
            var stats = new List<Obs.RoomStat>();
            foreach (var definition in new[] { RoomStatDefOf.Cleanliness, RoomStatDefOf.Wealth, RoomStatDefOf.Space, RoomStatDefOf.Beauty, RoomStatDefOf.Impressiveness })
            {
                if (definition == null) throw new InvalidOperationException("Required room stat definition is unavailable.");
                stats.Add(new Obs.RoomStat { DefName = Name(definition.defName), Value = Finite(room.GetStat(definition)) });
            }
            // Doors in the boundary: the door cell, the cell across it
            // from this room, and whether that far side is an outdoor room. A far side
            // with no room (map edge, impassable) or this room is not outdoors.
            var doors = new List<Obs.RoomDoor>();
            foreach (var door in things.OfType<Building_Door>().OrderBy(d => d.Position.z).ThenBy(d => d.Position.x))
            {
                foreach (var offset in GenAdj.CardinalDirections)
                {
                    var inside = door.Position + offset;
                    if (!inside.InBounds(map) || inside.GetRoom(map) != room) continue;
                    var outside = door.Position - offset;
                    var far = outside.InBounds(map) ? outside.GetRoom(map) : null;
                    doors.Add(new Obs.RoomDoor { Cell = Cell(door.Position), Outside = Cell(outside),
                        Outdoors = far != null && far != room && far.PsychologicallyOutdoors,
                        PlayerOwned = door.Faction == Faction.OfPlayerSilentFail,
                        Open = door.Open, HoldOpen = door.HoldOpen, BlockedOpen = door.BlockedOpenMomentary,
                        Forbidden = door.IsForbidden(Faction.OfPlayerSilentFail) });
                    break;
                }
            }
            var contents = buildings.GroupBy(t => Name(t.def.defName)).OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new Obs.RoomContent { DefName = g.Key, Units = g.LongCount() }).ToList();
            var id = Id(room.ID);
            var roleName = Name(role.defName);
            return new Obs.RoomState
            {
                Id = id,
                Role = DomainWire.TryParse<Obs.RoomRole>(roleName, out var parsed) ? parsed : Obs.RoomRole.Unrecognized,
                ProperRoom = room.ProperRoom,
                Doorway = room.IsDoorway,
                Outdoors = room.UsesOutdoorTemperature,
                PsychologicallyOutdoors = room.PsychologicallyOutdoors,
                TouchesMapEdge = room.TouchesMapEdge,
                Fogged = room.Fogged,
                TemperatureC = Finite(room.Temperature),
                OpenRoofCount = (uint)roof,
                CellCount = (uint)cells.Count,
                Extents = new Obs.CellExtents { Minimum = new DomainCell(cells.Min(c => c.x), cells.Min(c => c.z)), Maximum = new DomainCell(cells.Max(c => c.x), cells.Max(c => c.z)) },
                Center = Cell(Center(cells)),
                Stats = [.. stats],
                Pawns = [.. pawns.OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal).Select(p => NativeRef.Thing(p))],
                Beds = [.. beds.Select(NativeBuildingObservationTools.Ref)],
                Contents = [.. contents],
                Doors = [.. doors],
                GridRoom = (held.Count > 0 ? held : cells).Min(c => c.z * map.Size.x + c.x),
                Burning = things.Any(t => t is Fire),
                TemperatureControl = TemperatureControl(things),
                PerishableContents = Perishable(room, things),
            };
        }
        private static bool TemperatureControl(List<Thing> things) => things.Any(t => t is Building_TempControl || t.TryGetComp<CompHeatPusher>() != null);
        private static bool Perishable(Room room, List<Thing> things) => things.Any(t => room.ContainsCell(t.Position) && t.TryGetComp<CompRottable>() != null);
        internal static IntVec3 Center(IReadOnlyList<IntVec3> cells)
        {
            if (cells.Count == 0) throw new InvalidOperationException();
            var x = (long)Math.Round(cells.Average(c => (double)c.x), MidpointRounding.AwayFromZero);
            var z = (long)Math.Round(cells.Average(c => (double)c.z), MidpointRounding.AwayFromZero);
            return cells.OrderBy(c => ((long)c.x - x) * ((long)c.x - x) + ((long)c.z - z) * ((long)c.z - z)).ThenBy(c => c.z).ThenBy(c => c.x).First();
        }
        internal static double Finite(double value) => double.IsNaN(value) || double.IsInfinity(value) ? throw new InvalidOperationException("Nonfinite native room fact.") : value;
        private static bool CellPresent(Common.Cell? cell) => cell != null && cell.X >= 0 && cell.Z >= 0;
        private static IntVec3 NativeCell(Common.Cell? cell) => new IntVec3((cell?.X).GetValueOrDefault(), 0, (cell?.Z).GetValueOrDefault());
        private static DomainCell Cell(IntVec3 cell) => new DomainCell(cell.x, cell.z);
        private static string Id(int id) => id >= 0 ? id.ToString(CultureInfo.InvariantCulture) : throw new InvalidOperationException("Invalid room ID.");
        private static string Name(string value) => ProtoBoundary.IsIdentifier(value) ? value : throw new InvalidOperationException("Invalid native room identifier.");
    }
}
