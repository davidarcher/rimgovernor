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
    // Food policies: the food kinds the definition catalog classifies,
    // FoodPolicyIntent on Actions/Apply (the FoodPolicy labelled name, made
    // when missing, allows exactly the given foods; special filters keep
    // their values; corpses are always disallowed) and the pawn's current policy read. PawnSettingsIntent
    // .food_policy assigns it (NativePawnSettings.cs). Native suitability,
    // title, veneration and ingestion rules stay authoritative.
    internal static class NativeFoodPolicy
    {
        internal static bool IsFood(ThingDef d) => d.IsNutritionGivingIngestible && d.ingestible != null && !d.IsDrug && !d.IsCorpse;

        // The colonists that can ever eat a food, judged per eater class:
        // a baby is an eater only of a food that babiesCanIngest (the game's
        // FoodUtility.FoodIsSuitable), so a colony with a baby still has meal
        // eaters. A food is human food when every such eater WillEat it and
        // there is at least one.
        internal static List<Pawn> Eaters(IEnumerable<Pawn> people, ThingDef d) =>
            people.Where(p => !p.DevelopmentalStage.Baby() || d.ingestible.babiesCanIngest).ToList();

        internal static bool EatenByAll(IEnumerable<Pawn> people, ThingDef d)
        {
            var eaters = Eaters(people, d);
            return eaters.Count > 0 && eaters.All(p => p.WillEat(d));
        }

        internal static IEnumerable<ThingDef> Foods() =>
            DefDatabase<ThingDef>.AllDefsListForReading.Where(IsFood).OrderBy(d => d.defName, StringComparer.Ordinal);

        // The game's own race facts of one race def, computed once per load for
        // the definition catalog (a food's kind and a meal's ingredients are Go's,
        // over the def rows).
        internal static Obs.ThingDefFacts Facts(ThingDef d) =>
            new Obs.ThingDefFacts { DefName = d.defName, Race = NativeRaceFacts.Facts(d) };

        internal static IEnumerable<string> Allowed(FoodPolicy p) =>
            p.filter.AllowedThingDefs.Where(IsFood).Select(d => d.defName).OrderBy(d => d, StringComparer.Ordinal);

        internal static Obs.FoodRestriction? Read(Pawn pawn)
        {
            var policy = pawn.foodRestriction?.GetCurrentRespectedRestriction(pawn);
            if (policy == null || pawn.needs?.food == null || pawn.DevelopmentalStage.Baby()) return null;
            var row = new Obs.FoodRestriction { PolicyId = policy.GetUniqueLoadID() };
            row.AllowedDefs.Add(policy.filter.AllowedThingDefs.Select(d => d.defName).OrderBy(d => d, StringComparer.Ordinal));
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
            if (failure != null) throw new ApplyRefusedException(failure);
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
            // No bot policy lets an animal eat a corpse, a colony pet's least of all.
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
