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
    /// ZoneCellsIntent on Actions/Apply. Unlike the legacy op=add, this never
    /// takes a cell away from another zone: add is restricted to cells that
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

        private static bool Valid(Operations.ZoneCellsIntent? intent)
        {
            if (intent == null || !intent.HasZoneId || !ProtoBoundary.IsIdentifier(intent.ZoneId)) return false;
            if (!intent.HasEdit || (intent.Edit != Operations.CellEdit.Add && intent.Edit != Operations.CellEdit.Remove)) return false;
            var cells = intent.Cells?.ExplicitCells?.Cells;
            if (cells == null || cells.Count == 0) return false;
            if (!cells.All(c => c.HasX && c.HasZ && c.X >= 0 && c.Z >= 0)) return false;
            return cells.Select(c => Tuple.Create(c.X, c.Z)).Distinct().Count() == cells.Count;
        }

        // Same BFS NativeZoneCreation.Prepare uses: reached from the first
        // cell over cardinal neighbours within the set.
        private static bool Contiguous(HashSet<IntVec3> set)
        {
            if (set.Count <= 1) return true;
            var first = set.First();
            var reached = new HashSet<IntVec3> { first };
            var queue = new Queue<IntVec3>();
            queue.Enqueue(first);
            while (queue.Count > 0)
            {
                var c = queue.Dequeue();
                foreach (var offset in GenAdj.CardinalDirections)
                {
                    var next = c + offset;
                    if (set.Contains(next) && reached.Add(next)) queue.Enqueue(next);
                }
            }
            return reached.Count == set.Count;
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

        private static Zone? Resolve(Operations.ZoneCellsIntent? intent, Map map) =>
            Valid(intent) ? RefIndex.Zone(map, intent!.ZoneId) is Zone z && z.Cells.Count > 0 ? z : null : null;

        private static IntVec3[] Requested(Operations.ZoneCellsIntent intent) =>
            intent.Cells.ExplicitCells.Cells.Select(c => new IntVec3(c.X, 0, c.Z)).ToArray();

        private static bool Standing(Operations.ZoneCellsIntent? intent, Zone? zone) =>
            zone != null && (intent!.Edit == Operations.CellEdit.Add ? Requested(intent).All(zone.Cells.Contains) : !Requested(intent).Any(zone.Cells.Contains));

        // The apply-time precondition list for expand/shrink
        // (action-contracts.md): the zone, its consistency, every requested
        // cell and the resulting shape, one rule at a time, so a refusal
        // names the cell that moved.
        private static ApplyPreconditions Rules(Operations.ZoneCellsIntent? intent, Zone? zone, Map map)
        {
            var rules = new ApplyPreconditions(Kind)
                .Require(() => Valid(intent), "an edit requires a zone id, add or remove, and distinct explicit cells")
                .Present(() => zone != null, "the exact zone no longer exists on this map");
            if (!rules.Holds) return rules;
            var candidate = zone!;
            var slotGroup = (candidate as Zone_Stockpile)?.slotGroup;
            var requested = Requested(intent!);
            var adding = intent!.Edit == Operations.CellEdit.Add;
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
            return rules.Require(() =>
            {
                var result = new HashSet<IntVec3>(candidate.Cells);
                foreach (var c in requested) { if (adding) result.Add(c); else result.Remove(c); }
                return result.Count == 0 || Contiguous(result);
            }, "the edited zone would not be contiguous");
        }

        public Common.Failure? Validate(Operations.Action action, Common.ObservationContext context)
        {
            var map = ProtoBoundary.LoadedMap(context);
            var zone = Resolve(action.ZoneCells, map);
            if (Standing(action.ZoneCells, zone)) return null;
            var rules = Rules(action.ZoneCells, zone, map);
            return rules.Holds ? null : rules.Failure();
        }

        public Receipts.EffectEvidence Apply(Operations.Action action, Common.ObservationContext context)
        {
            var intent = action.ZoneCells;
            var map = ProtoBoundary.LoadedMap(context);
            var zone = Resolve(intent, map);
            if (!Standing(intent, zone))
            {
                var rules = Rules(intent, zone, map);
                if (!rules.Holds) throw new InvalidOperationException("Zone cell edit prerequisites changed before apply: " + rules.Reason);
                foreach (var c in Requested(intent))
                {
                    if (intent.Edit == Operations.CellEdit.Add && !zone!.Cells.Contains(c)) zone.AddCell(c);
                    else if (intent.Edit == Operations.CellEdit.Remove && zone!.Cells.Contains(c)) zone.RemoveCell(c);
                }
                // Removing a zone's last cell deregisters it (Zone.RemoveCell).
                if (map.zoneManager.AllZones.Contains(zone!) && !Standing(intent, zone)) throw new InvalidOperationException("Native zone cell readback did not apply.");
            }
            return NativeZoneCreation.Evidence(intent.ZoneId, zone, map);
        }
    }
}
