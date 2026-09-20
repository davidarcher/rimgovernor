#nullable enable
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Obs = RimGovernor.Protocol.Observations;

namespace HomeBridge.BridgeTools
{
    // Read roof-connected floor independently of ownership and Room merging at
    // a breached wall. Ambiguous or large footprints are deliberately unavailable.
    internal static class NativeShrineHeat
    {
        internal static Obs.ShrineHeat? Read(Map map, List<Building_AncientCryptosleepCasket> caskets)
        {
            if (caskets.Count == 0 || caskets.Any(c => c.Position.Fogged(map))) return null;
            bool Floor(IntVec3 c) => c.InBounds(map) && !c.Fogged(map) && c.Roofed(map)
                && (!(c.GetEdifice(map) is Building b) || b is Building_AncientCryptosleepCasket
                    || !(b is Building_Door) && b.def.passability != Traversability.Impassable);
            var start = caskets[0].InteractionCell;
            if (!Floor(start)) return null;
            var inside = new HashSet<IntVec3> { start };
            var pending = new Queue<IntVec3>(); pending.Enqueue(start);
            while (pending.Count > 0)
            {
                var cell = pending.Dequeue();
                foreach (var direction in GenAdj.CardinalDirections)
                {
                    var next = cell + direction;
                    if (!Floor(next) || !inside.Add(next)) continue;
                    if (inside.Count > 256) return null;
                    pending.Enqueue(next);
                }
            }
            if (caskets.Any(c => !inside.Contains(c.InteractionCell))) return null;
            var gaps = inside.Where(c => GenAdj.CardinalDirections.Any(d =>
                (c + d).InBounds(map) && !(c + d).Roofed(map) && (c + d).Walkable(map))).ToList();
            // A single breached wall is repairable with one door. An open roof,
            // broad opening or porch needs player layout, not guessed walls.
            if (gaps.Count > 1) return null;
            foreach (var gap in gaps) inside.Remove(gap);
            if (inside.Count == 0) return null;
            var border = inside.SelectMany(c => GenAdj.CardinalDirections.Select(d => c + d))
                .Where(c => !inside.Contains(c)).Distinct().ToList();
            var doors = border.Where(c => c.InBounds(map) && c.GetEdifice(map) is Building_Door).ToList();
            if (border.Any(c => !c.InBounds(map) || !gaps.Contains(c) && !(c.GetEdifice(map) is Building b
                && (b is Building_Door || b.def.passability == Traversability.Impassable)))) return null;
            var room = caskets[0].GetRoom();
            var result = new Obs.ShrineHeat {
                TemperatureCelsius = caskets[0].AmbientTemperature,
                OutdoorTemperatureCelsius = map.mapTemperature.OutdoorTemp,
                CellCount = (uint)inside.Count, BoundaryCells = (uint)border.Count,
                Enclosed = gaps.Count == 0 && room != null && !room.PsychologicallyOutdoors && room.OpenRoofCount == 0,
                ColonistsInside = map.mapPawns.FreeColonistsSpawned.Any(p => inside.Contains(p.Position))
            };
            foreach (var gap in gaps) result.DoorSites.Add(Cell(gap));
            foreach (var door in doors.OrderBy(c => c.x).ThenBy(c => c.z))
                foreach (var direction in GenAdj.CardinalDirections) {
                    var exit = door + direction;
                    if (!exit.InBounds(map) || inside.Contains(exit) || !exit.Walkable(map) || exit.GetEdifice(map) is Building_Door) continue;
                    result.FiringCells.Add(Cell(door)); result.RetreatCells.Add(Cell(exit)); break;
                }
            foreach (var cell in inside.OrderBy(c => c.x).ThenBy(c => c.z))
            {
                var heater = cell.GetThingList(map).OfType<Building>().FirstOrDefault(b => b.def.defName == "Heater" && b.Faction == Faction.OfPlayer);
                if (heater != null)
                    result.Heaters.Add(new Obs.EntityRef { Id = heater.GetUniqueLoadID(), Position = Cell(cell), DefName = heater.def.defName });
                else if (cell.Walkable(map) && cell.GetThingList(map).All(t => t.def.defName == "PowerConduit" || !(t is Building) && !(t is Blueprint) && !(t is Frame))
                    && !caskets.Any(c => c.InteractionCell == cell)) result.HeaterSites.Add(Cell(cell));
            }
            return result;
        }

        private static Common.Cell Cell(IntVec3 c) => new Common.Cell { X = c.x, Z = c.z };
    }
}
