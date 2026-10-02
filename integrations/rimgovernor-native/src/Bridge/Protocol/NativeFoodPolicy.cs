#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Obs = RimGovernor.Protocol.Observations;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    // Food policies (#1541): the food catalog PolicyFacts.foods classifies,
    // FoodPolicyIntent on Actions/Apply (the FoodPolicy labelled name, made
    // when missing, allows exactly the given foods; special filters keep
    // their values; corpses are always disallowed, #1543) and the pawn's current policy read. PawnSettingsIntent
    // .food_policy assigns it (NativePawnSettings.cs). Native suitability,
    // title, veneration and ingestion rules stay authoritative.
    internal static class NativeFoodPolicy
    {
        internal static bool IsFood(ThingDef d) => d.IsNutritionGivingIngestible && d.ingestible != null && !d.IsDrug && !d.IsCorpse;

        internal static IEnumerable<ThingDef> Foods() =>
            DefDatabase<ThingDef>.AllDefsListForReading.Where(IsFood).OrderBy(d => d.defName, StringComparer.Ordinal);

        internal static Obs.FoodKind Kind(ThingDef d)
        {
            switch (d.ingestible.preferability) {
                case FoodPreferability.MealAwful: return Obs.FoodKind.MealAwful;
                case FoodPreferability.MealSimple: return Obs.FoodKind.MealSimple;
                case FoodPreferability.MealFine: return Obs.FoodKind.MealFine;
                case FoodPreferability.MealLavish: return Obs.FoodKind.MealLavish;
            }
            if ((d.ingestible.foodType & FoodTypeFlags.Kibble) != 0) return Obs.FoodKind.Kibble;
            if (d == ThingDefOf.Hay) return Obs.FoodKind.Hay;
            if (d.IsMeat)
                switch (FoodUtility.GetMeatSourceCategory(d)) {
                    case MeatSourceCategory.Humanlike: return Obs.FoodKind.HumanMeat;
                    case MeatSourceCategory.Insect: return Obs.FoodKind.InsectMeat;
                    default: return Obs.FoodKind.RawMeat;
                }
            if (d.IsFungus) return Obs.FoodKind.Fungus;
            if (d.IsAnimalProduct) return Obs.FoodKind.AnimalProduct;
            if ((d.ingestible.foodType & FoodTypeFlags.VegetableOrFruit) != 0) return Obs.FoodKind.Vegetable;
            return Obs.FoodKind.Other;
        }

        internal static Obs.FoodDefinition Definition(ThingDef d)
        {
            var row = new Obs.FoodDefinition { DefName = d.defName, Kind = Kind(d) };
            if (row.Kind >= Obs.FoodKind.MealAwful && row.Kind <= Obs.FoodKind.MealLavish)
                row.Ingredients = FoodUtility.GetFoodKind(d) switch {
                    FoodKind.Meat => Obs.MealIngredients.Meat,
                    FoodKind.NonMeat => Obs.MealIngredients.NonMeat,
                    _ => Obs.MealIngredients.Any,
                };
            return row;
        }

        internal static IEnumerable<string> Allowed(FoodPolicy p) =>
            p.filter.AllowedThingDefs.Where(IsFood).Select(d => d.defName).OrderBy(d => d, StringComparer.Ordinal);

        // Native diet eligibility, never WillEat (which includes the policy).
        private static bool Eligible(Pawn pawn, ThingDef def) => def.IsNutritionGivingIngestible
            && !def.IsDrug && !def.IsCorpse && def.ingestible != null
            && (def.ingestible.foodType & FoodTypeFlags.Kibble) == 0
            && pawn.FoodIsSuitable(def) && !FoodUtility.IsVeneratedAnimalMeatOrCorpse(def, pawn)
            && !FoodUtility.InappropriateForTitle(def, pawn, allowIfStarving: true)
            && (!HumanFoodFacts.IsHumanMeat(def) || HumanFoodFacts.AcceptsMeat(pawn));

        internal static Obs.FoodRestriction? Read(Pawn pawn)
        {
            var policy = pawn.foodRestriction?.GetCurrentRespectedRestriction(pawn);
            if (policy == null || pawn.needs?.food == null || pawn.DevelopmentalStage.Baby()) return null;
            var row = new Obs.FoodRestriction { PolicyId = policy.GetUniqueLoadID() };
            row.AllowedDefs.Add(policy.filter.AllowedThingDefs.Select(d => d.defName).OrderBy(d => d, StringComparer.Ordinal));
            row.EligibleDefs.Add(DefDatabase<ThingDef>.AllDefsListForReading.Where(d => Eligible(pawn, d))
                .Select(d => d.defName).OrderBy(d => d, StringComparer.Ordinal));
            return row;
        }

        internal static Common.Failure? Validate(Operations.FoodPolicyIntent? intent, Common.ObservationContext context)
        {
            if (intent == null || !intent.HasName || !ProtoBoundary.IsIdentifier(intent.Name) || intent.Name.Length > 80
                || intent.AllowedDefs.Distinct().Count() != intent.AllowedDefs.Count
                || intent.AllowedDefs.Any(d => DefDatabase<ThingDef>.GetNamedSilentFail(d) is not ThingDef def || !IsFood(def)))
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "A food policy needs a name and distinct food definitions.");
            if (Current.Game.foodRestrictionDatabase.AllFoodRestrictions.Count(p => p.label == intent.Name) > 1)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "More than one food policy has this name.");
            return null;
        }

        internal static Receipts.EffectEvidence Apply(Operations.FoodPolicyIntent intent, Common.ObservationContext context)
        {
            var failure = Validate(intent, context);
            if (failure != null) throw new ApplyRefusedException(failure.Code, failure.Detail);
            var db = Current.Game.foodRestrictionDatabase;
            var want = intent.AllowedDefs.OrderBy(d => d, StringComparer.Ordinal).ToList();
            var policy = db.AllFoodRestrictions.FirstOrDefault(p => p.label == intent.Name);
            var outcome = Receipts.FieldOutcome.Unchanged;
            if (policy == null || !Allowed(policy).SequenceEqual(want))
            {
                if (policy == null) { policy = db.MakeNewFoodRestriction(); policy.label = intent.Name; }
                foreach (var d in Foods()) policy.filter.SetAllow(d, want.Contains(d.defName));
                if (!Allowed(policy).SequenceEqual(want)) throw new InvalidOperationException("Native food policy requires readback.");
                outcome = Receipts.FieldOutcome.Applied;
            }
            // No bot policy lets an animal eat a corpse, a colony pet's least of all (#1543).
            foreach (var corpse in DefDatabase<ThingDef>.AllDefsListForReading.Where(d => d.IsCorpse && policy.filter.Allows(d)).ToList()) {
                policy.filter.SetAllow(corpse, false);
                outcome = Receipts.FieldOutcome.Applied;
            }
            return new Receipts.EffectEvidence { Settings = new Receipts.SettingsEffect {
                Snapshot = new Receipts.SnapshotEvidence { EntityId = policy.GetUniqueLoadID() },
                Fields = { new Receipts.FieldResult { Field = Receipts.SettingsField.FoodRestriction, Outcome = outcome } } } };
        }
    }

    internal sealed class FoodPolicyActionHandler : IActionHandler
    {
        public Common.Failure? Validate(Operations.Action action, Common.ObservationContext context) => NativeFoodPolicy.Validate(action.FoodPolicy, context);
        public Receipts.EffectEvidence Apply(Operations.Action action, Common.ObservationContext context) => NativeFoodPolicy.Apply(action.FoodPolicy, context);
    }
}
