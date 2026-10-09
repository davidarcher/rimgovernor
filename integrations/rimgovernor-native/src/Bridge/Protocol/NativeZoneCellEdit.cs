#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    /// <summary>
    /// The zone intent's cells shape. It never takes a cell away from another
    /// zone: add is restricted to cells that
    /// are genuinely free (no grid owner, and no zone's own cell list holds
    /// them either -- the same orphan-safe test NativeZoneCreation.Prepare
    /// uses for a fresh zone). Remove only ever touches this zone's own,
    /// already-consistent cells. Both directions refuse, rather than silently
    /// discard, a result that would not be contiguous. Cells already in (add)
    /// or already out of (remove) the zone apply again.
    /// </summary>
    internal sealed class ZoneCellsActionHandler : IActionHandler
    {
        private const string Kind = "Zone cell edit";
        private static string At(IntVec3 c) => "(" + c.x + ", " + c.z + ")";

        private static bool Valid(Operations.ZoneIntent? intent)
        {
            if (NativeZoneIntent.Id(intent) == null || intent!.HasKind || (intent.AddCells == null) == (intent.RemoveCells == null)) return false;
            var cells = NativeZoneIntent.EditCells(intent)?.ExplicitCells?.Cells;
            if (cells == null || cells.Count == 0) return false;
            if (!cells.All(c => c.HasX && c.HasZ && c.X >= 0 && c.Z >= 0)) return false;
            return cells.Select(c => Tuple.Create(c.X, c.Z)).Distinct().Count() == cells.Count;
        }

        private static bool IsFreeGround(IntVec3 c, Map map)
        {
            try
            {
                if (!c.InBounds(map)) return false;
                if (!Designator_ZoneAdd.IsZoneableCell(c, map).Accepted) return false;
                if (map.zoneManager.ZoneAt(c) != null) return false;
                if (map.zoneManager.AllZones.Any(z => z.Cells.Contains(c))) return false;
                return true;
            }
            catch { return false; }
        }

        private static Zone? Resolve(Operations.ZoneIntent? intent, Map map) =>
            Valid(intent) ? RefIndex.Zone(map, intent!.Zone.Id) is Zone z && z.Cells.Count > 0 ? z : null : null;

        private static IntVec3[] Requested(Operations.ZoneIntent intent) =>
            NativeZoneIntent.EditCells(intent)!.ExplicitCells.Cells.Select(c => new IntVec3(c.X, 0, c.Z)).ToArray();

        private static bool Standing(Operations.ZoneIntent? intent, Zone? zone) =>
            zone != null && (NativeZoneIntent.Adding(intent!) ? Requested(intent!).All(zone.Cells.Contains) : !Requested(intent!).Any(zone.Cells.Contains));

        // The apply-time precondition list for expand/shrink
        // (action-contracts.md): the zone, its consistency, every requested
        // cell and the resulting shape, one rule at a time, so a refusal
        // names the cell that moved.
        private static ApplyPreconditions Rules(Operations.ZoneIntent? intent, Zone? zone, Map map)
        {
            var rules = new ApplyPreconditions(Kind)
                .Require(() => Valid(intent), "an edit requires a zone id, add or remove cells, and distinct explicit cells")
                .Present(() => zone != null, "the exact zone no longer exists on this map");
            if (!rules.Holds) return rules;
            var candidate = zone!;
            var slotGroup = (candidate as Zone_Stockpile)?.slotGroup;
            var requested = Requested(intent!);
            var adding = NativeZoneIntent.Adding(intent!);
            // Whole-zone consistency first: neither AddCell/RemoveCell may run
            // against a zone that already has a phantom cell or a haul grid
            // pointing away from it.
            rules.Require(() => candidate.Cells.All(c => map.zoneManager.ZoneAt(c) == candidate), "the zone holds a phantom cell the zone grid does not map to it")
                .Require(() => slotGroup == null || candidate.Cells.All(c => map.haulDestinationManager?.SlotGroupAt(c) == slotGroup), "the stockpile's haul grid no longer matches its cells");
            foreach (var cell in requested)
            {
                var c = cell;
                if (adding)
                    rules.Require(() => candidate.Cells.Contains(c) || IsFreeGround(c, map), "cell " + At(c) + " is not free zoneable ground")
                        .Require(() => candidate.Cells.Contains(c) || !(candidate is Zone_Fishing) || NativeZoneCreation.FishableCell(c, map, candidate.Cells[0].GetWaterBody(map)),
                            "cell " + At(c) + " is not fishable water in the zone's water body")
                        .Require(() => candidate.Cells.Contains(c) || slotGroup == null || map.haulDestinationManager?.SlotGroupAt(c) == null, "cell " + At(c) + " already belongs to a storage group");
                else
                    rules.Require(() => !candidate.Cells.Contains(c) || map.zoneManager.ZoneAt(c) == candidate, "cell " + At(c) + " is not mapped to the zone on the zone grid")
                        .Require(() => !candidate.Cells.Contains(c) || slotGroup == null || map.haulDestinationManager?.SlotGroupAt(c) == slotGroup, "cell " + At(c) + " is not mapped to the stockpile's storage group");
            }
            return rules;
        }

        public Common.Failure? Validate(Operations.Action action, Common.ObservationContext context)
        {
            var map = ProtoBoundary.LoadedMap(context);
            var zone = Resolve(action.Zone, map);
            if (Standing(action.Zone, zone)) return null;
            var rules = Rules(action.Zone, zone, map);
            return rules.Holds ? null : rules.Failure();
        }

        public Receipts.EffectEvidence Apply(Operations.Action action, Common.ObservationContext context)
        {
            var intent = action.Zone;
            var map = ProtoBoundary.LoadedMap(context);
            var zone = Resolve(intent, map);
            if (!Standing(intent, zone))
            {
                var rules = Rules(intent, zone, map);
                if (!rules.Holds) throw new InvalidOperationException("Zone cell edit prerequisites changed before apply: " + rules.Reason);
                foreach (var c in Requested(intent))
                {
                    if (NativeZoneIntent.Adding(intent!) && !zone!.Cells.Contains(c)) zone.AddCell(c);
                    else if (!NativeZoneIntent.Adding(intent) && zone!.Cells.Contains(c)) zone.RemoveCell(c);
                }
                // Removing a zone's last cell deregisters it (Zone.RemoveCell).
                if (map.zoneManager.AllZones.Contains(zone!) && !Standing(intent, zone)) throw new InvalidOperationException("Native zone cell readback did not apply.");
            }
            return NativeZoneCreation.Evidence(intent.Zone.Id, zone, map);
        }
    }
}
