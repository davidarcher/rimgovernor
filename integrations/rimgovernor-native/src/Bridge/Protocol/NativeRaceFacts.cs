#nullable enable
using System;
using System.Linq;
using System.Reflection;
using RimWorld;
using UnityEngine;
using Verse;
using Obs = RimGovernor.Protocol.Observations;

namespace HomeBridge.BridgeTools
{
    // What the game's own race code says about one race (#1722), carried on the
    // race's ThingDefFacts row once per load. The race's numbers and comps are
    // the def rows and the stat table; Go derives its views from those.
    internal static class NativeRaceFacts
    {
        internal static Obs.RaceFacts Facts(ThingDef def)
        {
            var race = def.race;
            var row = new Obs.RaceFacts { Animal = race.Animal, Mechanoid = race.IsMechanoid, Insect = race.Insect };
            // Pawn_TrainingTracker.CanAssignToTrain's race rules, without the pawn's own state.
            foreach (var trainable in DefDatabase<TrainableDef>.AllDefsListForReading.Where(t => Trainable(race, t)).OrderBy(t => t.defName, StringComparer.Ordinal))
                row.Trainables.Add(Named(trainable.defName));
            foreach (var food in NativeFoodPolicy.Foods().Where(f => race.CanEverEat(f)))
                row.EdibleDefs.Add(Named(food.defName));
            Husbandry(def, race, row);
            return row;
        }

        // The race's husbandry facts (#2238): game answers about the def that
        // the def rows and the stat table do not carry.
        private static void Husbandry(ThingDef def, RaceProperties race, Obs.RaceFacts row)
        {
            var stages = race.lifeStageAges ?? new System.Collections.Generic.List<LifeStageAge>();
            // Pawn_AgeTracker.AdultMinAge: a humanlike race's first adult stage, otherwise the last stage.
            var adultMinAge = race.Humanlike
                ? stages.FirstOrDefault(s => s.def.developmentalStage.Adult())?.minAge ?? 0f
                : stages.Count > 0 ? stages[stages.Count - 1].minAge : 0f;
            row.AdultMinAgeTicks = Mathf.FloorToInt(adultMinAge * 3600000f);
            if (StageTicks(stages, s => s.def.reproductive) is long reproductive) row.ReproductiveMinAgeTicks = reproductive;
            if (StageTicks(stages, s => s.def.milkable) is long milkable) row.MilkableMinAgeTicks = milkable;
            if (StageTicks(stages, s => s.def.shearable) is long shearable) row.ShearableMinAgeTicks = shearable;
            row.TamenessCanDecay = TrainableUtility.TamenessCanDecay(def);
            row.TamenessDecayPeriodTicks = TrainableUtility.DegradationPeriodTicks(def);
            row.TameChanceFactor = TameChanceFactorCurve().Evaluate(def.GetStatValueAbstract(StatDefOf.Wildness));
            if (race.meatDef != null)
            {
                row.MeatDef = Named(race.meatDef.defName);
                row.MeatAmount = def.GetStatValueAbstract(StatDefOf.MeatAmount);
            }
        }

        private static long? StageTicks(System.Collections.Generic.List<LifeStageAge> stages, Func<LifeStageAge, bool> match)
        {
            foreach (var stage in stages)
                if (match(stage)) return Mathf.FloorToInt(stage.minAge * 3600000f);
            return null;
        }

        // InteractionWorker_RecruitAttempt.TameChanceFactorCurve_Wildness is
        // private: read the game's own curve by name and fail naming it when a
        // game version moves it.
        private static SimpleCurve TameChanceFactorCurve()
        {
            var field = typeof(InteractionWorker_RecruitAttempt).GetField("TameChanceFactorCurve_Wildness", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            return field?.GetValue(null) as SimpleCurve
                ?? throw new InvalidOperationException("InteractionWorker_RecruitAttempt.TameChanceFactorCurve_Wildness is not a SimpleCurve of this game version.");
        }

        // The trainability rank, the minimum body size and the tag lists.
        private static bool Trainable(RaceProperties race, TrainableDef trainable)
        {
            if (race.trainability == null || trainable.requiredTrainability == null) return false;
            if (race.trainability.intelligenceOrder < trainable.requiredTrainability.intelligenceOrder) return false;
            if (race.baseBodySize < trainable.minBodySize) return false;
            if (race.untrainableTags != null && race.untrainableTags.Any(trainable.MatchesTag)) return false;
            return race.trainableTags == null || race.trainableTags.Count == 0 || race.trainableTags.Any(trainable.MatchesTag);
        }

        private static string Named(string defName) =>
            ProtoBoundary.IsIdentifier(defName) ? defName : throw new InvalidOperationException($"Def name '{defName}' is not an identifier.");
    }
}
