#nullable enable
using System;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;
using Obs = RimGovernor.Protocol.Observations;
using static HomeBridge.BridgeTools.NativePawnObservationTools;

namespace HomeBridge.BridgeTools
{
    internal static class ComfortFacts
    {
        internal static bool UsesWatchCells(ThingDef definition) => DefDatabase<JoyGiverDef>.AllDefsListForReading
            .Any(d => d.thingDefs?.Contains(definition) == true && d.giverClass != null
                && typeof(JoyGiver_WatchBuilding).IsAssignableFrom(d.giverClass));

        private static bool WatchCellAccessible(Pawn pawn, IntVec3 cell) => !cell.IsForbidden(pawn)
            && pawn.CanReach(cell, PathEndMode.OnCell, Danger.None);

        internal static bool? WatchCellsAccessible(Map map, BuildableDef definition, IntVec3 center, Rot4 rotation)
        {
            if (!(definition is ThingDef thing) || !UsesWatchCells(thing)) return null;
            // CalculateWatchCells supplies native stand distance, same-room and
            // line-of-sight rules without spawning a hypothetical building.
            var cells = WatchBuildingUtility.CalculateWatchCells(thing, center, rotation, map).Take(4097).ToList();
            var people = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead && !p.Downed
                && p.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation)).ToList();
            if (cells.Count > 4096 || people.Count > 256) return null;
            return people.Count > 0 && people.All(p => cells.Any(c => WatchCellAccessible(p, c)));
        }

        internal static object Read(Map map)
        {
            var v = ReadProtocol(map);
            return new {
                people = v.People.ToList(),
                surfaces = v.Surfaces.Select(s => new { id = s.Id, roomId = s.HasRoomId ? s.RoomId : null,
                    adjacent = s.Adjacent.Select(c => new { x = c.X, z = c.Z }).ToList() }).ToList(),
                dining = v.Dining.Select(f => new { id = f.Id, roomId = f.HasRoomId ? f.RoomId : null,
                    accessibleTo = f.AccessibleTo.ToList(), users = f.Users.ToList() }).ToList(),
                recreation = v.Recreation.Select(f => new { id = f.Id, kind = f.Kind, roomId = f.HasRoomId ? f.RoomId : null,
                    accessibleTo = f.AccessibleTo.ToList(), users = f.Users.ToList() }).ToList()
            };
        }

        internal static Obs.ComfortFacts ReadProtocol(Map map)
        {
            var people = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead && !p.Downed
                && p.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation))
                .OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal).ToList();
            var buildings = map.listerBuildings.allBuildingsColonist
                .OrderBy(b => b.GetUniqueLoadID(), StringComparer.Ordinal).ToList();
            bool Safe(Pawn p, Building b) => !b.IsForbidden(p) && !b.IsBurning() && b.IsSociallyProper(p)
                && p.CanReach(b, PathEndMode.OnCell, Danger.None)
                && (p.playerSettings?.AreaRestrictionInPawnCurrentMap == null
                    || p.playerSettings.AreaRestrictionInPawnCurrentMap[b.Position]);
            bool Indoors(Building b) => b.GetRoom() != null && b.GetRoom().ProperRoom
                && !b.GetRoom().PsychologicallyOutdoors && b.OccupiedRect().All(c => c.Roofed(map));
            // The hosting room is the same Room.ID the typed room census reports, so
            // the controller can join a facility to that room's native role. Only a
            // proper indoor room is named; an outdoor facility has no host.
            string? HostRoom(Building b) => Indoors(b) ? Id(b.GetRoom().ID.ToString(System.Globalization.CultureInfo.InvariantCulture)) : null;
            var seats = buildings.Where(b => b.def.building.isSittable && Indoors(b)
                && b.OccupiedRect().Any(c => GenAdj.CardinalDirections.Any(d => (c+d).InBounds(map)
                    && (c+d).GetEdifice(map)?.def.surfaceType == SurfaceType.Eat))).ToList();
            var play = buildings.Where(b => b.def.building.joyKind != null && !b.IsBurning()
                && (b.TryGetComp<CompPowerTrader>() == null || b.TryGetComp<CompPowerTrader>().PowerOn)).ToList();
            var surfaces = buildings.Where(b => b.def.surfaceType == SurfaceType.Eat && Indoors(b)).ToList();
            if (people.Count > 256 || seats.Count > 256 || play.Count > 256 || surfaces.Count > 256)
                throw new InvalidOperationException("Comfort census exceeds its complete-read bound.");
            var result = new Obs.ComfortFacts { Completeness = Complete(people.Count) };
            result.People.Add(people.Select(p => Id(p.GetUniqueLoadID())));
            foreach (var b in surfaces) {
                var adjacent = b.OccupiedRect().SelectMany(c => GenAdj.CardinalDirections.Select(d => c+d)).Distinct()
                    .Where(c => c.InBounds(map) && c.Standable(map) && c.GetEdifice(map) == null)
                    .OrderBy(c => c.z).ThenBy(c => c.x).ToList();
                if (adjacent.Count > 4096) throw new InvalidOperationException("Dining adjacency exceeds bound.");
                var row = new Obs.ComfortSurface { Id = Id(b.GetUniqueLoadID()) };
                if (HostRoom(b) is string surfaceRoom) row.RoomId = surfaceRoom;
                row.Adjacent.Add(adjacent.Select(Cell)); result.Surfaces.Add(row);
            }
            foreach (var b in seats) {
                var row = new Obs.ComfortFacility { Id = Id(b.GetUniqueLoadID()) };
                if (HostRoom(b) is string seatRoom) row.RoomId = seatRoom;
                row.AccessibleTo.Add(people.Where(p => Safe(p, b)).Select(p => Id(p.GetUniqueLoadID())));
                row.Users.Add(people.Where(p => p.CurJob?.def == JobDefOf.Ingest && b.OccupiedRect().Contains(p.Position))
                    .Select(p => Id(p.GetUniqueLoadID())));
                result.Dining.Add(row);
            }
            foreach (var b in play) {
                var watchCells = UsesWatchCells(b.def)
                    ? WatchBuildingUtility.CalculateWatchCells(b.def, b.Position, b.Rotation, map).Take(4097).ToList() : null;
                if (watchCells?.Count > 4096) throw new InvalidOperationException("Recreation watch geometry exceeds bound.");
                var row = new Obs.ComfortFacility { Id = Id(b.GetUniqueLoadID()), Kind = Id(b.def.building.joyKind.defName) };
                if (HostRoom(b) is string playRoom) row.RoomId = playRoom;
                row.AccessibleTo.Add(people.Where(p => !b.IsForbidden(p) && b.IsSociallyProper(p)
                    && p.CanReach(b, PathEndMode.Touch, Danger.None)
                    && (watchCells == null || watchCells.Any(c => WatchCellAccessible(p, c)))
                    && (p.playerSettings?.AreaRestrictionInPawnCurrentMap == null || p.playerSettings.AreaRestrictionInPawnCurrentMap[b.Position]))
                    .Select(p => Id(p.GetUniqueLoadID())));
                row.Users.Add(people.Where(p => p.CurJob?.def.joyKind != null && p.CurJob.targetA.Thing == b)
                    .Select(p => Id(p.GetUniqueLoadID())));
                result.Recreation.Add(row);
            }
            return result;
        }
    }
}
