using System;
using System.Linq;
using System.Text;
using RimWorld;
using Verse;

namespace RimGovernor.Native
{
    // PrisonerInteractionIntent: one colony prisoner's exclusive
    // interaction (Recruit, MaintainOnly, ReduceResistance, Release, or
    // Enslave/Convert while Ideology is active), an immediate settings write
    // with no native job. Native checks the prisoner and the mode's
    // ITab_Pawn_Visitor gates live when it applies; a prisoner already set to
    // the mode applies again.
    internal sealed class PrisonerInteractionActionHandler : IActionHandler
    {
        // Wire names map to installed defs by exact defName; a DLC mode whose
        // def is absent (Ideology inactive) resolves null and is refused.
        private static string? DefName(RimGovernor.Brain.GameOrders.Operations.PrisonerInteraction interaction)
        {
            switch (interaction)
            {
                case RimGovernor.Brain.GameOrders.Operations.PrisonerInteraction.AttemptRecruit: return "AttemptRecruit";
                case RimGovernor.Brain.GameOrders.Operations.PrisonerInteraction.MaintainOnly: return "MaintainOnly";
                case RimGovernor.Brain.GameOrders.Operations.PrisonerInteraction.ReduceResistance: return "ReduceResistance";
                case RimGovernor.Brain.GameOrders.Operations.PrisonerInteraction.Release: return "Release";
                case RimGovernor.Brain.GameOrders.Operations.PrisonerInteraction.Enslave: return "Enslave";
                case RimGovernor.Brain.GameOrders.Operations.PrisonerInteraction.Convert: return "Convert";
                default: return null;
            }
        }

        // Mirrors ITab_Pawn_Visitor's exclusive-row gates: never a
        // non-exclusive toggle, recruit-gated modes need a recruitable
        // prisoner, wild men only take modes that allow them, and classic
        // ideology mode hides the Ideology-only modes.
        private static bool Supported(Pawn pawn, PrisonerInteractionModeDef def) => !def.isNonExclusiveInteraction
            && !(def.hideIfNotRecruitable && !pawn.guest.Recruitable)
            && !(pawn.IsWildMan() && !def.allowOnWildMan)
            && (def.allowInClassicIdeoMode || Find.IdeoManager == null || !Find.IdeoManager.classicMode);

        private static RimGovernor.Brain.GameFacts.Common.Failure? Resolve(RimGovernor.Brain.GameOrders.Operations.PrisonerInteractionIntent? intent, RimGovernor.Brain.GameFacts.Common.ObservationContext context, out Pawn? pawn, out PrisonerInteractionModeDef? def)
        {
            pawn = null; def = null;
            if (intent == null || intent.PawnId is null || !GameThread.IsIdentifier(intent.PawnId))
                return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "Prisoner interaction requires a pawn and an interaction.");
            var found = GameThread.LoadedMap(context).mapPawns.AllPawnsSpawned.ById(intent.PawnId);
            if (found == null || found.Destroyed || found.Dead || !found.IsPrisonerOfColony || found.guest == null)
                return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.NotFound, "Exact eligible current-map colony prisoner is unavailable.");
            var name = DefName(intent.Interaction);
            var mode = name == null ? null : DefDatabase<PrisonerInteractionModeDef>.GetNamedSilentFail(name);
            if (mode == null || !Supported(found, mode))
                return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "Unsupported or ineligible native prisoner interaction.");
            pawn = found; def = mode;
            return null;
        }

        public RimGovernor.Brain.GameFacts.Common.Failure? Validate(RimGovernor.Brain.GameOrders.Operations.Action action, RimGovernor.Brain.GameFacts.Common.ObservationContext context) => Resolve(action.Intent?.Prisoner, context, out _, out _);

        public RimGovernor.Brain.GameFacts.Receipts.EffectEvidence Apply(RimGovernor.Brain.GameOrders.Operations.Action action, RimGovernor.Brain.GameFacts.Common.ObservationContext context)
        {
            var failure = Resolve(action.Intent?.Prisoner, context, out var pawn, out var def);
            if (failure != null || pawn == null || def == null) throw new InvalidOperationException("Prisoner interaction prerequisites changed before apply: " + (failure?.Detail ?? ""));
            if (pawn.guest.ExclusiveInteractionMode != def)
            {
                if (def.defName == "Convert" && pawn.guest.ideoForConversion == null)
                    pawn.guest.ideoForConversion = Faction.OfPlayerSilentFail?.ideos?.PrimaryIdeo ?? throw new InvalidOperationException("Player ideo missing for conversion.");
                pawn.guest.SetExclusiveInteraction(def);
            }
            if (pawn.guest.ExclusiveInteractionMode != def) throw new InvalidOperationException("Native prisoner interaction readback did not apply.");
            return new RimGovernor.Brain.GameFacts.Receipts.EffectEvidence { Effect = new RimGovernor.Brain.GameFacts.Receipts.PrisonerEffect {
                InteractionDef = def.defName, Outcome = "held" } };
        }
    }
}
