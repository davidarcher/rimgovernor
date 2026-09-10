using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace HomeBridge.BridgeTools
{
    internal static class ComfortFacts
    {
        internal static object Read(Map map)
        {
            var people = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead && !p.Downed
                && p.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation)).ToList();
            var buildings = map.listerBuildings.allBuildingsColonist.ToList();
            bool Safe(Pawn p, Building b) => !b.IsForbidden(p) && !b.IsBurning() && b.IsSociallyProper(p)
                && p.CanReach(b, PathEndMode.OnCell, Danger.None)
                && (p.playerSettings?.AreaRestrictionInPawnCurrentMap == null
                    || p.playerSettings.AreaRestrictionInPawnCurrentMap[b.Position]);
            bool Indoors(Building b) => b.GetRoom() != null && b.GetRoom().ProperRoom
                && !b.GetRoom().PsychologicallyOutdoors && b.OccupiedRect().All(c => c.Roofed(map));
            var seats = buildings.Where(b => b.def.building.isSittable && Indoors(b)
                && b.OccupiedRect().Any(c => GenAdj.CardinalDirections.Any(d => (c+d).InBounds(map)
                    && (c+d).GetEdifice(map)?.def.surfaceType == SurfaceType.Eat))).ToList();
            var play = buildings.Where(b => b.def.building.joyKind != null && !b.IsBurning()
                && (b.TryGetComp<CompPowerTrader>() == null || b.TryGetComp<CompPowerTrader>().PowerOn)).ToList();
            return new {
                people = people.Select(p => p.GetUniqueLoadID()).ToList(),
                surfaces = buildings.Where(b => b.def.surfaceType == SurfaceType.Eat && Indoors(b)).Select(b => new {
                    id = b.GetUniqueLoadID(),
                    adjacent = b.OccupiedRect().SelectMany(c => GenAdj.CardinalDirections.Select(d => c+d)).Distinct()
                        .Where(c => c.InBounds(map) && c.Standable(map) && c.GetEdifice(map) == null)
                        .Select(c => new { x = c.x, z = c.z }).ToList()
                }).ToList(),
                dining = seats.Select(b => new { id = b.GetUniqueLoadID(),
                    accessibleTo = people.Where(p => Safe(p, b)).Select(p => p.GetUniqueLoadID()).ToList(),
                    users = people.Where(p => p.CurJob?.def == JobDefOf.Ingest && b.OccupiedRect().Contains(p.Position))
                        .Select(p => p.GetUniqueLoadID()).ToList()
                }).ToList(),
                recreation = play.Select(b => new { id = b.GetUniqueLoadID(), kind = b.def.building.joyKind.defName,
                    accessibleTo = people.Where(p => !b.IsForbidden(p) && b.IsSociallyProper(p)
                        && p.CanReach(b, PathEndMode.Touch, Danger.None)
                        && (p.playerSettings?.AreaRestrictionInPawnCurrentMap == null || p.playerSettings.AreaRestrictionInPawnCurrentMap[b.Position]))
                        .Select(p => p.GetUniqueLoadID()).ToList(),
                    users = people.Where(p => p.CurJob?.def.joyKind != null && p.CurJob.targetA.Thing == b)
                        .Select(p => p.GetUniqueLoadID()).ToList()
                }).ToList()
            };
        }
    }
}
