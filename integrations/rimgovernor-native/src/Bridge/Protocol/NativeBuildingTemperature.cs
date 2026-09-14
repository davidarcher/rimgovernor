#nullable enable
using System;
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
    // PatchBuilding's target_temperature field only -- the typed-Operation
    // successor of the legacy home/building_config tool's `temperature=`
    // write (see BuildingConfigTool.cs's PlanTemperature, verified against
    // Assembly-CSharp 1.6.9676.17735: RimWorld.CompTempControl.targetTemperature
    // is a public settable float, clamped by the game's own -273.15..1000 C
    // interface range). forbidden/power/medical/owner/forPrisoners on
    // PatchBuilding are not yet implemented by this adapter; a command that
    // sets any of them is refused rather than silently ignored.
    internal static class NativeBuildingTemperature
    {
        private const float MinCelsius = -273.15f;
        private const float MaxCelsius = 1000f;

        internal static bool Valid(Operations.PatchBuilding? command) => command != null
            && NativeDraftProtocol.ValidEntity(command.Building) && command.HasTargetTemperature
            && !float.IsNaN(command.TargetTemperature) && !float.IsInfinity(command.TargetTemperature)
            && command.TargetTemperature >= MinCelsius && command.TargetTemperature <= MaxCelsius
            && !command.HasForbidden && !command.HasPower && !command.HasMedical
            && command.Owner == null && !command.HasForPrisoners;

        internal static bool Eligible(Thing thing) => thing != null && !thing.Destroyed
            && thing.Spawned && thing.Map == Find.CurrentMap && thing.TryGetComp<CompTempControl>() != null;

        internal static string Token(Common.Identity identity, string id, float target)
        {
            using (var bytes = new MemoryStream())
            {
                using (var writer = new BinaryWriter(bytes, Encoding.UTF8, true))
                {
                    writer.Write(identity.ColonyId); writer.Write(identity.LoadToken); writer.Write(identity.MapId);
                    writer.Write(id); writer.Write(target);
                }
                using (var hash = SHA256.Create())
                    return "temp-" + BitConverter.ToString(hash.ComputeHash(bytes.ToArray())).Replace("-", "").ToLowerInvariant();
            }
        }

        internal static Obs.SnapshotRef? Snapshot(Thing thing, Common.ObservationContext context)
        {
            if (!Eligible(thing)) return null;
            var comp = thing.TryGetComp<CompTempControl>();
            return new Obs.SnapshotRef { Context = context.Clone(), EntityId = thing.GetUniqueLoadID(),
                Token = Token(context.Identity, thing.GetUniqueLoadID(), comp.targetTemperature) };
        }

        private static bool Prepare(Operations.PatchBuilding command, Common.ObservationContext context,
            out Thing? thing, out Common.Failure failure)
        {
            thing = null;
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest,
                "Building patch requires an exact current target-temperature snapshot and only target_temperature, "
                + "in the game's -273.15 to 1000 C interface range. forbidden/power/medical/owner/forPrisoners are not implemented by this adapter.");
            if (!Valid(command)) return false;
            thing = Find.CurrentMap.listerThings.AllThings.SingleOrDefault(t => t.GetUniqueLoadID() == command.Building.EntityId);
            if (thing == null || !Eligible(thing))
            { failure = ProtoBoundary.Fail(Common.FailureCode.NotFound, "Exact building with CompTempControl is unavailable."); return false; }
            if (Snapshot(thing, context)?.Token != command.Building.ExpectedSnapshotToken)
            { failure = ProtoBoundary.Fail(Common.FailureCode.StaleIdentity, "Building temperature snapshot changed; observe before new admission."); return false; }
            return true;
        }

        private static Receipts.EffectEvidence Evidence(Operations.PatchBuilding command, string after, bool matches) =>
            new Receipts.EffectEvidence { Settings = new Receipts.SettingsEffect {
                Snapshot = new Receipts.SnapshotEvidence { EntityId = command.Building.EntityId,
                    BeforeToken = command.Building.ExpectedSnapshotToken, AfterToken = after },
                Fields = { new Receipts.FieldResult { Field = Receipts.SettingsField.Temperature,
                    Outcome = matches ? Receipts.FieldOutcome.Applied : Receipts.FieldOutcome.Refused } } } };

        internal static Operations.PreviewReply Preview(Operations.PatchBuilding command, Common.ObservationContext context)
        {
            try
            {
                if (!Prepare(command, context, out _, out var failure)) return new Operations.PreviewReply { Failure = failure };
                return NativeOperationEnvelope.Preview(new Operations.PreviewReply { Evaluated = new Operations.PreviewEvaluation {
                    Context = context.Clone(), Accepted = true } });
            }
            catch (Exception error) { return new Operations.PreviewReply { Failure = ProtoBoundary.Fail(Common.FailureCode.NativeFailure, "Building temperature preview failed: " + error.GetType().Name) }; }
        }

        internal static Operations.ExecuteReply Execute(NativeOperationState state, Operations.ExecuteRequest request, Common.ObservationContext context)
        {
            NativeAttemptLedger.Admission? handle = null; Authority.Owner? owner = null; Receipts.EffectEvidence? evidence = null;
            var pre = request.Precondition; var command = request.Operation.PatchBuilding;
            try
            {
                if (!Prepare(command, context, out var thing, out var failure)) return new Operations.ExecuteReply { Failure = failure };
                if (!NativeControlAuthority.TryGetForGame(Current.Game, out var authority) || authority == null)
                    return new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(Common.FailureCode.AuthorityRequired, "Native authority is required.") };
                var guard = authority.Check(pre.ExpectedGeneration, pre.LeaseId, pre.Attempt.ControllerSessionId);
                context.NativeGeneration = guard.Snapshot.Generation;
                if (!guard.Success) return new Operations.ExecuteReply { Failure = NativeAuthorityControlTools.Refusal(guard.Error, context) };
                owner = new Authority.Owner { ControllerSessionId = guard.Snapshot.Lease!.ControllerSessionId, PlayerDirection = guard.Snapshot.Lease.PlayerDirection };
                var admitted = state.Ledger.Admit("rimgovernor.operations.v1.Operations/Execute", request, context, owner);
                if (admitted.Kind != NativeAttemptLedger.DecisionKind.Admitted) return admitted.Reply!;
                handle = admitted.Handle;
                // Track even a readback failure. A setter cannot un-happen.
                state.BuildingTemperatures.Add(pre.Attempt.Clone(), command.Clone());
                using (authority.Owned())
                {
                    if (!authority.Check(pre.ExpectedGeneration, pre.LeaseId, pre.Attempt.ControllerSessionId).Success
                        || !Prepare(command, context, out var checkedThing, out failure) || !ReferenceEquals(thing, checkedThing))
                        throw new InvalidOperationException("Building temperature admission changed before effect.");
                    thing!.TryGetComp<CompTempControl>().targetTemperature = command.TargetTemperature;
                    var snapshot = Snapshot(thing!, context);
                    var matches = snapshot != null && Math.Abs(thing.TryGetComp<CompTempControl>().targetTemperature - command.TargetTemperature) < 0.001f;
                    if (snapshot == null || !matches) throw new InvalidOperationException("Native building temperature requires readback.");
                    evidence = Evidence(command, snapshot.Token, true);
                }
                return new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Applied(state.Ledger, handle, pre.Attempt, context, owner, evidence) };
            }
            catch (Exception error)
            {
                return handle == null ? new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(Common.FailureCode.NativeFailure, "Building temperature admission failed: " + error.GetType().Name) }
                    : new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Uncertain(state.Ledger, handle, pre.Attempt, context, owner!, evidence!, "Admitted building temperature requires observation: " + error.GetType().Name) };
            }
        }

        private static bool Matches(Thing thing, Operations.PatchBuilding command)
        {
            var comp = thing.TryGetComp<CompTempControl>();
            return comp != null && Math.Abs(comp.targetTemperature - command.TargetTemperature) < 0.001f;
        }

        internal static Receipts.Progress Observe(Common.AttemptKey attempt, Common.ObservationContext context, Operations.PatchBuilding command)
        {
            var result = new Receipts.Progress { Attempt = attempt.Clone(), Context = context.Clone(), CompleteInspection = false,
                Unknown = new Receipts.UnknownEffect { Reason = "Exact building target temperature is unavailable." } };
            try
            {
                var thing = Find.CurrentMap.listerThings.AllThings.SingleOrDefault(t => t.GetUniqueLoadID() == command.Building.EntityId);
                var snapshot = thing == null ? null : Snapshot(thing, context);
                if (snapshot == null) return result;
                var matches = Matches(thing!, command); var evidence = Evidence(command, snapshot.Token, matches);
                result.CompleteInspection = true;
                if (matches) result.Completed = new Receipts.CompletedEffect { Evidence = evidence };
                else result.Unsuccessful = new Receipts.UnsuccessfulEffect { Reason = Receipts.UnsuccessfulReason.OutcomeNotAchieved,
                    Evidence = evidence, Detail = "Current target temperature differs; do not restore over player changes." };
            }
            catch (Exception) { }
            return result;
        }
    }
}
