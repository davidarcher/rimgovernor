#nullable enable
using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using RimWorld;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    internal static class NativePrisonerInteractionOperations
    {
        private static string Hash(string text)
        {
            using (var hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant();
        }

        // Exact custody and interaction state; NativePopulationObservation
        // publishes it as each person's PawnState.snapshot.
        internal static string Settings(Pawn pawn) => "prisoner-interaction-" + Hash(string.Join("|",
            pawn.GetUniqueLoadID(), pawn.Dead.ToString(), pawn.IsPrisonerOfColony.ToString(), pawn.IsFreeColonist.ToString(),
            (pawn.HostFaction == Faction.OfPlayerSilentFail).ToString(),
            pawn.guest?.ExclusiveInteractionMode?.defName ?? "", pawn.guest?.Recruitable.ToString() ?? ""));
    }

    // PrisonerInteractionIntent (#941): one colony prisoner's exclusive
    // interaction (Recruit, MaintainOnly, ReduceResistance, Release, or
    // Enslave/Convert while Ideology is active), an immediate settings write
    // with no native job. Native checks the prisoner and the mode's
    // ITab_Pawn_Visitor gates live when it applies; a prisoner already set to
    // the mode applies again.
    internal sealed class PrisonerInteractionActionHandler : IActionHandler
    {
        // Wire names map to installed defs by exact defName; a DLC mode whose
        // def is absent (Ideology inactive) resolves null and is refused.
        private static string? DefName(Operations.PrisonerInteraction interaction)
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

        // Mirrors ITab_Pawn_Visitor's exclusive-row gates: never a
        // non-exclusive toggle, recruit-gated modes need a recruitable
        // prisoner, wild men only take modes that allow them, and classic
        // ideology mode hides the Ideology-only modes.
        private static bool Supported(Pawn pawn, PrisonerInteractionModeDef def) => !def.isNonExclusiveInteraction
            && !(def.hideIfNotRecruitable && !pawn.guest!.Recruitable)
            && !(pawn.IsWildMan() && !def.allowOnWildMan)
            && (def.allowInClassicIdeoMode || Find.IdeoManager == null || !Find.IdeoManager.classicMode);

        private static Common.Failure? Resolve(Operations.PrisonerInteractionIntent? intent, Common.ObservationContext context, out Pawn pawn, out PrisonerInteractionModeDef def)
        {
            pawn = null!; def = null!;
            if (intent == null || !intent.HasPawnId || !ProtoBoundary.IsIdentifier(intent.PawnId) || !intent.HasInteraction)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Prisoner interaction requires a pawn and an interaction.");
            var found = ProtoBoundary.LoadedMap(context).mapPawns.AllPawnsSpawned.ById(intent.PawnId);
            if (found == null || found.Destroyed || found.Dead || !found.IsPrisonerOfColony || found.guest == null)
                return ProtoBoundary.Fail(Common.FailureCode.NotFound, "Exact eligible current-map colony prisoner is unavailable.");
            var name = DefName(intent.Interaction);
            var mode = name == null ? null : DefDatabase<PrisonerInteractionModeDef>.GetNamedSilentFail(name);
            if (mode == null || !Supported(found, mode))
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Unsupported or ineligible native prisoner interaction.");
            pawn = found; def = mode;
            return null;
        }

        public Common.Failure? Validate(Operations.Action action, Common.ObservationContext context) => Resolve(action.Prisoner, context, out _, out _);

        public Receipts.EffectEvidence Apply(Operations.Action action, Common.ObservationContext context)
        {
            var failure = Resolve(action.Prisoner, context, out var pawn, out var def);
            if (failure != null) throw new InvalidOperationException("Prisoner interaction prerequisites changed before apply: " + failure.Detail);
            var before = NativePrisonerInteractionOperations.Settings(pawn);
            if (pawn.guest!.ExclusiveInteractionMode != def)
            {
                if (def.defName == "Convert" && pawn.guest.ideoForConversion == null)
                    pawn.guest.ideoForConversion = Faction.OfPlayerSilentFail?.ideos?.PrimaryIdeo ?? throw new InvalidOperationException("Player ideo missing for conversion.");
                pawn.guest.SetExclusiveInteraction(def);
            }
            if (pawn.guest.ExclusiveInteractionMode != def) throw new InvalidOperationException("Native prisoner interaction readback did not apply.");
            return new Receipts.EffectEvidence { Prisoner = new Receipts.PrisonerEffect {
                Pawn = new Receipts.SnapshotEvidence { EntityId = pawn.GetUniqueLoadID(), BeforeToken = before, AfterToken = NativePrisonerInteractionOperations.Settings(pawn) },
                InteractionDef = def.defName, Outcome = "held" } };
        }
    }
}
