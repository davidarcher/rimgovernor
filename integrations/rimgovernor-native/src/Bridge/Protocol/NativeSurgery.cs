using System;
using System.Linq;
using RimWorld;
using Verse;

namespace RimGovernor.Native
{
    // A medical ProductionBillIntent (patient set) on Actions/Apply: queue one medical operation
    // bill on one patient (a colonist, slave or colony prisoner) through
    // HealthCardUtility.CreateSurgeryBill, the bill a player's operations tab
    // queues. Native re-checks the patient, the recipe on the part and the
    // part's current hediffs live; a recipe that is a violation on the
    // patient needs acknowledge_violation. Other bills never block. The same
    // recipe already queued on the same part applies again. Native doctor
    // jobs choose the surgeon. Applied means queued.
    internal static class NativeSurgery
    {
        private static RimGovernor.Brain.GameFacts.Common.Failure? Resolve(RimGovernor.Brain.GameOrders.Operations.ProductionBillIntent? intent, RimGovernor.Brain.GameFacts.Common.ObservationContext context,
            out Pawn? pawn, out RecipeDef? recipe, out BodyPartRecord? part)
        {
            pawn = null; recipe = null; part = null;
            if (intent == null || intent.PartIndex is null || intent.BenchId is not null || intent.Settings != null || intent.ReplaceOwnedBill != null
                || !GameThread.IsIdentifier(intent.Patient?.Id) || !GameThread.IsIdentifier(intent.RecipeDef)
                || (intent.PartIndex is RimGovernor.Brain.GameOrders.Operations.SurgeryTarget.Part { Index: < 0 }))
                return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "Surgery requires an exact patient, an exact recipe and a whole-body or exact part index.");
            var map = GameThread.LoadedMap(context);
            pawn = map.mapPawns.AllPawnsSpawned.ById(intent.Patient.Id);
            if (pawn == null || pawn.Dead || !pawn.RaceProps.Humanlike
                || !(pawn.Faction == Faction.OfPlayer || pawn.IsPrisonerOfColony))
                return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.NotFound, "Living humanlike colony patient is not spawned on this map.");
            recipe = pawn.def.AllRecipes?.FirstOrDefault(r => r.defName == (intent.RecipeDef ?? ""));
            if (recipe == null) return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.NotFound, "Recipe " + (intent.RecipeDef ?? "") + " is not offered for this patient.");
            var parts = pawn.RaceProps.body.AllParts;
            if (intent.PartIndex is RimGovernor.Brain.GameOrders.Operations.SurgeryTarget.Part selected)
            {
                if (selected.Index >= parts.Count) return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.NotFound, "Body part index is out of range.");
                part = parts[selected.Index];
            }
            if (recipe.targetsBodyPart != (part != null))
                return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, recipe.targetsBodyPart ? "Recipe needs a body part." : "Recipe is whole-body; no part index.");
            if (!recipe.AvailableNow || !recipe.Worker.AvailableReport(pawn).Accepted)
                return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "Recipe is not available now.");
            if (!recipe.AvailableOnNow(pawn, part)) return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "Recipe is not available on this body part.");
            if (part != null && !recipe.Worker.GetPartsToApplyOn(pawn, recipe).Contains(part))
                return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "Recipe does not apply to this body part.");
            if (Queued(pawn, recipe, part) != null) return null;
            if (Stands(pawn, recipe, part))
                return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "Recipe " + recipe.defName + " already stands on this part.");
            if (!intent.AcknowledgeViolation && recipe.Worker.IsViolationOnPawn(pawn, part, Faction.OfPlayer))
                return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "Recipe is a violation on this patient; acknowledge_violation is required.");
            return null;
        }

        private static bool Stands(Pawn pawn, RecipeDef recipe, BodyPartRecord? part) =>
            recipe.addsHediff != null && pawn.health.hediffSet.hediffs.Any(h => h.def == recipe.addsHediff && h.Part == part);

        private static Bill_Medical? Queued(Pawn pawn, RecipeDef recipe, BodyPartRecord? part) =>
            pawn.BillStack?.Bills.OfType<Bill_Medical>().FirstOrDefault(b => b.recipe == recipe && b.Part == part);

        internal static RimGovernor.Brain.GameFacts.Common.Failure? Validate(RimGovernor.Brain.GameOrders.Operations.ProductionBillIntent? intent, RimGovernor.Brain.GameFacts.Common.ObservationContext context) => Resolve(intent, context, out _, out _, out _);

        internal static RimGovernor.Brain.GameFacts.Receipts.EffectEvidence Apply(RimGovernor.Brain.GameOrders.Operations.ProductionBillIntent intent, RimGovernor.Brain.GameFacts.Common.ObservationContext context)
        {
            var failure = Resolve(intent, context, out var pawn, out var recipe, out var part);
            if (failure != null) throw new ApplyRefusedException(failure);
            if (pawn == null || recipe == null) throw new InvalidOperationException("Surgery prerequisites are unavailable.");
            var bill = Queued(pawn, recipe, part);
            if (bill == null)
            {
                HealthCardUtility.CreateSurgeryBill(pawn, recipe, part);
                bill = Queued(pawn, recipe, part);
                if (bill == null) throw new InvalidOperationException("Native surgery bill was not queued.");
            }
            var effect = new RimGovernor.Brain.GameFacts.Receipts.SurgeryEffect
            {
                PawnId = pawn.GetUniqueLoadID(), Bill = NativeRef.Of(bill.GetUniqueLoadID()),
                State = RimGovernor.Brain.GameFacts.Receipts.SurgeryState.Queued,
                Target = part is null ? new RimGovernor.Brain.GameFacts.Receipts.SurgeryTarget.WholeBodyCase() : new RimGovernor.Brain.GameFacts.Receipts.SurgeryTarget.PartCase(((RimGovernor.Brain.GameOrders.Operations.SurgeryTarget.Part)intent.PartIndex).Index),
            };
            return new RimGovernor.Brain.GameFacts.Receipts.EffectEvidence { Effect = effect };
        }
    }
}
