using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RimWorld;
using Verse;
using Verse.AI;
using RimGovernor.Brain.Domain;
using RequiredFactException = RimGovernor.Brain.Policy.RequiredFactException;

namespace RimGovernor.Native;

/// <summary>Read-only needs mechanics/targets for every colonist given, every target of each kind (#3341). No priority, timetable or trigger preselection.</summary>
internal static class NativeNeedsForecast
{
    internal static RimGovernor.Brain.GameFacts.Observations.NeedsForecastRead Read(Map map, List<Pawn> people, NativePathLegs? legs = null)
    {
        // Despawning into a carry tracker does not remove a baby's needs or fit exposure.
        // Extend the canonical needs census from the native holder, keeping one row per pawn.
        people = people.Concat(map.mapPawns.AllPawnsSpawned.Select(p => p.carryTracker?.CarriedThing).OfType<Pawn>()
            .Where(p => p.IsFreeColonist && !p.Dead && p.DevelopmentalStage.Baby())).Distinct().ToList();
        legs ??= new(map);
        // The organ replacement reads (#3830) share one capture's surgery facts.
        using var surgery = new NativePawnDetails.SurgeryScope();
        var rows = new List<RimGovernor.Brain.GameFacts.Observations.NeedsForecastPawn>();
        var sightings = map.GetComponent<TrafficState>()?.Sightings;
        var now = Find.TickManager.TicksGame;
        // The candidates, read once for every colonist; only each pawn's survivors are sorted, by id then grouped by kind.
        (Thing Thing, string Kind)[]? possible = null;
        // The map's outdoor recreation weather, read once for every colonist with a joy need (#3825 D7).
        NativeRecreation.OutdoorClimate? outdoorClimate = null;
        NativeRecreation.OutdoorClimate Climate() => outdoorClimate ??= new(map);
        foreach (var pawn in people.OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal))
        {
            var needs = NeedRatesOf(pawn, map, Climate, pawn.needs?.joy);
            var state = pawn.Dead ? "dead" : !pawn.Spawned ? "carried" : pawn.Downed ? "downed" : pawn.InMentalState ? "mental_state"
                : pawn.Drafted ? "drafted" : !pawn.IsColonistPlayerControlled ? "player_authority"
                : pawn.CurJob?.playerForced == true || pawn.jobs.jobQueue.Count > 0 ? "player_work" : "eligible";
            var blockers = VoluntaryControlBlockers(pawn);
            if (state == "eligible" && blockers.Count != 0) state = "unsupported_voluntary_state";
            // A blocked pawn's supported needs stay the brain's to serve (#3557): its targets are read like an eligible pawn's.
            var voluntary = state is "eligible" or "unsupported_voluntary_state";
            var targets = new List<RimGovernor.Brain.GameFacts.Observations.NeedTarget>();
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
                    if (group == "joy" && !NativeRecreation.Usable(pawn, target, (NativeRecreation.GiverFor(target) ?? throw new System.InvalidOperationException("Required native value is unavailable: NativeRecreation.GiverFor(target).")))) continue;
                    // Reserve with the claim the service job makes, as bind-time does: a stack or slot another pawn
                    // holds under the same maxPawns stays observable while its capacity lasts.
                    var (kind, count) = group == "food" ? (PawnActivityKind.Eat, IngestWantedCount(pawn, target))
                        : group == "rest" ? (PawnActivityKind.Sleep, 0) : (PawnActivityKind.Recreation, 0);
                    var (maxPawns, stackCount) = ServiceReservation(kind, target, count);
                    if (!pawn.CanReserveAndReach(target, PathEndMode.Touch, Danger.None, maxPawns, stackCount)) continue;
                    if (legs.Cost(pawn, pawn.Position, target, PathEndMode.Touch) is not { } route) continue;
                    var travel = F(route, "route cost");
                    if (group == "food")
                    {
                        var nutrition = FoodUtility.NutritionForEater(pawn, target) / (pawn.needs ?? throw new System.InvalidOperationException("Required native value is unavailable: pawn.needs.")).food.MaxLevel;
                        var service = target.def.ingestible.baseIngestTicks / (target.def.ingestible.useEatingSpeedStat
                            ? pawn.GetStatValue(StatDefOf.EatingSpeed) : 1);
                        var thoughts = FoodUtility.ThoughtsFromIngesting(pawn, target, target.def)
                            .Select(t => MealMemory(pawn, t)).ToArray();
                        targets.Add(new(target.GetUniqueLoadID(), "food", target.def.defName, target.def.ingestible.maxNumToIngestAtOnce > 0 ? Math.Min(target.stackCount, target.def.ingestible.maxNumToIngestAtOnce) : target.stackCount,
                            travel, new RimGovernor.Brain.GameFacts.Observations.NeedRecovery.Ingest(F(service, "ingest ticks"), F(nutrition, "nutrition")))
                            { ServicePosition = NativeServicePositions.Meal(pawn, target, legs), MealThoughts = EquatableArray.From(thoughts), Location = new(target.Position.x, target.Position.z), Stock = new RimGovernor.Brain.GameFacts.Observations.NeedStock.Stacked(target.stackCount, NativeCoupledFood.StackFreshness(target)), MaxPawns = Claimants(maxPawns, target),
                            Cell = CellAt(map, target.Position), Bed = null, Deterioration = DeteriorationOf(map, target),
                            RoomStage = StageAt(target.Position, map), Meal = new(target.def.ingestible.chairSearchRadius, target.def.ingestible.tableDesired),
                            Unsupported = ["chew_spot_travel", "ingestion_outcomes_and_poisoning", "meal_memory_stacking"] });
                    }
                    else if (group == "rest")
                    {
                        var sleepingCell = SleepingCell(pawn, (Building_Bed)target);
                        targets.Add(new(target.GetUniqueLoadID(), "rest", "", 1, travel,
                        new RimGovernor.Brain.GameFacts.Observations.NeedRecovery.Rate(F(Need_Rest.BaseRestGainPerTick * RestEffectiveness((Building_Bed)target) * pawn.GetStatValue(StatDefOf.RestRateMultiplier), "bed rest gain")))
                        { ServicePosition = new RimGovernor.Brain.GameFacts.Observations.ServicePositionRead.Located(new(sleepingCell.x, sleepingCell.z)), MealThoughts = [], Location = new(sleepingCell.x, sleepingCell.z), Stock = new RimGovernor.Brain.GameFacts.Observations.NeedStock.NotStacked(), MaxPawns = Claimants(maxPawns, target),
                            Cell = CellAt(map, sleepingCell), Bed = BedRoomOf((Building_Bed)target), Deterioration = null, Unsupported = [] });
                    }
                    else
                    {
                        var job = (NativeRecreation.GiverFor(target) ?? throw new System.InvalidOperationException("Required native value is unavailable: NativeRecreation.GiverFor(target).")).jobDef;
                        // Native JoyTickCheckEnd conversion, projected at observed tolerance only; future tolerance is explicit.
                        targets.Add(new(target.GetUniqueLoadID(), "joy", "", 1, travel, new RimGovernor.Brain.GameFacts.Observations.NeedRecovery.Rate(F(NativeRecreation.GainPerTick(pawn, target, job), "joy gain")))
                            { ServicePosition = NativeServicePositions.Recreation(pawn, target, job, legs), MealThoughts = [], Location = new(target.Position.x,target.Position.z), Stock = new RimGovernor.Brain.GameFacts.Observations.NeedStock.NotStacked(), MaxPawns = Claimants(maxPawns, target), Cell = CellAt(map, target.Position),
                                Bed = null, Deterioration = null, RoomStage = StageAt(target.Position, map), JoySession = NativeRecreation.Session(job), Unsupported = ["play_position_travel", "future_tolerance_changes"] });
                    }
                }
            }
            var live = PawnSettingsRead.LiveMemories(pawn);
            var memories = (live ?? new List<Thought>()).OfType<Thought_Memory>().Select(m => new RimGovernor.Brain.GameFacts.Observations.NeedMemory(m.def.defName,
                F(m.MoodOffset(), "memory offset"), m.permanent ? new RimGovernor.Brain.GameFacts.Observations.MemoryLife.Permanent() : new RimGovernor.Brain.GameFacts.Observations.MemoryLife.Expires(Math.Max(0, m.DurationTicks - m.age)),
                m.def.lerpMoodToZero) { StackLimit = m.def.stackLimit, Group = MemoryGroup(pawn, m) });
            var unsupported = pawn.needs?.AllNeeds.Where(n => n is not Need_Food && n is not Need_Rest && n is not Need_Joy && n is not Need_Mood)
                .Select(n => "unsupported_need:" + n.def.defName).ToList() ?? new List<string>();
            unsupported.Add("disease_and_relationship_changes");
            var sleep = NativeConditionalMood.ReadSleep(pawn);
            var moodRead = NativeConditionalMood.ReadMood(pawn, EquatableArray.From(memories), unsupported);
            var health = pawn.health ?? throw new InvalidOperationException("colonist without a health tracker: " + pawn.GetUniqueLoadID());
            rows.Add(new() { Pawn = pawn.GetUniqueLoadID(), State = state, Location = new(pawn.Position.x, pawn.Position.z),
                CurrentService = voluntary ? CurrentService(pawn) : null,
                RoomUses = new(pawn.Spawned ? DiningSeats(pawn, map) : [], pawn.Spawned ? NativeRecreation.Definitions(pawn) : []),
                LocalHour = F(GenLocalDate.HourFloat(pawn), "local hour"), Awake = pawn.Awake(), BedThoughts = BedThoughtsOf(pawn),
                SleepThreshold = sleep.SleepThreshold, WakeThreshold = sleep.WakeThreshold, SleepBlocked = sleep.Blocked,
                SleepBlockedUntilTicks = sleep.BlockedUntilTicks, SleepWhileStarving = sleep.WhileStarving, Mood = moodRead,
                ComfortableMin = F(pawn.GetStatValue(StatDefOf.ComfyTemperatureMin), "comfortable min"), ComfortableMax = F(pawn.GetStatValue(StatDefOf.ComfyTemperatureMax), "comfortable max"),
                SleepComfortableMin = SleepComfortable(pawn, StatDefOf.ComfyTemperatureMin), SleepComfortableMax = SleepComfortable(pawn, StatDefOf.ComfyTemperatureMax),
                Sleep = SleepOf(pawn, map), LyingIn = LyingInOf(pawn, map), Needs = EquatableArray.From(needs),
                Targets = EquatableArray.From(targets.OrderBy(t => t.Id, StringComparer.Ordinal).GroupBy(t => t.Kind).SelectMany(g => g)), Unsupported = EquatableArray.From(unsupported), VoluntaryControlBlockers = blockers,
                Outdoors = pawn.needs?.outdoors is { } outdoors ? new RimGovernor.Brain.GameFacts.Observations.NeedLevel.Has(F(outdoors.CurLevel, "outdoors level")) : new RimGovernor.Brain.GameFacts.Observations.NeedLevel.Lacks(),
                OutdoorsDisabled = pawn.needs?.outdoors != null && pawn.needs.PrefersIndoors,
                OwnedBed = pawn.ownership?.OwnedBed?.GetUniqueLoadID() ?? "",
                OwnedBedRoom = pawn.ownership?.OwnedBed is { } ownedBed ? BedRoomOf(ownedBed) : null,
                OwnedRoom = OwnedRoomOf(pawn),
                Ascetic = pawn.story?.traits?.HasTrait(TraitDefOf.Ascetic) == true, RivalRooms = RivalRoomsOf(pawn, map),
                ThoughtStages = ThoughtStagesOf(pawn, map), Apparel = ApparelOf(pawn),
                Consciousness = Capacity(health, PawnCapacityDefOf.Consciousness), Moving = Capacity(health, PawnCapacityDefOf.Moving),
                Manipulation = Capacity(health, PawnCapacityDefOf.Manipulation), Sight = Capacity(health, PawnCapacityDefOf.Sight),
                Talking = Capacity(health, PawnCapacityDefOf.Talking),
                MalnutritionSeverity = Severity(health, HediffDefOf.Malnutrition), FoodPoisoningSeverity = Severity(health, HediffDefOf.FoodPoisoning),
                HypothermiaSeverity = Severity(health, HediffDefOf.Hypothermia), HeatstrokeSeverity = Severity(health, HediffDefOf.Heatstroke),
                PainShockThreshold = F(pawn.GetStatValue(StatDefOf.PainShockThreshold), "pain shock threshold"), HealthScale = F(pawn.HealthScale, "health scale"),
                IncomingDamageFactor = F(pawn.GetStatValue(StatDefOf.IncomingDamageFactor), "incoming damage factor"),
                InjuryHealingFactor = F(pawn.GetStatValue(StatDefOf.InjuryHealingFactor), "injury healing factor"),
                ResearchSpeed = F(pawn.GetStatValue(StatDefOf.ResearchSpeed) * Find.Storyteller.difficulty.researchSpeedFactor, "research speed"),
                PlantWorkSpeed = F(pawn.GetStatValue(StatDefOf.PlantWorkSpeed), "plant work speed"),
                Disease = NativeDiseaseFacts.ReadPawn(pawn) is { } disease ? disease with { ImmunityGainSpeed = F(disease.ImmunityGainSpeed, "immunity gain speed") } : throw new InvalidOperationException("colonist without an immunity handler: " + pawn.GetUniqueLoadID()),
                PartLoss = NativePartLossFacts.ReadPawn(pawn) ?? throw new InvalidOperationException("colonist without a hediff set: " + pawn.GetUniqueLoadID()),
                CorpseThoughts = CorpseThoughtsOf(pawn), SightingTicks = sightings?.Covered(pawn.thingIDNumber, now) ?? 0,
                Upkeep = UpkeepOf(pawn, moodRead), Deathrest = DeathresterOf(pawn) });
        }
        var benches = map.listerBuildings.AllBuildingsColonistOfClass<Building_ResearchBench>()
            .Where(b => b.Spawned && (b.GetComp<CompPowerTrader>()?.PowerOn ?? true))
            .OrderBy(b => b.GetUniqueLoadID(), StringComparer.Ordinal)
            .Select(b => new RimGovernor.Brain.GameFacts.Observations.ResearchBenchRead(b.GetUniqueLoadID(), b.def.defName, new(b.Position.x, b.Position.z), b.GetStatValue(StatDefOf.ResearchSpeedFactor))).ToArray();
        var weather = map.weatherManager.CurWeatherLerped;
        return new() { Tick = new(Find.TickManager.TicksGame), Pawns = EquatableArray.From(rows),
            WeatherThought = weather?.weatherThought?.defName ?? "", WeatherThoughtStages = weather?.weatherThought?.stages?.Count ?? 0,
            RainRate = F(map.weatherManager.RainRate, "rain rate"), MapArea = map.Area, Deterioration = DeteriorationPart(), ResearchBenches = EquatableArray.From(benches),
            Corpses = CorpsesInSight(map, people, sightings, now), Deathrest = DeathrestDefs(), Prison = NativePrisonRecruit.Read(map, people, Climate), School = NativeSchool.Read(map, people), Babies = NativeBabyFits.Read(map, people, rows) };
    }
    private static RimGovernor.Brain.GameFacts.Observations.NeedRoomStage StageAt(IntVec3 cell, Map map) => cell.GetRoom(map) is { } room
        ? new RimGovernor.Brain.GameFacts.Observations.NeedRoomStage.Staged(RoomStatDefOf.Impressiveness.GetScoreStageIndex(room.GetStat(RoomStatDefOf.Impressiveness)))
        : new RimGovernor.Brain.GameFacts.Observations.NeedRoomStage.NoRoom();

    // Toils_Ingest.TryFindChairOrSpot's chair predicate; no random standing-chew spot is drawn by a read.
    private static EquatableArray<RimGovernor.Brain.GameFacts.Observations.NeedDiningSeat> DiningSeats(Pawn pawn, Map map) =>
        EquatableArray.From(map.listerBuildings.allBuildingsColonist
            .Where(t => t.def.building?.isSittable == true && t.Faction == pawn.Faction && !t.IsForbidden(pawn)
                && !t.Position.Fogged(map) && t.IsSociallyProper(pawn) && !t.IsBurning() && !t.HostileTo(pawn)
                && pawn.CanReserveAndReach(t, PathEndMode.OnCell, Danger.None))
            .SelectMany(t => t.OccupiedRect()).Distinct()
            .Where(c => pawn.CanReserveSittableOrSpot(c) && GenAdj.CardinalDirections.Any(d => (c + d).HasEatSurface(map)))
            .OrderBy(c => c.x).ThenBy(c => c.z)
            .Select(c => new RimGovernor.Brain.GameFacts.Observations.NeedDiningSeat(new(c.x, c.z), StageAt(c, map))));
    /// <summary>
    /// The pawn's food, rest and joy rates (RimGovernor.Brain.GameFacts.Observations.NeedRate), each need it has; joy from <paramref name="joy"/>, the pawn's own or a
    /// detached one of a pawn that lacks it now (the joiner read, #3805), none when null.
    /// </summary>
    internal static List<RimGovernor.Brain.GameFacts.Observations.NeedRate> NeedRatesOf(Pawn pawn, Map map, Func<NativeRecreation.OutdoorClimate> climate, Need_Joy? joy)
    {
        var needs = new List<RimGovernor.Brain.GameFacts.Observations.NeedRate>();
        if (pawn.needs?.food is { } food) needs.Add(new("food", F(food.CurLevelPercentage, "food level"),
            F(food.FoodFallPerTick / food.MaxLevel, "food fall"), F(food.PercentageThreshHungry, "hungry threshold"))
            { UnservedGainPerTick = 0, GainPerTickPerEffectiveness = 0, Unsupported = ["food_category_and_bed_rate_changes"] });
        if (pawn.needs?.rest is { } rest) needs.Add(new("rest", F(rest.CurLevelPercentage, "rest level"),
            F(rest.RestFallPerTick * pawn.GetStatValue(StatDefOf.RestFallRateFactor), "rest fall"), Need_Rest.ThreshTired)
            {
                UnservedGainPerTick = F(Need_Rest.BaseRestGainPerTick * RestEffectiveness(null) * pawn.GetStatValue(StatDefOf.RestRateMultiplier), "unserved rest gain"),
                GainPerTickPerEffectiveness = Need_Rest.BaseRestGainPerTick * pawn.GetStatValue(StatDefOf.RestRateMultiplier),
                Unsupported = ["rest_category_rate_changes", "involuntary_sleep_risk"],
            });
        if (joy != null)
        {
            var decay = typeof(Need_Joy).GetProperty("FallPerInterval", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(joy) as float?
                ?? throw new InvalidOperationException("Need_Joy.FallPerInterval is not readable.");
            needs.Add(new("joy", F(joy.CurLevelPercentage, "joy level"), F(decay / 150.0 * pawn.GetStatValue(StatDefOf.JoyFallRateFactor), "joy fall"), JoyThreshold(joy))
                {
                    UnservedGainPerTick = F(NativeRecreation.UnservedGainPerTick(pawn, joy), "unserved joy gain"), GainPerTickPerEffectiveness = 0,
                    OutdoorShareByTwelfth = EquatableArray.From(NativeRecreation.OutdoorShares(pawn, map, climate())),
                    Unsupported = ["joy_category_rate_changes", "future_tolerance_changes", "skygaze_condition_gain"],
                });
        }
        return needs;
    }
    /// <summary>The joy need's second threshold (Need.threshPercents[1], the Satisfied category's floor), under which a pawn seeks recreation.</summary>
    internal static double JoyThreshold(Need_Joy joy)
    {
        var thresholds = BridgeCommon.PrivateInstanceField(typeof(Need), "threshPercents")?.GetValue(joy) as List<float>;
        if (thresholds is not { Count: > 1 }) throw new InvalidOperationException("Need.threshPercents is not readable for joy.");
        return thresholds[1];
    }
    /// <summary>
    /// The pawn's deathrest (#3810): null without a Gene_Deathrest. Its efficiency is Gene_Deathrest.DeathrestEfficiency's product
    /// over every spawned bound building, not only the ones usable from the bed it lies in now (BoundComps), so a deathrester awake
    /// reads the casket and accelerators its next deathrest binds again.
    /// </summary>
    private static RimGovernor.Brain.GameFacts.Observations.DeathresterRead? DeathresterOf(Pawn pawn)
    {
        if (pawn.genes?.GetFirstGeneOfType<Gene_Deathrest>() is not Gene_Deathrest gene) return null;
        var efficiency = 1d;
        foreach (var building in gene.BoundBuildings)
            if (building.Spawned && building.TryGetComp<CompDeathrestBindable>() is { Props.deathrestEffectivenessFactor: > 0f } comp)
                efficiency *= comp.Props.deathrestEffectivenessFactor;
        return new(gene.DeathrestCapacity, efficiency, gene.chamberThoughtIndex);
    }
    /// <summary>The deathrest defs (#3810); null without Biotech. Read-only def data.</summary>
    private static RimGovernor.Brain.GameFacts.Observations.DeathrestDefsRead? DeathrestDefs()
    {
        if (!ModsConfig.BiotechActive) return null;
        var bindables = DefDatabase<ThingDef>.AllDefsListForReading
            .Select(d => (Def: d, Props: d.GetCompProperties<CompProperties_DeathrestBindable>())).Where(d => d.Props is not null)
            .OrderBy(d => d.Def.defName, StringComparer.Ordinal)
            .Select(d => new RimGovernor.Brain.GameFacts.Observations.DeathrestBindableRead(d.Def.defName, d.Props.deathrestEffectivenessFactor, d.Props.stackLimit, d.Props.countsTowardsBuildingLimit, d.Props.mustBeLayingInToBind));
        var stages = RoomStatDefOf.Impressiveness.scoreStages?.Select(s => (double)s.minScore)
            ?? throw new InvalidOperationException("RoomStatDefOf.Impressiveness has no score stages");
        return new(Need_Deathrest.FallPerDay, Need_Deathrest.GainPerDayDeathresting, EquatableArray.From(bindables), EquatableArray.From(stages));
    }
    /// <summary>PawnObserver.ObservationRadius (a private const in 1.7): the observer sees a thing within this many cells (InHorDistOf).</summary>
    internal const float ObservationRadius = 5f;
    /// <summary>
    /// The pawn's value reads (#3825, RimGovernor.Brain.GameFacts.Observations.PawnUpkeepRead): its speed at each work speed stat the catalog maps and its move speed and carrying capacity (#3849), 0 where the stat is
    /// disabled for it; the work types it is incapable of; its food fall at the Fed category over a day; its mood target and minor
    /// break threshold, null without a mood need or a mental breaker. A mood need whose target was not read is a broken read.
    /// </summary>
    internal static RimGovernor.Brain.GameFacts.Observations.PawnUpkeepRead UpkeepOf(Pawn pawn, RimGovernor.Brain.GameFacts.Observations.NeedMood? needMood)
    {
        var speeds = WorkSpeeds(pawn);
        var incapable = DefDatabase<WorkTypeDef>.AllDefsListForReading.Where(pawn.WorkTypeIsDisabled).Select(w => w.defName).OrderBy(n => n, StringComparer.Ordinal);
        var food = pawn.needs?.food is { } need ? need.FoodFallPerTickAssumingCategory(HungerCategory.Fed) * GenDate.TicksPerDay : 0;
        var mood = needMood is null ? null : new RimGovernor.Brain.GameFacts.Observations.PawnMoodRead(needMood.UnclampedTarget, needMood.BreakThreshold);
        return new() { Speeds = speeds, SkillSpeeds = SkillSpeeds(pawn), ChildWork = NativeChildWork.Read(pawn), IncapableWork = EquatableArray.From(incapable), FoodPerDay = food, Mood = mood };
    }
    /// <summary>
    /// For each skill of the pawn and each work speed stat that reads it, the stat's value with the skill one stored level higher
    /// (#3912, <see cref="NativeSkillStat.OneLevelUp"/>): what one more level adds to the pawn's speed. Throws where the stat's
    /// pipeline cannot hold that value exact.
    /// </summary>
    internal static EquatableArray<RimGovernor.Brain.GameFacts.Observations.SkillSpeedRead> SkillSpeeds(Pawn pawn)
    {
        var rows = new List<RimGovernor.Brain.GameFacts.Observations.SkillSpeedRead>();
        if (pawn.skills is null) return default;
        var names = RimGovernor.Brain.Bridge.DefinitionCatalog.UpkeepStatNames(DefDatabase<RecipeDef>.AllDefsListForReading);
        foreach (var record in pawn.skills.skills.OrderBy(s => s.def.defName, StringComparer.Ordinal))
            foreach (var name in names)
            {
                var stat = DefDatabase<StatDef>.GetNamedSilentFail(name) ?? throw new InvalidOperationException("upkeep stat not loaded: " + name);
                if (NativeSkillStat.OneLevelUp(pawn, stat, record.def) is { } raised) rows.Add(new(record.def.defName, stat.defName, raised));
            }
        return EquatableArray.From(rows);
    }
    /// <summary>
    /// The pawn's value of each work speed stat the catalog maps (<c>DefinitionCatalog.UpkeepStatNames</c>), 0 where the stat is
    /// disabled for it: the needs forecast's upkeep and the biography's work speeds (#3868) read the same rows.
    /// </summary>
    internal static EquatableArray<RimGovernor.Brain.GameFacts.Observations.WorkSpeedRead> WorkSpeeds(Pawn pawn)
    {
        var names = RimGovernor.Brain.Bridge.DefinitionCatalog.UpkeepStatNames(DefDatabase<RecipeDef>.AllDefsListForReading);
        var speeds = new List<RimGovernor.Brain.GameFacts.Observations.WorkSpeedRead>();
        foreach (var name in names)
        {
            var stat = DefDatabase<StatDef>.GetNamedSilentFail(name) ?? throw new InvalidOperationException("upkeep stat not loaded: " + name);
            speeds.Add(new(stat.defName, stat.Worker.IsDisabledFor(pawn) ? 0 : pawn.GetStatValue(stat)));
        }
        return EquatableArray.From(speeds);
    }
    /// <summary>
    /// The corpses a sighting raises an observed-corpse event for (#3806), each with every colonist's sightings of it: the
    /// colonist's recorded observer passes (<see cref="SightingLog"/>) from whose cell PawnObserver.PossibleToObserve sees the
    /// corpse where it lies now. Corpse.GiveObservedHistoryEvent ignores its observer; the first colonist is passed.
    /// </summary>
    private static EquatableArray<RimGovernor.Brain.GameFacts.Observations.CorpseSightingRead> CorpsesInSight(Map map, List<Pawn> people, SightingLog? log, int now)
    {
        if (people.Count == 0) return default;
        var rows = new List<RimGovernor.Brain.GameFacts.Observations.CorpseSightingRead>();
        var corpses = map.listerThings.ThingsInGroup(ThingRequestGroup.Corpse).OfType<Corpse>()
            .Where(c => c.Spawned && c.InnerPawn != null && !c.Position.Fogged(map)).OrderBy(c => c.GetUniqueLoadID(), StringComparer.Ordinal);
        var passes = people.ToDictionary(p => p, p => log?.Passes(p.thingIDNumber, now) ?? new List<(int Tick, int Cell)>());
        foreach (var corpse in corpses)
        {
            var ev = corpse.GiveObservedHistoryEvent(people[0]);
            if (ev != HistoryEventDefOf.ObservedLayingCorpse && ev != HistoryEventDefOf.ObservedLayingRottingCorpse) continue;
            var seen = new List<RimGovernor.Brain.GameFacts.Observations.CorpseSightings>();
            foreach (var pawn in people.OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal))
            {
                var ago = new List<int>();
                foreach (var (tick, index) in passes[pawn])
                {
                    var cell = map.cellIndices.IndexToCell(index);
                    if (corpse.Position.InHorDistOf(cell, ObservationRadius) && GenSight.LineOfSight(corpse.Position, cell, map, skipFirstCell: true)) ago.Add(now - tick);
                }
                if (ago.Count != 0) seen.Add(new(pawn.GetUniqueLoadID(), EquatableArray.From(ago)));
            }
            rows.Add(new(ev == HistoryEventDefOf.ObservedLayingRottingCorpse,
                corpse.InnerPawn.Faction == Faction.OfPlayer, EquatableArray.From(seen)));
        }
        return EquatableArray.From(rows);
    }
    /// <summary>
    /// The memories the pawn's ideo gives it on an observed-corpse event (#3806), as IdeoUtility.Notify_HistoryEvent dispatches it
    /// to the doer's ideo: each precept's PreceptComp_SelfTookMemoryThought keyed by the event, skipped where its
    /// Notify_MemberTookAction would (no mood, a slave under onlyForNonSlaves, below the thought's minExpectation), priced by the
    /// game's MoodOffset of the unattached memory (zero where MemoryThoughtHandler.TryGainMemory's CanGetThought refuses it).
    /// </summary>
    private static EquatableArray<RimGovernor.Brain.GameFacts.Observations.CorpseThoughtRead> CorpseThoughtsOf(Pawn pawn)
    {
        if (pawn.needs?.mood == null || pawn.Ideo is not { } ideo) return default;
        var rows = new List<RimGovernor.Brain.GameFacts.Observations.CorpseThoughtRead>();
        foreach (var precept in ideo.PreceptsListForReading)
            foreach (var comp in precept.def.comps.OfType<PreceptComp_SelfTookMemoryThought>())
            {
                var rotting = comp.eventDef == HistoryEventDefOf.ObservedLayingRottingCorpse;
                if (!rotting && comp.eventDef != HistoryEventDefOf.ObservedLayingCorpse || comp.thought is not { } def) continue;
                if (comp.onlyForNonSlaves && pawn.IsSlave) continue;
                if (def.minExpectation != null && ExpectationsUtility.CurrentExpectationFor(pawn).order < def.minExpectation.order) continue;
                // Unattached memory: inspect the game's own offset, never add it to the pawn.
                var thought = ThoughtMaker.MakeThought(def, precept);
                thought.pawn = pawn;
                var offset = ThoughtUtility.CanGetThought(pawn, def) ? thought.MoodOffset() : 0;
                if (!double.IsFinite(offset)) continue;
                rows.Add(new(rotting, def.defName, offset, thought.DurationTicks, def.stackLimit, def.stackedEffectMultiplier));
            }
        return EquatableArray.From(rows);
    }
    /// <summary>The cell's roof and room as Need_Outdoors, the undergrounder workers, ApplyBedThoughts and SteadyEnvironmentEffects read them (#3339).</summary>
    internal static RimGovernor.Brain.GameFacts.Observations.NeedCell CellAt(Map map, IntVec3 cell)
    {
        var roof = map.roofGrid.RoofAt(cell);
        return new(roof != null, roof?.isThickRoof == true, roof?.isNatural == true,
            cell.UsesOutdoorTemperature(map), cell.GetRoom(map)?.PsychologicallyOutdoors ?? true)
            { NearbyFires = new RimGovernor.Brain.GameFacts.Observations.NeedNearbyFires.Counted(NativeJoinerTraits.FiresAt(map, cell)) };
    }
    /// <summary>
    /// The bed's room as ApplyBedThoughts reads it: Room.Role and RoomStatDefOf.Impressiveness.GetScoreStageIndex(room.GetStat(Impressiveness));
    /// and the care a patient lying in it gets (#3804): whether it is costless (ApplyBedThoughts' SleptOnGround), the room's PsychologicallyOutdoors and InfectionChanceFactor (the roomless
    /// score without a room), the bed's SurgerySuccessChanceFactor and ImmunityGainSpeedFactor. Read-only.
    /// </summary>
    private static RimGovernor.Brain.GameFacts.Observations.NeedBedRoom BedRoomOf(Building_Bed bed)
    {
        var room = bed.GetRoom();
        var stage = room == null ? throw new InvalidOperationException("bed without a room: " + bed.GetUniqueLoadID())
            : RoomStatDefOf.Impressiveness.GetScoreStageIndex(room.GetStat(RoomStatDefOf.Impressiveness));
        return new(room.Role?.defName ?? "", stage, bed.ForPrisoners)
        {
            PsychologicallyOutdoors = room.PsychologicallyOutdoors, Costless = bed.CostListAdjusted().Count == 0,
            InfectionChanceFactor = room.GetStat(RoomStatDefOf.InfectionChanceFactor),
            SurgerySuccessFactor = bed.GetStatValue(StatDefOf.SurgerySuccessChanceFactor),
            ImmunityGainSpeedFactor = bed.GetStatValue(StatDefOf.ImmunityGainSpeedFactor),
        };
    }
    /// <summary>
    /// When the pawn's current lie-down gives its bed thoughts (RimGovernor.Brain.GameFacts.Observations.BedThoughtOnset, #3871), from the two Pawn_MindState fields
    /// Toils_LayDown reads: applyBedThoughtsTick 0 (reset whenever the pawn is not laying) draws a fresh onset at the next
    /// lie-down; applyBedThoughtsOnLeave is set once ApplyBedRelatedEffects gave them, so FinalizeLayingJob gives them again; else
    /// they are given once the drawn tick passes. Read-only.
    /// </summary>
    private static RimGovernor.Brain.GameFacts.Observations.BedThoughtOnset BedThoughtsOf(Pawn pawn)
    {
        var mind = pawn.mindState;
        if (mind.applyBedThoughtsTick == 0) return new RimGovernor.Brain.GameFacts.Observations.BedThoughtOnset.None();
        return mind.applyBedThoughtsOnLeave ? new RimGovernor.Brain.GameFacts.Observations.BedThoughtOnset.Given()
            : new RimGovernor.Brain.GameFacts.Observations.BedThoughtOnset.Due(Math.Max(0, mind.applyBedThoughtsTick - Find.TickManager.TicksGame));
    }
    /// <summary>
    /// Where JobGiver_GetRest lays <paramref name="pawn"/> now and what Toils_LayDown gives it there (#3840, RimGovernor.Brain.GameFacts.Observations.NeedSleep): the
    /// bed RestUtility.FindBedFor picks, else the ground at its cell. Null for a wild man (it never rests in a bed and gets no
    /// SleptOutside), a pawn without a rest need or one absent from this map. Carried babies retain their future rest surface.
    /// </summary>
    private static RimGovernor.Brain.GameFacts.Observations.NeedSleep? SleepOf(Pawn pawn, Map map)
    {
        if (pawn.needs?.rest == null || pawn.MapHeld != map || pawn.IsWildMan()) return null;
        var traveler = SleepTraveler(pawn);
        var selected = RestUtility.FindBedFor(pawn, traveler, true, false, pawn.GuestStatus);
        var bed = pawn.DevelopmentalStage == DevelopmentalStage.Baby ? pawn.CurrentBed() ?? selected : selected;
        return SleepAt(pawn, map, bed, bed is null ? pawn.PositionHeld : SleepingCell(pawn, bed));
    }

    // SafePlaceForBaby passes the hauler as traveler. A carried baby's path is its holder's, never its own unspawned pawn path.
    private static Pawn SleepTraveler(Pawn pawn) => pawn.Spawned ? pawn : pawn.SpawnedParentOrMe as Pawn
        ?? throw new RequiredFactException("carried baby sleeping traveler:" + pawn.GetUniqueLoadID());

    /// <summary>RestUtility.GetBedSleepingSlotPosFor / Toils_Bed.GotoBed, without the game's slot-zero error fallback.</summary>
    internal static IntVec3 SleepingCell(Pawn pawn, Building_Bed bed)
    {
        if (bed.IsOwner(pawn, out var assigned) && assigned is int slot) return bed.GetSleepingSlotPos(slot);
        for (var i = 0; i < bed.SleepingSlotsCount; i++)
            if ((i >= bed.OwnersForReading.Count || bed.OwnersForReading[i] == null) && bed.GetCurOccupant(i) == pawn)
                return bed.GetSleepingSlotPos(i);
        for (var i = 0; i < bed.SleepingSlotsCount; i++)
            if ((i >= bed.OwnersForReading.Count || bed.OwnersForReading[i] == null) && bed.GetCurOccupant(i) == null)
                return bed.GetSleepingSlotPos(i);
        throw new RequiredFactException("sleeping slot:" + pawn.GetUniqueLoadID() + ":" + bed.GetUniqueLoadID());
    }

    /// <summary>
    /// The baby's sleeping surface when its nursery beds are absent (#3939). ChildcareUtility.SafePlaceForBaby
    /// retains CurrentBed first, otherwise RestUtility.FindBedFor. Vanilla exposes no excluded-room selector;
    /// after excluding that room, use its owned-bed priority, def order, danger passes and native legality.
    /// Ground is a real absent-bed state, read by the same SleepAt as every other sleeper.
    /// </summary>
    internal static RimGovernor.Brain.GameFacts.Observations.NeedSleep SleepWithoutNursery(Pawn pawn, Map map, Room? nursery)
    {
        if (pawn.needs?.rest == null) throw new InvalidOperationException("baby rest is unreadable: " + pawn.GetUniqueLoadID());
        bool Allowed(Building_Bed bed) => nursery == null || bed.GetRoom() != nursery;
        var current = pawn.CurrentBed();
        var traveler = SleepTraveler(pawn);
        var selected = current ?? RestUtility.FindBedFor(pawn, traveler, true, false, pawn.GuestStatus);
        if (selected == null || Allowed(selected)) return SleepAt(pawn, map, selected, selected == null ? pawn.PositionHeld : SleepingCell(pawn, selected));
        var candidates = map.listerThings.AllThings.OfType<Building_Bed>().Where(Allowed).ToArray();
        var slab = pawn.Ideo?.PreceptsListForReading.Any(p => p.def.prefersSlabBed) == true;
        bool Valid(Building_Bed bed) => RestUtility.IsValidBedFor(bed, pawn, traveler, true, guestStatus: pawn.GuestStatus);
        Building_Bed? Closest(ThingDef def, bool medical, Danger danger, bool avoidCorpse) =>
            (Building_Bed?)GenClosest.ClosestThingReachable(pawn.PositionHeld, map, ThingRequest.ForDef(def), PathEndMode.OnCell,
                TraverseParms.For(traveler), 9999f, t => t is Building_Bed bed && Allowed(bed) && bed.Medical == medical
                    && bed.Position.GetDangerFor(pawn, map) <= danger && Valid(bed)
                    && (!avoidCorpse || !bed.Position.GetItems(map).Any(t => t.def.IsCorpse)));
        if (HealthAIUtility.ShouldSeekMedicalRest(pawn))
            foreach (var def in candidates.Select(b => b.def).Distinct().OrderBy(d => slab && !d.building.bed_slabBed ? 1 : 0)
                .ThenBy(d => d.building.bed_maxBodySize)
                .ThenByDescending(d => d.GetStatValueAbstract(StatDefOf.MedicalTendQualityOffset))
                .ThenByDescending(d => d.GetStatValueAbstract(StatDefOf.BedRestEffectiveness)))
                foreach (var danger in new[] { Danger.None, Danger.Deadly })
                    if (Closest(def, true, danger, false) is { } medical) return SleepAt(pawn, map, medical, SleepingCell(pawn, medical));
        if (pawn.ownership?.OwnedBed is { } owned && Allowed(owned) && Valid(owned)) return SleepAt(pawn, map, owned, SleepingCell(pawn, owned));
        var partnerBed = LovePartnerRelationUtility.ExistingMostLikedLovePartnerRel(pawn, allowDead: false)?.otherPawn.ownership.OwnedBed;
        if (partnerBed != null && Allowed(partnerBed) && Valid(partnerBed)) return SleepAt(pawn, map, partnerBed, SleepingCell(pawn, partnerBed));
        var defs = RestUtility.AllBedDefBestToWorst.OrderBy(d => slab && !d.building.bed_slabBed ? 1 : 0).ToArray();
        for (var pass = 0; pass < 3; pass++)
            foreach (var def in defs.Where(d => RestUtility.CanUseBedEver(pawn, d)))
                if (Closest(def, false, pass < 2 ? Danger.None : Danger.Deadly, pass == 0) is { } bed)
                    return SleepAt(pawn, map, bed, SleepingCell(pawn, bed));
        return SleepAt(pawn, map, null, pawn.PositionHeld);
    }
    /// <summary>
    /// The lie-down <paramref name="pawn"/> is in now (#3882, RimGovernor.Brain.GameFacts.Observations.NeedLyingIn): its CurrentBed, else the ground, at its own cell,
    /// and the two facts besides its rest level RestUtility.ShouldWakeUp reads: a hediff that blocks sleeping and its deathrest
    /// (#3883). Null when it is not laying (GetPosture), has no rest need or is not spawned.
    /// </summary>
    private static RimGovernor.Brain.GameFacts.Observations.NeedLyingIn? LyingInOf(Pawn pawn, Map map) => pawn.needs?.rest == null || !pawn.Spawned || !pawn.GetPosture().Laying() ? null
        : new(SleepAt(pawn, map, pawn.CurrentBed(), pawn.Position), pawn.health.hediffSet.HasHediffBlocksSleeping(), DeathrestOf(pawn, map))
        { DeathrestWakeStage = pawn.Deathresting ? DeathrestWakeStageOf(pawn) : -1, Cell = CellAt(map, pawn.Position) };

    // Toils_LayDown.UpdateDeathrestThoughtIndex is private and mutates the gene. Read its typed inputs without changing it.
    private static int DeathrestWakeStageOf(Pawn pawn)
    {
        var room = pawn.GetRoom();
        if (pawn.needs.mood == null || room == null) return -1;
        if (!pawn.IsWildMan() && room.PsychologicallyOutdoors) return 0;
        if (pawn.story.traits.HasTrait(TraitDefOf.Ascetic)) return -1;
        return RoomStatDefOf.Impressiveness.GetScoreStageIndex(room.GetStat(RoomStatDefOf.Impressiveness)) + 1;
    }
    /// <summary>
    /// The deathrest the pawn lies in now (#3883, RimGovernor.Brain.GameFacts.Observations.LyingInDeathrest), as Hediff_Deathrest.PostTickInterval runs
    /// Gene_Deathrest.TickDeathresting: paused while SanguophageUtility.ShouldBeDeathrestingOrInComaInsteadOfDead (Hediff_Deathrest.Paused's
    /// test, called directly so the read leaves the hediff's pause cache alone); else it ends at MinDeathrestTicks when autoWake is
    /// set and notifiedWakeOK (private) is not, and is held otherwise, either way until its bed touches sunlight. Read-only.
    /// </summary>
    private static RimGovernor.Brain.GameFacts.Observations.LyingInDeathrest DeathrestOf(Pawn pawn, Map map)
    {
        if (!pawn.Deathresting) return new RimGovernor.Brain.GameFacts.Observations.LyingInDeathrest.None();
        if (SanguophageUtility.ShouldBeDeathrestingOrInComaInsteadOfDead(pawn)) return new RimGovernor.Brain.GameFacts.Observations.LyingInDeathrest.Paused();
        var gene = pawn.genes?.GetFirstGeneOfType<Gene_Deathrest>()
            ?? throw new InvalidOperationException("deathresting without a Gene_Deathrest: " + pawn.GetUniqueLoadID());
        var notified = BridgeCommon.PrivateInstanceField(typeof(Gene_Deathrest), "notifiedWakeOK")?.GetValue(gene) as bool?
            ?? throw new InvalidOperationException("Gene_Deathrest.notifiedWakeOK is not readable.");
        var sunlight = SunlightOf(pawn, map);
        return gene.autoWake && !notified ? new RimGovernor.Brain.GameFacts.Observations.LyingInDeathrest.Wakes(Math.Max(0, gene.MinDeathrestTicks - gene.deathrestTicks), sunlight)
            : new RimGovernor.Brain.GameFacts.Observations.LyingInDeathrest.Held(sunlight);
    }
    /// <summary>Gene_Deathrest.SunlightCheckInterval (private const): TickDeathresting checks sunlight on this hash interval.</summary>
    private const int SunlightCheckInterval = 150;
    /// <summary>IntVec3.InSunlight's glow limit, a literal in the game's code (SkyManager.CurSkyGlow above 0.1).</summary>
    private const float SunlightGlow = 0.1f;
    /// <summary>
    /// When the deathrester's bed first touches sunlight (#3883, RimGovernor.Brain.GameFacts.Observations.DeathrestSunlight), within a year: shaded without a bed
    /// (Gene_Deathrest.PawnOrBedTouchingSunlight reads only CurrentBed) or with every bed cell roofed; else the first of the pawn's
    /// sunlight checks (IsHashIntervalTick(150): the ticks where TicksGame plus thingIDNumber.HashOffset() is a multiple of 150) whose
    /// sky glow is above 0.1. That glow is GenCelestial.CelestialSunGlow at the check's absolute tick, darkened as
    /// SkyManager.CurrentSkyTarget darkens it by each condition affecting the map (GetAllGameConditionsAffectingMap) with a SkyTarget,
    /// while the condition lasts (Permanent, or TicksLeft past the check), at its lerp factor then. A condition's SkyTargetLerpFactor
    /// is GameConditionUtility.LerpInOutValue of its TicksPassed and TicksLeft over its TransitionTicks, scaled by a target its class
    /// fixes; the target is the factor now over that ramp now. Only a condition whose SkyTarget glow is at most 0.1 can bring a glow
    /// above 0.1 down to it (LerpDarken moves toward the lower glow), so only those are read. Current weather caps and transition
    /// are applied before conditions until its native duration expires. Future weather draws, lightning and CompAffectsSky
    /// things remain separate named omissions.
    /// </summary>
    private static RimGovernor.Brain.GameFacts.Observations.DeathrestSunlight SunlightOf(Pawn pawn, Map map)
    {
        if (pawn.CurrentBed() is not { } bed) return new RimGovernor.Brain.GameFacts.Observations.DeathrestSunlight.Shaded();
        var open = bed.OccupiedRect().Cells.Where(c => c.InBounds(map) && !map.roofGrid.Roofed(c)).Select(c => new Cell(c.x, c.z)).ToArray();
        if (open.Length == 0) return new RimGovernor.Brain.GameFacts.Observations.DeathrestSunlight.Shaded();
        var affecting = new List<GameCondition>();
        map.gameConditionManager.GetAllGameConditionsAffectingMap(map, affecting);
        var darkening = new List<(GameCondition Condition, float Glow, float Target)>();
        foreach (var condition in affecting)
        {
            if (condition.SkyTarget(map) is not { } sky || sky.glow > SunlightGlow) continue;
            var ramp = GameConditionUtility.LerpInOutValue(condition, condition.TransitionTicks);
            var factor = condition.SkyTargetLerpFactor(map);
            if (ramp <= 0 && factor != 0) throw new InvalidOperationException("sky lerp target of condition " + condition.def.defName + " is not readable at its ramp's end.");
            darkening.Add((condition, sky.glow, ramp <= 0 ? 0 : factor / ramp));
        }
        var now = Find.TickManager.TicksGame;
        var abs = Find.TickManager.TicksAbs;
        var weather = NativeDeathrestWeather.Read(map);
        var findings = new List<string> { "deathrest_sunlight_lightning_not_modelled", "deathrest_sunlight_sky_things_not_modelled" };
        EquatableArray<string> Findings(int through)
        {
            if (through > weather.RemainingTicks) findings.Add("deathrest_sunlight_future_weather_not_modelled");
            return EquatableArray.From(findings);
        }
        var phase = ((now + 1 + pawn.thingIDNumber.HashOffset()) % SunlightCheckInterval + SunlightCheckInterval) % SunlightCheckInterval;
        for (var ahead = 1 + (phase == 0 ? 0 : SunlightCheckInterval - phase); ahead <= GenDate.TicksPerYear; ahead += SunlightCheckInterval)
        {
            var glow = GenCelestial.CelestialSunGlow(map.Tile, abs + ahead);
            if (ahead <= weather.RemainingTicks) glow = weather.GlowAt(glow, ahead);
            foreach (var (condition, sky, target) in darkening)
            {
                if (!condition.Permanent && condition.TicksLeft <= ahead) continue;
                var left = condition.Permanent ? condition.TransitionTicks + 1f : condition.TicksLeft - ahead;
                var factor = GameConditionUtility.LerpInOutValue(condition.TicksPassed + ahead, left, condition.TransitionTicks, target);
                glow = UnityEngine.Mathf.Lerp(glow, Math.Min(glow, sky), factor);
            }
            if (glow > SunlightGlow) return new RimGovernor.Brain.GameFacts.Observations.DeathrestSunlight.Sunlit(EquatableArray.From(open), ahead) { NotModelled = Findings(ahead) };
        }
        return new RimGovernor.Brain.GameFacts.Observations.DeathrestSunlight.Unlit(GenDate.TicksPerYear) { NotModelled = Findings(GenDate.TicksPerYear) };
    }
    private static RimGovernor.Brain.GameFacts.Observations.NeedSleep SleepAt(Pawn pawn, Map map, Building_Bed? bed, IntVec3 cell) =>
        new(bed?.GetUniqueLoadID() ?? "", cell.GetRoom(map)?.PsychologicallyOutdoors ?? true, bed == null || bed.CostListAdjusted().Count == 0,
            Need_Rest.BaseRestGainPerTick * RestEffectiveness(bed) * pawn.GetStatValue(StatDefOf.RestRateMultiplier),
            bed?.GetStatValue(StatDefOf.Comfort) ?? 0f, cell.GetTemperature(map))
        { Position = bed == null && !pawn.Spawned ? new RimGovernor.Brain.GameFacts.Observations.ServicePositionRead.Unprojected("carried_baby_safe_drop_random")
            : new RimGovernor.Brain.GameFacts.Observations.ServicePositionRead.Located(new(cell.x, cell.z)) };
    /// <summary>
    /// The rest effectiveness Toils_LayDown.LayDown's tick applies (#3840): the bed's BedRestEffectiveness when its def's
    /// statBases list the stat, else the stat's valueIfMissing (a sleeping spot, or no bed). GetStatValue alone would
    /// answer a spot the stat's minValue.
    /// </summary>
    private static float RestEffectiveness(Building_Bed? bed) => bed == null || !bed.def.statBases.StatListContains(StatDefOf.BedRestEffectiveness)
        ? StatDefOf.BedRestEffectiveness.valueIfMissing : bed.GetStatValue(StatDefOf.BedRestEffectiveness);
    /// <summary>
    /// The comfortable bound Toils_LayDown.ApplyBedThoughts compares a sleeper's ambient temperature with (#3840): the race's
    /// abstract stat (no apparel) plus each active gene's offset under Biotech.
    /// </summary>
    private static float SleepComfortable(Pawn pawn, StatDef stat)
    {
        var value = pawn.def.GetStatValueAbstract(stat);
        if (ModsConfig.BiotechActive && pawn.genes != null)
            foreach (var gene in pawn.genes.GenesListForReading)
                if (gene.Active) value += gene.def.statOffsets.GetStatOffsetFromList(stat);
        return value;
    }
    /// <summary>StatPart_EnvironmentalEffects def data on the DeteriorationRate stat, read by reflection (private fields); null without the part.</summary>
    internal static RimGovernor.Brain.GameFacts.Observations.NeedDeteriorationPart? DeteriorationPart()
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
    private static RimGovernor.Brain.GameFacts.Observations.NeedDeterioration? DeteriorationOf(Map map, Thing thing)
    {
        if (!thing.def.useHitPoints || !thing.def.CanEverDeteriorate) return null;
        return new(thing.HitPoints, F(SteadyEnvironmentEffects.FinalDeteriorationRate(thing), "deterioration rate"), thing.def.deteriorateFromEnvironmentalEffects,
            thing.Position.GetTerrain(map)?.extraDeteriorationFactor ?? 0, SteadyEnvironmentEffects.ProtectedByEdifice(thing.Position, map));
    }
    /// <summary>
    /// The stage rows of the thoughts #3339 and the bedroom credit (#3655) value, each priced by the game's MoodOffset for this
    /// pawn on an unattached thought (zero when ThoughtUtility.CanGetThought refuses it, as the game would never give it). The
    /// bed and ascetic rows also carry each null stage at 0: Toils_LayDown.ApplyBedThoughts gains no bed memory at a null
    /// room stage (SleptInBedroom's dull stage, #3822), and ThoughtWorker_Ascetic leaves the thought inactive at those stages;
    /// so do the dining and rec room memories (#3803), which no meal or joy activity gains at a null stage.
    /// </summary>
    private static EquatableArray<RimGovernor.Brain.GameFacts.Observations.NeedThoughtStage> ThoughtStagesOf(Pawn pawn, Map map)
    {
        var rows = new List<RimGovernor.Brain.GameFacts.Observations.NeedThoughtStage>();
        void Rows(ThoughtDef? def, string role, bool nullStagesInactive = false) => StageRows(rows, pawn, def, role, nullStagesInactive);
        Rows(ThoughtDefOf.SleptOutside, "slept_outside");
        Rows(ThoughtDefOf.SleptOnGround, "slept_on_ground");
        Rows(ThoughtDefOf.SleptInBedroom, "slept_in_bedroom", nullStagesInactive: true);
        Rows(ThoughtDefOf.SleptInBarracks, "slept_in_barracks", nullStagesInactive: true);
        // The other bed memories ApplyBedThoughts gives (#3840): on the ground or a costless bed, below and above the comfortable range.
        Rows(ThoughtDefOf.SleptOnGround, "slept_on_ground");
        Rows(ThoughtDefOf.SleptInCold, "slept_in_cold");
        Rows(ThoughtDefOf.SleptInHeat, "slept_in_heat");
        // The comfort need's thought (#3840), the comfort a bed gives over the day; no ThoughtDefOf field names it, its worker
        // class is the typed route. A pawn without the need never has it.
        if (pawn.needs?.comfort != null)
            foreach (var def in DefDatabase<ThoughtDef>.AllDefsListForReading.Where(d => d.workerClass == typeof(ThoughtWorker_NeedComfort)))
                Rows(def, "need_comfort");
        Rows(map.weatherManager.CurWeatherLerped?.weatherThought, "weather");
        foreach (var def in DefDatabase<ThoughtDef>.AllDefsListForReading.Where(d => d.workerClass == typeof(ThoughtWorker_NeedOutdoors)))
            Rows(def, "need_outdoors");
        foreach (var def in DefDatabase<ThoughtDef>.AllDefsListForReading.Where(d => d.workerClass == typeof(ThoughtWorker_IsIndoorsForUndergrounder)))
            Rows(def, "undergrounder_indoors");
        foreach (var def in DefDatabase<ThoughtDef>.AllDefsListForReading.Where(d => d.workerClass == typeof(ThoughtWorker_IsUndergroundForUndergrounder)))
            Rows(def, "undergrounder_underground");
        foreach (var def in DefDatabase<ThoughtDef>.AllDefsListForReading.Where(d => d.workerClass == typeof(ThoughtWorker_IsOutdoorsForUndergrounder)))
            Rows(def, "undergrounder_outdoors");
        rows.AddRange(RoomTraitStages(pawn, _ => true));
        // The hospital patient's room thought (#3804): ThoughtWorker_HospitalPatientRoomStats, active in a medical bed of a
        // Hospital room at its Impressiveness stage, inactive at a null stage (rows at 0). No ThoughtDefOf field names it.
        foreach (var def in DefDatabase<ThoughtDef>.AllDefsListForReading.Where(d => d.workerClass == typeof(ThoughtWorker_HospitalPatientRoomStats)))
            Rows(def, "hospital_patient", nullStagesInactive: true);
        // The apparel thoughts a garment changes (#3816). ThoughtWorker_PsychologicallyNude's Naked is inactive under Ideology,
        // where the precept workers below take its place.
        Rows(ThoughtDefOf.ApparelDamaged, "apparel_damaged");
        if (!ModsConfig.IdeologyActive) Rows(ThoughtDefOf.Naked, "naked");
        Rows(ThoughtDefOf.DeadMansApparel, "dead_mans_apparel");
        foreach (var (worker, role) in ApparelPrecepts)
            foreach (var def in DefDatabase<ThoughtDef>.AllDefsListForReading.Where(d => d.workerClass == worker && PreceptAllows(pawn, d)))
                Rows(def, role);
        // The required-apparel thoughts a garment a title or role requires lifts (#3836). No ThoughtDefOf field names the
        // title's; its worker class is the typed route. Without Royalty (or Ideology) neither def is loaded and no row is read.
        foreach (var def in DefDatabase<ThoughtDef>.AllDefsListForReading.Where(d => d.workerClass == typeof(ThoughtWorker_RoyalTitleApparelRequirementNotMet)))
            Rows(def, TitleApparelRole);
        Rows(ThoughtDefOf.IdeoRoleApparelRequirementNotMet, RoleApparelRole);
        // The title's minimum-quality thought a garment at the minimum lifts (#3841); its worker class is the typed route.
        foreach (var def in DefDatabase<ThoughtDef>.AllDefsListForReading.Where(d => d.workerClass == typeof(ThoughtWorker_RoyalTitleApparelMinQualityNotMet)))
            Rows(def, TitleApparelQualityRole);
        // The title room thoughts a planned throne room or suite relieves (#3807); no ThoughtDefOf field names them, their
        // worker classes are the typed route. Without Royalty no def has these workers and no row is read.
        foreach (var (worker, role) in TitleRoomThoughts)
            foreach (var def in DefDatabase<ThoughtDef>.AllDefsListForReading.Where(d => d.workerClass == worker))
                Rows(def, role);
        // The thought a deathrester's chamber gives it between deathrests (#3810), by stage (dull and mediocre give none); no
        // ThoughtDefOf field names it, its worker class is the typed route.
        if (pawn.genes?.GetFirstGeneOfType<Gene_Deathrest>() is not null)
            foreach (var def in DefDatabase<ThoughtDef>.AllDefsListForReading.Where(d => d.workerClass == typeof(ThoughtWorker_DeathrestChamber)))
                Rows(def, "deathrest_chamber", nullStagesInactive: true);
        // The room memories a planned dining or rec room gives (#3803): Toils_Ingest.FinalizeIngest gains AteInImpressiveDiningRoom
        // and JoyUtility.TryGainRecRoomThought JoyActivityInImpressiveRecRoom at the Impressiveness stage of the room the pawn is
        // in, none at a null stage (rows at 0); FinalizeIngest gains AteWithoutTable eating with no table, which a dining room lifts.
        Rows(ThoughtDefOf.AteInImpressiveDiningRoom, "ate_in_dining_room", nullStagesInactive: true);
        Rows(ThoughtDefOf.JoyActivityInImpressiveRecRoom, "joy_in_rec_room", nullStagesInactive: true);
        Rows(ThoughtDefOf.AteWithoutTable, "ate_without_table");
        return EquatableArray.From(rows);
    }
    /// <summary>How long a thought of <paramref name="def"/> lasts: a memory's DurationTicks, lerpMoodToZero and stackLimit, else situational.</summary>
    private static RimGovernor.Brain.GameFacts.Observations.ThoughtLasting Lasting(ThoughtDef def) => def.IsMemory
        ? new RimGovernor.Brain.GameFacts.Observations.ThoughtLasting.Memory(def.DurationTicks, def.lerpMoodToZero, def.stackLimit, def.stackedEffectMultiplier, def.stagesStack) : new RimGovernor.Brain.GameFacts.Observations.ThoughtLasting.Situational();
    /// <summary>
    /// The Impressiveness of each room another pawn owns, as ThoughtWorker_BedroomJealous compares the pawn's own room against
    /// them (#3826): every spawned pawn of the player faction but this one, without a host faction, by its ownership.OwnedRoom,
    /// each room once, ascending. The pawn's current room stays in when another pawn owns it too: the suite credit asks about
    /// the pawn moved out of it.
    /// </summary>
    /// <summary>
    /// One thought def's stage rows for <paramref name="pawn"/> (ThoughtStagesOf): each stage at the game's MoodOffset on an
    /// unattached thought, 0 when ThoughtUtility.CanGetThought refuses it; a null stage a 0 row with <paramref name="nullStagesInactive"/>
    /// (the worker inactive there), else left out, as is a stage the game cannot price.
    /// </summary>
    internal static void StageRows(List<RimGovernor.Brain.GameFacts.Observations.NeedThoughtStage> rows, Pawn pawn, ThoughtDef? def, string role, bool nullStagesInactive)
    {
        if (def?.stages == null) return;
        var can = ThoughtUtility.CanGetThought(pawn, def);
        for (var stage = 0; stage < def.stages.Count; stage++)
        {
            if (def.stages[stage] == null)
            {
                if (nullStagesInactive) rows.Add(new(def.defName, role, stage, 0, Lasting(def)));
                continue;
            }
            var offset = can ? StageOffset(pawn, def, stage) : 0;
            if (!double.IsFinite(offset)) continue;
            rows.Add(new(def.defName, role, stage, offset, Lasting(def)));
        }
    }

    /// <summary>
    /// The own-bedroom trait thoughts' stage rows (#3655, #3826, #3899) of the defs <paramref name="include"/> keeps: Ascetic
    /// (ThoughtWorker_Ascetic, by the owned room's Impressiveness stage, inactive at a null stage), Greedy (ThoughtWorker_Greedy,
    /// staged by that stage + 1, stage 0 without an owned room; its null stages are the worker inactive, rows at 0) and Jealous
    /// (ThoughtWorker_BedroomJealous). No ThoughtDefOf field names them; their worker classes are the typed route.
    /// </summary>
    internal static List<RimGovernor.Brain.GameFacts.Observations.NeedThoughtStage> RoomTraitStages(Pawn pawn, Func<ThoughtDef, bool> include)
    {
        var rows = new List<RimGovernor.Brain.GameFacts.Observations.NeedThoughtStage>();
        foreach (var (worker, role, nullStagesInactive) in RoomTraitWorkers)
            foreach (var def in DefDatabase<ThoughtDef>.AllDefsListForReading.Where(d => d.workerClass == worker && include(d)))
                StageRows(rows, pawn, def, role, nullStagesInactive);
        return rows;
    }

    /// <summary>The own-bedroom trait thoughts' worker classes, their stage-row roles and whether a null stage is the worker inactive.</summary>
    internal static readonly (Type Worker, string Role, bool NullStagesInactive)[] RoomTraitWorkers =
    [
        (typeof(ThoughtWorker_Ascetic), "ascetic", true), (typeof(ThoughtWorker_Greedy), "greedy", true), (typeof(ThoughtWorker_BedroomJealous), "jealous", false),
    ];

    /// <summary>The pawn's own room (RimGovernor.Brain.GameFacts.Observations.NeedOwnedRoom, #3899): Pawn_Ownership.OwnedRoom's Impressiveness and its score stage; null without one.</summary>
    private static RimGovernor.Brain.GameFacts.Observations.NeedOwnedRoom? OwnedRoomOf(Pawn pawn)
    {
        if (pawn.ownership?.OwnedRoom is not { } room) return null;
        var impressiveness = F(room.GetStat(RoomStatDefOf.Impressiveness), "owned room impressiveness");
        return new(impressiveness, RoomStatDefOf.Impressiveness.GetScoreStageIndex((float)impressiveness));
    }
    private static EquatableArray<double> RivalRoomsOf(Pawn pawn, Map map)
    {
        var rooms = new HashSet<Room>();
        foreach (var other in map.mapPawns.SpawnedPawnsInFaction(Faction.OfPlayer))
            if (other != pawn && other.HostFaction == null && other.ownership?.OwnedRoom is { } room) rooms.Add(room);
        return EquatableArray.From(rooms.Select(r => (double)r.GetStat(RoomStatDefOf.Impressiveness)).OrderBy(v => v).ToArray());
    }
    /// <summary>The non-social apparel precept workers by class, the typed route to their gendered defs (#3816).</summary>
    private static readonly (Type Worker, string Role)[] ApparelPrecepts =
    [
        (typeof(ThoughtWorker_Precept_GroinUncovered), "groin_uncovered"),
        (typeof(ThoughtWorker_Precept_GroinOrChestUncovered), "groin_or_chest_uncovered"),
        (typeof(ThoughtWorker_Precept_GroinChestOrHairUncovered), "groin_chest_or_hair_uncovered"),
        (typeof(ThoughtWorker_Precept_GroinChestHairOrFaceUncovered), "groin_chest_hair_or_face_uncovered"),
        (typeof(ThoughtWorker_Precept_AnyBodyPartCovered), "any_body_part_covered"),
        (typeof(ThoughtWorker_Precept_AnyBodyPartButGroinCovered), "any_body_part_but_groin_covered"),
        (typeof(ThoughtWorker_Precept_AnyBodyPartButHairOrFaceCovered), "any_body_part_but_hair_or_face_covered"),
    ];
    /// <summary>ThoughtWorker_Precept.CurrentStateInternal's ideoligion gate, before its ShouldHaveThought.</summary>
    private static bool PreceptAllows(Pawn pawn, ThoughtDef def)
    {
        var ideo = pawn.Ideo;
        if (ideo == null) return false;
        var precept = ideo.GetFirstPreceptAllowingSituationalThought(def);
        if (precept != null && !precept.def.enabledForNPCFactions && !pawn.CountsAsNonNPCForPrecepts()) return false;
        return ideo.cachedPossibleSituationalThoughts.Contains(def);
    }
    /// <summary>The pawn facts the apparel workers read beside the worn apparel (#3816); null without an apparel tracker.</summary>
    private static RimGovernor.Brain.GameFacts.Observations.NeedApparel? ApparelOf(Pawn pawn)
    {
        if (pawn.apparel == null) return null;
        var legs = pawn.health.hediffSet.GetNotMissingParts().Any(p => p.IsInGroup(BodyPartGroupDefOf.Legs));
        var safe = GenTemperature.SafeTemperatureRange(pawn.def).Includes(pawn.AmbientTemperature);
        return new((RimGovernor.Brain.GameFacts.Defs.Gender)(int)pawn.gender, legs, safe)
            { KindRequired = EquatableArray.From((pawn.kindDef?.apparelRequired ?? new List<ThingDef>()).Select(d => d.defName).OrderBy(d => d, StringComparer.Ordinal)),
              Requirements = ApparelRequirementsOf(pawn, out var minimum), MinimumQuality = minimum };
    }
    /// <summary>The stage-row roles of the required-apparel thoughts (ForecastApparel.TitleApparel, RoleApparel and TitleApparelQuality).</summary>
    private const string TitleApparelRole = "title_apparel_unmet", RoleApparelRole = "role_apparel_unmet", TitleApparelQualityRole = "title_apparel_quality_unmet";
    /// <summary>
    /// The active apparel requirements whose unmet thought the pawn can have (#3836), each with every apparel def that meets it:
    /// ThoughtWorker_RoyalTitleApparelRequirementNotMet.Validate's titles in effect, and the role Precept_Role.SituationThoughtsToAdd
    /// gives Thought_IdeoRoleApparelRequirementNotMet for (an active role of a free non-slave colonist who is no quest lodger).
    /// Also <paramref name="minimum"/>, the highest requiredMinimumApparelQuality of the titles in effect (#3841):
    /// ThoughtWorker_RoyalTitleApparelMinQualityNotMet.Validate holds every worn garment to each title's, ungated by
    /// allowApparelRequirements; Awful with no title.
    /// </summary>
    private static EquatableArray<RimGovernor.Brain.GameFacts.Observations.NeedApparelRequirement> ApparelRequirementsOf(Pawn pawn, out RimGovernor.Brain.GameFacts.Defs.QualityCategory minimum)
    {
        var least = QualityCategory.Awful;
        foreach (var title in pawn.royalty?.AllTitlesInEffectForReading ?? new List<RoyalTitle>())
            if (title.def.requiredMinimumApparelQuality > least) least = title.def.requiredMinimumApparelQuality;
        minimum = (RimGovernor.Brain.GameFacts.Defs.QualityCategory)(int)least;
        var rows = new List<RimGovernor.Brain.GameFacts.Observations.NeedApparelRequirement>();
        var active = pawn.apparel.ActiveRequirementsForReading;
        void Add(ApparelRequirement requirement, string thought)
        {
            if (!active.Contains(requirement)) return;
            // ApparelMeetsRequirement reads the def's tags past a requiredDefs match, as IsMet does of a worn garment's.
            var meets = DefDatabase<ThingDef>.AllDefsListForReading.Where(d => d.IsApparel
                && (d.apparel.tags != null || requirement.requiredDefs?.Contains(d) == true) && requirement.ApparelMeetsRequirement(d, false));
            rows.Add(new(thought, EquatableArray.From(meets.Select(d => d.defName).OrderBy(d => d, StringComparer.Ordinal))));
        }
        if (pawn.royalty is { allowApparelRequirements: true } royalty)
            foreach (var title in royalty.AllTitlesInEffectForReading)
                foreach (var requirement in title.def.requiredApparel ?? new List<ApparelRequirement>())
                    Add(requirement, TitleApparelRole);
        if (ModsConfig.IdeologyActive && pawn.Ideo?.GetRole(pawn) is { Active: true } role && pawn.IsFreeNonSlaveColonist && !pawn.IsQuestLodger())
            foreach (var requirement in role.apparelRequirements ?? new List<PreceptApparelRequirement>())
                if (requirement.requirement != null) Add(requirement.requirement, RoleApparelRole);
        return EquatableArray.From(rows);
    }
    /// <summary>The title room thoughts' worker classes and the stage-row roles the brain reads them by (ForecastHousing.Title*Role).</summary>
    private static readonly (Type Worker, string Role)[] TitleRoomThoughts =
    [
        (typeof(ThoughtWorker_BedroomRequirementsNotMet), "title_bedroom_unmet"), (typeof(ThoughtWorker_NoPersonalBedroom), "title_no_bedroom"),
        (typeof(ThoughtWorker_ThroneRoomRequirementsNotMet), "title_throne_unmet"), (typeof(ThoughtWorker_NoThroneroom), "title_no_throne"),
    ];
    private static readonly FieldInfo? SituationalStage = BridgeCommon.PrivateInstanceField(typeof(Thought_Situational), "curStageIndex");
    /// <summary>Thought.MoodOffset of an unattached thought at one stage: a memory forced to the stage, a situational thought with its stage set.</summary>
    internal static double StageOffset(Pawn pawn, ThoughtDef def, int stage)
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
    private static RimGovernor.Brain.GameFacts.Observations.NeedCurrentService? CurrentService(Pawn pawn)
    {
        var execution = NativePawnActivities.Read(pawn);
        var step = NativePawnActivities.ActiveStep(pawn);
        var job = pawn.CurJob;
        var driver = pawn.jobs?.curDriver;
        if (execution == null || execution.State != "started" || step == null || job == null
            || NativePawnActivities.RunningJobId(pawn) != job.loadID || job.playerForced || (pawn.jobs ?? throw new System.InvalidOperationException("Required native value is unavailable: pawn.jobs.")).jobQueue.Count != 0
            || pawn.pather.Moving || step.Deadline <= Find.TickManager.TicksGame) return null;
        // A stationary recreation driver's final Delay toil (NativeRecreation.StationaryDriver); billiards' shot loop
        // and sleep's dynamic wake/medical/other-job conditions remain unassessed.
        if (step.Kind != PawnActivityKind.Recreation || !NativeRecreation.StationaryDriver(driver)
            || !(driver ?? throw new System.InvalidOperationException("Required native value is unavailable: driver.")).OnLastToil || driver.ticksLeftThisToil <= 0 || job.doUntilGatheringEnded
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
            new(pawn.Position.x, pawn.Position.z), remaining, gain)
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
    private static RimGovernor.Brain.GameFacts.Observations.NeedMemoryGroupRead MemoryGroup(Pawn pawn, Thought_Memory memory)
    {
        var def = memory.def;
        var kind = RimGovernor.Brain.GameFacts.Observations.MemoryGroupKind.Mood;
        double offset;
        if (memory is Thought_MemorySocial social)
        {
            kind = social is Thought_MemorySocialCumulative
                ? RimGovernor.Brain.GameFacts.Observations.MemoryGroupKind.SocialCumulative
                : social.GetType() == typeof(Thought_MemorySocial)
                    ? RimGovernor.Brain.GameFacts.Observations.MemoryGroupKind.SocialFading
                    : throw new InvalidOperationException("unmodelled social memory class:" + social.GetType().Name);
            offset = ThoughtUtility.ThoughtNullified(pawn, def) ? 0 : social.opinionOffset;
        }
        else
        {
            offset = memory.MoodOffset();
            if (memory.GetType() == typeof(Thought_Memory) && def.lerpMoodToZero && memory.age < memory.DurationTicks)
                offset /= 1 - (double)memory.age / memory.DurationTicks;
        }
        return new(memory.otherPawn?.GetUniqueLoadID() ?? "", memory.age, memory.DurationTicks,
            F(offset, "unfaded memory offset"), def.stackedEffectMultiplier, def.stackLimitForSameOtherPawn,
            memory is Thought_MemorySocial ? def.lerpOpinionToZeroAfterDurationPct : def.lerpMoodToZero ? 0 : 1,
            kind, def.maxCumulatedOpinionOffset)
        {
            Label = memory.LabelCap,
            Unprojected = memory is not Thought_MemorySocial && memory.GetType() != typeof(Thought_Memory)
                ? "nursery_opinion_mood_class_unprojected:" + def.defName : "",
        };
    }

    internal static RimGovernor.Brain.GameFacts.Observations.NeedMemory MealMemory(Pawn pawn, FoodUtility.ThoughtFromIngesting source) => Memory(pawn, source.thought, source.fromPrecept);
    /// <summary>The memory <paramref name="def"/> would be on the pawn: an unattached native memory, so the native stage, stat
    /// and trait multipliers price it; it is never added to the pawn.</summary>
    internal static RimGovernor.Brain.GameFacts.Observations.NeedMemory Memory(Pawn pawn, ThoughtDef def, Precept? precept = null)
    {
        var thought = ThoughtMaker.MakeThought(def, precept);
        thought.pawn = pawn;
        return new(def.defName, F(thought.MoodOffset(), "memory offset"), new RimGovernor.Brain.GameFacts.Observations.MemoryLife.Expires(thought.DurationTicks), def.lerpMoodToZero) { StackLimit = def.stackLimit };
    }
    /// <summary>The maxPawns literal JobDriver_Ingest passes to Reserve for a meal stack (decompiled 1.7; vanilla exposes no symbol for it).</summary>
    internal const int IngestMaxPawns = 10;
    /// <summary>Vanilla claimant capacity of a target; a non-positive read is a broken read and throws.</summary>
    private static int Claimants(int maxPawns, Thing target) =>
        maxPawns > 0 ? maxPawns : throw new InvalidOperationException("no claimant capacity for " + target.GetUniqueLoadID());
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
    /// <summary>The capacity's level now (#3519).</summary>
    private static double Capacity(Pawn_HealthTracker health, PawnCapacityDef capacity) => F(health.capacities.GetLevel(capacity), capacity.defName);
    /// <summary>The hediff's current severity, 0 when the pawn has none (#3519).</summary>
    private static double Severity(Pawn_HealthTracker health, HediffDef hediff) => F(health.hediffSet.GetFirstHediffOfDef(hediff)?.Severity ?? 0, hediff.defName);
    /// <summary>A finite read; a non-finite one is a broken read and throws.</summary>
    private static double F(double value, string what) => double.IsFinite(value) ? value : throw new InvalidOperationException(what + " is not finite.");
}
