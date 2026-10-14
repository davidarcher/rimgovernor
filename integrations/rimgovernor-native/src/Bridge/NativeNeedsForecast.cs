#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RimWorld;
using Verse;
using Verse.AI;
using RimGovernor.Brain.Domain;
using Obs = RimGovernor.Contracts.Wire.Observations;

namespace HomeBridge.BridgeTools;

/// <summary>Read-only bounded needs mechanics/targets. No priority, timetable or trigger preselection.</summary>
internal static class NativeNeedsForecast
{
    internal static Obs.NeedsForecastRead Read(Map map, List<Pawn> people)
    {
        var rows = new List<Obs.NeedsForecastPawn>();
        var budget = 128;
        var truncated = people.Count > 16;
        foreach (var pawn in people.OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal).Take(16))
        {
            var needs = new List<Obs.NeedRate>();
            if (pawn.needs?.food is { } food) needs.Add(new("food", K(food.CurLevelPercentage),
                K(food.FoodFallPerTick / food.MaxLevel), K(food.PercentageThreshHungry), K(food.MaxLevel))
                { Unsupported = ["food_category_and_bed_rate_changes"] });
            if (pawn.needs?.rest is { } rest) needs.Add(new("rest", K(rest.CurLevelPercentage),
                K(rest.RestFallPerTick * pawn.GetStatValue(StatDefOf.RestFallRateFactor)), K(Need_Rest.ThreshTired), K(1))
                { Unsupported = ["rest_category_rate_changes", "involuntary_sleep_risk"] });
            if (pawn.needs?.joy is { } joy)
            {
                var decay = typeof(Need_Joy).GetProperty("FallPerInterval", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(joy);
                var thresholds = BridgeCommon.PrivateInstanceField(typeof(Need), "threshPercents")?.GetValue(joy) as List<float>;
                needs.Add(new("joy", K(joy.CurLevelPercentage), decay is float fall
                    ? K(fall / 150.0 * pawn.GetStatValue(StatDefOf.JoyFallRateFactor)) : default,
                    thresholds is { Count: > 1 } ? K(thresholds[1]) : default, K(1))
                    { Unsupported = ["joy_category_rate_changes", "future_tolerance_changes"] });
            }
            var state = pawn.Dead ? "dead" : pawn.Downed ? "downed" : pawn.InMentalState ? "mental_state"
                : pawn.Drafted ? "drafted" : !pawn.IsColonistPlayerControlled ? "player_authority"
                : pawn.CurJob?.playerForced == true || pawn.jobs.jobQueue.Count > 0 ? "player_work" : "eligible";
            var targets = new List<Obs.NeedTarget>();
            if (state == "eligible")
            {
                var possible = map.listerThings.AllThings.Where(t => t.Spawned &&
                    (t.def.IsNutritionGivingIngestible && t is not Corpse || t is Building_Bed || t.def.defName == "HorseshoesPin"))
                    .OrderBy(t => t.GetUniqueLoadID(), StringComparer.Ordinal).ToArray();
                foreach (var group in possible.GroupBy(t => t is Building_Bed ? "rest" : t.def.defName == "HorseshoesPin" ? "joy" : "food"))
                {
                    if (group.Count() > 8) truncated = true;
                    foreach (var target in group.Take(8))
                    {
                        if (budget <= 0) { truncated = true; break; }
                        if (target.IsForbidden(pawn) || !pawn.CanReserveAndReach(target, PathEndMode.Touch, Danger.None)) continue;
                        if (group.Key == "food" && (pawn.needs?.food == null || !target.IngestibleNow
                            || !pawn.WillEat(target) || !FoodSupplyFacts.PolicyAllows(pawn, target))) continue;
                        if (group.Key == "rest" && (pawn.needs?.rest == null || !RestUtility.CanUseBedNow(target, pawn, true))) continue;
                        if (group.Key == "joy" && (pawn.needs?.joy == null || !pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation))) continue;
                        using var route = map.pathFinder.FindPathNow(pawn.Position, target, TraverseParms.For(pawn, Danger.None), peMode: PathEndMode.Touch);
                        budget--;
                        if (!route.Found) continue;
                        var travel = K(route.TotalCost);
                        if (group.Key == "food")
                        {
                            var nutrition = FoodUtility.NutritionForEater(pawn, target) / pawn.needs!.food.MaxLevel;
                            var service = target.def.ingestible.baseIngestTicks / (target.def.ingestible.useEatingSpeedStat
                                ? pawn.GetStatValue(StatDefOf.EatingSpeed) : 1);
                            var thoughts = FoodUtility.ThoughtsFromIngesting(pawn, target, target.def)
                                .Select(t => MealMemory(pawn, t)).ToArray();
                            targets.Add(new(target.GetUniqueLoadID(), "food", target.def.defName, target.def.ingestible.maxNumToIngestAtOnce > 0 ? Math.Min(target.stackCount, target.def.ingestible.maxNumToIngestAtOnce) : target.stackCount,
                                travel, K(service), K(nutrition), default) { MealThoughts = EquatableArray.From(thoughts),
                                Unsupported = ["chew_spot_travel", "ingestion_outcomes_and_poisoning", "meal_memory_stacking"] });
                        }
                        else if (group.Key == "rest") targets.Add(new(target.GetUniqueLoadID(), "rest", "", 1, travel, default, default,
                            K(Need_Rest.BaseRestGainPerTick * target.GetStatValue(StatDefOf.BedRestEffectiveness) * pawn.GetStatValue(StatDefOf.RestRateMultiplier))));
                        else
                        {
                            var job = DefDatabase<JoyGiverDef>.GetNamed("Play_Horseshoes").jobDef;
                            // Native JoyTickCheckEnd conversion, projected at observed tolerance only; future tolerance is explicit.
                            var gain = job.joyGainRate * 0.36 / RimGovernor.Brain.Domain.GameTime.TicksPerHour * target.GetStatValue(StatDefOf.JoyGainFactor)
                                * pawn.needs!.joy.tolerances.JoyFactorFromTolerance(job.joyKind);
                            targets.Add(new(target.GetUniqueLoadID(), "joy", "", 1, travel, default, default, K(gain))
                                { Unsupported = ["horseshoes_position_travel", "future_tolerance_changes"] });
                        }
                    }
                }
            }
            var live = PawnSettingsRead.LiveMemories(pawn);
            var memories = live?.OfType<Thought_Memory>().Select(m => new Obs.NeedMemory(m.def.defName,
                m.MoodOffset(), m.permanent ? default : K(Math.Max(0, m.DurationTicks - m.age)), m.def.lerpMoodToZero) { Permanent = m.permanent });
            var unsupported = pawn.needs?.AllNeeds.Where(n => n is not Need_Food && n is not Need_Rest && n is not Need_Joy && n is not Need_Mood)
                .Select(n => "unsupported_need:" + n.def.defName).ToList() ?? new List<string>();
            unsupported.Add("disease_and_relationship_changes");
            rows.Add(new() { Pawn = pawn.GetUniqueLoadID(), State = state, Needs = EquatableArray.From(needs),
                Mood = pawn.needs?.mood is { } mood ? K(mood.CurLevelPercentage) : default,
                MoodTarget = pawn.needs?.mood is { } targetMood ? K(targetMood.CurInstantLevel) : default,
                BreakThreshold = pawn.mindState?.mentalBreaker is { } breaker ? K(breaker.BreakThresholdMinor) : default,
                Memories = memories is null ? default : Fact.Known(EquatableArray.From(memories)),
                Targets = EquatableArray.From(targets), Unsupported = EquatableArray.From(unsupported) });
        }
        return new() { Tick = Find.TickManager.TicksGame, Pawns = EquatableArray.From(rows), Truncated = truncated };
    }
    private static Obs.NeedMemory MealMemory(Pawn pawn, FoodUtility.ThoughtFromIngesting source)
    {
        // Unattached native memory: inspect the native stage/stat/trait multiplier, never add it to the pawn.
        var thought = ThoughtMaker.MakeThought(source.thought, source.fromPrecept);
        thought.pawn = pawn;
        return new(source.thought.defName, thought.MoodOffset(), K(thought.DurationTicks), source.thought.lerpMoodToZero);
    }
    private static Fact<double> K(double value) => double.IsFinite(value) ? Fact.Known(value) : default;
}
