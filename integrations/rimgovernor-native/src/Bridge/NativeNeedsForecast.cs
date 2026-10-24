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
        var sightings = map.GetComponent<TrafficState>()?.Sightings;
        var now = Find.TickManager.TicksGame;
        // The candidates, read once for every colonist; only each pawn's survivors are sorted, by id then grouped by kind.
        (Thing Thing, string Kind)[]? possible = null;
        // The map's outdoor recreation weather, read once for every colonist with a joy need (#3825 D7).
        NativeRecreation.OutdoorClimate? outdoorClimate = null;
        foreach (var pawn in people.OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal))
        {
            var needs = new List<Obs.NeedRate>();
            if (pawn.needs?.food is { } food) needs.Add(new("food", K(food.CurLevelPercentage),
                K(food.FoodFallPerTick / food.MaxLevel), K(food.PercentageThreshHungry))
                { Unsupported = ["food_category_and_bed_rate_changes"] });
            if (pawn.needs?.rest is { } rest) needs.Add(new("rest", K(rest.CurLevelPercentage),
                K(rest.RestFallPerTick * pawn.GetStatValue(StatDefOf.RestFallRateFactor)), K(Need_Rest.ThreshTired))
                {
                    UnservedGainPerTick = K(Need_Rest.BaseRestGainPerTick * RestEffectiveness(null) * pawn.GetStatValue(StatDefOf.RestRateMultiplier)),
                    GainPerTickPerEffectiveness = Need_Rest.BaseRestGainPerTick * pawn.GetStatValue(StatDefOf.RestRateMultiplier),
                    Unsupported = ["rest_category_rate_changes", "involuntary_sleep_risk"],
                });
            if (pawn.needs?.joy is { } joy)
            {
                var decay = typeof(Need_Joy).GetProperty("FallPerInterval", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(joy);
                var thresholds = BridgeCommon.PrivateInstanceField(typeof(Need), "threshPercents")?.GetValue(joy) as List<float>;
                needs.Add(new("joy", K(joy.CurLevelPercentage), decay is float fall
                    ? K(fall / 150.0 * pawn.GetStatValue(StatDefOf.JoyFallRateFactor)) : default,
                    thresholds is { Count: > 1 } ? K(thresholds[1]) : default)
                    {
                        UnservedGainPerTick = K(NativeRecreation.UnservedGainPerTick(pawn)),
                        OutdoorShareByTwelfth = EquatableArray.From(NativeRecreation.OutdoorShares(pawn, map, outdoorClimate ??= new(map))),
                        Unsupported = ["joy_category_rate_changes", "future_tolerance_changes", "skygaze_condition_gain"],
                    });
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
                        K(Need_Rest.BaseRestGainPerTick * RestEffectiveness((Building_Bed)target) * pawn.GetStatValue(StatDefOf.RestRateMultiplier))) { Location = new(target.Position.x,target.Position.z), MaxPawns = Capacity(((Building_Bed)target).SleepingSlotsCount),
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
                ComfortableMin = K(pawn.GetStatValue(StatDefOf.ComfyTemperatureMin)), ComfortableMax = K(pawn.GetStatValue(StatDefOf.ComfyTemperatureMax)),
                SleepComfortableMin = SleepComfortable(pawn, StatDefOf.ComfyTemperatureMin), SleepComfortableMax = SleepComfortable(pawn, StatDefOf.ComfyTemperatureMax),
                Sleep = SleepOf(pawn, map), Needs = EquatableArray.From(needs),
                Mood = pawn.needs?.mood is { } mood ? K(mood.CurLevelPercentage) : default,
                MoodTarget = pawn.needs?.mood is { } targetMood ? K(targetMood.CurInstantLevel) : default,
                BreakThreshold = pawn.mindState?.mentalBreaker is { } breaker ? K(breaker.BreakThresholdMinor) : default,
                Memories = memories is null ? default : Fact.Known(EquatableArray.From(memories)),
                Targets = EquatableArray.From(targets.OrderBy(t => t.Id, StringComparer.Ordinal).GroupBy(t => t.Kind).SelectMany(g => g)), Unsupported = EquatableArray.From(unsupported), VoluntaryControlBlockers = blockers,
                OutdoorsLevel = pawn.needs?.outdoors is { } outdoors ? K(outdoors.CurLevel) : default,
                OutdoorsDisabled = pawn.needs?.outdoors != null && pawn.needs.PrefersIndoors,
                OwnedBed = pawn.ownership?.OwnedBed?.GetUniqueLoadID() ?? "",
                OwnedBedRoom = pawn.ownership?.OwnedBed is { } ownedBed ? BedRoomOf(ownedBed) : null,
                Ascetic = pawn.story?.traits?.HasTrait(TraitDefOf.Ascetic) == true, RivalRooms = RivalRoomsOf(pawn, map),
                ThoughtStages = ThoughtStagesOf(pawn, map), Apparel = ApparelOf(pawn),
                Consciousness = Capacity(pawn, PawnCapacityDefOf.Consciousness), Moving = Capacity(pawn, PawnCapacityDefOf.Moving),
                Manipulation = Capacity(pawn, PawnCapacityDefOf.Manipulation), Sight = Capacity(pawn, PawnCapacityDefOf.Sight),
                Talking = Capacity(pawn, PawnCapacityDefOf.Talking),
                MalnutritionSeverity = Severity(pawn, HediffDefOf.Malnutrition), FoodPoisoningSeverity = Severity(pawn, HediffDefOf.FoodPoisoning),
                HypothermiaSeverity = Severity(pawn, HediffDefOf.Hypothermia), HeatstrokeSeverity = Severity(pawn, HediffDefOf.Heatstroke),
                PainShockThreshold = HealthStat(pawn, StatDefOf.PainShockThreshold), HealthScale = HealthScaleOf(pawn),
                IncomingDamageFactor = HealthStat(pawn, StatDefOf.IncomingDamageFactor), InjuryHealingFactor = HealthStat(pawn, StatDefOf.InjuryHealingFactor),
                ResearchSpeed = K(pawn.GetStatValue(StatDefOf.ResearchSpeed) * Find.Storyteller.difficulty.researchSpeedFactor),
                PlantWorkSpeed = K(pawn.GetStatValue(StatDefOf.PlantWorkSpeed)), Disease = NativeDiseaseFacts.ReadPawn(pawn),
                CorpseThoughts = CorpseThoughtsOf(pawn), SightingTicks = sightings?.Covered(pawn.thingIDNumber, now) ?? 0,
                Upkeep = UpkeepOf(pawn, conditionalMood.MoodUnclampedTarget), Deathrest = DeathresterOf(pawn) });
        }
        var benches = map.listerBuildings.AllBuildingsColonistOfClass<Building_ResearchBench>()
            .Where(b => b.Spawned && (b.GetComp<CompPowerTrader>()?.PowerOn ?? true))
            .OrderBy(b => b.GetUniqueLoadID(), StringComparer.Ordinal)
            .Select(b => new Obs.ResearchBenchRead(b.GetUniqueLoadID(), b.def.defName, new(b.Position.x, b.Position.z), b.GetStatValue(StatDefOf.ResearchSpeedFactor))).ToArray();
        var weather = map.weatherManager.CurWeatherLerped;
        return new() { Tick = Find.TickManager.TicksGame, Pawns = EquatableArray.From(rows),
            WeatherThought = weather?.weatherThought?.defName ?? "", WeatherThoughtStages = weather?.weatherThought?.stages?.Count ?? 0,
            RainRate = K(map.weatherManager.RainRate), MapArea = Fact.Known(map.Area), Deterioration = DeteriorationPart(), ResearchBenches = EquatableArray.From(benches),
            Corpses = CorpsesInSight(map, people, sightings, now), Deathrest = DeathrestDefs() };
    }
    /// <summary>
    /// The pawn's deathrest (#3810): null without a Gene_Deathrest. Its efficiency is Gene_Deathrest.DeathrestEfficiency's product
    /// over every spawned bound building, not only the ones usable from the bed it lies in now (BoundComps), so a deathrester awake
    /// reads the casket and accelerators its next deathrest binds again.
    /// </summary>
    private static Obs.DeathresterRead? DeathresterOf(Pawn pawn)
    {
        if (pawn.genes?.GetFirstGeneOfType<Gene_Deathrest>() is not Gene_Deathrest gene) return null;
        var efficiency = 1d;
        foreach (var building in gene.BoundBuildings)
            if (building.Spawned && building.TryGetComp<CompDeathrestBindable>() is { Props.deathrestEffectivenessFactor: > 0f } comp)
                efficiency *= comp.Props.deathrestEffectivenessFactor;
        return new(gene.DeathrestCapacity, efficiency, gene.chamberThoughtIndex);
    }
    /// <summary>The deathrest defs (#3810); null without Biotech. Read-only def data.</summary>
    private static Obs.DeathrestDefsRead? DeathrestDefs()
    {
        if (!ModsConfig.BiotechActive) return null;
        var bindables = DefDatabase<ThingDef>.AllDefsListForReading
            .Select(d => (Def: d, Props: d.GetCompProperties<CompProperties_DeathrestBindable>())).Where(d => d.Props is not null)
            .OrderBy(d => d.Def.defName, StringComparer.Ordinal)
            .Select(d => new Obs.DeathrestBindableRead(d.Def.defName, d.Props!.deathrestEffectivenessFactor, d.Props.stackLimit, d.Props.countsTowardsBuildingLimit, d.Props.mustBeLayingInToBind));
        var stages = RoomStatDefOf.Impressiveness.scoreStages?.Select(s => (double)s.minScore)
            ?? throw new InvalidOperationException("RoomStatDefOf.Impressiveness has no score stages");
        return new(Need_Deathrest.FallPerDay, Need_Deathrest.GainPerDayDeathresting, EquatableArray.From(bindables), EquatableArray.From(stages));
    }
    /// <summary>PawnObserver.ObservationRadius (a private const in 1.7): the observer sees a thing within this many cells (InHorDistOf).</summary>
    internal const float ObservationRadius = 5f;
    /// <summary>
    /// The pawn's value reads (#3825, Obs.PawnUpkeepRead): its speed at each work speed stat the catalog maps and its move speed and carrying capacity (#3849), 0 where the stat is
    /// disabled for it; the work types it is incapable of; its food fall at the Fed category over a day; its mood target and minor
    /// break threshold, null without a mood need or a mental breaker. A mood need whose target was not read is a broken read.
    /// </summary>
    private static Obs.PawnUpkeepRead UpkeepOf(Pawn pawn, Fact<double> moodTarget)
    {
        var names = RimGovernor.Brain.Bridge.DefinitionCatalog.UpkeepStatNames(DefDatabase<RecipeDef>.AllDefsListForReading);
        var speeds = new List<Obs.WorkSpeedRead>();
        foreach (var name in names)
        {
            var stat = DefDatabase<StatDef>.GetNamedSilentFail(name) ?? throw new InvalidOperationException("upkeep stat not loaded: " + name);
            speeds.Add(new(stat.defName, stat.Worker.IsDisabledFor(pawn) ? 0 : pawn.GetStatValue(stat)));
        }
        var incapable = DefDatabase<WorkTypeDef>.AllDefsListForReading.Where(pawn.WorkTypeIsDisabled).Select(w => w.defName).OrderBy(n => n, StringComparer.Ordinal);
        var food = pawn.needs?.food is { } need ? need.FoodFallPerTickAssumingCategory(HungerCategory.Fed) * GenDate.TicksPerDay : 0;
        Obs.PawnMoodRead? mood = null;
        if (pawn.needs?.mood != null && pawn.mindState?.mentalBreaker is { } breaker)
        {
            if (!moodTarget.TryGetValue(out var target)) throw new InvalidOperationException("mood target unread for " + pawn.GetUniqueLoadID());
            mood = new(target, breaker.BreakThresholdMinor);
        }
        return new() { Speeds = EquatableArray.From(speeds), IncapableWork = EquatableArray.From(incapable), FoodPerDay = food, Mood = mood };
    }
    /// <summary>
    /// The corpses a sighting raises an observed-corpse event for (#3806), each with every colonist's sightings of it: the
    /// colonist's recorded observer passes (<see cref="SightingLog"/>) from whose cell PawnObserver.PossibleToObserve sees the
    /// corpse where it lies now. Corpse.GiveObservedHistoryEvent ignores its observer; the first colonist is passed.
    /// </summary>
    private static EquatableArray<Obs.CorpseSightingRead> CorpsesInSight(Map map, List<Pawn> people, SightingLog? log, int now)
    {
        if (people.Count == 0) return default;
        var rows = new List<Obs.CorpseSightingRead>();
        var corpses = map.listerThings.ThingsInGroup(ThingRequestGroup.Corpse).OfType<Corpse>()
            .Where(c => c.Spawned && c.InnerPawn != null && !c.Position.Fogged(map)).OrderBy(c => c.GetUniqueLoadID(), StringComparer.Ordinal);
        var passes = people.ToDictionary(p => p, p => log?.Passes(p.thingIDNumber, now) ?? new List<(int Tick, int Cell)>());
        foreach (var corpse in corpses)
        {
            var ev = corpse.GiveObservedHistoryEvent(people[0]);
            if (ev != HistoryEventDefOf.ObservedLayingCorpse && ev != HistoryEventDefOf.ObservedLayingRottingCorpse) continue;
            var seen = new List<Obs.CorpseSightings>();
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
    private static EquatableArray<Obs.CorpseThoughtRead> CorpseThoughtsOf(Pawn pawn)
    {
        if (pawn.needs?.mood == null || pawn.Ideo is not { } ideo) return default;
        var rows = new List<Obs.CorpseThoughtRead>();
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
    internal static Obs.NeedCell CellAt(Map map, IntVec3 cell)
    {
        var roof = map.roofGrid.RoofAt(cell);
        return new(Fact.Known(roof != null), Fact.Known(roof?.isThickRoof == true), Fact.Known(roof?.isNatural == true),
            Fact.Known(cell.UsesOutdoorTemperature(map)), Fact.Known(cell.GetRoom(map)?.PsychologicallyOutdoors ?? true));
    }
    /// <summary>
    /// The bed's room as ApplyBedThoughts reads it: Room.Role and RoomStatDefOf.Impressiveness.GetScoreStageIndex(room.GetStat(Impressiveness));
    /// and the care a patient lying in it gets (#3804): whether it is costless (ApplyBedThoughts' SleptOnGround), the room's PsychologicallyOutdoors and InfectionChanceFactor (the roomless
    /// score without a room), the bed's SurgerySuccessChanceFactor and ImmunityGainSpeedFactor. Read-only.
    /// </summary>
    private static Obs.NeedBedRoom BedRoomOf(Building_Bed bed)
    {
        var room = bed.GetRoom();
        var stage = room == null ? default : Fact.Known(RoomStatDefOf.Impressiveness.GetScoreStageIndex(room.GetStat(RoomStatDefOf.Impressiveness)));
        return new(room?.Role?.defName ?? "", stage, bed.ForPrisoners)
        {
            PsychologicallyOutdoors = room?.PsychologicallyOutdoors ?? true, Costless = bed.CostListAdjusted().Count == 0,
            InfectionChanceFactor = room?.GetStat(RoomStatDefOf.InfectionChanceFactor) ?? RoomStatDefOf.InfectionChanceFactor.roomlessScore,
            SurgerySuccessFactor = bed.GetStatValue(StatDefOf.SurgerySuccessChanceFactor),
            ImmunityGainSpeedFactor = bed.GetStatValue(StatDefOf.ImmunityGainSpeedFactor),
        };
    }
    /// <summary>
    /// Where JobGiver_GetRest lays <paramref name="pawn"/> now and what Toils_LayDown gives it there (#3840, Obs.NeedSleep): the
    /// bed RestUtility.FindBedFor picks, else the ground at its cell. Null for a wild man (it never rests in a bed and gets no
    /// SleptOutside), a pawn without a rest need or one not spawned.
    /// </summary>
    private static Obs.NeedSleep? SleepOf(Pawn pawn, Map map)
    {
        if (pawn.needs?.rest == null || !pawn.Spawned || pawn.IsWildMan()) return null;
        var bed = RestUtility.FindBedFor(pawn);
        var cell = bed?.Position ?? pawn.Position;
        return new(bed?.GetUniqueLoadID() ?? "", cell.GetRoom(map)?.PsychologicallyOutdoors ?? true, bed == null || bed.CostListAdjusted().Count == 0,
            Need_Rest.BaseRestGainPerTick * RestEffectiveness(bed) * pawn.GetStatValue(StatDefOf.RestRateMultiplier),
            bed?.GetStatValue(StatDefOf.Comfort) ?? 0f, cell.GetTemperature(map));
    }
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
    /// bed and ascetic rows also carry each null stage at 0: Toils_LayDown.ApplyBedThoughts gains no bed memory at a null
    /// room stage (SleptInBedroom's dull stage, #3822), and ThoughtWorker_Ascetic leaves the thought inactive at those stages.
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
                    if (nullStagesInactive) rows.Add(new(def.defName, role, stage, 0, def.IsMemory ? K(def.DurationTicks) : default, def.IsMemory && def.lerpMoodToZero));
                    continue;
                }
                var offset = can ? StageOffset(pawn, def, stage) : 0;
                if (!double.IsFinite(offset)) continue;
                rows.Add(new(def.defName, role, stage, offset,
                    def.IsMemory ? K(def.DurationTicks) : default, def.IsMemory && def.lerpMoodToZero));
            }
        }
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
        // No ThoughtDefOf field names the Ascetic thought; its worker class is the typed route (#3655).
        foreach (var def in DefDatabase<ThoughtDef>.AllDefsListForReading.Where(d => d.workerClass == typeof(ThoughtWorker_Ascetic)))
            Rows(def, "ascetic", nullStagesInactive: true);
        // The trait bedroom thoughts a planned suite relieves (#3826): Greedy (ThoughtWorker_Greedy, staged by the owned room's
        // Impressiveness stage + 1, stage 0 without one; its null stages are the worker inactive, rows at 0) and Jealous
        // (ThoughtWorker_BedroomJealous). No ThoughtDefOf field names them; their worker classes are the typed route.
        foreach (var def in DefDatabase<ThoughtDef>.AllDefsListForReading.Where(d => d.workerClass == typeof(ThoughtWorker_Greedy)))
            Rows(def, "greedy", nullStagesInactive: true);
        foreach (var def in DefDatabase<ThoughtDef>.AllDefsListForReading.Where(d => d.workerClass == typeof(ThoughtWorker_BedroomJealous)))
            Rows(def, "jealous");
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
        return EquatableArray.From(rows);
    }
    /// <summary>
    /// The Impressiveness of each room another pawn owns, as ThoughtWorker_BedroomJealous compares the pawn's own room against
    /// them (#3826): every spawned pawn of the player faction but this one, without a host faction, by its ownership.OwnedRoom,
    /// each room once, ascending. The pawn's current room stays in when another pawn owns it too: the suite credit asks about
    /// the pawn moved out of it.
    /// </summary>
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
    private static Obs.NeedApparel? ApparelOf(Pawn pawn)
    {
        if (pawn.apparel == null) return null;
        var legs = pawn.health.hediffSet.GetNotMissingParts().Any(p => p.IsInGroup(BodyPartGroupDefOf.Legs));
        var safe = GenTemperature.SafeTemperatureRange(pawn.def).Includes(pawn.AmbientTemperature);
        return new((RimGovernor.Contracts.Wire.Defs.Gender)(int)pawn.gender, legs, safe)
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
    private static EquatableArray<Obs.NeedApparelRequirement> ApparelRequirementsOf(Pawn pawn, out RimGovernor.Contracts.Wire.Defs.QualityCategory minimum)
    {
        var least = QualityCategory.Awful;
        foreach (var title in pawn.royalty?.AllTitlesInEffectForReading ?? new List<RoyalTitle>())
            if (title.def.requiredMinimumApparelQuality > least) least = title.def.requiredMinimumApparelQuality;
        minimum = (RimGovernor.Contracts.Wire.Defs.QualityCategory)(int)least;
        var rows = new List<Obs.NeedApparelRequirement>();
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
            || NativePawnActivities.RunningJobId(pawn) != job.loadID || job.playerForced || pawn.jobs!.jobQueue.Count != 0
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
