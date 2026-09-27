#nullable enable
using System;
using System.Linq;
using RimWorld;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Obs = RimGovernor.Protocol.Observations;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    /// <summary>
    /// Progress record for one admitted PatchStockpile: the storage target
    /// (a stockpile zone or a storage building such as a shelf), the CAS token
    /// it was admitted against and the resolved settings body. The after-token
    /// is the target's live token, which covers priority and the filter
    /// signature, so a later external edit is visible as a token that no
    /// longer matches the desired projection.
    /// </summary>
    internal sealed class NativeStockpilePatchRecord
    {
        internal readonly IStoreSettingsParent Parent;
        internal readonly Zone_Stockpile? Zone;
        internal readonly Building_Storage? Building;
        internal readonly Map Map;
        internal readonly string BeforeToken;
        internal readonly NativeStockpileSettings.Resolved Desired;
        internal NativeStockpilePatchRecord(IStoreSettingsParent parent, Map map, string beforeToken, NativeStockpileSettings.Resolved desired)
        { Parent = parent; Zone = parent as Zone_Stockpile; Building = parent as Building_Storage; Map = map; BeforeToken = beforeToken; Desired = desired; }

        private bool Present => Zone != null ? Map.zoneManager.AllZones.Contains(Zone)
            : Building != null && !Building.Destroyed && Building.Spawned && Building.Map == Map;

        internal Receipts.EffectEvidence Evidence(Common.ObservationContext context)
        {
            if (Building != null)
            {
                // A storage building has no cells to read back: the settings
                // effect names it and carries its storage token while it stands.
                var id = Building.GetUniqueLoadID();
                var settings = new Receipts.SettingsEffect { Snapshot = new Receipts.SnapshotEvidence { EntityId = id, BeforeToken = BeforeToken } };
                if (Present) settings.Snapshot.AfterToken = NativeStockpilePatch.StorageToken(Building, context).Token;
                return new Receipts.EffectEvidence { Settings = settings };
            }
            var present = Present;
            var result = new Receipts.ZoneEffect { ZoneId = Zone!.GetUniqueLoadID(), Present = present, ChangedCells = 0,
                Snapshot = new Receipts.SnapshotEvidence { EntityId = Zone.GetUniqueLoadID(), BeforeToken = BeforeToken } };
            if (!present) return new Receipts.EffectEvidence { Zone = result };
            var cells = Zone.Cells.OrderBy(c => c.x).ThenBy(c => c.z).ToArray();
            result.ListedCellCount = cells.Length; result.GridCellCount = Map.AllCells.Count(c => Map.zoneManager.ZoneAt(c) == Zone);
            result.PhantomCellCount = cells.Count(c => Map.zoneManager.ZoneAt(c) != Zone);
            foreach (var c in cells) result.Cells.Add(new Receipts.CellResult { Cell = new Common.Cell { X = c.x, Z = c.z }, Accepted = Map.zoneManager.ZoneAt(c) == Zone });
            result.Snapshot.AfterToken = NativeZoneObservationTools.Token(Zone, context).Token;
            return new Receipts.EffectEvidence { Zone = result };
        }

        internal bool Matches() => Present && NativeStockpileSettings.Matches(Parent, Desired);

        internal Receipts.Progress Observe(Common.AttemptKey attempt, Common.ObservationContext context)
        {
            var result = new Receipts.Progress { Attempt = attempt.Clone(), Context = context.Clone(), CompleteInspection = true };
            if (Map != ProtoBoundary.ResolveMap(context)) { result.CompleteInspection = false; result.Unknown = new Receipts.UnknownEffect { Reason = "Patched storage's map is unavailable." }; return result; }
            var evidence = Evidence(context);
            if (Matches()) result.Completed = new Receipts.CompletedEffect { Evidence = evidence };
            else result.Unsuccessful = new Receipts.UnsuccessfulEffect { Reason = Receipts.UnsuccessfulReason.OutcomeNotAchieved, Evidence = evidence,
                Detail = Present ? "Storage settings differ from the admitted body." : "Storage target is no longer present." };
            return result;
        }
    }

    /// <summary>
    /// Typed PatchStockpile: replaces the priority and/or patches the filter
    /// of one existing storage target under its CAS token. The target
    /// (PatchStockpile.zone) is either a stockpile zone, guarded by its
    /// per-zone token, or a player storage building (Building_Storage, e.g.
    /// Shelf), guarded by the storage token its building listing row carries
    /// (StorageToken). Absent parts of the body preserve the live setting; the
    /// operation is refused outright when any selector fails to resolve, so a
    /// body never applies partially. Cells are never touched (EditZoneCells
    /// owns them).
    /// </summary>
    internal static class NativeStockpilePatch
    {
        internal static bool Valid(Operations.PatchStockpile? command)
            => command != null && NativeDraftProtocol.ValidEntity(command.Zone) && NativeStockpileSettings.Valid(command.Settings);

        // StorageEligible is a spawned, loaded storage building the player
        // owns with storage settings: the building listing reports its
        // storage token as the row's settings snapshot.
        internal static bool StorageEligible(Thing thing) => thing is Building_Storage storage && !storage.Destroyed && storage.Spawned
            && ProtoBoundary.IsLoaded(storage.Map) && storage.Faction != null && storage.Faction == Faction.OfPlayerSilentFail
            && storage.GetStoreSettings()?.filter != null;

        // StorageToken covers exactly what PatchStockpile writes: priority and
        // the filter signature, the same contribution the per-zone token makes.
        internal static Obs.SnapshotRef StorageToken(Building_Storage storage, Common.ObservationContext context) =>
            NativeObservationSnapshot.Snapshot("storage", context, storage.GetUniqueLoadID(), w => {
                var settings = storage.GetStoreSettings();
                w.Write((int)settings.Priority);
                NativeStockpileSettings.WriteSignature(w, settings.filter);
            });

        // Settings is the storage building's listing-row settings: its
        // storage token and, since it is by definition the player's, the
        // faction reading.
        internal static Obs.BuildingSettings Settings(Building_Storage storage, Common.ObservationContext context) =>
            new Obs.BuildingSettings { Snapshot = StorageToken(storage, context), PlayerOwned = true };

        private static string? Token(IStoreSettingsParent parent, Common.ObservationContext context) =>
            parent is Zone_Stockpile zone ? NativeZoneObservationTools.Token(zone, context).Token
            : parent is Building_Storage storage ? StorageToken(storage, context).Token : null;

        private static IStoreSettingsParent? Resolve(Map map, string id)
        {
            if (map.zoneManager.AllZones.FirstOrDefault(z => z.GetUniqueLoadID() == id) is Zone_Stockpile zone) return zone;
            var thing = map.listerThings.AllThings.FirstOrDefault(t => t.GetUniqueLoadID() == id);
            return thing != null && StorageEligible(thing) ? (Building_Storage)thing : null;
        }

        internal const string Kind = "Stockpile patch";
        private static bool Prepare(Operations.PatchStockpile command, Common.ObservationContext context,
            out IStoreSettingsParent? target, out Map? targetMap, out NativeStockpileSettings.Resolved? resolved, out Common.Failure failure)
        {
            target = null; targetMap = null; resolved = null;
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest,
                "Stockpile patches require an exact stockpile zone or player storage building identity, a matching CAS snapshot token and a settings body whose "
                + "priority, preset, selectors (exact defNames of storable things, categories or configurable special filters) and "
                + "hit-point/quality ranges all resolve.");
            if (!Valid(command)) return false;
            var map = ProtoBoundary.ResolveMap(context);
            if (map == null) return false;
            var candidate = Resolve(map, command.Zone.EntityId);
            NativeStockpileSettings.Resolved? body = null;
            // The apply-time precondition list for stockpile settings
            // (action-contracts.md), one rule at a time.
            var rules = new ApplyPreconditions(Kind)
                .Present(() => candidate != null, "the exact stockpile zone or storage building no longer exists on this map")
                .Require(() => candidate!.GetStoreSettings()?.filter != null, "the target has no storage settings")
                .Require(() => (body = NativeStockpileSettings.Resolve(command.Settings, StockpileFilter.StorableDefs(candidate!))) != null, "the settings body does not resolve against the target's storable definitions")
                .Token(() => Token(candidate!, context) == command.Zone.ExpectedSnapshotToken, "the storage snapshot changed since it was read");
            if (!rules.Holds) { failure = rules.Failure(); return false; }
            target = candidate; targetMap = map; resolved = body;
            return true;
        }

        internal static Operations.PreviewReply Preview(Operations.PatchStockpile command, Common.ObservationContext context)
        {
            if (!Prepare(command, context, out _, out _, out _, out var failure)) return new Operations.PreviewReply { Failure = failure };
            return new Operations.PreviewReply { Evaluated = new Operations.PreviewEvaluation { Context = context.Clone(), Accepted = true } };
        }

        internal static Operations.ExecuteReply Execute(NativeOperationState state, Operations.ExecuteRequest request, Common.ObservationContext context)
        {
            NativeAttemptLedger.Admission? handle = null; Receipts.EffectEvidence? evidence = null;
            var pre = request.Precondition; var command = request.Operation.PatchStockpile;
            try
            {
                if (!Prepare(command, context, out var target, out var map, out var resolved, out var failure)) return new Operations.ExecuteReply { Failure = failure };
                if (!NativeControlAuthority.TryGetForGame(Current.Game, out var authority) || authority == null)
                    return new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(Common.FailureCode.AuthorityRequired, "Native authority required.") };
                var guard = authority.Check(pre.ExpectedGeneration); context.NativeGeneration = guard.Snapshot.Generation;
                if (!guard.Success) return new Operations.ExecuteReply { Failure = NativeAuthorityControlTools.Refusal(guard.Error, context) };
                var admitted = state.Ledger.Admit("rimgovernor.operations.v1.Operations/Execute", request, context);
                if (admitted.Kind != NativeAttemptLedger.DecisionKind.Admitted) return admitted.DecidedReply; handle = admitted.AdmittedHandle;
                using (authority.Owned())
                {
                    if (!authority.Check(pre.ExpectedGeneration).Success || !Prepare(command, context, out target, out map, out resolved, out failure))
                        throw new InvalidOperationException("Storage scope changed before the stockpile patch.");
                    var record = new NativeStockpilePatchRecord(target!, map!, command.Zone.ExpectedSnapshotToken, resolved!);
                    state.StockpilePatches.Add(pre.Attempt.Clone(), record);
                    var settings = target!.GetStoreSettings();
                    if (resolved!.Priority.HasValue) settings.Priority = resolved.Priority.Value;
                    NativeStockpileSettings.Apply(settings.filter, resolved, StockpileFilter.ParentFilter(target), StockpileFilter.StorableDefs(target));
                    evidence = record.Evidence(context);
                }
                return new Operations.ExecuteReply { Receipt = state.Ledger.FinishApplied(handle, evidence) };
            }
            catch (Exception error)
            {
                return handle == null ? new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(Common.FailureCode.NativeFailure, "Stockpile patch admission failed: " + error.GetType().Name) }
                    : new Operations.ExecuteReply { Receipt = state.Ledger.FinishUncertain(handle, evidence!, "Patched storage requires inspection: " + error.GetType().Name) };
            }
        }
    }
}
