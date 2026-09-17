#nullable enable
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using RimWorld;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Authority = RimGovernor.Protocol.Authority;
using Obs = RimGovernor.Protocol.Observations;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    // A work-only settings token never authorizes schedules, care or pawn
    // orders. AllowedArea (Assignment: a named area or an explicit clear) is
    // the one exception admitted alongside -- or instead of -- work
    // priorities through this same PatchPawn dispatch: the wire shape
    // already carries both fields together, and the CAS/admission/receipt
    // mechanics required are identical, so the combined snapshot token below
    // now covers whichever of the two fields a given command actually
    // touches. A pure work-priority write is consequently also sensitive to
    // an unrelated area change (and vice versa); that is the same
    // single-token-per-write-surface discipline PatchBuilding's settings
    // fields already share.
    internal static class NativeWorkSettings
    {
        private static bool ValidArea(Operations.Assignment? area) => area == null
            || area.ValueCase == Operations.Assignment.ValueOneofCase.Clear
            || (area.ValueCase == Operations.Assignment.ValueOneofCase.EntityId && ProtoBoundary.IsIdentifier(area.EntityId));

        internal static bool Valid(Operations.PatchPawn? command) => command != null
            && NativeDraftProtocol.ValidEntity(command.Pawn) && command.Work.Count <= 256
            && (command.Work.Count > 0 || command.AllowedArea != null)
            && command.Work.All(w => w.HasWorkTypeDef && ProtoBoundary.IsIdentifier(w.WorkTypeDef) && w.HasPriority && w.Priority >= 0 && w.Priority <= 4)
            && command.Work.Select(w => w.WorkTypeDef).Distinct(StringComparer.Ordinal).Count() == command.Work.Count
            && command.Schedule == null && !command.HasMedicalCare && !command.HasHostilityResponse && !command.HasSelfTend
            && !command.HasFollowDrafted && !command.HasFollowFieldwork && ValidArea(command.AllowedArea) && command.Master == null
            && command.Training.Count == 0 && !command.HasSlaughter && !command.HasReleaseToWild;

        private static bool Eligible(Pawn pawn) => pawn != null && !pawn.Destroyed && pawn.Spawned && ProtoBoundary.IsLoaded(pawn.Map)
            && pawn.IsFreeColonist && !pawn.Dead && !pawn.Downed && !pawn.Drafted && !pawn.InMentalState
            && pawn.workSettings?.Initialized == true && pawn.workSettings.EverWork;

        // area is the pawn's actual current restriction identity (empty
        // string when unrestricted), always the real GetUniqueLoadID()
        // value published elsewhere for this same pawn field
        // (NativePawnDetails' allowed_area_id) -- never the caller-supplied
        // request identifier, so a request naming an area by a different
        // (but equivalent) identifier scheme cannot desync the token.
        internal static string Token(Common.Identity identity, string pawn, bool manual, Obs.WorkSetting[] work, string area)
        {
            using (var bytes = new MemoryStream())
            {
                using (var writer = new BinaryWriter(bytes, Encoding.UTF8, true))
                {
                    writer.Write(identity.ColonyId); writer.Write(identity.LoadToken); writer.Write(identity.MapId);
                    writer.Write(pawn); writer.Write(manual); writer.Write(area);
                    foreach (var row in work.OrderBy(w => w.DefName, StringComparer.Ordinal))
                    { writer.Write(row.DefName); writer.Write(row.Priority); writer.Write(row.Disabled); }
                }
                using (var hash = SHA256.Create())
                    return "work-" + BitConverter.ToString(hash.ComputeHash(bytes.ToArray())).Replace("-", "").ToLowerInvariant();
            }
        }

        private static string CurrentAreaId(Pawn pawn) => pawn.playerSettings?.AreaRestrictionInPawnCurrentMap?.GetUniqueLoadID() ?? "";

        internal static Obs.SnapshotRef? Snapshot(Pawn pawn, Common.ObservationContext context)
        {
            if (!Eligible(pawn)) return null;
            var manual = PawnSettingsRead.ManualPriorities();
            var defs = DefDatabase<WorkTypeDef>.AllDefsListForReading;
            if (!manual.HasValue || defs.Count == 0 || defs.Count > 256) return null;
            var rows = defs.Select(d => new Obs.WorkSetting { DefName = d.defName, Priority = pawn.workSettings.GetPriority(d), Disabled = pawn.WorkTypeIsDisabled(d) }).ToArray();
            return new Obs.SnapshotRef { Context = context.Clone(), EntityId = pawn.GetUniqueLoadID(),
                Token = Token(context.Identity, pawn.GetUniqueLoadID(), manual.Value, rows, CurrentAreaId(pawn)) };
        }

        // Resolves a requested area identifier tolerantly against either the
        // GetUniqueLoadID() scheme published by observations_list_pawns'
        // allowed_area_id, or the Area.ID integer scheme
        // NativeRecoveryFacts's roofed-refuge census currently publishes: a
        // RecoveryAreaProposal candidate names a refuge from that census, so
        // this write path must accept its identifier as-is.
        private static Area_Allowed? ResolveArea(Pawn pawn, string entityId) => pawn.Map?.areaManager.AllAreas.OfType<Area_Allowed>()
            .FirstOrDefault(a => a.GetUniqueLoadID() == entityId || a.ID.ToString(CultureInfo.InvariantCulture) == entityId);

        private static bool PrepareArea(Operations.PatchPawn command, Pawn pawn, out bool requested, out bool clear, out Area_Allowed? area)
        {
            requested = command.AllowedArea != null; clear = false; area = null;
            if (!requested) return true;
            if (command.AllowedArea!.ValueCase == Operations.Assignment.ValueOneofCase.Clear) { clear = true; return true; }
            area = ResolveArea(pawn, command.AllowedArea.EntityId);
            return area != null;
        }

        private static bool Prepare(Operations.PatchPawn command, Common.ObservationContext context, out Pawn? pawn, out Common.Failure failure)
        {
            pawn = null;
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Work settings require an exact current work/area snapshot and only work priorities or an allowed-area assignment.");
            if (!Valid(command)) return false;
            pawn = ProtoBoundary.LoadedMap(context).mapPawns.AllPawnsSpawned.SingleOrDefault(p => p.GetUniqueLoadID() == command.Pawn.EntityId);
            if (pawn == null || Snapshot(pawn, context)?.Token != command.Pawn.ExpectedSnapshotToken) return false;
            var manual = PawnSettingsRead.ManualPriorities();
            if (!manual.HasValue) return false;
            foreach (var row in command.Work)
            {
                var def = DefDatabase<WorkTypeDef>.GetNamedSilentFail(row.WorkTypeDef);
                if (def == null || row.Priority != 0 && pawn.WorkTypeIsDisabled(def)
                    || !manual.Value && row.Priority != 0 && row.Priority != 3) return false;
            }
            if (!PrepareArea(command, pawn, out _, out _, out _)) return false;
            return true;
        }

        private static Receipts.EffectEvidence Evidence(Operations.PatchPawn command, string after, bool matches)
        {
            var effect = new Receipts.SettingsEffect { Snapshot = new Receipts.SnapshotEvidence { EntityId = command.Pawn.EntityId,
                BeforeToken = command.Pawn.ExpectedSnapshotToken, AfterToken = after } };
            foreach (var row in command.Work) effect.Fields.Add(new Receipts.FieldResult { Field = Receipts.SettingsField.Work,
                WorkTypeDef = row.WorkTypeDef, Outcome = matches ? Receipts.FieldOutcome.Applied : Receipts.FieldOutcome.Refused });
            if (command.AllowedArea != null) effect.Fields.Add(new Receipts.FieldResult { Field = Receipts.SettingsField.AllowedArea,
                Outcome = matches ? Receipts.FieldOutcome.Applied : Receipts.FieldOutcome.Refused });
            return new Receipts.EffectEvidence { Settings = effect };
        }

        internal static Operations.PreviewReply Preview(Operations.PatchPawn command, Common.ObservationContext context)
        {
            try
            {
                if (!Prepare(command, context, out _, out var failure)) return new Operations.PreviewReply { Failure = failure };
                return NativeOperationEnvelope.Preview(new Operations.PreviewReply { Evaluated = new Operations.PreviewEvaluation {
                    Context = context.Clone(), Accepted = true } });
            }
            catch (Exception error) { return new Operations.PreviewReply { Failure = ProtoBoundary.Fail(Common.FailureCode.NativeFailure, "Work preview failed: " + error.GetType().Name) }; }
        }

        internal static Operations.ExecuteReply Execute(NativeOperationState state, Operations.ExecuteRequest request, Common.ObservationContext context)
        {
            NativeAttemptLedger.Admission? handle = null; Receipts.EffectEvidence? evidence = null;
            var pre = request.Precondition; var command = request.Operation.PatchPawn;
            try
            {
                if (!Prepare(command, context, out var pawn, out var failure)) return new Operations.ExecuteReply { Failure = failure };
                if (!NativeControlAuthority.TryGetForGame(Current.Game, out var authority) || authority == null)
                    return new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(Common.FailureCode.AuthorityRequired, "Native authority is required.") };
                var guard = authority.Check(pre.ExpectedGeneration);
                context.NativeGeneration = guard.Snapshot.Generation;
                if (!guard.Success) return new Operations.ExecuteReply { Failure = NativeAuthorityControlTools.Refusal(guard.Error, context) };
                var admitted = state.Ledger.Admit("rimgovernor.operations.v1.Operations/Execute", request, context);
                if (admitted.Kind != NativeAttemptLedger.DecisionKind.Admitted) return admitted.DecidedReply;
                handle = admitted.AdmittedHandle;
                // Track even partial application. A setter failure cannot erase a write.
                state.WorkSettings.Add(pre.Attempt.Clone(), command.Clone());
                using (authority.Owned())
                {
                    if (!authority.Check(pre.ExpectedGeneration).Success
                        || !Prepare(command, context, out var checkedPawn, out failure) || !ReferenceEquals(pawn, checkedPawn))
                        throw new InvalidOperationException("Work admission changed before effect.");
                    foreach (var row in command.Work) pawn!.workSettings.SetPriority(DefDatabase<WorkTypeDef>.GetNamed(row.WorkTypeDef), row.Priority);
                    if (!PrepareArea(command, pawn!, out var requested, out var clear, out var area))
                        throw new InvalidOperationException("Requested allowed area is no longer resolvable.");
                    if (requested) pawn!.playerSettings.AreaRestrictionInPawnCurrentMap = clear ? null : area;
                    var snapshot = Snapshot(pawn!, context);
                    if (snapshot == null || !Matches(pawn!, command)) throw new InvalidOperationException("Native work settings require readback.");
                    evidence = Evidence(command, snapshot.Token, true);
                }
                return new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Applied(state.Ledger, handle, pre.Attempt, context, evidence) };
            }
            catch (Exception error)
            {
                return handle == null ? new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(Common.FailureCode.NativeFailure, "Work admission failed: " + error.GetType().Name) }
                    : new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Uncertain(state.Ledger, handle, pre.Attempt, context, evidence, "Admitted work settings require observation: " + error.GetType().Name) };
            }
        }

        private static bool Matches(Pawn pawn, Operations.PatchPawn command)
        {
            if (!command.Work.All(row => {
                var def = DefDatabase<WorkTypeDef>.GetNamedSilentFail(row.WorkTypeDef);
                return def != null && pawn.workSettings.GetPriority(def) == row.Priority;
            })) return false;
            if (command.AllowedArea == null) return true;
            var current = pawn.playerSettings?.AreaRestrictionInPawnCurrentMap;
            if (command.AllowedArea.ValueCase == Operations.Assignment.ValueOneofCase.Clear) return current == null;
            return current != null && (current.GetUniqueLoadID() == command.AllowedArea.EntityId
                || current.ID.ToString(CultureInfo.InvariantCulture) == command.AllowedArea.EntityId);
        }

        internal static Receipts.Progress Observe(Common.AttemptKey attempt, Common.ObservationContext context, Operations.PatchPawn command)
        {
            var result = new Receipts.Progress { Attempt = attempt.Clone(), Context = context.Clone(), CompleteInspection = false,
                Unknown = new Receipts.UnknownEffect { Reason = "Exact work settings are unavailable." } };
            try
            {
                var pawn = ProtoBoundary.LoadedMap(context).mapPawns.AllPawnsSpawned.SingleOrDefault(p => p.GetUniqueLoadID() == command.Pawn.EntityId);
                var snapshot = pawn == null ? null : Snapshot(pawn, context);
                if (snapshot == null) return result;
                var matches = Matches(pawn!, command); var evidence = Evidence(command, snapshot.Token, matches);
                result.CompleteInspection = true;
                if (matches) result.Completed = new Receipts.CompletedEffect { Evidence = evidence };
                else result.Unsuccessful = new Receipts.UnsuccessfulEffect { Reason = Receipts.UnsuccessfulReason.OutcomeNotAchieved, Evidence = evidence,
                    Detail = "Current work settings differ; do not restore over player changes." };
            }
            catch (Exception) { }
            return result;
        }
    }
}
