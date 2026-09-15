#nullable enable
using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using RimWorld;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Authority = RimGovernor.Protocol.Authority;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    // Typed dispatch for Population-*'s direct-write prisoner custody order:
    // SetPrisonerInteraction (Recruit or MaintainOnly exclusive interaction).
    // Ports the legacy JSON home/population tool's (PopulationTools.Population)
    // eligibility checks behind the typed boundary. Like husbandry, this is an
    // immediate settings write with no native job.
    internal sealed class NativePrisonerInteractionRecord
    {
        internal readonly string PawnId;
        internal NativePrisonerInteractionRecord(string pawnId) { PawnId = pawnId; }
    }

    internal static class NativePrisonerInteractionOperations
    {
        private static string Hash(string text)
        {
            using (var hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant();
        }

        // Mirrors NativeHusbandryOperations.Settings: exact prisoner custody
        // interaction state, so a settings change (including one this same
        // order just made) invalidates a stale expected_snapshot_token.
        internal static string Settings(Pawn pawn) => "prisoner-interaction-" + Hash(string.Join("|",
            pawn.GetUniqueLoadID(), pawn.guest?.ExclusiveInteractionMode?.defName ?? "", pawn.guest?.Recruitable.ToString() ?? ""));

        internal static bool Eligible(Pawn? pawn) => pawn != null && !pawn.Destroyed && pawn.Spawned
            && pawn.Map == Find.CurrentMap && !pawn.Dead && pawn.IsPrisonerOfColony && pawn.guest != null;

        private static PrisonerInteractionModeDef? Wire(Operations.PrisonerInteraction interaction)
        {
            switch (interaction)
            {
                case Operations.PrisonerInteraction.AttemptRecruit: return PrisonerInteractionModeDefOf.AttemptRecruit;
                case Operations.PrisonerInteraction.MaintainOnly: return PrisonerInteractionModeDefOf.MaintainOnly;
                default: return null;
            }
        }

        private static bool ValidCommand(Operations.SetPrisonerInteraction? command) => command != null
            && NativeDraftProtocol.ValidEntity(command.Pawn) && command.HasInteraction;

        private static bool Prepare(Operations.SetPrisonerInteraction command, out Pawn? pawn, out PrisonerInteractionModeDef? def, out Common.Failure failure)
        {
            pawn = null; def = null;
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Prisoner interaction requires an exact current prisoner settings snapshot and a supported interaction.");
            if (!ValidCommand(command)) return false;
            pawn = Find.CurrentMap.mapPawns.AllPawnsSpawned.SingleOrDefault(p => p.GetUniqueLoadID() == command.Pawn.EntityId);
            if (pawn == null || !Eligible(pawn)) { failure = ProtoBoundary.Fail(Common.FailureCode.NotFound, "Exact eligible current-map colony prisoner is unavailable."); return false; }
            if (Settings(pawn) != command.Pawn.ExpectedSnapshotToken)
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Prisoner interaction settings changed; observe before new admission."); return false; }
            def = Wire(command.Interaction);
            if (def == null || def.isNonExclusiveInteraction || (def.hideIfNotRecruitable && !pawn.guest!.Recruitable) || (pawn.IsWildMan() && !def.allowOnWildMan))
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Unsupported or ineligible native prisoner interaction."); return false; }
            return true;
        }

        private static Receipts.EffectEvidence Evidence(Operations.SetPrisonerInteraction command, string after) => new Receipts.EffectEvidence
        {
            Prisoner = new Receipts.PrisonerEffect
            {
                Pawn = new Receipts.SnapshotEvidence { EntityId = command.Pawn.EntityId, BeforeToken = command.Pawn.ExpectedSnapshotToken, AfterToken = after },
                InteractionDef = command.Interaction == Operations.PrisonerInteraction.AttemptRecruit ? "AttemptRecruit" : "MaintainOnly",
            }
        };

        internal static Operations.PreviewReply Preview(Operations.SetPrisonerInteraction? command, Common.ObservationContext context)
        {
            try
            {
                if (!Prepare(command!, out var pawn, out _, out var failure))
                    return new Operations.PreviewReply { Failure = failure };
                return NativeOperationEnvelope.Preview(new Operations.PreviewReply { Evaluated = new Operations.PreviewEvaluation {
                    Context = context.Clone(), Accepted = true, Projected = Evidence(command!, Settings(pawn!)) } });
            }
            catch (Exception error) { return new Operations.PreviewReply { Failure = ProtoBoundary.Fail(Common.FailureCode.NativeFailure, "Prisoner interaction preview failed: " + error.GetType().Name) }; }
        }

        internal static Operations.ExecuteReply Execute(NativeOperationState state, Operations.ExecuteRequest request, Common.ObservationContext context)
        {
            var command = request.Operation.SetPrisonerInteraction; var pre = request.Precondition;
            NativeAttemptLedger.Admission? handle = null; Receipts.EffectEvidence? evidence = null;
            try
            {
                if (!Prepare(command, out var pawn, out _, out var failure)) return new Operations.ExecuteReply { Failure = failure };
                if (!NativeControlAuthority.TryGetForGame(Current.Game, out var authority) || authority == null)
                    return Refuse(Common.FailureCode.AuthorityRequired, "Current native authority is required.");
                var guard = authority.Check(pre.ExpectedGeneration);
                context.NativeGeneration = guard.Snapshot.Generation;
                if (!guard.Success) return new Operations.ExecuteReply { Failure = NativeAuthorityControlTools.Refusal(guard.Error, context) };
                var admission = state.Ledger.Admit("rimgovernor.operations.v1.Operations/Execute", request, context);
                if (admission.Kind != NativeAttemptLedger.DecisionKind.Admitted) return admission.Reply!;
                handle = admission.Handle!;
                using (authority.Owned())
                {
                    var current = authority.Check(pre.ExpectedGeneration);
                    if (!current.Success) throw new InvalidOperationException("Prisoner interaction authority changed before native effect.");
                    if (!Prepare(command, out pawn, out var def, out failure) || pawn == null || def == null)
                        throw new InvalidOperationException("Prisoner interaction prerequisites changed after admission.");
                    pawn.guest!.SetExclusiveInteraction(def);
                    var after = Settings(pawn);
                    state.PrisonerInteractions.Add(pre.Attempt.Clone(), new NativePrisonerInteractionRecord(pawn.GetUniqueLoadID()));
                    evidence = Evidence(command, after);
                    if (pawn.guest.ExclusiveInteractionMode != def) throw new InvalidOperationException("Native prisoner interaction readback did not apply.");
                }
                return new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Applied(state.Ledger, handle, pre.Attempt, context, evidence) };
            }
            catch (Exception error)
            {
                return handle == null
                    ? Refuse(Common.FailureCode.NativeFailure, "Prisoner interaction validation failed: " + error.GetType().Name)
                    : new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Uncertain(state.Ledger, handle, pre.Attempt, context, evidence!, "Admitted prisoner interaction order requires observation: " + error.GetType().Name) };
            }
        }

        internal static Receipts.Progress Observe(Common.AttemptKey attempt, Common.ObservationContext context, NativePrisonerInteractionRecord record)
        {
            var result = new Receipts.Progress { Attempt = attempt.Clone(), Context = context.Clone(), CompleteInspection = false };
            try
            {
                var pawn = Find.CurrentMap.mapPawns.AllPawnsSpawned.SingleOrDefault(p => p.GetUniqueLoadID() == record.PawnId);
                if (pawn == null || !Eligible(pawn))
                {
                    result.Unknown = new Receipts.UnknownEffect { Reason = "The exact prisoner is no longer observable; absence does not prove the setting held." };
                    return result;
                }
                result.CompleteInspection = true;
                var after = Settings(pawn);
                var interactionDef = pawn.guest!.ExclusiveInteractionMode?.defName;
                var evidence = new Receipts.EffectEvidence { Prisoner = new Receipts.PrisonerEffect {
                    Pawn = new Receipts.SnapshotEvidence { EntityId = record.PawnId, AfterToken = after }, InteractionDef = interactionDef ?? "" } };
                if (interactionDef == PrisonerInteractionModeDefOf.AttemptRecruit.defName || interactionDef == PrisonerInteractionModeDefOf.MaintainOnly.defName)
                    result.Completed = new Receipts.CompletedEffect { Evidence = evidence };
                else
                    result.Unsuccessful = new Receipts.UnsuccessfulEffect { Reason = Receipts.UnsuccessfulReason.OutcomeNotAchieved, Evidence = evidence,
                        Detail = "The prisoner interaction is no longer set; do not restore over player changes." };
            }
            catch (Exception) { result.CompleteInspection = false; result.Unknown = new Receipts.UnknownEffect { Reason = "Prisoner interaction inspection unavailable." }; }
            return result;
        }

        private static Operations.ExecuteReply Refuse(Common.FailureCode code, string detail) => new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(code, detail) };
    }
}
