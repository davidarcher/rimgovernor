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
    // Shared by DeleteZone now and EditZoneCells later: both guard one
    // specific zone's own per-zone CAS token (NativeZoneObservationTools.Token),
    // unlike NativeZoneRecord's whole-map CreateZone token.
    /// <summary>
    /// Records a pending zone mutation for progress observation. A null
    /// <see cref="DesiredCells"/> means delete semantics (success = the zone
    /// is gone); a non-null set means cell-edit semantics (success = the
    /// zone's live cells exactly equal the set -- or, when the set is empty,
    /// that the zone deregistered itself, which Zone.RemoveCell does on its
    /// own once a zone's last cell is removed).
    /// </summary>
    internal sealed class NativeZoneEditRecord
    {
        internal readonly Zone Zone;
        internal readonly Map Map;
        internal readonly string BeforeToken;
        internal readonly HashSet<IntVec3>? DesiredCells;
        internal NativeZoneEditRecord(Zone zone, Map map, string beforeToken) { Zone = zone; Map = map; BeforeToken = beforeToken; DesiredCells = null; }
        internal NativeZoneEditRecord(Zone zone, Map map, string beforeToken, HashSet<IntVec3> desiredCells) { Zone = zone; Map = map; BeforeToken = beforeToken; DesiredCells = desiredCells; }

        internal Receipts.ZoneEffect Evidence()
        {
            var present = Map.zoneManager.AllZones.Contains(Zone);
            var result = new Receipts.ZoneEffect { ZoneId = Zone.GetUniqueLoadID(), Present = present,
                Snapshot = new Receipts.SnapshotEvidence { EntityId = Zone.GetUniqueLoadID(), BeforeToken = BeforeToken } };
            if (present)
            {
                var cells = Zone.Cells.OrderBy(c => c.x).ThenBy(c => c.z).ToArray();
                var grid = Map.AllCells.Count(c => Map.zoneManager.ZoneAt(c) == Zone);
                result.ChangedCells = cells.Length; result.ListedCellCount = cells.Length; result.GridCellCount = grid;
                result.PhantomCellCount = cells.Count(c => Map.zoneManager.ZoneAt(c) != Zone);
                foreach (var c in cells) result.Cells.Add(new Receipts.CellResult { Cell = new Common.Cell { X = c.x, Z = c.z }, Accepted = Map.zoneManager.ZoneAt(c) == Zone });
            }
            return result;
        }

        internal Receipts.Progress Observe(Common.AttemptKey attempt, Common.ObservationContext context)
        {
            var result = new Receipts.Progress { Attempt = attempt.Clone(), Context = context.Clone(), CompleteInspection = true };
            var evidence = Evidence();
            var reason = DesiredCells == null ? "Deleted zone's map is unavailable." : "Edited zone's map is unavailable.";
            if (Map != ProtoBoundary.ResolveMap(context)) { result.CompleteInspection = false; result.Unknown = new Receipts.UnknownEffect { Reason = reason }; return result; }

            bool matches;
            if (DesiredCells == null || DesiredCells.Count == 0) matches = !evidence.Present;
            else matches = evidence.Present && evidence.PhantomCellCount == 0 && evidence.GridCellCount == evidence.ListedCellCount
                && new HashSet<IntVec3>(Zone.Cells).SetEquals(DesiredCells);

            if (matches) result.Completed = new Receipts.CompletedEffect { Evidence = new Receipts.EffectEvidence { Zone = evidence } };
            else result.Unsuccessful = new Receipts.UnsuccessfulEffect { Reason = Receipts.UnsuccessfulReason.OutcomeNotAchieved,
                Evidence = new Receipts.EffectEvidence { Zone = evidence },
                Detail = DesiredCells == null ? "Zone is still present." : "Zone cells differ from the requested edit." };
            return result;
        }
    }

    // DeleteZone only. Mirrors ZoneCellsTool.Delete's two landmine checks
    // (phantom cells; a stockpile's haul grid pointing elsewhere), which exist
    // because Zone.Delete() loops RemoveCell and RemoveCell's Notify_LostCell
    // Log.Errors -- pausing the sim -- when the grid disagrees with the zone's
    // own cell list. See ZoneCellsTool.cs's header comment for the full case.
    internal static class NativeZoneDeletion
    {
        internal static bool Valid(Operations.DeleteZone? command) =>
            command?.Zone != null && command.Zone.HasEntityId && ProtoBoundary.IsIdentifier(command.Zone.EntityId)
            && command.Zone.HasExpectedSnapshotToken && ProtoBoundary.IsIdentifier(command.Zone.ExpectedSnapshotToken);

        private static bool Prepare(Operations.DeleteZone command, Common.ObservationContext context, out Zone? zone, out Common.Failure failure)
        {
            zone = null;
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Zone deletion requires an exact zone identity, a matching CAS snapshot token, no phantom cells and a consistent haul grid.");
            if (!Valid(command)) return false;
            var map = ProtoBoundary.LoadedMap(context);
            var candidate = map.zoneManager.AllZones.FirstOrDefault(z => z.GetUniqueLoadID() == command.Zone.EntityId);
            if (candidate == null || candidate.Cells.Count == 0) return false;
            if (NativeZoneObservationTools.Token(candidate, context).Token != command.Zone.ExpectedSnapshotToken) return false;
            if (candidate.Cells.Any(c => map.zoneManager.ZoneAt(c) != candidate)) return false;
            if (candidate is Zone_Stockpile stockpile && candidate.Cells.Any(c => map.haulDestinationManager?.SlotGroupAt(c) != stockpile.slotGroup)) return false;
            zone = candidate;
            return true;
        }

        internal static Operations.PreviewReply Preview(Operations.DeleteZone command, Common.ObservationContext context)
        {
            if (!Prepare(command, context, out _, out var failure)) return new Operations.PreviewReply { Failure = failure };
            return new Operations.PreviewReply { Evaluated = new Operations.PreviewEvaluation { Context = context.Clone(), Accepted = true } };
        }

        internal static Operations.ExecuteReply Execute(NativeOperationState state, Operations.ExecuteRequest request, Common.ObservationContext context)
        {
            NativeAttemptLedger.Admission? handle = null; Receipts.EffectEvidence? evidence = null;
            var pre = request.Precondition; var command = request.Operation.DeleteZone;
            try
            {
                if (!Prepare(command, context, out var zone, out var failure)) return new Operations.ExecuteReply { Failure = failure };
                if (!NativeControlAuthority.TryGetForGame(Current.Game, out var authority) || authority == null) return new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(Common.FailureCode.AuthorityRequired, "Native authority required.") };
                var guard = authority.Check(pre.ExpectedGeneration); context.NativeGeneration = guard.Snapshot.Generation;
                if (!guard.Success) return new Operations.ExecuteReply { Failure = NativeAuthorityControlTools.Refusal(guard.Error, context) };
                var admitted = state.Ledger.Admit("rimgovernor.operations.v1.Operations/Execute", request, context);
                if (admitted.Kind != NativeAttemptLedger.DecisionKind.Admitted) return admitted.DecidedReply; handle = admitted.AdmittedHandle;
                using (authority.Owned())
                {
                    if (!authority.Check(pre.ExpectedGeneration).Success || !Prepare(command, context, out zone, out failure)) throw new InvalidOperationException("Zone scope changed before deletion.");
                    var record = new NativeZoneEditRecord(zone!, ProtoBoundary.LoadedMap(context), command.Zone.ExpectedSnapshotToken);
                    state.ZoneEdits.Add(pre.Attempt.Clone(), record);
                    zone!.Delete(false);
                    evidence = new Receipts.EffectEvidence { Zone = record.Evidence() };
                }
                return new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Applied(state.Ledger, handle, pre.Attempt, context, evidence) };
            }
            catch (Exception error)
            {
                return handle == null ? new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(Common.FailureCode.NativeFailure, "Zone deletion admission failed: " + error.GetType().Name) }
                    : new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Uncertain(state.Ledger, handle, pre.Attempt, context, evidence, "Deleted zone requires inspection: " + error.GetType().Name) };
            }
        }
    }
}
