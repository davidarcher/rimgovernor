#nullable enable
using System;
using System.Linq;
using System.Reflection;
using RimWorld;
using Verse;
using Obs = RimGovernor.Protocol.Observations;

namespace HomeBridge.BridgeTools
{
    // What the game's own race code says about one race that its def fields do
    // not, carried on the race's ThingDefFacts row once per load: the foods the
    // race can eat, tameness decay, the wildness curve and the adult feed. The
    // race flags, trainables, meat and life stage ages are Go's, derived from the
    // def rows (bridge/race_rules.go).
    internal static class NativeRaceFacts
    {
        internal static Obs.RaceFacts Facts(ThingDef def)
        {
            var race = def.race;
            var row = new Obs.RaceFacts();
            foreach (var food in NativeFoodPolicy.Foods().Where(f => race.CanEverEat(f)))
                row.EdibleDefs.Add(Named(food.defName));
            row.TamenessCanDecay = TrainableUtility.TamenessCanDecay(def);
            row.TamenessDecayPeriodTicks = TrainableUtility.DegradationPeriodTicks(def);
            row.TameChanceFactor = TameChanceFactorCurve().Evaluate(def.GetStatValueAbstract(StatDefOf.Wildness));
            var stages = race.lifeStageAges;
            if (stages != null && stages.Count > 0)
                row.AdultFeedPerDay = SimplifiedPastureNutritionSimulator.NutritionConsumedPerDay(def, stages[stages.Count - 1].def);
            return row;
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

        private static string Named(string defName) =>
            ProtoBoundary.IsIdentifier(defName) ? defName : throw new InvalidOperationException($"Def name '{defName}' is not an identifier.");
    }
}
