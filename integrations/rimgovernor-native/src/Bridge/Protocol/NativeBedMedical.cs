#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using RimWorld;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Obs = RimGovernor.Protocol.Observations;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    // PatchBuilding's medical field only -- the typed-Operation successor of
    // the legacy home/building_config tool's `medical=` write (see
    // BuildingConfigTool.cs's PlanMedical, verified against Assembly-CSharp
    // 1.6: RimWorld.Building_Bed.Medical's setter early-returns on an equal
    // value, otherwise calls RemoveAllOwners() and re-scores the room role).
    // A bed whose def has bed_canBeMedical false is refused rather than
    // written, because the game's setter would silently ignore it. The CAS
    // token covers the medical flag and the owner set, so an owner the
    // planner did not see being dropped stales the admission.
    internal static class NativeBedMedical
    {
        internal static bool Valid(Operations.PatchBuilding? command) => command != null
            && NativeDraftProtocol.ValidEntity(command.Building) && command.HasMedical
            && !command.HasTargetTemperature && !command.HasForbidden && !command.HasPower
            && command.Owner == null && !command.HasForPrisoners;

        internal static bool Eligible(Thing thing) => thing is Building_Bed bed && !bed.Destroyed && bed.Spawned
            && ProtoBoundary.IsLoaded(bed.Map) && bed.def?.building != null && bed.def.building.bed_humanlike;

        internal static string Token(Common.Identity identity, Building_Bed bed)
        {
            using (var bytes = new MemoryStream())
            {
                using (var writer = new BinaryWriter(bytes, Encoding.UTF8, true))
                {
                    writer.Write(identity.ColonyId); writer.Write(identity.LoadToken); writer.Write(identity.MapId);
                    writer.Write(bed.GetUniqueLoadID()); writer.Write(bed.Medical); writer.Write(bed.ForPrisoners);
                    foreach (var owner in bed.OwnersForReading.Select(p => p.GetUniqueLoadID()).OrderBy(id => id, StringComparer.Ordinal))
                        writer.Write(owner);
                }
                using (var hash = SHA256.Create())
                    return "bed-" + BitConverter.ToString(hash.ComputeHash(bytes.ToArray())).Replace("-", "").ToLowerInvariant();
            }
        }

        internal static Obs.SnapshotRef? Snapshot(Thing thing, Common.ObservationContext context)
        {
            if (!Eligible(thing)) return null;
            return new Obs.SnapshotRef { Context = context.Clone(), EntityId = thing.GetUniqueLoadID(),
                Token = Token(context.Identity, (Building_Bed)thing) };
        }

        // Settings carries the bed's writable medical state beside its CAS
        // snapshot on the building listing row; the settable flag itself is
        // only implied (a refused preview names the gate).
        internal static Obs.BuildingSettings Settings(Building_Bed bed, Common.ObservationContext context)
        {
            var settings = new Obs.BuildingSettings { Snapshot = Snapshot(bed, context), Medical = bed.Medical, ForPrisoners = bed.ForPrisoners };
            foreach (var owner in bed.OwnersForReading) settings.AssignedPawnIds.Add(owner.GetUniqueLoadID());
            return settings;
        }

        private static bool Prepare(Operations.PatchBuilding command, Common.ObservationContext context,
            out Building_Bed? bed, out Common.Failure failure)
        {
            bed = null;
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest,
                "Bed patch requires an exact current bed snapshot and only medical. forbidden/power/owner/forPrisoners are not implemented by this adapter.");
            if (!Valid(command)) return false;
            var thing = ProtoBoundary.LoadedMap(context).listerThings.AllThings.SingleOrDefault(t => t.GetUniqueLoadID() == command.Building.EntityId);
            if (thing == null || !Eligible(thing))
            { failure = ProtoBoundary.Fail(Common.FailureCode.NotFound, "Exact humanlike bed is unavailable."); return false; }
            bed = (Building_Bed)thing;
            if (command.Medical && !bed.def.building.bed_canBeMedical)
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Bed definition cannot be medical; the game's setter would ignore the write."); return false; }
            if (Snapshot(bed, context)?.Token != command.Building.ExpectedSnapshotToken)
            { failure = ProtoBoundary.Fail(Common.FailureCode.StaleIdentity, "Bed snapshot changed; observe before new admission."); return false; }
            return true;
        }

        private static Receipts.EffectEvidence Evidence(Operations.PatchBuilding command, string after, bool matches) =>
            new Receipts.EffectEvidence { Settings = new Receipts.SettingsEffect {
                Snapshot = new Receipts.SnapshotEvidence { EntityId = command.Building.EntityId,
                    BeforeToken = command.Building.ExpectedSnapshotToken, AfterToken = after },
                Fields = { new Receipts.FieldResult { Field = Receipts.SettingsField.MedicalBed,
                    Outcome = matches ? Receipts.FieldOutcome.Applied : Receipts.FieldOutcome.Refused } } } };

        internal static Operations.PreviewReply Preview(Operations.PatchBuilding command, Common.ObservationContext context)
        {
            try
            {
                if (!Prepare(command, context, out _, out var failure)) return new Operations.PreviewReply { Failure = failure };
                return NativeOperationEnvelope.Preview(new Operations.PreviewReply { Evaluated = new Operations.PreviewEvaluation {
                    Context = context.Clone(), Accepted = true } });
            }
            catch (Exception error) { return new Operations.PreviewReply { Failure = ProtoBoundary.Fail(Common.FailureCode.NativeFailure, "Bed medical preview failed: " + error.GetType().Name) }; }
        }

        internal static Operations.ExecuteReply Execute(NativeOperationState state, Operations.ExecuteRequest request, Common.ObservationContext context)
        {
            NativeAttemptLedger.Admission? handle = null; Receipts.EffectEvidence? evidence = null;
            var pre = request.Precondition; var command = request.Operation.PatchBuilding;
            try
            {
                if (!Prepare(command, context, out var bed, out var failure)) return new Operations.ExecuteReply { Failure = failure };
                if (!NativeControlAuthority.TryGetForGame(Current.Game, out var authority) || authority == null)
                    return new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(Common.FailureCode.AuthorityRequired, "Native authority is required.") };
                var guard = authority.Check(pre.ExpectedGeneration);
                context.NativeGeneration = guard.Snapshot.Generation;
                if (!guard.Success) return new Operations.ExecuteReply { Failure = NativeAuthorityControlTools.Refusal(guard.Error, context) };
                var admitted = state.Ledger.Admit("rimgovernor.operations.v1.Operations/Execute", request, context);
                if (admitted.Kind != NativeAttemptLedger.DecisionKind.Admitted) return admitted.DecidedReply;
                handle = admitted.AdmittedHandle;
                // Track even a readback failure. A setter cannot un-happen.
                state.BuildingPatches.Add(pre.Attempt.Clone(), command.Clone());
                using (authority.Owned())
                {
                    if (!authority.Check(pre.ExpectedGeneration).Success
                        || !Prepare(command, context, out var checkedBed, out failure) || !ReferenceEquals(bed, checkedBed))
                        throw new InvalidOperationException("Bed medical admission changed before effect.");
                    bed!.Medical = command.Medical;
                    var snapshot = Snapshot(bed, context);
                    if (snapshot == null || bed.Medical != command.Medical) throw new InvalidOperationException("Native bed medical requires readback.");
                    evidence = Evidence(command, snapshot.Token, true);
                }
                return new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Applied(state.Ledger, handle, pre.Attempt, context, evidence) };
            }
            catch (Exception error)
            {
                return handle == null ? new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(Common.FailureCode.NativeFailure, "Bed medical admission failed: " + error.GetType().Name) }
                    : new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Uncertain(state.Ledger, handle, pre.Attempt, context, evidence, "Admitted bed medical patch requires observation: " + error.GetType().Name) };
            }
        }

        internal static Receipts.Progress Observe(Common.AttemptKey attempt, Common.ObservationContext context, Operations.PatchBuilding command)
        {
            var result = new Receipts.Progress { Attempt = attempt.Clone(), Context = context.Clone(), CompleteInspection = false,
                Unknown = new Receipts.UnknownEffect { Reason = "Exact bed medical state is unavailable." } };
            try
            {
                var thing = ProtoBoundary.LoadedMap(context).listerThings.AllThings.SingleOrDefault(t => t.GetUniqueLoadID() == command.Building.EntityId);
                var snapshot = thing == null ? null : Snapshot(thing, context);
                if (snapshot == null) return result;
                var matches = ((Building_Bed)thing!).Medical == command.Medical; var evidence = Evidence(command, snapshot.Token, matches);
                result.CompleteInspection = true;
                if (matches) result.Completed = new Receipts.CompletedEffect { Evidence = evidence };
                else result.Unsuccessful = new Receipts.UnsuccessfulEffect { Reason = Receipts.UnsuccessfulReason.OutcomeNotAchieved,
                    Evidence = evidence, Detail = "Current medical flag differs; do not restore over player changes." };
            }
            catch (Exception) { }
            return result;
        }
    }
}
