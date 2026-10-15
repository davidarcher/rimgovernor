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
            var blockers = VoluntaryControlBlockers(pawn);
            if (state == "eligible" && blockers.Count != 0) state = "unsupported_voluntary_state";
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
                        if (target.IsForbidden(pawn)) continue;
                        if (group.Key == "food" && (pawn.needs?.food == null || !target.IngestibleNow
                            || !pawn.WillEat(target) || !FoodSupplyFacts.PolicyAllows(pawn, target))) continue;
                        if (group.Key == "rest" && (pawn.needs?.rest == null || !RestUtility.CanUseBedNow(target, pawn, true))) continue;
                        if (group.Key == "joy" && (pawn.needs?.joy == null || !pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation))) continue;
                        // Reserve with the claim the service job makes, as bind-time does: a stack or slot another pawn
                        // holds under the same maxPawns stays observable while its capacity lasts.
                        var (kind, count) = group.Key == "food" ? (PawnActivityKind.Eat, IngestWantedCount(pawn, target))
                            : group.Key == "rest" ? (PawnActivityKind.Sleep, 0) : (PawnActivityKind.Recreation, 0);
                        var (maxPawns, stackCount) = ServiceReservation(kind, target, count);
                        if (!pawn.CanReserveAndReach(target, PathEndMode.Touch, Danger.None, maxPawns, stackCount)) continue;
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
                                travel, K(service), K(nutrition), default) { MealThoughts = EquatableArray.From(thoughts), Location = new(target.Position.x, target.Position.z), StockCount = K(target.stackCount), MaxPawns = Capacity(IngestMaxPawns),
                                FreshTicks = K(target.TryGetComp<CompRottable>()?.TicksUntilRotAtCurrentTemp ?? RimGovernor.Brain.Domain.GameTime.TicksPerDay),
                                Unsupported = ["chew_spot_travel", "ingestion_outcomes_and_poisoning", "meal_memory_stacking"] });
                        }
                        else if (group.Key == "rest") targets.Add(new(target.GetUniqueLoadID(), "rest", "", 1, travel, default, default,
                            K(Need_Rest.BaseRestGainPerTick * target.GetStatValue(StatDefOf.BedRestEffectiveness) * pawn.GetStatValue(StatDefOf.RestRateMultiplier))) { Location = new(target.Position.x,target.Position.z), MaxPawns = Capacity(((Building_Bed)target).SleepingSlotsCount) });
                        else
                        {
                            var job = DefDatabase<JoyGiverDef>.GetNamed("Play_Horseshoes").jobDef;
                            // Native JoyTickCheckEnd conversion, projected at observed tolerance only; future tolerance is explicit.
                            var gain = job.joyGainRate * 0.36 / RimGovernor.Brain.Domain.GameTime.TicksPerHour * target.GetStatValue(StatDefOf.JoyGainFactor)
                                * pawn.needs!.joy.tolerances.JoyFactorFromTolerance(job.joyKind);
                            targets.Add(new(target.GetUniqueLoadID(), "joy", "", 1, travel, default, default, K(gain))
                                { Location = new(target.Position.x,target.Position.z), MaxPawns = Capacity(job.joyMaxParticipants), Unsupported = ["horseshoes_position_travel", "future_tolerance_changes"] });
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
            var conditionalMood = NativeConditionalMood.Read(pawn, unsupported);
            rows.Add(conditionalMood with { Pawn = pawn.GetUniqueLoadID(), State = state, Location = new(pawn.Position.x, pawn.Position.z),
                CurrentService = state == "eligible" ? CurrentService(pawn) : null,
                ComfortableMin = K(pawn.GetStatValue(StatDefOf.ComfyTemperatureMin)), ComfortableMax = K(pawn.GetStatValue(StatDefOf.ComfyTemperatureMax)), Needs = EquatableArray.From(needs),
                Mood = pawn.needs?.mood is { } mood ? K(mood.CurLevelPercentage) : default,
                MoodTarget = pawn.needs?.mood is { } targetMood ? K(targetMood.CurInstantLevel) : default,
                BreakThreshold = pawn.mindState?.mentalBreaker is { } breaker ? K(breaker.BreakThresholdMinor) : default,
                Memories = memories is null ? default : Fact.Known(EquatableArray.From(memories)),
                Targets = EquatableArray.From(targets), Unsupported = EquatableArray.From(unsupported), VoluntaryControlBlockers = blockers });
        }
        return new() { Tick = Find.TickManager.TicksGame, Pawns = EquatableArray.From(rows), Truncated = truncated };
    }
    private static Obs.NeedCurrentService? CurrentService(Pawn pawn)
    {
        var execution = NativePawnActivities.Read(pawn);
        var step = NativePawnActivities.ActiveStep(pawn);
        var job = pawn.CurJob;
        var driver = pawn.jobs?.curDriver;
        if (execution == null || execution.State != "started" || step == null || job == null
            || execution.JobId != job.loadID || job.playerForced || pawn.jobs!.jobQueue.Count != 0
            || pawn.pather.Moving || step.Deadline <= Find.TickManager.TicksGame) return null;
        // Exact native horseshoes is a stationary final Delay toil; other recreation drivers
        // and sleep's dynamic wake/medical/other-job conditions remain unassessed.
        if (step.Kind != PawnActivityKind.Recreation || driver?.GetType() != typeof(JobDriver_PlayHorseshoes)
            || !driver.OnLastToil || driver.ticksLeftThisToil <= 0 || job.doUntilGatheringEnded
            || job.def != DefDatabase<JoyGiverDef>.GetNamed("Play_Horseshoes").jobDef
            || !job.ignoreJoyTimeAssignment && !pawn.GetTimeAssignment().allowJoy
            || job.targetA.Thing is not Verse.Building target || target.GetUniqueLoadID() != step.Target
            || pawn.needs?.joy is not { } joy || joy.GetType() != typeof(Need_Joy)) return null;
        var gain = job.def.joyGainRate * 0.36 / RimGovernor.Brain.Domain.GameTime.TicksPerHour
            * target.GetStatValue(StatDefOf.JoyGainFactor) * joy.tolerances.JoyFactorFromTolerance(job.def.joyKind);
        if (!double.IsFinite(gain) || gain <= 0) return null;
        // JoyTickCheckEnd ends strictly above 0.9999, not at the need threshold.
        var full = Math.Max(1, Math.Floor((0.9999 - joy.CurLevel) / gain) + 1);
        var remaining = Math.Min(driver.ticksLeftThisToil, Math.Min(step.Deadline - Find.TickManager.TicksGame, full));
        return new(execution.Assignment, execution.Method, execution.Revision, step.Target, "joy",
            new(pawn.Position.x, pawn.Position.z), K(remaining), K(gain))
        { Unsupported = ["current_rate_completion_projection", "future_tolerance_changes", "ownership_release_requires_observation"] };
    }
    /// <summary>Supported-state observation, not a job selector. Exact types prevent a mod subclass with
    /// different mechanics from inheriting a support claim. Additional services have no forecasted onset.</summary>
    internal static EquatableArray<string> VoluntaryControlBlockers(Pawn pawn)
    {
        var blockers = new SortedSet<string>(StringComparer.Ordinal);
        if (pawn.needs == null) blockers.Add("unknown_need_census");
        else foreach (var need in pawn.needs.AllNeeds)
        {
            var type = need.GetType();
            if (type == typeof(Need_Food) || type == typeof(Need_Rest) || type == typeof(Need_Joy)
                || type == typeof(Need_Mood) || type == typeof(Need_Comfort) || type == typeof(Need_Beauty)
                || type == typeof(Need_Outdoors) || type == typeof(Need_Indoors) || type == typeof(Need_RoomSize)) continue;
            // Native Chemical_Any.Disabled is exposed by ShowOnNeedList; ordinary pawns carry this
            // inactive need too. Visibility is not a support test for any other need class.
            if (type == typeof(Need_Chemical_Any) && !need.ShowOnNeedList) continue;
            blockers.Add("unsupported_need:" + need.def.defName);
        }
        if (pawn.HasPsylink) blockers.Add("unsupported_psyfocus");
        if (pawn.genes?.GenesListForReading.Any(g => g is Gene_Hemogen && g.Active) == true)
            blockers.Add("unsupported_hemogen");
        if (pawn.genes?.GenesListForReading.Any(g => g is Gene_ChemicalDependency && g.Active) == true)
            blockers.Add("unsupported_genetic_chemical_dependency");
        if (pawn.health?.hediffSet.hediffs.OfType<Hediff_ChemicalDependency>().Any(h => !h.ShouldRemove) == true)
            blockers.Add("unsupported_genetic_chemical_dependency");
        var drugs = pawn.drugs;
        if (drugs?.CurrentPolicy is { } policy)
            for (var i = 0; i < policy.Count; i++)
            {
                var drug = policy[i].drug;
                if (drug == null) { blockers.Add("unknown_scheduled_drug"); continue; }
                if (!drugs.AllowedToTakeScheduledEver(drug)) continue;
                blockers.Add((drugs.ShouldTryToTakeScheduledNow(drug)
                    ? "scheduled_drug_due:" : "scheduled_drug_onset_unassessed:") + drug.defName);
            }
        return EquatableArray.From(blockers);
    }
    private static Obs.NeedMemory MealMemory(Pawn pawn, FoodUtility.ThoughtFromIngesting source)
    {
        // Unattached native memory: inspect the native stage/stat/trait multiplier, never add it to the pawn.
        var thought = ThoughtMaker.MakeThought(source.thought, source.fromPrecept);
        thought.pawn = pawn;
        return new(source.thought.defName, thought.MoodOffset(), K(thought.DurationTicks), source.thought.lerpMoodToZero);
    }
    /// <summary>The maxPawns literal JobDriver_Ingest passes to Reserve for a meal stack (decompiled 1.7; vanilla exposes no symbol for it).</summary>
    internal const int IngestMaxPawns = 10;
    /// <summary>Vanilla claimant capacity; a non-positive read is unknown, never a guess.</summary>
    internal static Fact<int> Capacity(int maxPawns) => maxPawns > 0 ? Fact.Known(maxPawns) : default;
    /// <summary>The count a vanilla ingest job asks for before it has a step: FoodUtility.WillIngestStackCountOf (the nutrition the
    /// pawn wants, capped by maxNumToIngestAtOnce), which JobDriver_Ingest then clamps to the stack via GetMaxAmountToPickup.</summary>
    private static int IngestWantedCount(Pawn pawn, Thing food) =>
        Math.Min(food.stackCount, FoodUtility.WillIngestStackCountOf(pawn, food.def, FoodUtility.NutritionForEater(pawn, food)));
    /// <summary>The (maxPawns, stackCount) the vanilla job driver of a service step reserves its target with
    /// (Ingest: stack, 10, amount; LayDown: bed, SleepingSlotsCount, 0; Play_Horseshoes: pin, joyMaxParticipants, 0). ReservationManager.CanReserve
    /// refuses a claim whose maxPawns differs from an existing one's and sums stack counts, so bind-time checks must pass exactly these.</summary>
    internal static (int MaxPawns, int StackCount) ServiceReservation(PawnActivityKind kind, Thing target, int count) => kind switch
    {
        PawnActivityKind.Eat => (IngestMaxPawns, count),
        PawnActivityKind.Sleep when target is Building_Bed bed => (bed.SleepingSlotsCount, 0),
        PawnActivityKind.Recreation => (DefDatabase<JoyGiverDef>.GetNamed("Play_Horseshoes").jobDef.joyMaxParticipants, 0),
        _ => (1, -1),
    };
    private static Fact<double> K(double value) => double.IsFinite(value) ? Fact.Known(value) : default;
}
