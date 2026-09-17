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
    // SetPrisonerInteraction (one exclusive interaction: Recruit, MaintainOnly,
    // ReduceResistance, Release, or Enslave/Convert while Ideology is active).
    // Ports the legacy JSON home/population tool's (PopulationTools.Population)
    // eligibility checks behind the typed boundary. Like husbandry, this is an
    // immediate settings write with no native job; progress observation reads
    // the prisoner's actual custody state afterwards (see Outcome) so a
    // completed or unsuccessful verdict rests on what happened to the pawn,
    // not only on the setting readback.
    internal sealed class NativePrisonerInteractionRecord
    {
        internal readonly string PawnId;
        internal readonly string InteractionDef;
        internal NativePrisonerInteractionRecord(string pawnId, string interactionDef) { PawnId = pawnId; InteractionDef = interactionDef; }
    }

    internal static class NativePrisonerInteractionOperations
    {
        private static string Hash(string text)
        {
            using (var hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant();
        }

        // Mirrors NativeHusbandryOperations.Settings: exact custody and
        // interaction state, so a settings or custody change (including one
        // this same order just made) invalidates a stale expected_snapshot_token.
        // NativePopulationObservation publishes this same token as each
        // person's PawnState.snapshot, the way ReadHusbandry publishes
        // NativeHusbandryOperations.Settings, so a census read is the CAS
        // evidence a write is admitted against.
        internal static string Settings(Pawn pawn) => "prisoner-interaction-" + Hash(string.Join("|",
            pawn.GetUniqueLoadID(), pawn.Dead.ToString(), pawn.IsPrisonerOfColony.ToString(), pawn.IsFreeColonist.ToString(),
            (pawn.HostFaction == Faction.OfPlayerSilentFail).ToString(),
            pawn.guest?.ExclusiveInteractionMode?.defName ?? "", pawn.guest?.Recruitable.ToString() ?? ""));

        internal static bool Eligible(Pawn? pawn) => pawn != null && !pawn.Destroyed && pawn.Spawned
            && ProtoBoundary.IsLoaded(pawn.Map) && !pawn.Dead && pawn.IsPrisonerOfColony && pawn.guest != null;

        // Wire names map to installed defs by exact defName; a DLC mode whose
        // def is absent (Ideology inactive) resolves null and is refused.
        internal static string? DefName(Operations.PrisonerInteraction interaction)
        {
            switch (interaction)
            {
                case Operations.PrisonerInteraction.AttemptRecruit: return "AttemptRecruit";
                case Operations.PrisonerInteraction.MaintainOnly: return "MaintainOnly";
                case Operations.PrisonerInteraction.ReduceResistance: return "ReduceResistance";
                case Operations.PrisonerInteraction.Release: return "Release";
                case Operations.PrisonerInteraction.Enslave: return "Enslave";
                case Operations.PrisonerInteraction.Convert: return "Convert";
                default: return null;
            }
        }

        private static PrisonerInteractionModeDef? Wire(Operations.PrisonerInteraction interaction)
        {
            var name = DefName(interaction);
            return name == null ? null : DefDatabase<PrisonerInteractionModeDef>.GetNamedSilentFail(name);
        }

        // Mirrors ITab_Pawn_Visitor's exclusive-row gates for the modes this
        // boundary exposes: never a non-exclusive toggle, recruit-gated modes
        // need a recruitable prisoner, wild men only take modes that allow
        // them, and classic ideology mode hides the Ideology-only modes.
        internal static bool Supported(Pawn pawn, PrisonerInteractionModeDef def) => !def.isNonExclusiveInteraction
            && !(def.hideIfNotRecruitable && !pawn.guest!.Recruitable)
            && !(pawn.IsWildMan() && !def.allowOnWildMan)
            && (def.allowInClassicIdeoMode || Find.IdeoManager == null || !Find.IdeoManager.classicMode);

        private static bool ValidCommand(Operations.SetPrisonerInteraction? command) => command != null
            && NativeDraftProtocol.ValidEntity(command.Pawn) && command.HasInteraction;

        private static bool Prepare(Operations.SetPrisonerInteraction command, Common.ObservationContext context, out Pawn? pawn, out PrisonerInteractionModeDef? def, out Common.Failure failure)
        {
            pawn = null; def = null;
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Prisoner interaction requires an exact current prisoner settings snapshot and a supported interaction.");
            if (!ValidCommand(command)) return false;
            pawn = ProtoBoundary.LoadedMap(context).mapPawns.AllPawnsSpawned.SingleOrDefault(p => p.GetUniqueLoadID() == command.Pawn.EntityId);
            if (pawn == null || !Eligible(pawn)) { failure = ProtoBoundary.Fail(Common.FailureCode.NotFound, "Exact eligible current-map colony prisoner is unavailable."); return false; }
            if (Settings(pawn) != command.Pawn.ExpectedSnapshotToken)
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Prisoner interaction settings changed; observe before new admission."); return false; }
            def = Wire(command.Interaction);
            if (def == null || !Supported(pawn, def))
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Unsupported or ineligible native prisoner interaction."); return false; }
            return true;
        }

        private static Receipts.EffectEvidence Evidence(Operations.SetPrisonerInteraction command, string after) => new Receipts.EffectEvidence
        {
            Prisoner = new Receipts.PrisonerEffect
            {
                Pawn = new Receipts.SnapshotEvidence { EntityId = command.Pawn.EntityId, BeforeToken = command.Pawn.ExpectedSnapshotToken, AfterToken = after },
                InteractionDef = DefName(command.Interaction) ?? "", Outcome = OutcomeHeld,
            }
        };

        internal const string OutcomeHeld = "held", OutcomeRecruited = "recruited", OutcomeEnslaved = "enslaved", OutcomeConverted = "converted",
            OutcomeReleased = "released", OutcomeEscaped = "escaped", OutcomeDied = "died";

        // Locates the ordered pawn wherever native keeps it now: spawned or
        // carried on the map, a corpse on the map, or a world pawn after
        // release/escape. Null means the pawn is not observable at all.
        private static Pawn? Locate(Map map, string pawnId)
        {
            var pawn = map.mapPawns.AllPawns.FirstOrDefault(p => p.GetUniqueLoadID() == pawnId);
            if (pawn != null) return pawn;
            foreach (var thing in map.listerThings.ThingsInGroup(ThingRequestGroup.Corpse))
                if (thing is Corpse corpse && corpse.InnerPawn?.GetUniqueLoadID() == pawnId) return corpse.InnerPawn;
            return Find.WorldPawns?.AllPawnsAliveOrDead.FirstOrDefault(p => p.GetUniqueLoadID() == pawnId);
        }

        // Actual custody outcome from native pawn state, independent of the
        // interaction setting: the evidence the progress verdict rests on.
        internal static string Outcome(Pawn pawn)
        {
            if (pawn.Dead) return OutcomeDied;
            if (pawn.IsPrisonerOfColony)
            {
                var ideo = Faction.OfPlayerSilentFail?.ideos?.PrimaryIdeo;
                return ideo != null && pawn.Ideo == ideo && pawn.guest?.ideoForConversion != null ? OutcomeConverted : OutcomeHeld;
            }
            if (pawn.IsSlaveOfColony) return OutcomeEnslaved;
            if (pawn.IsFreeColonist && pawn.Faction == Faction.OfPlayerSilentFail) return OutcomeRecruited;
            return pawn.guest?.Released == true ? OutcomeReleased : OutcomeEscaped;
        }

        // The terminal custody state each ordered mode is trying to reach;
        // any other terminal state is that order's unsuccessful outcome.
        private static string? Goal(string interactionDef)
        {
            switch (interactionDef)
            {
                case "AttemptRecruit": return OutcomeRecruited;
                case "Release": return OutcomeReleased;
                case "Enslave": return OutcomeEnslaved;
                case "Convert": return OutcomeConverted;
                default: return null;
            }
        }

        internal static Operations.PreviewReply Preview(Operations.SetPrisonerInteraction? command, Common.ObservationContext context)
        {
            try
            {
                if (!Prepare(command!, context, out var pawn, out _, out var failure))
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
                if (!Prepare(command, context, out var pawn, out _, out var failure)) return new Operations.ExecuteReply { Failure = failure };
                if (!NativeControlAuthority.TryGetForGame(Current.Game, out var authority) || authority == null)
                    return Refuse(Common.FailureCode.AuthorityRequired, "Current native authority is required.");
                var guard = authority.Check(pre.ExpectedGeneration);
                context.NativeGeneration = guard.Snapshot.Generation;
                if (!guard.Success) return new Operations.ExecuteReply { Failure = NativeAuthorityControlTools.Refusal(guard.Error, context) };
                var admission = state.Ledger.Admit("rimgovernor.operations.v1.Operations/Execute", request, context);
                if (admission.Kind != NativeAttemptLedger.DecisionKind.Admitted) return admission.DecidedReply;
                handle = admission.AdmittedHandle;
                using (authority.Owned())
                {
                    var current = authority.Check(pre.ExpectedGeneration);
                    if (!current.Success) throw new InvalidOperationException("Prisoner interaction authority changed before native effect.");
                    if (!Prepare(command, context, out pawn, out var def, out failure) || pawn == null || def == null)
                        throw new InvalidOperationException("Prisoner interaction prerequisites changed after admission.");
                    if (def.defName == "Convert" && pawn.guest!.ideoForConversion == null)
                        pawn.guest.ideoForConversion = Faction.OfPlayerSilentFail?.ideos?.PrimaryIdeo ?? throw new InvalidOperationException("Player ideo missing for conversion.");
                    pawn.guest!.SetExclusiveInteraction(def);
                    var after = Settings(pawn);
                    state.PrisonerInteractions.Add(pre.Attempt.Clone(), new NativePrisonerInteractionRecord(pawn.GetUniqueLoadID(), def.defName));
                    evidence = Evidence(command, after);
                    if (pawn.guest.ExclusiveInteractionMode != def) throw new InvalidOperationException("Native prisoner interaction readback did not apply.");
                }
                return new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Applied(state.Ledger, handle, pre.Attempt, context, evidence) };
            }
            catch (Exception error)
            {
                return handle == null
                    ? Refuse(Common.FailureCode.NativeFailure, "Prisoner interaction validation failed: " + error.GetType().Name)
                    : new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Uncertain(state.Ledger, handle, pre.Attempt, context, evidence, "Admitted prisoner interaction order requires observation: " + error.GetType().Name) };
            }
        }

        internal static Receipts.Progress Observe(Common.AttemptKey attempt, Common.ObservationContext context, NativePrisonerInteractionRecord record)
        {
            var result = new Receipts.Progress { Attempt = attempt.Clone(), Context = context.Clone(), CompleteInspection = false };
            try
            {
                var pawn = Locate(ProtoBoundary.LoadedMap(context), record.PawnId);
                if (pawn == null)
                {
                    result.Unknown = new Receipts.UnknownEffect { Reason = "The exact prisoner is no longer observable; absence does not prove the setting held." };
                    return result;
                }
                result.CompleteInspection = true;
                var outcome = Outcome(pawn);
                var interactionDef = pawn.guest?.ExclusiveInteractionMode?.defName;
                var evidence = new Receipts.EffectEvidence { Prisoner = new Receipts.PrisonerEffect {
                    Pawn = new Receipts.SnapshotEvidence { EntityId = record.PawnId, AfterToken = Settings(pawn) }, InteractionDef = interactionDef ?? "", Outcome = outcome } };
                if (outcome == OutcomeHeld)
                {
                    // Still in custody: the order holds while its setting does.
                    if (interactionDef == record.InteractionDef)
                        result.Completed = new Receipts.CompletedEffect { Evidence = evidence };
                    else
                        result.Unsuccessful = new Receipts.UnsuccessfulEffect { Reason = Receipts.UnsuccessfulReason.OutcomeNotAchieved, Evidence = evidence,
                            Detail = "The prisoner interaction is no longer set; do not restore over player changes." };
                }
                else if (outcome == Goal(record.InteractionDef))
                    result.Completed = new Receipts.CompletedEffect { Evidence = evidence };
                else
                    result.Unsuccessful = new Receipts.UnsuccessfulEffect { Reason = Receipts.UnsuccessfulReason.OutcomeNotAchieved, Evidence = evidence,
                        Detail = "The prisoner left colony custody (" + outcome + ") without the ordered interaction's outcome." };
            }
            catch (Exception) { result.CompleteInspection = false; result.Unknown = new Receipts.UnknownEffect { Reason = "Prisoner interaction inspection unavailable." }; }
            return result;
        }

        private static Operations.ExecuteReply Refuse(Common.FailureCode code, string detail) => new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(code, detail) };
    }
}
