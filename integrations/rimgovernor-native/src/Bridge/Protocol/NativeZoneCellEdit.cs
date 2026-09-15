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

        private static bool Prepare(Operations.EditZoneCells command, Common.ObservationContext context,
            out Zone? zone, out HashSet<IntVec3>? finalCells, out bool needsSplit, out Common.Failure failure)
        {
            zone = null; finalCells = null; needsSplit = false;
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest,
                "Zone cell edits require an exact zone identity, a matching CAS snapshot token, a phantom-free zone with an intact "
                + "haul grid, cells that are either genuinely free ground (add) or already exactly this zone's own consistent cells "
                + "(remove), and a contiguous result unless allow_split is set.");
            if (!Valid(command)) return false;

            var map = Find.CurrentMap;
            var candidate = map.zoneManager.AllZones.FirstOrDefault(z => z.GetUniqueLoadID() == command.Zone.EntityId);
            if (candidate == null || candidate.Cells.Count == 0) return false;
            if (NativeZoneObservationTools.Token(candidate, context).Token != command.Zone.ExpectedSnapshotToken) return false;

            // Whole-zone consistency first: neither AddCell/RemoveCell nor a
            // later CheckContiguous may run against a zone that already has a
            // phantom cell or a haul grid pointing away from it.
            if (candidate.Cells.Any(c => map.zoneManager.ZoneAt(c) != candidate)) return false;
            var slotGroup = (candidate as Zone_Stockpile)?.slotGroup;
            if (slotGroup != null && candidate.Cells.Any(c => map.haulDestinationManager?.SlotGroupAt(c) != slotGroup)) return false;

            var requested = command.Cells.ExplicitCells.Cells.Select(c => new IntVec3(c.X, 0, c.Z)).ToArray();
            var currentSet = new HashSet<IntVec3>(candidate.Cells);
            var result = new HashSet<IntVec3>(currentSet);

            if (command.Edit == Operations.CellEdit.Add)
            {
                foreach (var c in requested)
                {
                    if (currentSet.Contains(c)) return false;
                    if (!IsFreeGround(c, map)) return false;
                    if (command.RequireCoveredEmpty
                        && (!c.Roofed(map) || c.GetEdifice(map) != null || c.GetThingList(map).Count != 0 || map.roofCollapseBuffer.IsMarkedToCollapse(c)))
                        return false;
                    if (slotGroup != null && map.haulDestinationManager?.SlotGroupAt(c) != null) return false;
                    result.Add(c);
                }
            }
            else
            {
                foreach (var c in requested)
                {
                    if (!currentSet.Contains(c)) return false;
                    if (map.zoneManager.ZoneAt(c) != candidate) return false;
                    if (slotGroup != null && map.haulDestinationManager?.SlotGroupAt(c) != slotGroup) return false;
                    result.Remove(c);
                }
            }

            if (result.Count > 0 && !Contiguous(result))
            {
                if (!command.AllowSplit) return false;
                needsSplit = true;
            }

            zone = candidate;
            finalCells = result;
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
                if (admitted.Kind != NativeAttemptLedger.DecisionKind.Admitted) return admitted.Reply!; handle = admitted.Handle;
                using (authority.Owned())
                {
                    if (!authority.Check(pre.ExpectedGeneration).Success
                        || !Prepare(command, context, out zone, out finalCells, out needsSplit, out failure))
                        throw new InvalidOperationException("Zone scope changed before cell edit.");
                    var map = Find.CurrentMap;
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
                    : new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Uncertain(state.Ledger, handle, pre.Attempt, context, evidence!, "Edited zone requires inspection: " + error.GetType().Name) };
            }
        }
    }
}
