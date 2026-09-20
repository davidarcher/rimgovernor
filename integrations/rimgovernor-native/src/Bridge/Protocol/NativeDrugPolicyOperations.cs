#nullable enable
using System;
using System.Linq;
using RimWorld;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;
using Obs = RimGovernor.Protocol.Observations;
namespace HomeBridge.BridgeTools
{
    internal static class NativeDrugPolicyOperations
    {
        private static Obs.SnapshotRef? Snapshot(Pawn pawn, Common.ObservationContext context) => NativeWorkSettings.Snapshot(pawn, context);
        private static bool Matches(Pawn pawn, Operations.SetDrugPolicy command) => NativeDrugPolicy.Matches(pawn, command.Name);
        private static bool Prepare(Operations.SetDrugPolicy command, Common.ObservationContext context, out Pawn? pawn, out Common.Failure failure)
        {
            pawn = null;
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Drug policy requires an exact eligible pawn snapshot and an policy name.");
            if (command == null || !NativeDraftProtocol.ValidEntity(command.Pawn) || !command.HasName || !ProtoBoundary.IsIdentifier(command.Name)) return false;
            pawn = ProtoBoundary.LoadedMap(context).mapPawns.AllPawnsSpawned.SingleOrDefault(p => p.GetUniqueLoadID() == command.Pawn.EntityId);
            return pawn != null && pawn.IsFreeColonist && !pawn.Dead && NativeDrugPolicy.Writable(pawn) && Snapshot(pawn, context)?.Token == command.Pawn.ExpectedSnapshotToken;
        }
        private static Receipts.EffectEvidence Evidence(Operations.SetDrugPolicy command, string after, bool matches)
        {
            var effect = new Receipts.SettingsEffect { Snapshot = new Receipts.SnapshotEvidence { EntityId = command.Pawn.EntityId, BeforeToken = command.Pawn.ExpectedSnapshotToken, AfterToken = after } };
            effect.Fields.Add(new Receipts.FieldResult { Field = Receipts.SettingsField.DrugPolicy, Outcome = matches ? Receipts.FieldOutcome.Applied : Receipts.FieldOutcome.Refused });
            return new Receipts.EffectEvidence { Settings = effect };
        }
        internal static Operations.PreviewReply Preview(Operations.SetDrugPolicy command, Common.ObservationContext context)
        {
            if (!Prepare(command, context, out _, out var failure)) return new Operations.PreviewReply { Failure = failure };
            return NativeOperationEnvelope.Preview(new Operations.PreviewReply { Evaluated = new Operations.PreviewEvaluation { Context = context.Clone(), Accepted = true } });
        }
        internal static Operations.ExecuteReply Execute(NativeOperationState state, Operations.ExecuteRequest request, Common.ObservationContext context)
        {
            NativeAttemptLedger.Admission? handle = null; Receipts.EffectEvidence? evidence = null;
            var pre = request.Precondition; var command = request.Operation.SetDrugPolicy;
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
                state.DrugPolicies.Add(pre.Attempt.Clone(), command.Clone());
                using (authority.Owned())
                {
                    if (!authority.Check(pre.ExpectedGeneration).Success
                        || !Prepare(command, context, out var checkedPawn, out failure) || !ReferenceEquals(pawn, checkedPawn))
                        throw new InvalidOperationException("Drug policy admission changed before effect.");
                    NativeDrugPolicy.Apply(pawn!, command.Name);
                    var snapshot = Snapshot(pawn!, context);
                    if (snapshot == null || !Matches(pawn!, command)) throw new InvalidOperationException("Native drug policy require readback.");
                    evidence = Evidence(command, snapshot.Token, true);
                }
                return new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Applied(state.Ledger, handle, pre.Attempt, context, evidence) };
            }
            catch (Exception error)
            {
                return handle == null ? new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(Common.FailureCode.NativeFailure, "Drug policy admission failed: " + error.GetType().Name) }
                    : new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Uncertain(state.Ledger, handle, pre.Attempt, context, evidence, "Admitted drug policy require observation: " + error.GetType().Name) };
            }
        }

        internal static Receipts.Progress Observe(Common.AttemptKey attempt, Common.ObservationContext context, Operations.SetDrugPolicy command)
        {
            var result = new Receipts.Progress { Attempt = attempt.Clone(), Context = context.Clone(), CompleteInspection = false,
                Unknown = new Receipts.UnknownEffect { Reason = "Exact drug policy are unavailable." } };
            try
            {
                var pawn = ProtoBoundary.LoadedMap(context).mapPawns.AllPawnsSpawned.SingleOrDefault(p => p.GetUniqueLoadID() == command.Pawn.EntityId);
                var snapshot = pawn == null ? null : Snapshot(pawn, context);
                if (snapshot == null) return result;
                var matches = Matches(pawn!, command); var evidence = Evidence(command, snapshot.Token, matches);
                result.CompleteInspection = true;
                if (matches) result.Completed = new Receipts.CompletedEffect { Evidence = evidence };
                else result.Unsuccessful = new Receipts.UnsuccessfulEffect { Reason = Receipts.UnsuccessfulReason.OutcomeNotAchieved, Evidence = evidence,
                    Detail = "Current drug policy differs from the requested policy." };
            }
            catch (Exception) { }
            return result;
        }
    }
}
