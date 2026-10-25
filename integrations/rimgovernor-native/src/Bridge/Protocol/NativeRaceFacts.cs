using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RimWorld;
using Verse;
using BrainRaceFacts = RimGovernor.Brain.Bridge.RaceGameFacts;

namespace RimGovernor.Native
{
    // What the game's own race code says about one race that its def fields do
    // not, computed on the game thread once per load by the definition catalog
    // read and published for the in-process brain's catalog (NativeCatalogFactory):
    // the foods the race can eat, the wildness curve and the adult
    // feed. The race flags, trainables, meat and life stage ages are the brain's,
    // derived from the def objects.
    internal static class NativeRaceFacts
    {
        private sealed class Published
        {
            internal readonly Game? Game;
            internal readonly string LoadToken;
            internal readonly IReadOnlyDictionary<string, BrainRaceFacts> Races;
            internal Published(Game? game, string loadToken, IReadOnlyDictionary<string, BrainRaceFacts> races) { Game = game; LoadToken = loadToken; Races = races; }
        }

        private static volatile Published? latest;

        /// <summary>On the game thread: builds the race facts of the loaded game unless they are already published for it.</summary>
        internal static void EnsureForLoad(string loadToken)
        {
            var game = Current.Game;
            var held = latest;
            if (held != null && ReferenceEquals(held.Game, game) && held.LoadToken == loadToken) return;
            var races = new Dictionary<string, BrainRaceFacts>(StringComparer.Ordinal);
            foreach (var def in DefDatabase<ThingDef>.AllDefsListForReading.Where(d => d.race != null).OrderBy(d => Named(d.defName), StringComparer.Ordinal))
                races.Add(def.defName, Facts(def));
            latest = new Published(game, loadToken, races);
        }

        /// <summary>On any thread: the race facts the definition catalog read of <paramref name="loadToken"/> published.</summary>
        internal static IReadOnlyDictionary<string, BrainRaceFacts> ForLoad(string loadToken)
        {
            var held = latest;
            return held != null && held.LoadToken == loadToken
                ? held.Races
                : throw new InvalidOperationException($"No race facts are published for load {loadToken}: the load's definition catalog read builds them.");
        }

        private static BrainRaceFacts Facts(ThingDef def)
        {
            var race = def.race;
            var edible = NativeFoodPolicy.Foods().Where(f => race.CanEverEat(f)).Select(f => Named(f.defName)).ToList();
            var stages = race.lifeStageAges;
            double? adultFeed = stages != null && stages.Count > 0
                ? SimplifiedPastureNutritionSimulator.NutritionConsumedPerDay(def, stages[stages.Count - 1].def)
                : null;
            return new BrainRaceFacts(
                edible,
                TameChanceFactorCurve().Evaluate(def.GetStatValueAbstract(StatDefOf.Wildness)),
                adultFeed);
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
            GameThread.IsIdentifier(defName) ? defName : throw new InvalidOperationException($"Def name '{defName}' is not an identifier.");
    }
}
