#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Obs = RimGovernor.Protocol.Observations;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    // Typed successor of home/upkeep_wall: RemoveWall admits one guarded
    // native deconstruct designation through the legacy WallUpgradeSafety
    // ledger, whose Harmony guards keep re-checking enclosure, supports and
    // roof support until the pawn finishes. The wire carries only the wall's
    // identity, so the site is resolved here from the same census the Go
    // boundary just re-validated (NativeWallUpgradeObservationTools):
    //  1. a straight replacement site whose backup cells all hold same-stuff
    //     stone walls (demolish the original);
    //  2. a cleanup site where this wall is a completed backup of a standing
    //     stone permanent wall (clear the backup);
    //  3. a corner site with open salvage access, rebuilt from whichever
    //     stone material current stock and policy still cover.
    // Several admissible sites of one class refuse rather than guess.
    internal sealed class NativeWallRemovalRecord
    {
        private readonly Map map;
        private readonly WallRemovalRecord ledger;
        private readonly string target;
        private readonly string before;
        private readonly List<string> workers;
        private readonly Func<Obs.WallUpgradeSite?> reread;
        internal NativeWallRemovalRecord(Map map, WallRemovalRecord ledger, string target, string before, List<string> workers, Func<Obs.WallUpgradeSite?> reread)
        { this.map = map; this.ledger = ledger; this.target = target; this.before = before; this.workers = workers; this.reread = reread; }

        internal Receipts.WallEffect Evidence()
        {
            var effect = new Receipts.WallEffect { TargetId = target, RemovalId = ledger.Id, DemolitionObserved = ledger.Complete,
                Site = new Receipts.SnapshotEvidence { EntityId = target, BeforeToken = before } };
            effect.WorkerIds.AddRange(workers);
            var site = WallUpgradeSafety.Wall(map, target) == null ? null : reread();
            if (site?.Snapshot != null) effect.Site.AfterToken = site.Snapshot.Token;
            return effect;
        }

        internal Receipts.Progress Observe(Common.AttemptKey attempt, Common.ObservationContext context)
        {
            var result = new Receipts.Progress { Attempt = attempt.Clone(), Context = context.Clone(), CompleteInspection = true };
            if (map != ProtoBoundary.ResolveMap(context)) { result.CompleteInspection = false; result.Unknown = new Receipts.UnknownEffect { Reason = "Wall removal map is not the identity's map." }; return result; }
            var evidence = new Receipts.EffectEvidence { Wall = Evidence() };
            if (ledger.Complete) { result.Completed = new Receipts.CompletedEffect { Evidence = evidence }; return result; }
            var wall = WallUpgradeSafety.Wall(map, target);
            string? detail = ledger.Blocker;
            if (detail == null && wall == null) detail = "Wall is gone without a verified guarded demolition.";
            if (detail == null && map.designationManager.DesignationOn(wall, DesignationDefOf.Deconstruct) == null) detail = "Demolition designation was removed.";
            if (detail == null) detail = WallUpgradeSafety.Check(ledger);
            if (detail == null) result.Pending = new Receipts.PendingEffect { Evidence = evidence };
            else result.Unsuccessful = new Receipts.UnsuccessfulEffect { Reason = Receipts.UnsuccessfulReason.OutcomeNotAchieved, Evidence = evidence, Detail = detail };
            return result;
        }
    }

    internal static class NativeWallRemovalOperations
    {
        private sealed class Candidate
        {
            internal WallRemovalRecord Record = null!;
            internal Obs.WallUpgradeSite Site = null!;
            internal Func<Obs.WallUpgradeSite?> Reread = null!;
            internal List<Pawn> Workers = new List<Pawn>();
        }

        internal static bool Valid(Operations.RemoveWall? command) => command?.Wall != null && command.Wall.HasEntityId && ProtoBoundary.IsIdentifier(command.Wall.EntityId)
            && (!command.Wall.HasExpectedSnapshotToken || command.Wall.ExpectedSnapshotToken.Length > 0)
            && (!command.HasExpectedSiteSnapshotToken || command.ExpectedSiteSnapshotToken.Length > 0);

        [ThreadStatic] private static string? lastBlocker;

        private static Candidate? Try(WallRemovalRecord record, Obs.WallUpgradeSite? site, Func<Obs.WallUpgradeSite?> reread)
        {
            if (site == null) return null;
            if (site.HasBlocker || site.PlayerOwned) { lastBlocker = site.HasBlocker ? site.Blocker : "A player-owned demolition designation is preserved"; return null; }
            var candidate = new Candidate { Record = record, Site = site, Reread = reread };
            var blocker = WallUpgradeSafety.Prepare(record, out candidate.Workers);
            if (blocker != null) lastBlocker = blocker;
            return blocker == null ? candidate : null;
        }

        // Every admissible site of one wall, best class first.
        private static List<List<Candidate>> Resolve(Map map, Building wall, Common.ObservationContext context)
        {
            var id = wall.GetUniqueLoadID();
            var materials = NativeWallUpgradeObservationTools.Materials();
            var straight = new List<Candidate>(); var cleanup = new List<Candidate>(); var corner = new List<Candidate>();
            foreach (var normal in WallUpgradeSafety.Directions)
            {
                var n = normal;
                Func<Obs.WallUpgradeSite?> reread = () => NativeWallUpgradeObservationTools.Replacement(map, wall, n, materials, context);
                var site = reread();
                if (site == null) continue;
                var left = site.LeftSupport.Building.Id; var right = site.RightSupport.Building.Id;
                if (!WallUpgradeSafety.Corner(normal))
                {
                    if (site.CompletedBackups.Count != site.BackupCells.Count || site.BackupCells.Count == 0) continue;
                    var record = WallUpgradeSafety.NewRecord(map, id, id, left, right, site.CompletedBackups.Select(b => b.Building.Id), "", "",
                        wall.Position.x, wall.Position.z, normal.x, normal.z);
                    if (Try(record, site, reread) is Candidate found) straight.Add(found);
                    continue;
                }
                foreach (var material in materials)
                {
                    var record = WallUpgradeSafety.NewRecord(map, id, id, left, right, Enumerable.Empty<string>(), "", material.Stuff,
                        wall.Position.x, wall.Position.z, normal.x, normal.z);
                    if (Try(record, site, reread) is Candidate found) { corner.Add(found); break; }
                }
            }
            if (WallUpgradeSafety.Stone(wall))
                foreach (var normal in WallUpgradeSafety.Directions.Where(d => !WallUpgradeSafety.Corner(d)))
                {
                    var side = new IntVec3(-normal.z, 0, normal.x);
                    for (var k = -1; k <= 1; k++)
                    {
                        var origin = wall.Position - normal - side * k;
                        var permanent = NativeWallUpgradeObservationTools.ColonistWall(map, origin);
                        if (permanent == null || !WallUpgradeSafety.Stone(permanent) || permanent.Stuff != wall.Stuff) continue;
                        var n = normal; var p = permanent;
                        Func<Obs.WallUpgradeSite?> reread = () => NativeWallUpgradeObservationTools.Cleanup(map, p, n, context);
                        var site = reread();
                        if (site == null || !site.CompletedBackups.Any(b => b.Building.Id == id)) continue;
                        var record = WallUpgradeSafety.NewRecord(map, id, permanent.GetUniqueLoadID(), site.LeftSupport.Building.Id, site.RightSupport.Building.Id,
                            site.CompletedBackups.Select(b => b.Building.Id), permanent.GetUniqueLoadID(), "", origin.x, origin.z, normal.x, normal.z);
                        if (Try(record, site, reread) is Candidate found) cleanup.Add(found);
                    }
                }
            return new List<List<Candidate>> { straight, cleanup, corner };
        }

        private static bool Prepare(Operations.RemoveWall command, Common.ObservationContext context, out Candidate? candidate, out Common.Failure failure)
        {
            candidate = null; failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "RemoveWall requires one exact colonist wall identity.");
            if (!Valid(command)) return false;
            var map = ProtoBoundary.ResolveMap(context);
            if (map == null || Find.TickManager.CurTimeSpeed != TimeSpeed.Paused) { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Paused loaded map required."); return false; }
            var wall = NativeWallUpgradeObservationTools.ColonistWallById(map, command.Wall.EntityId);
            if (wall == null) { failure = ProtoBoundary.Fail(Common.FailureCode.NotFound, "No spawned colonist wall with that id is on the current map."); return false; }
            if (command.Wall.HasExpectedSnapshotToken && NativeBuildingObservationTools.Token(wall, context).Token != command.Wall.ExpectedSnapshotToken)
            { failure = ProtoBoundary.Fail(Common.FailureCode.StaleIdentity, "Wall snapshot changed; observe before new admission."); return false; }
            if (WallUpgradeSafety.Pending(wall) != null)
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Existing demolition designation is preserved; observe its original receipt."); return false; }
            lastBlocker = null;
            var classes = Resolve(map, wall, context).FirstOrDefault(c => c.Count > 0);
            if (classes == null)
            {
                var why = lastBlocker ?? "No wall-upgrade site: completed same-stuff stone backups, a standing stone permanent wall or open corner access is required.";
                failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, why); return false;
            }
            if (classes.Count > 1) { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Several admissible wall-upgrade sites share this wall; the request cannot name one."); return false; }
            candidate = classes[0];
            if (command.HasExpectedSiteSnapshotToken && candidate.Site.Snapshot?.Token != command.ExpectedSiteSnapshotToken)
            { candidate = null; failure = ProtoBoundary.Fail(Common.FailureCode.StaleIdentity, "Wall site snapshot changed; observe before new admission."); return false; }
            return true;
        }

        internal static Operations.PreviewReply Preview(Operations.RemoveWall command, Common.ObservationContext context)
        {
            if (!Prepare(command, context, out _, out var failure)) return new Operations.PreviewReply { Failure = failure };
            return new Operations.PreviewReply { Evaluated = new Operations.PreviewEvaluation { Context = context.Clone(), Accepted = true } };
        }

        internal static Operations.ExecuteReply Execute(NativeOperationState state, Operations.ExecuteRequest request, Common.ObservationContext context)
        {
            NativeAttemptLedger.Admission? handle = null; Receipts.EffectEvidence? evidence = null;
            var pre = request.Precondition; var command = request.Operation.RemoveWall;
            try
            {
                if (!Prepare(command, context, out var candidate, out var failure)) return new Operations.ExecuteReply { Failure = failure };
                if (!NativeControlAuthority.TryGetForGame(Current.Game, out var authority) || authority == null)
                    return new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(Common.FailureCode.AuthorityRequired, "Native authority is required.") };
                var guard = authority.Check(pre.ExpectedGeneration);
                context.NativeGeneration = guard.Snapshot.Generation;
                if (!guard.Success) return new Operations.ExecuteReply { Failure = NativeAuthorityControlTools.Refusal(guard.Error, context) };
                var admitted = state.Ledger.Admit("rimgovernor.operations.v1.Operations/Execute", request, context);
                if (admitted.Kind != NativeAttemptLedger.DecisionKind.Admitted) return admitted.DecidedReply;
                handle = admitted.AdmittedHandle;
                var map = ProtoBoundary.LoadedMap(context); var chosen = candidate!;
                var record = new NativeWallRemovalRecord(map, chosen.Record, chosen.Record.Target, chosen.Site.Snapshot?.Token ?? "",
                    chosen.Workers.Select(p => p.GetUniqueLoadID()).ToList(), chosen.Reread);
                state.WallRemovals.Add(pre.Attempt.Clone(), record);
                using (authority.Owned())
                {
                    if (!authority.Check(pre.ExpectedGeneration).Success || WallUpgradeSafety.Prepare(chosen.Record, out _) != null)
                        throw new InvalidOperationException("Wall removal site changed before designation.");
                    var blocker = WallUpgradeSafety.Commit(chosen.Record);
                    if (blocker != null) throw new InvalidOperationException(blocker);
                    evidence = new Receipts.EffectEvidence { Wall = record.Evidence() };
                }
                return new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Applied(state.Ledger, handle, pre.Attempt, context, evidence) };
            }
            catch (Exception error)
            {
                if (handle != null) return new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Uncertain(state.Ledger, handle, pre.Attempt, context, evidence, "Wall removal write interrupted: " + error.GetType().Name) };
                return new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(Common.FailureCode.NativeFailure, "Wall removal failed: " + error.GetType().Name) };
            }
        }

        // ReleaseWallRemovals retires every open guarded removal on this game
        // (the legacy release action); the receipt counts what was open.
        internal static Operations.PreviewReply PreviewRelease(Operations.ReleaseWallRemovals command, Common.ObservationContext context)
        {
            if (command.HasExpectedSnapshotToken && command.ExpectedSnapshotToken.Length == 0)
                return new Operations.PreviewReply { Failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "An expected snapshot token must not be blank.") };
            return new Operations.PreviewReply { Evaluated = new Operations.PreviewEvaluation { Context = context.Clone(), Accepted = true } };
        }

        internal static Operations.ExecuteReply ExecuteRelease(NativeOperationState state, Operations.ExecuteRequest request, Common.ObservationContext context)
        {
            NativeAttemptLedger.Admission? handle = null;
            var pre = request.Precondition;
            try
            {
                var preview = PreviewRelease(request.Operation.ReleaseWallRemovals, context);
                if (preview.Failure != null) return new Operations.ExecuteReply { Failure = preview.Failure };
                if (!NativeControlAuthority.TryGetForGame(Current.Game, out var authority) || authority == null)
                    return new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(Common.FailureCode.AuthorityRequired, "Native authority is required.") };
                var guard = authority.Check(pre.ExpectedGeneration);
                context.NativeGeneration = guard.Snapshot.Generation;
                if (!guard.Success) return new Operations.ExecuteReply { Failure = NativeAuthorityControlTools.Refusal(guard.Error, context) };
                var admitted = state.Ledger.Admit("rimgovernor.operations.v1.Operations/Execute", request, context);
                if (admitted.Kind != NativeAttemptLedger.DecisionKind.Admitted) return admitted.DecidedReply;
                handle = admitted.AdmittedHandle;
                int released;
                using (authority.Owned()) released = WallUpgradeSafety.ReleaseAll();
                var evidence = new Receipts.EffectEvidence { Wall = new Receipts.WallEffect { ReleasedCount = released, DemolitionObserved = false } };
                return new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Applied(state.Ledger, handle, pre.Attempt, context, evidence) };
            }
            catch (Exception error)
            {
                if (handle != null) return new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Uncertain(state.Ledger, handle, pre.Attempt, context, null!, "Wall release interrupted: " + error.GetType().Name) };
                return new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(Common.FailureCode.NativeFailure, "Wall release failed: " + error.GetType().Name) };
            }
        }
    }
}
