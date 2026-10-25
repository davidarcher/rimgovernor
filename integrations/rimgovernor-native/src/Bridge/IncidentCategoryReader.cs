using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using RimGovernor.Brain.GameFacts.Observations;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace RimGovernor.Native;

/// <summary>Native category eligibility, including RandomMain's point range and the Anomaly category split.</summary>
internal static class IncidentCategoryReader
{
    private const ulong RandomValues = 1UL << 32;

    internal static float Points(float current, FloatRange factor, ulong value) => current < 0 ? current
        : factor.max <= factor.min ? current * factor.min
        : current * (factor.min + (float)((double)value / uint.MaxValue) * (factor.max - factor.min));

    // These members are private and have no public/protected typed route. Only detached copies are modified; the
    // accessors preserve native caches and RNG state, including a quest def's separate tick/points eligibility cache.
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "MemberwiseClone")]
    private static extern object Copy(object value);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "lastCheckCanRunTick")]
    private static extern ref int WorkerTick(IncidentWorker worker);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "lastCheckCanRunTick")]
    private static extern ref int QuestTick(QuestScriptDef quest);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "selectionWeightPerPointsCurve")]
    private static extern ref SimpleCurve FactionWeightCurve(FactionCurve curve);

    private sealed class Chances : StorytellerComp
    {
        internal float Weight(IncidentDef incident, Map map) => IncidentChanceFinal(incident, map);
    }

    internal static float Weight(Map map, StorytellerCompProperties props, IncidentDef incident) => new Chances { props = props }.Weight(incident, map);

    internal static bool Unmodelled(IncidentDef def) => def.workerClass.Assembly != typeof(IncidentWorker).Assembly
        || typeof(IncidentWorker_GiveQuest).IsAssignableFrom(def.workerClass) && (def.questScriptDef == null
            || def.questScriptDef.modContentPack is { IsOfficialMod: false }
            || def.questScriptDef.root.GetType().Assembly != typeof(IncidentWorker).Assembly);

    internal static double Eligibility(Map map, IncidentDef def, float? points = null)
    {
        var budget = points ?? StorytellerUtility.DefaultParmsNow(def.category, map).points;
        return def.workerClass == typeof(IncidentWorker_AggressiveAnimals)
            ? BaseGate(map, def, budget) ? AnimalChance(map, budget) : 0
            : Gate(map, def, budget) ? 1 : 0;
    }

    internal static double TradeKindQualifies(Map map, TraderKindDef kind)
    {
        if (kind.faction == null) return 1;
        var factions = Find.FactionManager.AllFactions.Where(f => f.def == kind.faction).ToArray();
        return factions.Length == 0 ? 0 : factions.Count(f => map.mapPawns.FreeColonists.Any(p => p.CanTradeWith(f, kind).Accepted)) / (double)factions.Length;
    }

    // An empty CanSpawn candidate set fails execution after consuming its category selection.
    internal static double ExecutionChance(Map map, IncidentDef def, float? points = null)
    {
        if (def.workerClass == typeof(IncidentWorker_AggressiveAnimals))
            return AnimalChance(map, points ?? StorytellerUtility.DefaultParmsNow(def.category, map).points);
        if (def.workerClass == typeof(IncidentWorker_OrbitalTraderArrival))
            return 1 - DefDatabase<TraderKindDef>.AllDefsListForReading.Where(k => k.orbital && k.CalculatedCommonality > 0)
                .Aggregate(1.0, (none, kind) => none * (1 - TradeKindQualifies(map, kind)));
        if (def.workerClass == typeof(IncidentWorker_PsychicRitualSiege))
        {
            var budget = points ?? StorytellerUtility.DefaultParmsNow(def.category, map).points;
            return DefDatabase<PsychicRitualDef>.AllDefsListForReading.Any(d => d.aiCastable && d.minThreatPoints <= budget) ? 1 : 0;
        }
        return 1;
    }

    internal static IEnumerable<IncidentCategoryRead> Read(Map map)
    {
        for (var index = 0; index < Find.Storyteller.def.comps.Count; index++)
        {
            var props = Find.Storyteller.def.comps[index];
            if (!props.Enabled || !Find.Storyteller.storytellerComps.Any(c => c.props == props)) continue;
            var targetTags = map.IncidentTargetTags().ToArray();
            if (targetTags.Any(tag => props.disallowedTargetTags?.Contains(tag) == true)
                || props.allowedTargetTags is { Count: > 0 } tags && !targetTags.Any(tags.Contains)) continue;
            if (props is StorytellerCompProperties_RandomMain random && props.GetType() == typeof(StorytellerCompProperties_RandomMain))
            {
                foreach (var entry in random.categoryWeights)
                    yield return Read(map, props, index, entry.category, entry.weight, random.randomPointsFactorRange);
                if (!random.categoryWeights.Any(e => e.category == IncidentCategoryDefOf.ThreatBig))
                    yield return Read(map, props, index, IncidentCategoryDefOf.ThreatBig, 0, random.randomPointsFactorRange);
            }
            else if (props is StorytellerCompProperties_OnOffCycle cycle && cycle.IncidentCategory == IncidentCategoryDefOf.ThreatBig)
                yield return Read(map, props, index, cycle.IncidentCategory, 1, new FloatRange(1, 1));
        }
    }

    internal static IncidentCategoryRead Read(Map map, StorytellerCompProperties props, int index, IncidentCategoryDef category,
        float weight, FloatRange factor, Func<IncidentDef, float>? selectionWeight = null)
    {
        var defs = DefDatabase<IncidentDef>.AllDefsListForReading.Where(d => d.category == category).OrderBy(d => d.defName, StringComparer.Ordinal).ToArray();
        var unknown = defs.Where(Unmodelled)
            .Select(d => d.defName + ":" + (d.workerClass.FullName ?? d.workerClass.Name)).Distinct().OrderBy(n => n, StringComparer.Ordinal).ToArray();
        var reader = new Chances { props = props };
        var suppressed = props is StorytellerCompProperties_RandomMain { skipThreatBigIfRaidBeacon: true }
            && category == IncidentCategoryDefOf.ThreatBig && map.IncidentTargetTags().Contains(IncidentTargetTagDefOf.Map_RaidBeacon);
        var native = StorytellerUtility.DefaultParmsNow(category, map);
        var defaultPoints = native.points;
        float PointAt(ulong value) => Points(defaultPoints, factor, value);
        var cuts = new SortedSet<ulong> { 0, RandomValues };
        if (defaultPoints >= 0 && factor.max > factor.min)
        {
            foreach (var threshold in Breakpoints(defs))
            {
                cuts.Add(First(value => PointAt(value) >= threshold));
                cuts.Add(First(value => PointAt(value) > threshold));
            }
        }
        var boundaries = cuts.ToArray();
        var outcomes = new List<UsableIncidentSet>();
        var fixedGates = new Dictionary<IncidentDef, bool>();
        var chance = ModsConfig.AnomalyActive && category.canUseAnomalyChance ? Chance(Find.Storyteller.AnomalyIncidentChanceNow) : 0;
        var branches = category.canUseAnomalyChance
            ? new[] { (Anomaly: false, Chance: 1 - chance), (Anomaly: true, Chance: chance) }
            : new[] { (Anomaly: false, Chance: 1.0) };
        Rand.PushState();
        try
        {
            foreach (var branch in branches.Where(b => b.Chance > 0))
                for (var at = 0; at + 1 < boundaries.Length; at++)
                {
                    var points = PointAt(boundaries[at] + (boundaries[at + 1] - boundaries[at]) / 2);
                    var probability = branch.Chance * (boundaries[at + 1] - boundaries[at]) / RandomValues;
                    var candidates = new List<(UsableIncidentWeight Incident, double Chance)>();
                    foreach (var def in defs)
                    {
                        if (category.canUseAnomalyChance && def.IsAnomalyIncident != branch.Anomaly) continue;
                        if (ModsConfig.AnomalyActive && !Find.Storyteller.difficulty.AnomalyPlaystyleDef.enableAnomalyContent
                            && def.modContentPack is { IsOfficialMod: true } pack && ExpansionDefOf.Anomaly.linkedMod == pack.PackageId) continue;
                        if (points >= 0 && (points < def.minThreatPoints || points > def.maxThreatPoints)) continue;
                        if (def.category == IncidentCategoryDefOf.ThreatBig && !Find.Storyteller.difficulty.allowBigThreats) continue;
                        var finalWeight = selectionWeight?.Invoke(def) ?? reader.Weight(def, map);
                        if (!(finalWeight > 0)) continue;
                        double allowed;
                        if (def.workerClass == typeof(IncidentWorker_AggressiveAnimals))
                        {
                            // Spatial entry is the explicit forecast condition; never sample one placement and call it a probability.
                            allowed = BaseGate(map, def, points) ? AnimalChance(map, points) : 0;
                        }
                        else if (PointSensitive(def.workerClass)) allowed = Gate(map, def, points) ? 1 : 0;
                        else
                        {
                            if (!fixedGates.TryGetValue(def, out var open)) fixedGates[def] = open = Gate(map, def, points);
                            allowed = open ? 1 : 0;
                        }
                        if (allowed > 0)
                            candidates.Add((new(def.defName, finalWeight, def.populationEffect != IncidentPopulationEffect.None)
                            { ExecutionChance = suppressed ? 0 : ExecutionChance(map, def, points),
                                EarliestDay = def.earliestDay,
                                ExecuteAfterDay = def.category == IncidentCategoryDefOf.ThreatBig && ModsConfig.AnomalyActive
                                    ? GenDate.DaysPassedSinceSettleFloat + (Find.Anomaly.metalHellClosedTick + 300000 - Find.TickManager.TicksGame) / 60000.0 : 0,
                                Raid = typeof(IncidentWorker_Raid).IsAssignableFrom(def.workerClass) }, allowed));
                    }
                    Expand(0, [], probability);
                    void Expand(int item, List<UsableIncidentWeight> selected, double mass)
                    {
                        if (!(mass > 0)) return;
                        if (item == candidates.Count) { outcomes.Add(new(mass, [.. selected])); return; }
                        var candidate = candidates[item];
                        if (candidate.Chance < 1) Expand(item + 1, selected, mass * (1 - candidate.Chance));
                        selected.Add(candidate.Incident);
                        Expand(item + 1, selected, mass * candidate.Chance);
                        selected.RemoveAt(selected.Count - 1);
                    }
                }
        }
        finally { Rand.PopState(); }
        return new(index, category.defName, [.. outcomes]) { Weight = weight, ThreatBig = category == IncidentCategoryDefOf.ThreatBig, Unmodelled = [.. unknown] };
    }

    private static bool PointSensitive(Type worker) => typeof(IncidentWorker_RaidEnemy).IsAssignableFrom(worker)
        || typeof(IncidentWorker_EntitySwarm).IsAssignableFrom(worker) || typeof(IncidentWorker_GiveQuest).IsAssignableFrom(worker);

    private static bool BaseGate(Map map, IncidentDef def, float points)
    {
        var copy = (IncidentDef)Copy(def);
        copy.earliestDay = 0; // A future eligibility boundary travels with the incident instead of blocking the whole horizon.
        return new IncidentWorker { def = copy }.CanFireNow(new IncidentParms { target = map, points = points });
    }

    private static bool Gate(Map map, IncidentDef def, float points)
    {
        var worker = (IncidentWorker)Copy(def.Worker);
        var copy = (IncidentDef)Copy(def);
        copy.earliestDay = 0;
        if (copy.questScriptDef is { } script)
        {
            copy.questScriptDef = (QuestScriptDef)Copy(script);
            QuestTick(copy.questScriptDef) = -1;
        }
        worker.def = copy;
        WorkerTick(worker) = -1;
        return worker.CanFireNow(new IncidentParms { target = map, points = points });
    }

    private static IEnumerable<float> Breakpoints(IEnumerable<IncidentDef> incidents)
    {
        yield return 0;
        foreach (var def in incidents) { yield return def.minThreatPoints; yield return def.maxThreatPoints; }
        foreach (var kind in DefDatabase<PawnKindDef>.AllDefsListForReading) yield return kind.combatPower * 2;
        foreach (var faction in DefDatabase<FactionDef>.AllDefsListForReading)
            foreach (var maker in faction.pawnGroupMakers ?? [])
                yield return faction.MinPointsToGeneratePawnGroup(maker.kindDef);
        foreach (var site in DefDatabase<SitePartDef>.AllDefsListForReading) yield return site.minThreatPoints;
        foreach (var ritual in DefDatabase<PsychicRitualDef>.AllDefsListForReading) yield return ritual.minThreatPoints;
        foreach (var quest in DefDatabase<QuestScriptDef>.AllDefsListForReading)
        {
            yield return quest.rootMinPoints;
            foreach (var point in CurveBreakpoints(quest.rootSelectionWeightFactorFromPointsCurve)) yield return point;
        }
        foreach (var strategy in DefDatabase<RaidStrategyDef>.AllDefsListForReading)
        {
            foreach (var point in CurveBreakpoints(strategy.selectionWeightPerPointsCurve)) yield return point;
            foreach (var curve in strategy.selectionWeightCurvesPerFaction ?? [])
                foreach (var point in CurveBreakpoints(FactionWeightCurve(curve))) yield return point;
        }
    }

    private static IEnumerable<float> CurveBreakpoints(SimpleCurve curve)
    {
        if (curve == null) yield break;
        for (var i = 0; i < curve.PointsCount; i++)
        {
            var point = curve.Points[i];
            yield return point.x;
            if (i > 0 && Math.Sign(curve.Points[i - 1].y) != Math.Sign(point.y))
            {
                var previous = curve.Points[i - 1];
                yield return previous.x - previous.y * (point.x - previous.x) / (point.y - previous.y);
            }
        }
    }

    private static double AnimalChance(Map map, float points)
    {
        bool Manhunter(PawnKindDef kind) => kind.RaceProps.Animal && kind.canArriveManhunter && kind.RaceProps.CanPassFences;
        bool Pollution(PawnKindDef kind, bool polluted) => polluted
            ? map.Biomes.Any(b => b.CommonalityOfPollutionAnimal(kind) > 0)
            : map.Biomes.Any(b => b.CommonalityOfAnimal(kind) > 0 || map.TileInfo.IsCoastal && b.CommonalityOfCoastalAnimal(kind) > 0);
        bool Any(IEnumerable<PawnKindDef> kinds) => kinds.Any(k => AggressiveAnimalIncidentUtility.AnimalWeight(k, points) > 0)
            || kinds.Any() && points > kinds.Min(k => k.combatPower) * 2;
        var habitat = 0.0;
        if (ModsConfig.OdysseyActive && map.Tile.Valid && Find.WorldGrid[map.Tile].Mutators.Contains(TileMutatorDefOf.AnimalHabitat))
        {
            var animal = ((TileMutatorWorker_AnimalHabitat)TileMutatorDefOf.AnimalHabitat.Worker).GetAnimalKind(map.Tile);
            habitat = Manhunter(animal) ? Chance(0.5f) : 0;
        }
        var pollutedChance = ModsConfig.BiotechActive && map.Tile.Valid
            ? Chance(WildAnimalSpawner.PollutionAnimalSpawnChanceFromPollutionCurve.Evaluate(Find.WorldGrid[map.Tile].pollution)) : 0;
        double Branch(bool polluted)
        {
            var local = map.Biomes.SelectMany(b => b.AllWildAnimals).Where(k => Manhunter(k) && Pollution(k, polluted)).ToArray();
            if (polluted && local.Length == 0)
            {
                polluted = false;
                local = map.Biomes.SelectMany(b => b.AllWildAnimals).Where(k => Manhunter(k) && Pollution(k, false)).ToArray();
            }
            return Any(local) || Any(DefDatabase<PawnKindDef>.AllDefsListForReading.Where(k => Manhunter(k) && Pollution(k, polluted)
                && (!map.Tile.Valid || Find.World.tileTemperatures.SeasonAndOutdoorTemperatureAcceptableFor(map.Tile, k.race)))) ? 1 : 0;
        }
        return habitat + (1 - habitat) * (pollutedChance * Branch(true) + (1 - pollutedChance) * Branch(false));
    }

    // Rand.Value is the float conversion of a uniformly drawn uint divided by uint.MaxValue. Binary-search its native
    // float mapping rather than treating rounded point-range endpoints as a continuous uniform density.
    internal static ulong First(Func<ulong, bool> predicate)
    {
        ulong low = 0, high = RandomValues;
        while (low < high) { var middle = low + (high - low) / 2; if (predicate(middle)) high = middle; else low = middle + 1; }
        return low;
    }
    internal static double Chance(double chance) => chance <= 0 ? 0 : chance >= 1 ? 1
        : First(value => (float)((double)value / uint.MaxValue) >= chance) / (double)RandomValues;

    internal static double MtbChance(float mtb)
    {
        if (float.IsPositiveInfinity(mtb)) return 0;
        if (mtb <= 0) return 1;
        var probability = 1000.0 / (mtb * 60000.0);
        var gate = 1.0;
        if (probability < 0.0001)
        {
            var threshold = 1.0;
            while (probability < 0.0001) { probability *= 8; threshold /= 8; }
            gate = First(value => (float)((double)value / uint.MaxValue) > threshold) / (double)RandomValues;
        }
        return gate * First(value => (float)((double)value / uint.MaxValue) >= probability) / RandomValues;
    }
}
