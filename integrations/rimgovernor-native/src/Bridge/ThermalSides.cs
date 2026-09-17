#nullable enable

using System;
using System.Diagnostics.CodeAnalysis;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    internal static class ThermalSides
    {
        internal static bool Applies([NotNullWhen(true)] ThingDef? definition)
        {
            return definition != null && (definition.thingClass == typeof(Building_Cooler)
                || definition.thingClass == typeof(Building_Vent));
        }

        internal static object? Read(Map? map, Thing thing, ThingDef? definition)
        {
            if (!Applies(definition)) return null;
            try
            {
                bool cooler = definition.thingClass == typeof(Building_Cooler);
                var cells = cooler ? new[] { thing.Position }
                    : GenAdj.OccupiedRect(thing.Position, thing.Rotation, definition.Size).ToArray();
                return new Dictionary<string, object?>
                {
                    { "kind", cooler ? "cooler" : "vent" },
                    { "readable", true },
                    { "sides", cells.SelectMany(cell => new[] {
                        Cell(map, cell + thing.Rotation.FacingCell, cooler ? "exhaust" : "front"),
                        Cell(map, cell - thing.Rotation.FacingCell, cooler ? "intake" : "back")
                    }).ToArray() },
                    { "meaning", "Native temperature-exchange cells for this definition and rotation. Current geometry, not proof of operation, room connectivity or cooling capacity; blueprint/frame sides describe the intended building. Fogged cells remain unknown." }
                };
            }
            catch (Exception error)
            {
                return new { readable = false, error = error.Message };
            }
        }

        private static object Cell(Map? map, IntVec3 cell, string side)
        {
            bool inBounds = cell.InBounds(map);
            bool? fogged = inBounds ? (bool?)cell.Fogged(map) : null;
            bool visible = inBounds && fogged == false;
            return new Dictionary<string, object?>
            {
                { "side", side }, { "position", BridgeCommon.Pos(cell) },
                { "inBounds", inBounds }, { "fogged", fogged },
                { "impassable", visible ? (object)cell.Impassable(map) : null }
            };
        }
    }
}
