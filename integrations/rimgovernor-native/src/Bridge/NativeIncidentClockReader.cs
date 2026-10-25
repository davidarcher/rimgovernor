using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using RimGovernor.Brain.GameFacts.Observations;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace RimGovernor.Native;

/// <summary>Local interval events, conditional on the observed future eligibility and successful placement.</summary>
internal static class NativeIncidentClockReader
{
    // The native time curve is private, with no typed public route to its future values.
    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "ShipChunkDropMTBDaysCurve")]
    private static extern ref SimpleCurve ShipCurve(StorytellerComp_ShipChunkDrop? owner);

    internal static IncidentClockRead Read(Map map, double horizon, IReadOnlyList<IncidentCategoryRead> categories)
    {
        Rand.PushState();
        try { return ReadCore(map, horizon, categories); }
        finally { Rand.PopState(); }
    }

    private static IncidentClockRead ReadCore(Map map, double horizon, IReadOnlyList<IncidentCategoryRead> categories)
    {
        var now = Find.TickManager.TicksGame;
        var mains = new List<RandomMainIntervalRead>();
        var resets = new List<IncidentClockReset>();
        var unknown = new SortedSet<string>(StringComparer.Ordinal);
        var cycles = NativeStorytellerFacts.Cycles(map, horizon).ToDictionary(c => c.CompIndex);
        var renewal = "";
        var renewalTick = -1;
        for (var index = 0; index < Find.Storyteller.def.comps.Count; index++)
        {
            var props = Find.Storyteller.def.comps[index];
            if (!props.Enabled || !Find.Storyteller.storytellerComps.Any(c => c.props == props)) continue;
            var targetTags = map.IncidentTargetTags().ToArray();
            if (targetTags.Any(tag => props.disallowedTargetTags?.Contains(tag) == true)
                || props.allowedTargetTags is { Count: > 0 } tags && !targetTags.Any(tags.Contains)) continue;
            if (props.GetType() == typeof(StorytellerCompProperties_RandomMain) && props is StorytellerCompProperties_RandomMain random)
            {
                var space = map.Tile.Valid && map.Tile.LayerDef.isSpace;
                mains.Add(new(index, random.minDaysPassed, IncidentCategoryReader.MtbChance(random.mtbDays * (space ? random.spaceMtbDayFactor : 1)),
                    60000f * random.maxThreatBigIntervalDays >= int.MaxValue ? int.MaxValue : (int)Math.Floor(60000f * random.maxThreatBigIntervalDays),
                    space ? GenDate.DaysToTicks(random.spaceMinSpacingDays) : 0));
                continue;
            }
            // These comps create or service quests, which are outside the explicitly conditional local forecast.
            if (typeof(StorytellerComp_RandomQuest).IsAssignableFrom(props.compClass)
                || props.compClass == typeof(StorytellerComp_ImportantQuest) || props.compClass == typeof(StorytellerComp_RandomEpicQuest)
                || props.compClass == typeof(StorytellerComp_RefiringUniqueQuest) || props.compClass == typeof(StorytellerComp_MechanitorComplexQuest)
                || props.compClass == typeof(StorytellerComp_WorkSite) || props.compClass == typeof(StorytellerComp_MonolithMigration)) continue;
            var cache = new Dictionary<(IncidentCategoryDef Category, int Weight), IncidentCategoryRead>();
            var direct = new Dictionary<(IncidentDef Incident, float? Points), (double Eligible, double Execution)>();
            IncidentCategoryRead Category(IncidentCategoryDef category, int weight = 0)
            {
                if (cache.TryGetValue((category, weight), out var read)) return read;
                read = categories.FirstOrDefault(c => c.CompIndex == index && c.Category == category.defName)
                    ?? IncidentCategoryReader.Read(map, props, index, category, 1, new FloatRange(1, 1),
                        weight == 1 ? d => map.Biome.CommonalityOfDisease(d) : weight == 2 ? _ => 1 : null);
                foreach (var item in read.Unmodelled) unknown.Add(item);
                return cache[(category, weight)] = read;
            }
            IReadOnlyList<IncidentResetOutcome> Outcomes(IncidentCategoryRead read, double day, bool population = false)
            {
                var result = new Dictionary<string, double>(StringComparer.Ordinal);
                foreach (var outcome in read.Outcomes)
                {
                    var eligible = outcome.Incidents.Where(i => i.EarliestDay <= GenDate.DaysPassedSinceSettleFloat + day
                       ).ToArray();
                    var total = eligible.Sum(i => i.Weight);
                    if (!(total > 0)) continue;
                    var grow = IncidentCategoryReader.Chance(StorytellerComp.IncreasesPopChanceByPopIntentCurve.Evaluate(StorytellerUtilityPopulation.PopulationIntent));
                    var withPop = eligible.Where(i => i.ChangesPopulation).Sum(i => i.Weight);
                    var withoutPop = total - withPop;
                    foreach (var incident in eligible)
                    {
                        var chance = population ? incident.ChangesPopulation
                            ? (withPop > 0 ? grow / withPop : 0) + (withoutPop > 0 ? 0 : (1 - grow) / total)
                            : (withoutPop > 0 ? (1 - grow) / withoutPop : 0) + (withPop > 0 ? 0 : grow / total)
                            : 1 / total;
                        result[incident.Incident] = result.GetValueOrDefault(incident.Incident) + outcome.Probability * incident.Weight * chance
                            * (GenDate.DaysPassedSinceSettleFloat + day >= incident.ExecuteAfterDay ? incident.ExecutionChance : 0);
                    }
                }
                return result.Select(p => new IncidentResetOutcome(p.Key, p.Value, read.ThreatBig)).ToArray();
            }
            void Send(double day, double chance, IReadOnlyList<IncidentResetOutcome> outcomes, bool isRenewal = false, int min = 0, int max = 0)
            {
                if (day < 0 || day >= horizon || GenDate.DaysPassedSinceSettleFloat + day <= props.minDaysPassed || chance <= 0 || outcomes.Count == 0) return;
                resets.Add(new(day, index, chance) { Outcomes = [.. outcomes], Renewal = isRenewal, RenewalMinTicks = min, RenewalMaxTicks = max });
            }
            IReadOnlyList<IncidentResetOutcome> Direct(double day, IncidentDef def, bool forced = false, float? points = null)
            {
                if (!direct.TryGetValue((def, points), out var gate))
                {
                    if (IncidentCategoryReader.Unmodelled(def)) unknown.Add(def.defName + ":" + (def.workerClass.FullName ?? def.workerClass.Name));
                    direct[(def, points)] = gate = (IncidentCategoryReader.Eligibility(map, def, points), IncidentCategoryReader.ExecutionChance(map, def, points));
                }
                if (def.earliestDay > GenDate.DaysPassedSinceSettleFloat + day || gate.Eligible == 0) return [];
                if (def.category == IncidentCategoryDefOf.ThreatBig && (!Find.Storyteller.difficulty.allowBigThreats
                    || ModsConfig.AnomalyActive && now + day * 60000 - Find.Anomaly.metalHellClosedTick < 300000)) return [];
                return [new(def.defName, gate.Eligible * gate.Execution, def.category == IncidentCategoryDefOf.ThreatBig && !forced)];
            }
            void Single(double day, double chance, IncidentDef def, bool forced = false) => Send(day, chance, Direct(day, def, forced));
            IEnumerable<(int Tick, double Day)> Checks()
            {
                for (var tick = (now / 1000 + 1) * 1000; tick < now + horizon * 60000; tick += 1000)
                    yield return (tick, (tick - now) / 60000.0);
            }
            if (props.GetType() == typeof(StorytellerCompProperties_OnOffCycle) && props is StorytellerCompProperties_OnOffCycle cycle)
            {
                foreach (var day in cycles[index].Days)
                {
                    var incident = Math.Floor(GenDate.DaysPassedSinceSettleFloat + day) < cycle.forceRaidEnemyBeforeDaysPassed ? IncidentDefOf.RaidEnemy : cycle.incident;
                    if (incident != null) Send(day, 1, Direct(day, incident));
                    else if (cycle.IncidentCategory != null) Send(day, 1, Outcomes(Category(cycle.IncidentCategory), day));
                }
            }
            else if (props.GetType() == typeof(StorytellerCompProperties_FactionInteraction) && props is StorytellerCompProperties_FactionInteraction interaction)
            {
                foreach (var day in cycles[index].Days) Single(day, 1, interaction.incident);
            }
            else if (props.GetType() == typeof(StorytellerCompProperties_SingleMTB) && props is StorytellerCompProperties_SingleMTB single)
            {
                if (single.incident != null) foreach (var (_, day) in Checks()) Single(day, IncidentCategoryReader.MtbChance(single.mtbDays), single.incident);
            }
            else if (props.GetType() == typeof(StorytellerCompProperties_CategoryMTB) && props is StorytellerCompProperties_CategoryMTB category)
            {
                var read = Category(category.category);
                foreach (var (_, day) in Checks())
                    Send(day, IncidentCategoryReader.MtbChance(category.mtbDays * (category.mtbDaysFactorByDaysPassedCurve?.Evaluate((float)(GenDate.DaysPassedSinceSettleFloat + day)) ?? 1)), Outcomes(read, day, true));
            }
            else if (props.GetType() == typeof(StorytellerCompProperties_Disease) && props is StorytellerCompProperties_Disease disease)
            {
                if (!DebugSettings.enableRandomDiseases || Find.Storyteller.difficulty.diseaseIntervalFactor >= 100) continue;
                var read = Category(disease.category, 1);
                foreach (var (_, day) in Checks())
                    Send(day, IncidentCategoryReader.MtbChance(map.Biome.diseaseMtbDays * Find.Storyteller.difficulty.diseaseIntervalFactor), Outcomes(read, day));
            }
            else if (props.GetType() == typeof(StorytellerCompProperties_DeepDrillInfestation) && props is StorytellerCompProperties_DeepDrillInfestation drill)
            {
                var drills = new List<Verse.Thing>();
                DeepDrillInfestationIncidentUtility.GetUsableDeepDrills(map, drills);
                if (Find.Storyteller.difficulty.deepDrillInfestationChanceFactor <= 0 || drills.Count == 0) continue;
                var chance = IncidentCategoryReader.MtbChance(drill.baseMtbDaysPerDrill / Find.Storyteller.difficulty.deepDrillInfestationChanceFactor);
                var read = Category(IncidentCategoryDefOf.DeepDrillInfestation, 2);
                foreach (var (_, day) in Checks())
                    for (var n = 0; n < drills.Count; n++) Send(day, chance, Outcomes(read, day));
            }
            else if (props.GetType() == typeof(StorytellerCompProperties_ShipChunkDrop))
            {
                foreach (var (tick, day) in Checks()) Single(day, IncidentCategoryReader.MtbChance(ShipCurve(null).Evaluate(tick / 3600000f)), IncidentDefOf.ShipChunkDrop);
            }
            else if (props.GetType() == typeof(StorytellerCompProperties_GauranlenPodSpawn) && props is StorytellerCompProperties_GauranlenPodSpawn pod)
            {
                if (!ModsConfig.IdeologyActive || pod.daysBetweenPodSpawns == 0 || Faction.OfPlayer.ideos == null) continue;
                var factor = pod.countdownFactorAnyConnectors > 1 && !NativeStorytellerFacts.NoTreeConnection() ? pod.countdownFactorAnyConnectors : 1;
                var remaining = Find.IdeoManager.ticksToNextGauranlenSpawn;
                // The countdown is global and is decremented even by another eligible target's unsuccessful attempt.
                // The no-external-incidents condition removes those clock resets, not these deterministic timer updates.
                var targets = Find.Storyteller.AllIncidentTargets.Where(target =>
                    !target.IncidentTargetTags().Any(tag => props.disallowedTargetTags?.Contains(tag) == true)
                    && (props.allowedTargetTags.NullOrEmpty() || target.IncidentTargetTags().Any(tag => props.allowedTargetTags.Contains(tag)))).ToArray();
                foreach (var (_, day) in Checks())
                {
                    if (Math.Floor(GenDate.DaysPassedFloat + day) < pod.minDaysPassed || GenDate.DaysPassedSinceSettleFloat + day <= props.minDaysPassed) continue;
                    foreach (var target in targets)
                    {
                        remaining -= 1000 * factor;
                        if (remaining > 0) continue;
                        remaining = (int)(pod.daysBetweenPodSpawns * 60000f);
                        if (target == map) Single(day, 1, pod.incident);
                    }
                }
            }
            else if (props.GetType() == typeof(StorytellerCompProperties_CategoryIndividualMTBByBiome) && props is StorytellerCompProperties_CategoryIndividualMTBByBiome biome)
            {
                foreach (var incident in DefDatabase<IncidentDef>.AllDefsListForReading.Where(d => d.category == biome.category))
                {
                    var mtb = incident.mtbDaysByBiome?.FirstOrDefault(b => b.biome == Find.WorldGrid[map.Tile].PrimaryBiome);
                    if (mtb == null) continue;
                    var days = mtb.mtbDays;
                    if (biome.applyCaravanVisibility && map.Parent.def.isTempIncidentMapOwner)
                        days /= CaravanVisibilityCalculator.Visibility(map.mapPawns.SpawnedPawnsInFaction(Faction.OfPlayer)
                            .Concat(map.mapPawns.PrisonersOfColonySpawned), caravanMovingNow: false);
                    foreach (var (_, day) in Checks()) Single(day, IncidentCategoryReader.MtbChance(days), incident);
                }
            }
            else if (props.GetType() == typeof(StorytellerCompProperties_ThreatsGenerator) && props is StorytellerCompProperties_ThreatsGenerator)
            {
                var incidents = NativeStorytellerFacts.GeneratorIncidents(map).Where(i => i.CompIndex == index && i.CanFire && i.Weight > 0).ToArray();
                var total = incidents.Sum(i => i.Weight);
                foreach (var day in cycles[index].Days)
                    Send(day, 1, incidents.Select(i => new IncidentResetOutcome(i.Incident, i.Weight / total, false)).ToArray());
            }
            else if (props.GetType() == typeof(StorytellerCompProperties_NoxiousHaze) && props is StorytellerCompProperties_NoxiousHaze haze)
            {
                var pollution = WorldPollutionUtility.CalculateNearbyPollutionScore(map.Tile);
                if (pollution < GameConditionDefOf.NoxiousHaze.minNearbyPollution) continue;
                var mtb = GameConditionDefOf.NoxiousHaze.mtbOverNearbyPollutionCurve.Evaluate(pollution);
                renewal = IncidentDefOf.NoxiousHaze.defName;
                renewalTick = map.StoryState.lastFireTicks.TryGetValue(IncidentDefOf.NoxiousHaze, out var last) ? last : -1;
                foreach (var (_, day) in Checks()) Send(day, IncidentCategoryReader.MtbChance(mtb), Direct(day, IncidentDefOf.NoxiousHaze),
                    true, FirstAge(haze.lastFireMinMTBThreshold * mtb, false), FirstAge(haze.lastFireMaxMTBThreshold * mtb, true) - 1);
            }
            else if (props.GetType() == typeof(StorytellerCompProperties_SingleOnceFixed) && props is StorytellerCompProperties_SingleOnceFixed once)
            {
                if (once.minColonistCount > 0 && (map.StoryState.lastFireTicks.ContainsKey(once.incident)
                    || PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive_FreeColonists.Count < once.minColonistCount)) continue;
                if (once.skipIfColonistHasMinTitle != null && PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive_FreeColonists.Any(p => p.royalty?.MainTitle()?.seniority >= once.skipIfColonistHasMinTitle.seniority)) continue;
                if (once.skipIfOnExtremeBiome && (Find.AnyPlayerHomeMap == null || Find.AnyPlayerHomeMap.Biome.isExtremeBiome)) continue;
                var offset = once.minColonistCount > 0 ? map.StoryState.GetTicksFromColonistCount(once.minColonistCount) / 1000 : 0;
                Single(((once.fireAfterDaysPassed * 60 + offset) * 1000 - now) / 60000.0, 1, once.incident);
            }
            else if (props.compClass == typeof(StorytellerComp_ClassicIntro))
            {
                if (map != Find.Maps.Find(m => m.IsPlayerHome)) continue;
                void Intro(int tick, IncidentCategoryDef category)
                {
                    var day = (tick - now) / 60000.0;
                    var eligible = DefDatabase<IncidentDef>.AllDefsListForReading.Where(d => d.TargetAllowed(map) && d.category == category)
                        .Select(d => (Def: d, Weight: IncidentCategoryReader.Weight(map, props, d))).Where(d => d.Weight > 0).ToArray();
                    var total = eligible.Sum(d => d.Weight);
                    Send(day, 1, eligible.SelectMany(d => Direct(day, d.Def).Select(o => o with { Chance = o.Chance * d.Weight / total })).ToArray());
                }
                Single((150000 - now) / 60000.0, 1, IncidentDefOf.VisitorGroup);
                Intro(204000, Find.Storyteller.difficulty.allowIntroThreats ? IncidentCategoryDefOf.ThreatSmall : IncidentCategoryDefOf.Misc);
                Intro(264000, IncidentCategoryDefOf.Misc);
                if (Find.Storyteller.difficulty.allowIntroThreats)
                    Send((324000 - now) / 60000.0, 1, Direct((324000 - now) / 60000.0, IncidentDefOf.RaidEnemy, points: 40));
                else Intro(324000, IncidentCategoryDefOf.Misc);
            }
            // Triggered/dissolution comps do not emit interval events. Their future triggers are eligibility changes.
            else if (typeof(StorytellerComp_Triggered).IsAssignableFrom(props.compClass) || typeof(StorytellerComp_DissolutionTriggered).IsAssignableFrom(props.compClass)) { }
            else unknown.Add(props.compClass.FullName ?? props.compClass.Name);
        }
        return new(now, map.StoryState.LastThreatBigTick, Find.Storyteller.LastIncidentTick, horizon, [.. mains], [.. resets])
        { RenewalIncident = renewal, LastRenewalTick = renewalTick, Unmodelled = [.. unknown] };
    }

    // Native renewal compares float TicksToDays, rather than rounding a day threshold to ticks.
    private static int FirstAge(float days, bool strict)
    {
        long low = 0, high = int.MaxValue;
        while (low < high)
        {
            var middle = (low + high) / 2;
            var age = (float)middle / 60000f;
            if (strict ? age > days : age >= days) high = middle;
            else low = middle + 1;
        }
        return (int)low;
    }
}
