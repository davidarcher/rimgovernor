#nullable enable
using System;
using System.Linq;
using RimWorld;
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
            return row;
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
