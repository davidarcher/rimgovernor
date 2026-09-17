#nullable enable
using System;
using System.Linq;
using RimWorld;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    /// <summary>
    /// Progress record for one admitted PatchStockpile: the zone, the CAS
    /// token it was admitted against and the resolved settings body. The
    /// after-token is the zone's live per-zone token, which covers the
    /// filter signature, so a later external edit is visible as a token that
    /// no longer matches the desired projection.
    /// </summary>
    internal sealed class NativeStockpilePatchRecord
    {
        internal readonly Zone_Stockpile Zone;
        internal readonly Map Map;
        internal readonly string BeforeToken;
        internal readonly NativeStockpileSettings.Resolved Desired;
        internal NativeStockpilePatchRecord(Zone_Stockpile zone, Map map, string beforeToken, NativeStockpileSettings.Resolved desired)
        { Zone = zone; Map = map; BeforeToken = beforeToken; Desired = desired; }

        internal Receipts.ZoneEffect Evidence(Common.ObservationContext context)
        {
            var present = Map.zoneManager.AllZones.Contains(Zone);
            var result = new Receipts.ZoneEffect { ZoneId = Zone.GetUniqueLoadID(), Present = present, ChangedCells = 0,
                Snapshot = new Receipts.SnapshotEvidence { EntityId = Zone.GetUniqueLoadID(), BeforeToken = BeforeToken } };
            if (!present) return result;
            var cells = Zone.Cells.OrderBy(c => c.x).ThenBy(c => c.z).ToArray();
            result.ListedCellCount = cells.Length; result.GridCellCount = Map.AllCells.Count(c => Map.zoneManager.ZoneAt(c) == Zone);
            result.PhantomCellCount = cells.Count(c => Map.zoneManager.ZoneAt(c) != Zone);
            foreach (var c in cells) result.Cells.Add(new Receipts.CellResult { Cell = new Common.Cell { X = c.x, Z = c.z }, Accepted = Map.zoneManager.ZoneAt(c) == Zone });
            result.Snapshot.AfterToken = NativeZoneObservationTools.Token(Zone, context).Token;
            return result;
        }

        internal bool Matches() => Map.zoneManager.AllZones.Contains(Zone) && NativeStockpileSettings.Matches(Zone, Desired);

        internal Receipts.Progress Observe(Common.AttemptKey attempt, Common.ObservationContext context)
        {
            var result = new Receipts.Progress { Attempt = attempt.Clone(), Context = context.Clone(), CompleteInspection = true };
            if (Map != ProtoBoundary.ResolveMap(context)) { result.CompleteInspection = false; result.Unknown = new Receipts.UnknownEffect { Reason = "Patched stockpile's map is unavailable." }; return result; }
            var evidence = new Receipts.EffectEvidence { Zone = Evidence(context) };
            if (Matches()) result.Completed = new Receipts.CompletedEffect { Evidence = evidence };
            else result.Unsuccessful = new Receipts.UnsuccessfulEffect { Reason = Receipts.UnsuccessfulReason.OutcomeNotAchieved, Evidence = evidence,
                Detail = evidence.Zone.Present ? "Stockpile settings differ from the admitted body." : "Stockpile zone is no longer present." };
            return result;
        }
    }

    /// <summary>
    /// Typed PatchStockpile: replaces the priority and/or patches the filter
    /// of one existing stockpile zone under its per-zone CAS token. Absent
    /// parts of the body preserve the live setting; the operation is refused
    /// outright when any selector fails to resolve, so a body never applies
    /// partially. Cells are never touched (EditZoneCells owns them).
    /// </summary>
    internal static class NativeStockpilePatch
    {
        internal static bool Valid(Operations.PatchStockpile? command)
            => command != null && NativeDraftProtocol.ValidEntity(command.Zone) && NativeStockpileSettings.Valid(command.Settings);

        private static bool Prepare(Operations.PatchStockpile command, Common.ObservationContext context,
            out Zone_Stockpile? zone, out NativeStockpileSettings.Resolved? resolved, out Common.Failure failure)
        {
            zone = null; resolved = null;
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest,
                "Stockpile patches require an exact stockpile zone identity, a matching CAS snapshot token and a settings body whose "
                + "priority, preset, selectors (exact defNames of storable things, categories or configurable special filters) and "
                + "hit-point/quality ranges all resolve.");
            if (!Valid(command)) return false;
            var map = ProtoBoundary.ResolveMap(context);
            if (map == null) return false;
            var candidate = map.zoneManager.AllZones.FirstOrDefault(z => z.GetUniqueLoadID() == command.Zone.EntityId) as Zone_Stockpile;
            if (candidate == null || candidate.settings?.filter == null) return false;
            if (NativeZoneObservationTools.Token(candidate, context).Token != command.Zone.ExpectedSnapshotToken) return false;
            var body = NativeStockpileSettings.Resolve(command.Settings, StockpileFilter.StorableDefs(candidate));
            if (body == null) return false;
            zone = candidate; resolved = body;
            return true;
        }

        internal static Operations.PreviewReply Preview(Operations.PatchStockpile command, Common.ObservationContext context)
        {
            if (!Prepare(command, context, out _, out _, out var failure)) return new Operations.PreviewReply { Failure = failure };
            return new Operations.PreviewReply { Evaluated = new Operations.PreviewEvaluation { Context = context.Clone(), Accepted = true } };
        }

        internal static Operations.ExecuteReply Execute(NativeOperationState state, Operations.ExecuteRequest request, Common.ObservationContext context)
        {
            NativeAttemptLedger.Admission? handle = null; Receipts.EffectEvidence? evidence = null;
            var pre = request.Precondition; var command = request.Operation.PatchStockpile;
            try
            {
                if (!Prepare(command, context, out var zone, out var resolved, out var failure)) return new Operations.ExecuteReply { Failure = failure };
                if (!NativeControlAuthority.TryGetForGame(Current.Game, out var authority) || authority == null)
                    return new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(Common.FailureCode.AuthorityRequired, "Native authority required.") };
                var guard = authority.Check(pre.ExpectedGeneration); context.NativeGeneration = guard.Snapshot.Generation;
                if (!guard.Success) return new Operations.ExecuteReply { Failure = NativeAuthorityControlTools.Refusal(guard.Error, context) };
                var admitted = state.Ledger.Admit("rimgovernor.operations.v1.Operations/Execute", request, context);
                if (admitted.Kind != NativeAttemptLedger.DecisionKind.Admitted) return admitted.Reply!; handle = admitted.AdmittedHandle;
                using (authority.Owned())
                {
                    if (!authority.Check(pre.ExpectedGeneration).Success || !Prepare(command, context, out zone, out resolved, out failure))
                        throw new InvalidOperationException("Zone scope changed before the stockpile patch.");
                    var map = ProtoBoundary.LoadedMap(context);
                    var record = new NativeStockpilePatchRecord(zone!, map, command.Zone.ExpectedSnapshotToken, resolved!);
                    state.StockpilePatches.Add(pre.Attempt.Clone(), record);
                    if (resolved!.Priority.HasValue) zone!.settings.Priority = resolved.Priority.Value;
                    NativeStockpileSettings.Apply(zone!.settings.filter, resolved, StockpileFilter.ParentFilter(zone), StockpileFilter.StorableDefs(zone));
                    evidence = new Receipts.EffectEvidence { Zone = record.Evidence(context) };
                }
                return new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Applied(state.Ledger, handle, pre.Attempt, context, evidence) };
            }
            catch (Exception error)
            {
                return handle == null ? new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(Common.FailureCode.NativeFailure, "Stockpile patch admission failed: " + error.GetType().Name) }
                    : new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Uncertain(state.Ledger, handle, pre.Attempt, context, evidence!, "Patched stockpile requires inspection: " + error.GetType().Name) };
            }
        }
    }
}
