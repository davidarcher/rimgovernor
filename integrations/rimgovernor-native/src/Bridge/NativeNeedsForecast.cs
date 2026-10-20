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

/// <summary>Read-only needs mechanics/targets for every colonist given, every target of each kind (#3341). No priority, timetable or trigger preselection.</summary>
internal static class NativeNeedsForecast
{
    internal static Obs.NeedsForecastRead Read(Map map, List<Pawn> people, NativePathLegs? legs = null)
    {
        legs ??= new(map);
        var rows = new List<Obs.NeedsForecastPawn>();
        // The candidates, read once for every colonist; only each pawn's survivors are sorted, by id then grouped by kind.
        (Thing Thing, string Kind)[]? possible = null;
        foreach (var pawn in people.OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal))
        {
            var needs = new List<Obs.NeedRate>();
            if (pawn.needs?.food is { } food) needs.Add(new("food", K(food.CurLevelPercentage),
                K(food.FoodFallPerTick / food.MaxLevel), K(food.PercentageThreshHungry))
                { Unsupported = ["food_category_and_bed_rate_changes"] });
            if (pawn.needs?.rest is { } rest) needs.Add(new("rest", K(rest.CurLevelPercentage),
                K(rest.RestFallPerTick * pawn.GetStatValue(StatDefOf.RestFallRateFactor)), K(Need_Rest.ThreshTired))
                {
                    UnservedGainPerTick = K(Need_Rest.BaseRestGainPerTick * StatDefOf.BedRestEffectiveness.valueIfMissing * pawn.GetStatValue(StatDefOf.RestRateMultiplier)),
                    Unsupported = ["rest_category_rate_changes", "involuntary_sleep_risk"],
                });
            if (pawn.needs?.joy is { } joy)
            {
                var decay = typeof(Need_Joy).GetProperty("FallPerInterval", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(joy);
                var thresholds = BridgeCommon.PrivateInstanceField(typeof(Need), "threshPercents")?.GetValue(joy) as List<float>;
                needs.Add(new("joy", K(joy.CurLevelPercentage), decay is float fall
                    ? K(fall / 150.0 * pawn.GetStatValue(StatDefOf.JoyFallRateFactor)) : default,
                    thresholds is { Count: > 1 } ? K(thresholds[1]) : default)
                    { Unsupported = ["joy_category_rate_changes", "future_tolerance_changes"] });
            }
            var state = pawn.Dead ? "dead" : pawn.Downed ? "downed" : pawn.InMentalState ? "mental_state"
                : pawn.Drafted ? "drafted" : !pawn.IsColonistPlayerControlled ? "player_authority"
                : pawn.CurJob?.playerForced == true || pawn.jobs.jobQueue.Count > 0 ? "player_work" : "eligible";
            var blockers = VoluntaryControlBlockers(pawn);
            if (state == "eligible" && blockers.Count != 0) state = "unsupported_voluntary_state";
            // A blocked pawn's supported needs stay the brain's to serve (#3557): its targets are read like an eligible pawn's.
            var voluntary = state is "eligible" or "unsupported_voluntary_state";
            var targets = new List<Obs.NeedTarget>();
            if (voluntary)
            {
                possible ??= map.listerThings.AllThings.Where(t => t.Spawned &&
                    (t.def.IsNutritionGivingIngestible && t is not Corpse || t is Building_Bed || NativeRecreation.GiverFor(t) != null))
                    .Select(t => (t, t is Building_Bed ? "rest" : NativeRecreation.GiverFor(t) != null ? "joy" : "food")).ToArray();
                foreach (var (target, group) in possible)
                {
                    if (target.IsForbidden(pawn)) continue;
                    if (group == "food" && (pawn.needs?.food == null || !target.IngestibleNow
                        || !pawn.WillEat(target) || !FoodSupplyFacts.PolicyAllows(pawn, target))) continue;
                    if (group == "rest" && (pawn.needs?.rest == null || !RestUtility.CanUseBedNow(target, pawn, true))) continue;
                    if (group == "joy" && !NativeRecreation.Usable(pawn, target, NativeRecreation.GiverFor(target)!)) continue;
                    // Reserve with the claim the service job makes, as bind-time does: a stack or slot another pawn
                    // holds under the same maxPawns stays observable while its capacity lasts.
                    var (kind, count) = group == "food" ? (PawnActivityKind.Eat, IngestWantedCount(pawn, target))
                        : group == "rest" ? (PawnActivityKind.Sleep, 0) : (PawnActivityKind.Recreation, 0);
                    var (maxPawns, stackCount) = ServiceReservation(kind, target, count);
                    if (!pawn.CanReserveAndReach(target, PathEndMode.Touch, Danger.None, maxPawns, stackCount)) continue;
                    if (legs.Cost(pawn, pawn.Position, target, PathEndMode.Touch) is not { } route) continue;
                    var travel = K(route);
                    if (group == "food")
                    {
                        var nutrition = FoodUtility.NutritionForEater(pawn, target) / pawn.needs!.food.MaxLevel;
                        var service = target.def.ingestible.baseIngestTicks / (target.def.ingestible.useEatingSpeedStat
                            ? pawn.GetStatValue(StatDefOf.EatingSpeed) : 1);
                        var thoughts = FoodUtility.ThoughtsFromIngesting(pawn, target, target.def)
                            .Select(t => MealMemory(pawn, t)).ToArray();
                        targets.Add(new(target.GetUniqueLoadID(), "food", target.def.defName, target.def.ingestible.maxNumToIngestAtOnce > 0 ? Math.Min(target.stackCount, target.def.ingestible.maxNumToIngestAtOnce) : target.stackCount,
                            travel, K(service), K(nutrition), default) { MealThoughts = EquatableArray.From(thoughts), Location = new(target.Position.x, target.Position.z), StockCount = K(target.stackCount), MaxPawns = Capacity(IngestMaxPawns),
                            FreshTicks = K(target.TryGetComp<CompRottable>()?.TicksUntilRotAtCurrentTemp ?? RimGovernor.Brain.Domain.GameTime.TicksPerDay),
                            Cell = CellAt(map, target.Position), Deterioration = DeteriorationOf(map, target),
                            Unsupported = ["chew_spot_travel", "ingestion_outcomes_and_poisoning", "meal_memory_stacking"] });
                    }
                    else if (group == "rest") targets.Add(new(target.GetUniqueLoadID(), "rest", "", 1, travel, default, default,
                        K(Need_Rest.BaseRestGainPerTick * target.GetStatValue(StatDefOf.BedRestEffectiveness) * pawn.GetStatValue(StatDefOf.RestRateMultiplier))) { Location = new(target.Position.x,target.Position.z), MaxPawns = Capacity(((Building_Bed)target).SleepingSlotsCount),
                            Cell = CellAt(map, target.Position), Bed = BedRoomOf((Building_Bed)target) });
                    else
                    {
                        var job = NativeRecreation.GiverFor(target)!.jobDef;
                        // Native JoyTickCheckEnd conversion, projected at observed tolerance only; future tolerance is explicit.
                        targets.Add(new(target.GetUniqueLoadID(), "joy", "", 1, travel, default, default, K(NativeRecreation.GainPerTick(pawn, target, job)))
                            { Location = new(target.Position.x,target.Position.z), MaxPawns = Capacity(job.joyMaxParticipants), Cell = CellAt(map, target.Position),
                                Unsupported = ["play_position_travel", "future_tolerance_changes"] });
                    }
                }
            }
            var live = PawnSettingsRead.LiveMemories(pawn);
            var memories = live?.OfType<Thought_Memory>().Select(m => new Obs.NeedMemory(m.def.defName,
                m.MoodOffset(), m.permanent ? default : K(Math.Max(0, m.DurationTicks - m.age)), m.def.lerpMoodToZero) { Permanent = m.permanent, StackLimit = m.def.stackLimit });
            var unsupported = pawn.needs?.AllNeeds.Where(n => n is not Need_Food && n is not Need_Rest && n is not Need_Joy && n is not Need_Mood)
                .Select(n => "unsupported_need:" + n.def.defName).ToList() ?? new List<string>();
            unsupported.Add("disease_and_relationship_changes");
            var conditionalMood = NativeConditionalMood.Read(pawn, unsupported);
            rows.Add(conditionalMood with { Pawn = pawn.GetUniqueLoadID(), State = state, Location = new(pawn.Position.x, pawn.Position.z),
                CurrentService = voluntary ? CurrentService(pawn) : null,
                ComfortableMin = K(pawn.GetStatValue(StatDefOf.ComfyTemperatureMin)), ComfortableMax = K(pawn.GetStatValue(StatDefOf.ComfyTemperatureMax)), Needs = EquatableArray.From(needs),
                Mood = pawn.needs?.mood is { } mood ? K(mood.CurLevelPercentage) : default,
                MoodTarget = pawn.needs?.mood is { } targetMood ? K(targetMood.CurInstantLevel) : default,
                BreakThreshold = pawn.mindState?.mentalBreaker is { } breaker ? K(breaker.BreakThresholdMinor) : default,
                Memories = memories is null ? default : Fact.Known(EquatableArray.From(memories)),
                Targets = EquatableArray.From(targets.OrderBy(t => t.Id, StringComparer.Ordinal).GroupBy(t => t.Kind).SelectMany(g => g)), Unsupported = EquatableArray.From(unsupported), VoluntaryControlBlockers = blockers,
                OutdoorsLevel = pawn.needs?.outdoors is { } outdoors ? K(outdoors.CurLevel) : default,
                OutdoorsDisabled = pawn.needs?.outdoors != null && pawn.needs.PrefersIndoors,
                OwnedBed = pawn.ownership?.OwnedBed?.GetUniqueLoadID() ?? "",
                Ascetic = pawn.story?.traits?.HasTrait(TraitDefOf.Ascetic) == true,
                ThoughtStages = ThoughtStagesOf(pawn, map),
                Consciousness = Capacity(pawn, PawnCapacityDefOf.Consciousness), Moving = Capacity(pawn, PawnCapacityDefOf.Moving),
                Manipulation = Capacity(pawn, PawnCapacityDefOf.Manipulation), Sight = Capacity(pawn, PawnCapacityDefOf.Sight),
                Talking = Capacity(pawn, PawnCapacityDefOf.Talking),
                MalnutritionSeverity = Severity(pawn, HediffDefOf.Malnutrition), FoodPoisoningSeverity = Severity(pawn, HediffDefOf.FoodPoisoning),
                HypothermiaSeverity = Severity(pawn, HediffDefOf.Hypothermia), HeatstrokeSeverity = Severity(pawn, HediffDefOf.Heatstroke),
                PainShockThreshold = HealthStat(pawn, StatDefOf.PainShockThreshold), HealthScale = HealthScaleOf(pawn),
                IncomingDamageFactor = HealthStat(pawn, StatDefOf.IncomingDamageFactor), InjuryHealingFactor = HealthStat(pawn, StatDefOf.InjuryHealingFactor),
                ResearchSpeed = K(pawn.GetStatValue(StatDefOf.ResearchSpeed) * Find.Storyteller.difficulty.researchSpeedFactor),
                PlantWorkSpeed = K(pawn.GetStatValue(StatDefOf.PlantWorkSpeed)) });
        }
        var benchFactors = map.listerBuildings.AllBuildingsColonistOfClass<Building_ResearchBench>()
            .Where(b => b.Spawned && (b.GetComp<CompPowerTrader>()?.PowerOn ?? true))
            .OrderBy(b => b.GetUniqueLoadID(), StringComparer.Ordinal).Select(b => (double)b.GetStatValue(StatDefOf.ResearchSpeedFactor)).ToArray();
        var weather = map.weatherManager.CurWeatherLerped;
        return new() { Tick = Find.TickManager.TicksGame, Pawns = EquatableArray.From(rows),
            WeatherThought = weather?.weatherThought?.defName ?? "", WeatherThoughtStages = weather?.weatherThought?.stages?.Count ?? 0,
            RainRate = K(map.weatherManager.RainRate), MapArea = Fact.Known(map.Area), Deterioration = DeteriorationPart(), ResearchBenchFactors = EquatableArray.From(benchFactors) };
    }
    /// <summary>The cell's roof and room as Need_Outdoors, the undergrounder workers, ApplyBedThoughts and SteadyEnvironmentEffects read them (#3339).</summary>
    internal static Obs.NeedCell CellAt(Map map, IntVec3 cell)
    {
        var roof = map.roofGrid.RoofAt(cell);
        return new(Fact.Known(roof != null), Fact.Known(roof?.isThickRoof == true), Fact.Known(roof?.isNatural == true),
            Fact.Known(cell.UsesOutdoorTemperature(map)), Fact.Known(cell.GetRoom(map)?.PsychologicallyOutdoors ?? true));
    }
    /// <summary>The bed's room as ApplyBedThoughts reads it: Room.Role and RoomStatDefOf.Impressiveness.GetScoreStageIndex(room.GetStat(Impressiveness)).</summary>
    private static Obs.NeedBedRoom BedRoomOf(Building_Bed bed)
    {
        var room = bed.GetRoom();
        var stage = room == null ? default : Fact.Known(RoomStatDefOf.Impressiveness.GetScoreStageIndex(room.GetStat(RoomStatDefOf.Impressiveness)));
        return new(room?.Role?.defName ?? "", stage, bed.ForPrisoners);
    }
    /// <summary>StatPart_EnvironmentalEffects def data on the DeteriorationRate stat, read by reflection (private fields); null without the part.</summary>
    internal static Obs.NeedDeteriorationPart? DeteriorationPart()
    {
        var part = StatDefOf.DeteriorationRate.parts?.OfType<StatPart_EnvironmentalEffects>().FirstOrDefault();
        if (part == null) return null;
        float F(string name) => BridgeCommon.PrivateInstanceField(typeof(StatPart_EnvironmentalEffects), name)?.GetValue(part) is float f ? f : float.NaN;
        var (unroofed, outdoors, protectedFactor) = (F("factorOffsetUnroofed"), F("factorOffsetOutdoors"), F("protectedByEdificeFactor"));
        return float.IsNaN(unroofed) || float.IsNaN(outdoors) || float.IsNaN(protectedFactor) ? null : new(unroofed, outdoors, protectedFactor);
    }
    /// <summary>
    /// A food stack's deterioration inputs; null when it cannot deteriorate (no hit points, CanEverDeteriorate false). The
    /// rate is the game's own SteadyEnvironmentEffects.FinalDeteriorationRate now, every stat part applied at the stack's
    /// cell; the brain rescales its StatPart_EnvironmentalEffects factor when a planned room changes the cell.
    /// </summary>
    private static Obs.NeedDeterioration? DeteriorationOf(Map map, Thing thing)
    {
        if (!thing.def.useHitPoints || !thing.def.CanEverDeteriorate) return null;
        return new(thing.HitPoints, K(SteadyEnvironmentEffects.FinalDeteriorationRate(thing)), thing.def.deteriorateFromEnvironmentalEffects,
            thing.Position.GetTerrain(map)?.extraDeteriorationFactor ?? 0, SteadyEnvironmentEffects.ProtectedByEdifice(thing.Position, map));
    }
    /// <summary>
    /// The stage rows of the thoughts #3339 and the bedroom credit (#3655) value, each priced by the game's MoodOffset for this
    /// pawn on an unattached thought (zero when ThoughtUtility.CanGetThought refuses it, as the game would never give it). The
    /// ascetic rows also carry each null stage at 0: ThoughtWorker_Ascetic leaves the thought inactive at those room stages.
    /// </summary>
    private static EquatableArray<Obs.NeedThoughtStage> ThoughtStagesOf(Pawn pawn, Map map)
    {
        var rows = new List<Obs.NeedThoughtStage>();
        void Rows(ThoughtDef? def, string role, bool nullStagesInactive = false)
        {
            if (def?.stages == null) return;
            var can = ThoughtUtility.CanGetThought(pawn, def);
            for (var stage = 0; stage < def.stages.Count; stage++)
            {
                if (def.stages[stage] == null)
                {
                    if (nullStagesInactive) rows.Add(new(def.defName, role, stage, 0, default, false));
                    continue;
                }
                var offset = can ? StageOffset(pawn, def, stage) : 0;
                if (!double.IsFinite(offset)) continue;
                rows.Add(new(def.defName, role, stage, offset,
                    def.IsMemory ? K(def.DurationTicks) : default, def.IsMemory && def.lerpMoodToZero));
            }
        }
        Rows(ThoughtDefOf.SleptOutside, "slept_outside");
        Rows(ThoughtDefOf.SleptInBedroom, "slept_in_bedroom");
        Rows(ThoughtDefOf.SleptInBarracks, "slept_in_barracks");
        Rows(map.weatherManager.CurWeatherLerped?.weatherThought, "weather");
        foreach (var def in DefDatabase<ThoughtDef>.AllDefsListForReading.Where(d => d.workerClass == typeof(ThoughtWorker_NeedOutdoors)))
            Rows(def, "need_outdoors");
        foreach (var def in DefDatabase<ThoughtDef>.AllDefsListForReading.Where(d => d.workerClass == typeof(ThoughtWorker_IsIndoorsForUndergrounder)))
            Rows(def, "undergrounder_indoors");
        foreach (var def in DefDatabase<ThoughtDef>.AllDefsListForReading.Where(d => d.workerClass == typeof(ThoughtWorker_IsUndergroundForUndergrounder)))
            Rows(def, "undergrounder_underground");
        foreach (var def in DefDatabase<ThoughtDef>.AllDefsListForReading.Where(d => d.workerClass == typeof(ThoughtWorker_IsOutdoorsForUndergrounder)))
            Rows(def, "undergrounder_outdoors");
        // No ThoughtDefOf field names the Ascetic thought; its worker class is the typed route (#3655).
        foreach (var def in DefDatabase<ThoughtDef>.AllDefsListForReading.Where(d => d.workerClass == typeof(ThoughtWorker_Ascetic)))
            Rows(def, "ascetic", nullStagesInactive: true);
        return EquatableArray.From(rows);
    }
    private static readonly FieldInfo? SituationalStage = BridgeCommon.PrivateInstanceField(typeof(Thought_Situational), "curStageIndex");
    /// <summary>Thought.MoodOffset of an unattached thought at one stage: a memory forced to the stage, a situational thought with its stage set.</summary>
    private static double StageOffset(Pawn pawn, ThoughtDef def, int stage)
    {
        Thought thought;
        if (def.IsMemory) thought = ThoughtMaker.MakeThought(def, stage);
        else
        {
            thought = ThoughtMaker.MakeThought(def);
            if (thought is not Thought_Situational || SituationalStage == null) return double.NaN;
            SituationalStage.SetValue(thought, stage);
        }
        thought.pawn = pawn;
        return thought.MoodOffset();
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
        // A stationary recreation driver's final Delay toil (NativeRecreation.StationaryDriver); billiards' shot loop
        // and sleep's dynamic wake/medical/other-job conditions remain unassessed.
        if (step.Kind != PawnActivityKind.Recreation || !NativeRecreation.StationaryDriver(driver)
            || !driver!.OnLastToil || driver.ticksLeftThisToil <= 0 || job.doUntilGatheringEnded
            || !job.ignoreJoyTimeAssignment && !pawn.GetTimeAssignment().allowJoy
            || job.targetA.Thing is not Verse.Building target || target.GetUniqueLoadID() != step.Target
            || job.def != NativeRecreation.GiverFor(target)?.jobDef
            || pawn.needs?.joy is not { } joy || joy.GetType() != typeof(Need_Joy)) return null;
        var gain = NativeRecreation.GainPerTick(pawn, target, job.def);
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
    internal static Obs.NeedMemory MealMemory(Pawn pawn, FoodUtility.ThoughtFromIngesting source)
    {
        // Unattached native memory: inspect the native stage/stat/trait multiplier, never add it to the pawn.
        var thought = ThoughtMaker.MakeThought(source.thought, source.fromPrecept);
        thought.pawn = pawn;
        return new(source.thought.defName, thought.MoodOffset(), K(thought.DurationTicks), source.thought.lerpMoodToZero) { StackLimit = source.thought.stackLimit };
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
    /// (Ingest: stack, 10, amount; LayDown: bed, SleepingSlotsCount, 0; recreation: building, its giver job's joyMaxParticipants, 0). ReservationManager.CanReserve
    /// refuses a claim whose maxPawns differs from an existing one's and sums stack counts, so bind-time checks must pass exactly these.</summary>
    internal static (int MaxPawns, int StackCount) ServiceReservation(PawnActivityKind kind, Thing target, int count) => kind switch
    {
        PawnActivityKind.Eat => (IngestMaxPawns, count),
        PawnActivityKind.Sleep when target is Building_Bed bed => (bed.SleepingSlotsCount, 0),
        PawnActivityKind.Recreation when NativeRecreation.GiverFor(target) is { } giver => (giver.jobDef.joyMaxParticipants, 0),
        _ => (1, -1),
    };
    /// <summary>The capacity's level now (#3519); unknown without a health tracker.</summary>
    private static Fact<double> Capacity(Pawn pawn, PawnCapacityDef capacity) =>
        pawn.health?.capacities == null ? default : K(pawn.health.capacities.GetLevel(capacity));
    /// <summary>The hediff's current severity, 0 when the pawn has none (#3519); unknown without a health tracker.</summary>
    private static Fact<double> Severity(Pawn pawn, HediffDef hediff) =>
        pawn.health?.hediffSet == null ? default : K(pawn.health.hediffSet.GetFirstHediffOfDef(hediff)?.Severity ?? 0);
    /// <summary>The pawn's value of a health stat (#3544); unknown without a health tracker.</summary>
    private static Fact<double> HealthStat(Pawn pawn, StatDef stat) => pawn.health == null ? default : K(pawn.GetStatValue(stat));
    private static Fact<double> HealthScaleOf(Pawn pawn) => pawn.health == null ? default : K(pawn.HealthScale);
    private static Fact<double> K(double value) => double.IsFinite(value) ? Fact.Known(value) : default;
}
