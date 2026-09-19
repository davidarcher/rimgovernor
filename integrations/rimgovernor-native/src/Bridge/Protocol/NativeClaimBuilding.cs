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
    // PatchBuilding's claim field only (#459): the typed successor of the
    // game's Claim gizmo (Assembly-CSharp 1.6: Building.ClaimableBy(player)
    // requires def.Claimable, no faction or a non-player one, a spawned
    // building, and refuses a cryptosleep casket that holds anything or is
    // under a raid's spawn lock; then Building.SetFaction(player)). The CAS
    // token covers the faction and, for a casket, whether it holds anything,
    // so a claim raced by another faction change or a casket filling stales
    // the admission. Nothing here opens a casket.
    internal static class NativeClaimBuilding
    {
        internal static bool Valid(Operations.PatchBuilding? command) => command != null
            && NativeDraftProtocol.ValidEntity(command.Building) && command.HasClaim && command.Claim
            && !command.HasMedical && !command.HasTargetTemperature && !command.HasPlantDef && !command.HasForbidden && !command.HasPower
            && command.Owner == null && !command.HasForPrisoners;

        // Eligible is any claimable-by-definition building that is not one
        // of the other settings producers; a casket already the player's
        // stays eligible so the after-token can be read back.
        internal static bool Eligible(Thing thing) => thing is Building building && !building.Destroyed && building.Spawned
            && ProtoBoundary.IsLoaded(building.Map) && building.def != null && building.def.Claimable
            && !(thing is Building_Bed) && !(thing is Building_PlantGrower) && thing.TryGetComp<CompTempControl>() == null;

        internal static bool PlayerOwned(Building building) => building.Faction != null && building.Faction == Faction.OfPlayerSilentFail;

        internal static string Token(Common.Identity identity, Building building)
        {
            using (var bytes = new MemoryStream())
            {
                using (var writer = new BinaryWriter(bytes, Encoding.UTF8, true))
                {
                    writer.Write(identity.ColonyId); writer.Write(identity.LoadToken); writer.Write(identity.MapId);
                    writer.Write(building.GetUniqueLoadID()); writer.Write(building.Faction?.GetUniqueLoadID() ?? string.Empty);
                    writer.Write(building is Building_Casket casket && casket.HasAnyContents);
                }
                using (var hash = SHA256.Create())
                    return "claim-" + BitConverter.ToString(hash.ComputeHash(bytes.ToArray())).Replace("-", "").ToLowerInvariant();
            }
        }

        internal static Obs.SnapshotRef? Snapshot(Thing thing, Common.ObservationContext context)
        {
            if (!Eligible(thing)) return null;
            return new Obs.SnapshotRef { Context = context.Clone(), EntityId = thing.GetUniqueLoadID(),
                Token = Token(context.Identity, (Building)thing) };
        }

        // Settings carries the faction reading beside its CAS snapshot on
        // the building listing row.
        internal static Obs.BuildingSettings Settings(Building building, Common.ObservationContext context) =>
            new Obs.BuildingSettings { Snapshot = Snapshot(building, context), PlayerOwned = PlayerOwned(building) };

        private static bool Prepare(Operations.PatchBuilding command, Common.ObservationContext context,
            out Building? building, out Common.Failure failure)
        {
            building = null;
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest,
                "Claim requires an exact current building snapshot and claim=true alone.");
            if (!Valid(command)) return false;
            var thing = ProtoBoundary.LoadedMap(context).listerThings.AllThings.SingleOrDefault(t => t.GetUniqueLoadID() == command.Building.EntityId);
            if (thing == null || !Eligible(thing))
            { failure = ProtoBoundary.Fail(Common.FailureCode.NotFound, "Exact claimable building is unavailable."); return false; }
            building = (Building)thing;
            var player = Faction.OfPlayerSilentFail;
            if (player == null)
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "No player faction to claim for."); return false; }
            if (Snapshot(building, context)?.Token != command.Building.ExpectedSnapshotToken)
            { failure = ProtoBoundary.Fail(Common.FailureCode.StaleIdentity, "Building snapshot changed; observe before new admission."); return false; }
            if (!building.ClaimableBy(player))
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, PlayerOwned(building) ? "Building is already the player's." : "The game refuses the claim (contents, faction or a spawn lock)."); return false; }
            return true;
        }

        private static Receipts.EffectEvidence Evidence(Operations.PatchBuilding command, string after, bool matches) =>
            new Receipts.EffectEvidence { Settings = new Receipts.SettingsEffect {
                Snapshot = new Receipts.SnapshotEvidence { EntityId = command.Building.EntityId,
                    BeforeToken = command.Building.ExpectedSnapshotToken, AfterToken = after },
                Fields = { new Receipts.FieldResult { Field = Receipts.SettingsField.Claim,
                    Outcome = matches ? Receipts.FieldOutcome.Applied : Receipts.FieldOutcome.Refused } } } };

        internal static Operations.PreviewReply Preview(Operations.PatchBuilding command, Common.ObservationContext context)
        {
            try
            {
                if (!Prepare(command, context, out _, out var failure)) return new Operations.PreviewReply { Failure = failure };
                return NativeOperationEnvelope.Preview(new Operations.PreviewReply { Evaluated = new Operations.PreviewEvaluation {
                    Context = context.Clone(), Accepted = true } });
            }
            catch (Exception error) { return new Operations.PreviewReply { Failure = ProtoBoundary.Fail(Common.FailureCode.NativeFailure, "Claim preview failed: " + error.GetType().Name) }; }
        }

        internal static Operations.ExecuteReply Execute(NativeOperationState state, Operations.ExecuteRequest request, Common.ObservationContext context)
        {
            NativeAttemptLedger.Admission? handle = null; Receipts.EffectEvidence? evidence = null;
            var pre = request.Precondition; var command = request.Operation.PatchBuilding;
            try
            {
                if (!Prepare(command, context, out var building, out var failure)) return new Operations.ExecuteReply { Failure = failure };
                if (!NativeControlAuthority.TryGetForGame(Current.Game, out var authority) || authority == null)
                    return new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(Common.FailureCode.AuthorityRequired, "Native authority is required.") };
                var guard = authority.Check(pre.ExpectedGeneration);
                context.NativeGeneration = guard.Snapshot.Generation;
                if (!guard.Success) return new Operations.ExecuteReply { Failure = NativeAuthorityControlTools.Refusal(guard.Error, context) };
                var admitted = state.Ledger.Admit("rimgovernor.operations.v1.Operations/Execute", request, context);
                if (admitted.Kind != NativeAttemptLedger.DecisionKind.Admitted) return admitted.DecidedReply;
                handle = admitted.AdmittedHandle;
                // Track even a readback failure. A faction change cannot un-happen.
                state.BuildingPatches.Add(pre.Attempt.Clone(), command.Clone());
                using (authority.Owned())
                {
                    if (!authority.Check(pre.ExpectedGeneration).Success
                        || !Prepare(command, context, out var checkedBuilding, out failure) || !ReferenceEquals(building, checkedBuilding))
                        throw new InvalidOperationException("Claim admission changed before effect.");
                    building!.SetFaction(Faction.OfPlayer);
                    var snapshot = Snapshot(building, context);
                    if (snapshot == null || !PlayerOwned(building)) throw new InvalidOperationException("Native claim requires readback.");
                    evidence = Evidence(command, snapshot.Token, true);
                }
                return new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Applied(state.Ledger, handle, pre.Attempt, context, evidence) };
            }
            catch (Exception error)
            {
                return handle == null ? new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(Common.FailureCode.NativeFailure, "Claim admission failed: " + error.GetType().Name) }
                    : new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Uncertain(state.Ledger, handle, pre.Attempt, context, evidence, "Admitted claim requires observation: " + error.GetType().Name) };
            }
        }

        internal static Receipts.Progress Observe(Common.AttemptKey attempt, Common.ObservationContext context, Operations.PatchBuilding command)
        {
            var result = new Receipts.Progress { Attempt = attempt.Clone(), Context = context.Clone(), CompleteInspection = false,
                Unknown = new Receipts.UnknownEffect { Reason = "Exact building faction is unavailable." } };
            try
            {
                var thing = ProtoBoundary.LoadedMap(context).listerThings.AllThings.SingleOrDefault(t => t.GetUniqueLoadID() == command.Building.EntityId);
                var snapshot = thing == null ? null : Snapshot(thing, context);
                if (snapshot == null) return result;
                var matches = PlayerOwned((Building)thing!); var evidence = Evidence(command, snapshot.Token, matches);
                result.CompleteInspection = true;
                if (matches) result.Completed = new Receipts.CompletedEffect { Evidence = evidence };
                else result.Unsuccessful = new Receipts.UnsuccessfulEffect { Reason = Receipts.UnsuccessfulReason.OutcomeNotAchieved,
                    Evidence = evidence, Detail = "Building is not the player's; do not claim again over a later faction change." };
            }
            catch (Exception) { }
            return result;
        }
    }
}
