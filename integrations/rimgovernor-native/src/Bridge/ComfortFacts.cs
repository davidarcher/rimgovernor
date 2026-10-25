using System;
using System.Linq;
using System.Collections.Generic;
using PawnId = RimGovernor.Brain.Domain.PawnId;
using DomainCell = RimGovernor.Brain.Domain.Cell;
using RimWorld;
using Verse;
using Verse.AI;
using Obs = RimGovernor.Contracts.Wire.Observations;
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
            var cells = WatchBuildingUtility.CalculateWatchCells(thing, center, rotation, map).ToList();
            var people = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead && !p.Downed
                && p.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation)).ToList();
            return people.Count > 0 && people.All(p => cells.Any(c => WatchCellAccessible(p, c)));
        }

        // The comfort census, read whole: a failed read throws and the upkeep section fails with it (#3771).
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
            // The hosting room is the same Room.ID the typed room census reports, so the controller can join a
            // facility to that room's native role. Only a proper indoor room hosts; an outdoor facility has none.
            string HostRoom(Building b) => Id(b.GetRoom().ID.ToString(System.Globalization.CultureInfo.InvariantCulture));
            RimGovernor.Brain.Domain.EquatableArray<PawnId> Pawns(IEnumerable<Pawn> pawns) => [.. pawns.Select(p => new PawnId(Id(p.GetUniqueLoadID())))];
            var seats = buildings.Where(b => b.def.building.isSittable && Indoors(b)
                && b.OccupiedRect().Any(c => GenAdj.CardinalDirections.Any(d => (c+d).InBounds(map)
                    && (c+d).GetEdifice(map)?.def.surfaceType == SurfaceType.Eat))).ToList();
            var play = buildings.Where(b => b.def.building.joyKind != null && !b.IsBurning()
                && (b.TryGetComp<CompPowerTrader>() == null || b.TryGetComp<CompPowerTrader>().PowerOn)).ToList();
            var surfaces = buildings.Where(b => b.def.surfaceType == SurfaceType.Eat && Indoors(b)).Select(b => {
                var adjacent = b.OccupiedRect().SelectMany(c => GenAdj.CardinalDirections.Select(d => c+d)).Distinct()
                    .Where(c => c.InBounds(map) && c.Standable(map) && c.GetEdifice(map) == null)
                    .OrderBy(c => c.z).ThenBy(c => c.x);
                return new Obs.ComfortSurface(HostRoom(b), [.. adjacent.Select(c => new DomainCell(c.x, c.z))]);
            });
            var dining = seats.Select(b => new Obs.DiningSeat(Id(b.GetUniqueLoadID()), HostRoom(b),
                Pawns(people.Where(p => Safe(p, b))),
                Pawns(people.Where(p => p.CurJob?.def == JobDefOf.Ingest && b.OccupiedRect().Contains(p.Position)))));
            var recreation = play.Select(b => {
                var watchCells = UsesWatchCells(b.def)
                    ? WatchBuildingUtility.CalculateWatchCells(b.def, b.Position, b.Rotation, map).ToList() : null;
                Obs.ComfortHost host = Indoors(b) ? new Obs.ComfortHost.Indoors(HostRoom(b)) : new Obs.ComfortHost.Outdoors();
                return new Obs.RecreationSource(Id(b.GetUniqueLoadID()), Id(b.def.building.joyKind.defName), host,
                    Pawns(people.Where(p => !b.IsForbidden(p) && b.IsSociallyProper(p)
                        && p.CanReach(b, PathEndMode.Touch, Danger.None)
                        && (watchCells == null || watchCells.Any(c => WatchCellAccessible(p, c)))
                        && (p.playerSettings?.AreaRestrictionInPawnCurrentMap == null || p.playerSettings.AreaRestrictionInPawnCurrentMap[b.Position]))),
                    Pawns(people.Where(p => p.CurJob?.def.joyKind != null && p.CurJob.targetA.Thing == b)));
            });
            return new Obs.ComfortFacts {
                People = Pawns(people), Surfaces = [.. surfaces], Dining = [.. dining], Recreation = [.. recreation],
                Joy = ReadJoy(people, play)
            };
        }

        // The whole joy boredom matrix.
        private static Obs.RecreationCensus ReadJoy(List<Pawn> people, List<Building> play)
        {
            var kinds = play.Select(b => b.def.building.joyKind).Distinct()
                .OrderBy(k => k.defName, StringComparer.Ordinal).ToList();
            var pawns = people.Where(p => p.needs?.joy != null).Select(p => new Obs.JoyTolerance(
                new PawnId(Id(p.GetUniqueLoadID())), [.. kinds.Select(k => p.needs.joy.tolerances.BoredOf(k))]));
            return new Obs.RecreationCensus([.. kinds.Select(k => Id(k.defName))], [.. pawns]);
        }
    }
}