#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Obs = RimGovernor.Protocol.Observations;

namespace HomeBridge.BridgeTools
{
    // Odyssey colony facts: the colony map's active game conditions,
    // the terrain that hurts or contaminates by its own def flags, lava
    // emergences, and each ancient hatch's pocket (underground) map with its
    // hackables. Every verdict is the game's own; nothing is a def-name list.
    // The section is absent without Odyssey.
    internal static class NativeOdysseyColony
    {
        private static string? Id(string? value) => value != null && ProtoBoundary.IsIdentifier(value) ? value : null;
        private static Common.Cell Cell(IntVec3 c) => new Common.Cell { X = c.x, Z = c.z };
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        internal static Obs.OdysseySection? Read(Map map)
        {
            if (!ModsConfig.OdysseyActive) return null;
            try {
                var facts = new Obs.OdysseyColonyFacts();
                foreach (var condition in map.gameConditionManager.ActiveConditions.OrderBy(c => c.uniqueID)) facts.Conditions.Add(Condition(condition));
                facts.HazardTerrain.Add(HazardTerrain(map));
                foreach (var thing in map.listerThings.AllThings.OfType<LavaEmergence>().OrderBy(t => t.thingIDNumber))
                    facts.LavaEmergences.Add(new Obs.LavaEmergenceState { ThingId = Id(thing.GetUniqueLoadID()), Position = Cell(thing.Position) });
                foreach (var hatch in map.listerThings.AllThings.OfType<AncientHatch>().Where(h => h.PocketMapExists).OrderBy(t => t.thingIDNumber))
                    facts.Sites.Add(Site(hatch));
                return new Obs.OdysseySection { Observed = facts };
            } catch (Exception ex) {
                return new Obs.OdysseySection { Unavailable = new Common.Unavailable { Reason = Common.UnavailableReason.ReadFailed, Detail = PlacementPreviewOperation.Diagnostic(ex.Message) } };
            }
        }

        private static Obs.ActiveCondition Condition(GameCondition c)
        {
            var row = new Obs.ActiveCondition { ConditionId = Id("GameCondition_" + c.uniqueID), DefName = Id(c.def.defName), ConditionClass = Id(c.GetType().FullName),
                TicksPassed = c.TicksPassed, Permanent = c.Permanent };
            if (!c.Permanent) row.TicksLeft = c.TicksLeft;
            if (c.conditionCauser != null) row.CauserId = Id(c.conditionCauser.GetUniqueLoadID());
            return row;
        }

        // The terrain hurts or contaminates by its own def flags (also the
        // map survey's hazard bit).
        internal static bool IsHazard(TerrainDef terrain) => terrain.dangerous || terrain.burnDamage > 0 || terrain.heatPerTick > 0f || terrain.toxicBuildupFactor > 0f;

        // Terrain defs whose own flags hurt or contaminate, with the cells each covers.
        private static IEnumerable<Obs.HazardTerrain> HazardTerrain(Map map)
        {
            var counts = new Dictionary<TerrainDef, uint>();
            var grid = map.terrainGrid;
            foreach (var cell in map.AllCells) {
                var terrain = grid.TerrainAt(cell);
                if (IsHazard(terrain))
                    counts[terrain] = counts.TryGetValue(terrain, out var n) ? n + 1 : 1;
            }
            foreach (var pair in counts.Where(p => ProtoBoundary.IsIdentifier(p.Key.defName)).OrderBy(p => p.Key.defName, StringComparer.Ordinal)) {
                var t = pair.Key;
                var row = new Obs.HazardTerrain { DefName = t.defName, Cells = pair.Value, Dangerous = t.dangerous, BurnDamage = t.burnDamage };
                if (Finite(t.heatPerTick)) row.HeatPerTick = t.heatPerTick;
                if (Finite(t.toxicBuildupFactor)) row.ToxicBuildupFactor = t.toxicBuildupFactor;
                yield return row;
            }
        }

        private static Obs.UndergroundSite Site(AncientHatch hatch)
        {
            var pocket = hatch.PocketMap;
            var row = new Obs.UndergroundSite { HatchId = Id(hatch.GetUniqueLoadID()), PocketMapId = pocket.uniqueID, StockpileType = Id(hatch.stockpileType.ToString()),
                ColonistsPresent = (uint)pocket.mapPawns.FreeColonistsSpawnedCount };
            if (Id(hatch.layout?.defName) is string layout) row.Layout = layout;
            foreach (var thing in pocket.listerThings.AllThings.Where(t => t.TryGetComp<CompHackable>() != null).OrderBy(t => t.thingIDNumber)) {
                var hack = thing.TryGetComp<CompHackable>();
                var state = new Obs.UndergroundHackable { ThingId = Id(thing.GetUniqueLoadID()), DefName = Id(thing.def.defName), Position = Cell(thing.Position),
                    Hacked = hack.IsHacked, LockedOut = hack.LockedOut, Autohack = hack.Autohack };
                if (Finite(hack.ProgressPercent)) state.ProgressPercent = hack.ProgressPercent;
                if (Finite(hack.defence)) state.Defence = hack.defence;
                row.Hackables.Add(state);
            }
            return row;
        }
    }
}
