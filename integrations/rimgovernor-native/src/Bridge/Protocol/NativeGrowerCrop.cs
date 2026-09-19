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
    // PatchBuilding's plant_def field only: the crop one exact
    // Building_PlantGrower sows (Building_PlantGrower.SetPlantDefToGrow,
    // Assembly-CSharp 1.6). The crop is admitted under the game's own
    // set-plant gizmo rules -- sowable, carrying the grower's sow tag
    // (PlantUtility.CanSowOnGrower) and with its sow research finished --
    // so a definition the gizmo would not list is refused rather than
    // written. The CAS token covers the current crop; a player change
    // between observation and admission stales the write.
    internal static class NativeGrowerCrop
    {
        internal static bool Valid(Operations.PatchBuilding? command) => command != null
            && NativeDraftProtocol.ValidEntity(command.Building) && command.HasPlantDef && !string.IsNullOrEmpty(command.PlantDef)
            && !command.HasMedical && !command.HasTargetTemperature && !command.HasClaim && !command.HasForbidden && !command.HasPower
            && command.Owner == null && !command.HasForPrisoners;

        internal static bool Eligible(Thing thing) => thing is Building_PlantGrower grower && !grower.Destroyed && grower.Spawned
            && ProtoBoundary.IsLoaded(grower.Map);

        internal static bool Sowable(ThingDef? plant, Building_PlantGrower grower) => plant?.plant != null && plant.plant.Sowable
            && PlantUtility.CanSowOnGrower(plant, grower)
            && (plant.plant.sowResearchPrerequisites == null || plant.plant.sowResearchPrerequisites.All(r => r.IsFinished));

        internal static string Token(Common.Identity identity, Building_PlantGrower grower)
        {
            using (var bytes = new MemoryStream())
            {
                using (var writer = new BinaryWriter(bytes, Encoding.UTF8, true))
                {
                    writer.Write(identity.ColonyId); writer.Write(identity.LoadToken); writer.Write(identity.MapId);
                    writer.Write(grower.GetUniqueLoadID()); writer.Write(grower.GetPlantDefToGrow()?.defName ?? string.Empty);
                }
                using (var hash = SHA256.Create())
                    return "crop-" + BitConverter.ToString(hash.ComputeHash(bytes.ToArray())).Replace("-", "").ToLowerInvariant();
            }
        }

        internal static Obs.SnapshotRef? Snapshot(Thing thing, Common.ObservationContext context)
        {
            if (!Eligible(thing)) return null;
            return new Obs.SnapshotRef { Context = context.Clone(), EntityId = thing.GetUniqueLoadID(),
                Token = Token(context.Identity, (Building_PlantGrower)thing) };
        }

        // Settings carries the grower's current crop beside its CAS snapshot on
        // the building listing row.
        internal static Obs.BuildingSettings Settings(Building_PlantGrower grower, Common.ObservationContext context)
        {
            var settings = new Obs.BuildingSettings { Snapshot = Snapshot(grower, context) };
            var crop = grower.GetPlantDefToGrow();
            if (crop != null) settings.CropDefName = crop.defName;
            return settings;
        }

        private static bool Prepare(Operations.PatchBuilding command, Common.ObservationContext context,
            out Building_PlantGrower? grower, out ThingDef? plant, out Common.Failure failure)
        {
            grower = null; plant = null;
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest,
                "Grower patch requires an exact current crop snapshot and only plant_def. forbidden/power/owner/forPrisoners are not implemented by this adapter.");
            if (!Valid(command)) return false;
            var thing = ProtoBoundary.LoadedMap(context).listerThings.AllThings.SingleOrDefault(t => t.GetUniqueLoadID() == command.Building.EntityId);
            if (thing == null || !Eligible(thing))
            { failure = ProtoBoundary.Fail(Common.FailureCode.NotFound, "Exact plant grower is unavailable."); return false; }
            grower = (Building_PlantGrower)thing;
            plant = DefDatabase<ThingDef>.GetNamedSilentFail(command.PlantDef);
            if (!Sowable(plant, grower))
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Plant definition cannot be sown on this grower: it must be sowable, carry the grower's sow tag and have its sow research finished."); return false; }
            if (Snapshot(grower, context)?.Token != command.Building.ExpectedSnapshotToken)
            { failure = ProtoBoundary.Fail(Common.FailureCode.StaleIdentity, "Grower crop snapshot changed; observe before new admission."); return false; }
            return true;
        }

        private static Receipts.EffectEvidence Evidence(Operations.PatchBuilding command, string after, bool matches) =>
            new Receipts.EffectEvidence { Settings = new Receipts.SettingsEffect {
                Snapshot = new Receipts.SnapshotEvidence { EntityId = command.Building.EntityId,
                    BeforeToken = command.Building.ExpectedSnapshotToken, AfterToken = after },
                Fields = { new Receipts.FieldResult { Field = Receipts.SettingsField.GrowerCrop,
                    Outcome = matches ? Receipts.FieldOutcome.Applied : Receipts.FieldOutcome.Refused } } } };

        private static bool Matches(Building_PlantGrower grower, Operations.PatchBuilding command) =>
            grower.GetPlantDefToGrow()?.defName == command.PlantDef;

        internal static Operations.PreviewReply Preview(Operations.PatchBuilding command, Common.ObservationContext context)
        {
            try
            {
                if (!Prepare(command, context, out _, out _, out var failure)) return new Operations.PreviewReply { Failure = failure };
                return NativeOperationEnvelope.Preview(new Operations.PreviewReply { Evaluated = new Operations.PreviewEvaluation {
                    Context = context.Clone(), Accepted = true } });
            }
            catch (Exception error) { return new Operations.PreviewReply { Failure = ProtoBoundary.Fail(Common.FailureCode.NativeFailure, "Grower crop preview failed: " + error.GetType().Name) }; }
        }

        internal static Operations.ExecuteReply Execute(NativeOperationState state, Operations.ExecuteRequest request, Common.ObservationContext context)
        {
            NativeAttemptLedger.Admission? handle = null; Receipts.EffectEvidence? evidence = null;
            var pre = request.Precondition; var command = request.Operation.PatchBuilding;
            try
            {
                if (!Prepare(command, context, out var grower, out var plant, out var failure)) return new Operations.ExecuteReply { Failure = failure };
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
                        || !Prepare(command, context, out var checkedGrower, out _, out failure) || !ReferenceEquals(grower, checkedGrower))
                        throw new InvalidOperationException("Grower crop admission changed before effect.");
                    grower!.SetPlantDefToGrow(plant!);
                    var snapshot = Snapshot(grower, context);
                    if (snapshot == null || !Matches(grower, command)) throw new InvalidOperationException("Native grower crop requires readback.");
                    evidence = Evidence(command, snapshot.Token, true);
                }
                return new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Applied(state.Ledger, handle, pre.Attempt, context, evidence) };
            }
            catch (Exception error)
            {
                return handle == null ? new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(Common.FailureCode.NativeFailure, "Grower crop admission failed: " + error.GetType().Name) }
                    : new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Uncertain(state.Ledger, handle, pre.Attempt, context, evidence, "Admitted grower crop patch requires observation: " + error.GetType().Name) };
            }
        }

        internal static Receipts.Progress Observe(Common.AttemptKey attempt, Common.ObservationContext context, Operations.PatchBuilding command)
        {
            var result = new Receipts.Progress { Attempt = attempt.Clone(), Context = context.Clone(), CompleteInspection = false,
                Unknown = new Receipts.UnknownEffect { Reason = "Exact grower crop is unavailable." } };
            try
            {
                var thing = ProtoBoundary.LoadedMap(context).listerThings.AllThings.SingleOrDefault(t => t.GetUniqueLoadID() == command.Building.EntityId);
                var snapshot = thing == null ? null : Snapshot(thing, context);
                if (snapshot == null) return result;
                var matches = Matches((Building_PlantGrower)thing!, command); var evidence = Evidence(command, snapshot.Token, matches);
                result.CompleteInspection = true;
                if (matches) result.Completed = new Receipts.CompletedEffect { Evidence = evidence };
                else result.Unsuccessful = new Receipts.UnsuccessfulEffect { Reason = Receipts.UnsuccessfulReason.OutcomeNotAchieved,
                    Evidence = evidence, Detail = "Current crop differs; do not restore over player changes." };
            }
            catch (Exception) { }
            return result;
        }
    }
}
