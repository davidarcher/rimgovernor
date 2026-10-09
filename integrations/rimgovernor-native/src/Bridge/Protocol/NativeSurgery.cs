#nullable enable
using System;
using System.Linq;
using RimWorld;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    // A medical ProductionBillIntent (patient set) on Actions/Apply: queue one medical operation
    // bill on one patient (a colonist, slave or colony prisoner) through
    // HealthCardUtility.CreateSurgeryBill, the bill a player's operations tab
    // queues. Native re-checks the patient, the recipe on the part and the
    // part's current hediffs live; a recipe that is a violation on the
    // patient needs acknowledge_violation. Other bills never block. The same
    // recipe already queued on the same part applies again. Native doctor
    // jobs choose the surgeon unless surgeon names one: the bill's
    // pawn restriction, set on an already queued bill too. Applied means queued.
    internal static class NativeSurgery
    {
        private static Common.Failure? Resolve(Operations.ProductionBillIntent? intent, Common.ObservationContext context,
            out Pawn? pawn, out RecipeDef? recipe, out BodyPartRecord? part, out Pawn? surgeon)
        {
            pawn = null; recipe = null; part = null; surgeon = null;
            if (intent == null || intent.HasBenchId || intent.Settings != null || intent.ReplaceOwnedBill != null
                || !ProtoBoundary.IsIdentifier(intent.Patient?.Id) || !ProtoBoundary.IsIdentifier(intent.RecipeDef)
                || (intent.Surgeon != null && !ProtoBoundary.IsIdentifier(intent.Surgeon.Id))
                || (intent.HasPartIndex && intent.PartIndex < 0))
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Surgery requires an exact patient, an exact recipe and a whole-body or exact part index.");
            var map = ProtoBoundary.LoadedMap(context);
            pawn = map.mapPawns.AllPawnsSpawned.ById(intent.Patient.Id);
            if (pawn == null || pawn.Dead || !pawn.RaceProps.Humanlike
                || !(pawn.Faction == Faction.OfPlayer || pawn.IsPrisonerOfColony))
                return ProtoBoundary.Fail(Common.FailureCode.NotFound, "Living humanlike colony patient is not spawned on this map.");
            if (intent.Surgeon != null)
            {
                var id = intent.Surgeon.Id;
                surgeon = map.mapPawns.FreeColonistsSpawned.ById(id);
                if (surgeon == null || surgeon == pawn || surgeon.WorkTypeIsDisabled(WorkTypeDefOf.Doctor))
                    return ProtoBoundary.Fail(Common.FailureCode.NotFound, "Surgeon " + id + " is not a spawned free colonist who can doctor.");
            }
            recipe = pawn.def.AllRecipes?.FirstOrDefault(r => r.defName == intent.RecipeDef);
            if (recipe == null) return ProtoBoundary.Fail(Common.FailureCode.NotFound, "Recipe " + intent.RecipeDef + " is not offered for this patient.");
            var parts = pawn.RaceProps.body.AllParts;
            if (intent.HasPartIndex)
            {
                if (intent.PartIndex >= parts.Count) return ProtoBoundary.Fail(Common.FailureCode.NotFound, "Body part index is out of range.");
                part = parts[intent.PartIndex];
            }
            if (recipe.targetsBodyPart != (part != null))
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, recipe.targetsBodyPart ? "Recipe needs a body part." : "Recipe is whole-body; no part index.");
            if (!recipe.AvailableNow || !recipe.Worker.AvailableReport(pawn).Accepted)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Recipe is not available now.");
            if (!recipe.AvailableOnNow(pawn, part)) return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Recipe is not available on this body part.");
            if (part != null && !recipe.Worker.GetPartsToApplyOn(pawn, recipe).Contains(part))
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Recipe does not apply to this body part.");
            if (Queued(pawn, recipe, part) != null) return null;
            if (Stands(pawn, recipe, part))
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Recipe " + recipe.defName + " already stands on this part.");
            if (!intent.AcknowledgeViolation && recipe.Worker.IsViolationOnPawn(pawn, part, Faction.OfPlayer))
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Recipe is a violation on this patient; acknowledge_violation is required.");
            return null;
        }

        private static bool Stands(Pawn pawn, RecipeDef recipe, BodyPartRecord? part) =>
            recipe.addsHediff != null && pawn.health.hediffSet.hediffs.Any(h => h.def == recipe.addsHediff && h.Part == part);

        private static Bill_Medical? Queued(Pawn pawn, RecipeDef recipe, BodyPartRecord? part) =>
            pawn.BillStack?.Bills.OfType<Bill_Medical>().FirstOrDefault(b => b.recipe == recipe && b.Part == part);

        internal static Common.Failure? Validate(Operations.ProductionBillIntent? intent, Common.ObservationContext context) => Resolve(intent, context, out _, out _, out _, out _);

        internal static Receipts.EffectEvidence Apply(Operations.ProductionBillIntent intent, Common.ObservationContext context)
        {
            var failure = Resolve(intent, context, out var pawn, out var recipe, out var part, out var surgeon);
            if (failure != null) throw new ApplyRefusedException(failure.Code, failure.Detail);
            var bill = Queued(pawn!, recipe!, part);
            if (bill == null)
            {
                HealthCardUtility.CreateSurgeryBill(pawn!, recipe!, part);
                bill = Queued(pawn!, recipe!, part);
                if (bill == null) throw new InvalidOperationException("Native surgery bill was not queued.");
            }
            if (surgeon != null && bill.PawnRestriction != surgeon) bill.SetPawnRestriction(surgeon);
            var effect = new Receipts.SurgeryEffect
            {
                PawnId = pawn!.GetUniqueLoadID(), RecipeDef = recipe!.defName, Bill = NativeRef.Of(bill.GetUniqueLoadID()),
                State = Receipts.SurgeryState.Queued,
            };
            if (part != null) effect.PartIndex = intent.PartIndex;
            return new Receipts.EffectEvidence { SurgeryBill = effect };
        }
    }
}
