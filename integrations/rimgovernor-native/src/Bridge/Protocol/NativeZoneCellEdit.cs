#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Authority = RimGovernor.Protocol.Authority;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    /// <summary>
    /// Typed EditZoneCells. Unlike the legacy op=add, this never takes a cell
    /// away from another zone: EditZoneCells carries a CAS precondition on
    /// only ONE zone, so mutating a second zone's cells here would have no
    /// precondition guarding it. Add is therefore restricted to cells that
    /// are genuinely free (no grid owner, and no zone's own cell list holds
    /// them either -- the same orphan-safe test NativeZoneCreation.Prepare
    /// uses for a fresh zone). Remove only ever touches this zone's own,
    /// already-consistent cells. Both directions refuse outright, rather
    /// than silently discard, when the result would not be contiguous and
    /// the caller did not set allow_split.
    /// </summary>
    internal static class NativeZoneCellEdit
    {
        internal static bool Valid(Operations.EditZoneCells? command)
        {
            if (command?.Zone == null || !command.Zone.HasEntityId || !ProtoBoundary.IsIdentifier(command.Zone.EntityId)
                || !command.Zone.HasExpectedSnapshotToken || !ProtoBoundary.IsIdentifier(command.Zone.ExpectedSnapshotToken))
                return false;
            if (!command.HasEdit || (command.Edit != Operations.CellEdit.Add && command.Edit != Operations.CellEdit.Remove))
                return false;
            var cells = command.Cells?.ExplicitCells?.Cells;
            if (cells == null || cells.Count == 0 || cells.Count > 256)
                return false;
            if (!cells.All(c => c.HasX && c.HasZ && c.X >= 0 && c.Z >= 0))
                return false;
            return cells.Select(c => Tuple.Create(c.X, c.Z)).Distinct().Count() == cells.Count;
        }

        // Same BFS the legacy tool and NativeZoneCreation.Prepare use: reached
        // from cells[0] over cardinal neighbours within the set.
        private static bool Contiguous(IEnumerable<IntVec3> cellSet)
        {
            var cells = cellSet.ToArray();
            if (cells.Length <= 1) return true;
            var set = new HashSet<IntVec3>(cells);
            var reached = new HashSet<IntVec3> { cells[0] };
            var queue = new Queue<IntVec3>();
            queue.Enqueue(cells[0]);
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

        internal const string Kind = "Zone cell edit";
        private static string At(IntVec3 c) => "(" + c.x + ", " + c.z + ")";
        // Prepare is the apply-time precondition list for expand/shrink
        // (action-contracts.md): the zone, its consistency, every requested
        // cell and the resulting shape, one rule at a time, so a refusal
        // names the cell that moved.
        private static bool Prepare(Operations.EditZoneCells command, Common.ObservationContext context,
            out Zone? zone, out HashSet<IntVec3>? finalCells, out bool needsSplit, out Common.Failure failure)
        {
            zone = null; finalCells = null; needsSplit = false;
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest,
                "Zone cell edits require an exact zone identity, a matching CAS snapshot token, a phantom-free zone with an intact "
                + "haul grid, cells that are either genuinely free ground (add) or already exactly this zone's own consistent cells "
                + "(remove), and a contiguous result unless allow_split is set.");
            if (!Valid(command)) return false;

            var map = ProtoBoundary.LoadedMap(context);
            var candidate = map.zoneManager.AllZones.FirstOrDefault(z => z.GetUniqueLoadID() == command.Zone.EntityId);
            var slotGroup = (candidate as Zone_Stockpile)?.slotGroup;
            var requested = command.Cells.ExplicitCells.Cells.Select(c => new IntVec3(c.X, 0, c.Z)).ToArray();
            var adding = command.Edit == Operations.CellEdit.Add;
            var result = new HashSet<IntVec3>();
            var split = false;
            var rules = new ApplyPreconditions(Kind)
                .Present(() => candidate != null && candidate.Cells.Count > 0, "the exact zone no longer exists on this map")
                // Whole-zone consistency first: neither AddCell/RemoveCell nor a
                // later CheckContiguous may run against a zone that already has a
                // phantom cell or a haul grid pointing away from it.
                .Require(() => candidate!.Cells.All(c => map.zoneManager.ZoneAt(c) == candidate), "the zone holds a phantom cell the zone grid does not map to it")
                .Require(() => slotGroup == null || candidate!.Cells.All(c => map.haulDestinationManager?.SlotGroupAt(c) == slotGroup), "the stockpile's haul grid no longer matches its cells");
            if (adding)
            {
                foreach (var cell in requested)
                {
                    var c = cell;
                    rules.Require(() => !candidate!.Cells.Contains(c), "cell " + At(c) + " is already in the zone")
                        .Require(() => IsFreeGround(c, map), "cell " + At(c) + " is not free zoneable ground")
                        .Require(() => !(candidate is Zone_Fishing) || NativeZoneCreation.FishableCell(c, map, candidate.Cells[0].GetWaterBody(map)),
                            "cell " + At(c) + " is not fishable water in the zone's water body")
                        .Require(() => !command.RequireCoveredEmpty || c.Roofed(map) && c.GetEdifice(map) == null && c.GetThingList(map).Count == 0 && !map.roofCollapseBuffer.IsMarkedToCollapse(c),
                            "cell " + At(c) + " is not roofed, empty and clear")
                        .Require(() => slotGroup == null || map.haulDestinationManager?.SlotGroupAt(c) == null, "cell " + At(c) + " already belongs to a storage group");
                }
            }
            else
            {
                foreach (var cell in requested)
                {
                    var c = cell;
                    rules.Require(() => candidate!.Cells.Contains(c), "cell " + At(c) + " is not in the zone")
                        .Require(() => map.zoneManager.ZoneAt(c) == candidate, "cell " + At(c) + " is not mapped to the zone on the zone grid")
                        .Require(() => slotGroup == null || map.haulDestinationManager?.SlotGroupAt(c) == slotGroup, "cell " + At(c) + " is not mapped to the stockpile's storage group");
                }
            }
            rules.Require(() =>
                {
                    result = new HashSet<IntVec3>(candidate!.Cells);
                    foreach (var c in requested) { if (adding) result.Add(c); else result.Remove(c); }
                    split = result.Count > 0 && !Contiguous(result);
                    return !split || command.AllowSplit;
                }, "the edited zone would not be contiguous and allow_split is not set")
                .Token(() => NativeZoneObservationTools.Token(candidate!, context).Token == command.Zone.ExpectedSnapshotToken, "the zone snapshot changed since it was read");
            if (!rules.Holds) { failure = rules.Failure(); return false; }

            zone = candidate;
            finalCells = result;
            needsSplit = split;
            return true;
        }

        internal static Operations.PreviewReply Preview(Operations.EditZoneCells command, Common.ObservationContext context)
        {
            if (!Prepare(command, context, out _, out _, out _, out var failure)) return new Operations.PreviewReply { Failure = failure };
            return new Operations.PreviewReply { Evaluated = new Operations.PreviewEvaluation { Context = context.Clone(), Accepted = true } };
        }

        internal static Operations.ExecuteReply Execute(NativeOperationState state, Operations.ExecuteRequest request, Common.ObservationContext context)
        {
            NativeAttemptLedger.Admission? handle = null; Receipts.EffectEvidence? evidence = null;
            var pre = request.Precondition; var command = request.Operation.EditZoneCells;
            try
            {
                if (!Prepare(command, context, out var zone, out var finalCells, out var needsSplit, out var failure))
                    return new Operations.ExecuteReply { Failure = failure };
                if (!NativeControlAuthority.TryGetForGame(Current.Game, out var authority) || authority == null)
                    return new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(Common.FailureCode.AuthorityRequired, "Native authority required.") };
                var guard = authority.Check(pre.ExpectedGeneration); context.NativeGeneration = guard.Snapshot.Generation;
                if (!guard.Success) return new Operations.ExecuteReply { Failure = NativeAuthorityControlTools.Refusal(guard.Error, context) };
                var admitted = state.Ledger.Admit("rimgovernor.operations.v1.Operations/Execute", request, context);
                if (admitted.Kind != NativeAttemptLedger.DecisionKind.Admitted) return admitted.DecidedReply; handle = admitted.AdmittedHandle;
                using (authority.Owned())
                {
                    if (!authority.Check(pre.ExpectedGeneration).Success
                        || !Prepare(command, context, out zone, out finalCells, out needsSplit, out failure))
                        throw new InvalidOperationException("Zone scope changed before cell edit.");
                    var map = ProtoBoundary.LoadedMap(context);
                    var requested = command.Cells.ExplicitCells.Cells.Select(c => new IntVec3(c.X, 0, c.Z)).ToArray();
                    var record = new NativeZoneEditRecord(zone!, map, command.Zone.ExpectedSnapshotToken, finalCells!);
                    state.ZoneEdits.Add(pre.Attempt.Clone(), record);
                    if (command.Edit == Operations.CellEdit.Add)
                        foreach (var c in requested) zone!.AddCell(c);
                    else
                        foreach (var c in requested) zone!.RemoveCell(c);
                    // Deleting every cell zero-length list registers already
                    // deregisters via RemoveCell itself; CheckContiguous only
                    // runs when the caller opted into a split.
                    if (needsSplit && map.zoneManager.AllZones.Contains(zone!)) zone!.CheckContiguous();
                    evidence = new Receipts.EffectEvidence { Zone = record.Evidence() };
                }
                return new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Applied(state.Ledger, handle, pre.Attempt, context, evidence) };
            }
            catch (Exception error)
            {
                return handle == null ? new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(Common.FailureCode.NativeFailure, "Zone cell edit admission failed: " + error.GetType().Name) }
                    : new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Uncertain(state.Ledger, handle, pre.Attempt, context, evidence, "Edited zone requires inspection: " + error.GetType().Name) };
            }
        }
    }
}
