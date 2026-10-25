using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace RimGovernor.Native;

/// <summary>
/// The colony facts of trader visits as a stock-arrival source (#3869, #3909), read without changing game state: never
/// an incident's TryExecute. Category eligibility uses detached workers so CanFireNow cannot change live tick caches. Trade gates mirror IncidentWorker.CanFireNow
/// and the workers' CanFireNowSub over public members; the trader kind weights mirror
/// IncidentWorker_TraderCaravanArrival.TraderKindCommonality (protected) and IncidentWorker_OrbitalTraderArrival.CanSpawn (private),
/// and an incident's weight StorytellerComp.IncidentChanceFinal (protected) but its population-intent factor.
/// </summary>
internal static class NativeTradeVisitFacts
{
    // IncidentWorker_OrbitalTraderArrival.MaxShips (private const): CanFireNowSub fails at this many passing ships.
    private const int MaxShips = 5;

    internal static RimGovernor.Brain.GameFacts.Observations.TradeVisitFacts Read(Map map, double incidentHorizonDays)
    {
        var difficulty = Find.Storyteller.difficulty;
        var categories = IncidentCategoryReader.Read(map).ToArray();
        var negotiators = map.mapPawns.FreeColonistsSpawned
            .Where(p => NativeTradeObservation.EligibleNegotiator(p) && p.health.capacities.CapableOf(PawnCapacityDefOf.Talking)).ToList();
        // The exact worker classes the trader decoder models (Catalog/Storyteller.cs): their gates are mirrored here.
        var incidents = DefDatabase<IncidentDef>.AllDefsListForReading
            .Where(d => d.workerClass == typeof(IncidentWorker_TraderCaravanArrival) || d.workerClass == typeof(IncidentWorker_OrbitalTraderArrival)
                || d.workerClass == typeof(IncidentWorker_VisitorGroup) || d.workerClass == typeof(IncidentWorker_TravelerGroup)
                || d.workerClass == typeof(IncidentWorker_CaravanArrivalTributeCollector))
            .OrderBy(d => d.defName, System.StringComparer.Ordinal);
        return new RimGovernor.Brain.GameFacts.Observations.TradeVisitFacts {
            Categories = [.. categories],
            Clock = NativeStorytellerFacts.IncidentClock(map, incidentHorizonDays, categories),
            Danger = map.dangerWatcher.DangerRating switch { StoryDanger.High => RimGovernor.Brain.GameFacts.Observations.StoryDangerLevel.High, StoryDanger.Low => RimGovernor.Brain.GameFacts.Observations.StoryDangerLevel.Low, _ => RimGovernor.Brain.GameFacts.Observations.StoryDangerLevel.None },
            PopulationIntent = StorytellerUtilityPopulation.PopulationIntent,
            AllyFraction = StorytellerUtility.AllyIncidentFraction(fullAlliesOnly: false),
            FullAllyFraction = StorytellerUtility.AllyIncidentFraction(fullAlliesOnly: true),
            DaysSinceSettle = GenDate.DaysPassedSinceSettleFloat,
            CommsConsole = map.listerBuildings.allBuildingsColonist.Any(b => b.def.IsCommsConsole && (b.GetComp<CompPowerTrader>() == null || b.GetComp<CompPowerTrader>().PowerOn)),
            TradeBeacon = Building_OrbitalTradeBeacon.AllPowered(map).Any(),
            // TradeUtility.ColonyHasEnoughSilver's count: the silver an orbital trader is paid from.
            BeaconSilver = TradeUtility.AllLaunchableThingsForTrade(map).Where(t => t.def == ThingDefOf.Silver).Sum(t => (long)t.stackCount),
            GroundSilver = NativeTradeSilver.Ground(map),
            Negotiator = negotiators.Count != 0,
            NegotiatorPriceImprovement = negotiators.Count == 0 ? 0 : negotiators.Max(p => p.GetStatValue(StatDefOf.TradePriceImprovement)),
            PriceFactorLoss = difficulty.tradePriceFactorLoss,
            Requests = [.. NativeTradeAcquisition.RequestOptions(map)],
            Incidents = [.. incidents.Select(def => Incident(map, def, difficulty))],
        };
    }

    private static RimGovernor.Brain.GameFacts.Observations.TraderIncidentRead Incident(Map map, IncidentDef def, Difficulty difficulty)
    {
        var blocked = NativeDiseaseFacts.Blocked(map, def, difficulty).Concat(Gates(map, def)).ToList();
        var sources = new List<RimGovernor.Brain.GameFacts.Observations.TraderSourceRead>();
        if (def.workerClass == typeof(IncidentWorker_OrbitalTraderArrival))
        {
            if (map.passingShipManager.passingShips.Count >= MaxShips) blocked.Add("ships_full");
            var kinds = DefDatabase<TraderKindDef>.AllDefsListForReading.Where(k => k.orbital)
                .Select(k => new RimGovernor.Brain.GameFacts.Observations.TraderKindWeight(k.defName, k.CalculatedCommonality) { Qualifies = IncidentCategoryReader.TradeKindQualifies(map, k) }).Where(k => k.Weight > 0 && k.Qualifies > 0)
                .OrderBy(k => k.Kind, System.StringComparer.Ordinal).ToList();
            // No kind that can spawn fails TryExecuteWorker, not CanFireNow: the incident still takes its share of a category draw.
            sources.Add(new RimGovernor.Brain.GameFacts.Observations.TraderSourceRead { Kinds = [.. kinds] });
        }
        else
        {
            // IncidentWorker_NeutralGroup.CanFireNowSub: a candidate faction (the worker's FactionCanBeGroupSource) and no condition
            // preventing neutral visitors.
            var worker = (IncidentWorker_NeutralGroup)def.Worker;
            var parms = new IncidentParms { target = map };
            if (map.gameConditionManager.ActiveConditions.Any(c => c.def.preventNeutralVisitors)) blocked.Add("condition");
            var factions = Find.FactionManager.AllFactions.Where(f => worker.FactionCanBeGroupSource(f, parms)).OrderBy(f => f.GetUniqueLoadID(), System.StringComparer.Ordinal).ToList();
            if (factions.Count == 0) blocked.Add("no_faction");
            if (def.workerClass == typeof(IncidentWorker_CaravanArrivalTributeCollector))
            {
                // IncidentWorker_CaravanArrivalTributeCollector: the Empire must be a source; its kind is any TributeCollector kind by CalculatedCommonality.
                var empire = Faction.OfEmpire;
                if (empire == null || !worker.FactionCanBeGroupSource(empire, parms)) blocked.Add("no_faction");
                var kinds = DefDatabase<TraderKindDef>.AllDefsListForReading.Where(k => k.category == IncidentWorker_CaravanArrivalTributeCollector.TributeCollectorTraderKindCategory)
                    .Select(k => new RimGovernor.Brain.GameFacts.Observations.TraderKindWeight(k.defName, k.CalculatedCommonality)).Where(k => k.Weight > 0).OrderBy(k => k.Kind, System.StringComparer.Ordinal).ToList();
                sources.Add(new RimGovernor.Brain.GameFacts.Observations.TraderSourceRead { Kinds = [.. kinds] });
            }
            else if (def.workerClass == typeof(IncidentWorker_TraderCaravanArrival))
            {
                sources.AddRange(factions.Select(f => new RimGovernor.Brain.GameFacts.Observations.TraderSourceRead { Faction = f.def.defName, Kinds = [.. f.def.caravanTraderKinds
                    .Select(k => new RimGovernor.Brain.GameFacts.Observations.TraderKindWeight(k.defName, CaravanCommonality(k, map, f))).Where(k => k.Weight > 0)] }));
            }
            else if (def.workerClass == typeof(IncidentWorker_VisitorGroup))
            {
                // IncidentWorker_VisitorGroup.TryConvertOnePawnToSmallTrader: a visitorTraderKinds kind by CalculatedCommonality.
                sources.AddRange(factions.Select(f => new RimGovernor.Brain.GameFacts.Observations.TraderSourceRead { Faction = f.def.defName, Visitors = Visitors(f), Kinds = [.. (f.def.visitorTraderKinds ?? new List<TraderKindDef>())
                    .Select(k => new RimGovernor.Brain.GameFacts.Observations.TraderKindWeight(k.defName, k.CalculatedCommonality)).Where(k => k.Weight > 0)] }));
            }
        }

        return new RimGovernor.Brain.GameFacts.Observations.TraderIncidentRead(def.defName) { Blocked = [.. blocked.Distinct()], Weight = Weight(map, def), Sources = [.. sources] };
    }

    private static RimGovernor.Brain.GameFacts.Observations.VisitorGenerationRead Visitors(Faction faction)
    {
        var parms = new PawnGroupMakerParms { faction = faction, groupKind = PawnGroupKindDefOf.Peaceful };
        return new RimGovernor.Brain.GameFacts.Observations.VisitorGenerationRead {
            MinimumPoints = faction.def.MinPointsToGeneratePawnGroup(PawnGroupKindDefOf.Peaceful),
            CostCurve = [.. faction.def.maxPawnCostPerTotalPointsCurve.Points.Select(p => new RimGovernor.Brain.GameFacts.Observations.CurvePointRead(p.x, p.y))],
            Groups = [.. faction.def.pawnGroupMakers.Where(g => g.kindDef == PawnGroupKindDefOf.Peaceful).Select(g =>
                new RimGovernor.Brain.GameFacts.Observations.VisitorGroupRead {
                    Commonality = g.commonality,
                    MaximumPoints = g.maxTotalPoints,
                    // GetOptions reads native xenotype weights/costs and eligibility without drawing or generating a pawn.
                    // In 1.7 its CanUseOption applies ChildRaidersAllowed even to non-hostile groups. The later
                    // hostile-only forced Adult request in PawnGroupKindWorker_Normal is a different gate.
                    Options = [.. PawnGroupMakerUtility.GetOptions(parms, faction.def, g.options, float.MaxValue, float.MaxValue, float.MaxValue)
                        .Select(o => new RimGovernor.Brain.GameFacts.Observations.VisitorPawnOption(o.Option.kind.defName, o.Cost,
                            o.Option.Cost, o.SelectionWeight, !o.Option.kind.pawnGroupDevelopmentStage.HasValue
                                || o.Option.kind.pawnGroupDevelopmentStage.Value.Adult(), o.Option.kind.maxPerGroup, o.Option.kind.factionLeader))],
                })],
        };
    }

    // IncidentWorker.CanFireNow's other game-state gates: the target tags, the biome lists, minPopulation, minRefireDays,
    // minGreatestPopulation, a condition that prevents incidents, and an active void awakening. The incident's earliestDay is the
    // catalog's (a start day).
    private static IEnumerable<string> Gates(Map map, IncidentDef def)
    {
        if (!def.TargetAllowed(map)) yield return "target";
        var biome = Find.WorldGrid[map.Tile].PrimaryBiome;
        if (!def.allowedBiomes.NullOrEmpty() && !def.allowedBiomes.Contains(biome) || !def.disallowedBiomes.NullOrEmpty() && def.disallowedBiomes.Contains(biome)) yield return "biome";
        if (def.minPopulation > 0 && PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive_FreeColonists.Count() < def.minPopulation) yield return "min_population";
        if (def.Worker.FiredTooRecently(map)) yield return "refire";
        if (def.minGreatestPopulation > 0 && Find.StoryWatcher.statsRecord.greatestPopulation < def.minGreatestPopulation) yield return "greatest_population";
        if (map.gameConditionManager.ActiveConditions.Any(c => c.def.preventIncidents)) yield return "condition";
        if (ModsConfig.AnomalyActive && Find.Anomaly.VoidAwakeningActive()) yield return "void_awakening";
    }

    // StorytellerComp.IncidentChanceFinal without IncidentChanceFactor_PopulationIntent (the comp's minIncChancePopulationIntentFactor
    // is the catalog's): BaseChanceThisGame, IncidentChanceFactor_CurrentPopulation, ChanceFactorNow and the recent weighting.
    internal static double Weight(Map map, IncidentDef def)
    {
        var chance = def.Worker.BaseChanceThisGame;
        if (def.chanceFactorByPopulationCurve != null) chance *= def.chanceFactorByPopulationCurve.Evaluate(PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive_Colonists.Count());
        chance *= def.Worker.ChanceFactorNow(map);
        if (!def.ShouldIgnoreRecentWeighting)
        {
            var recent = map.StoryState.RecentRandomIncidents;
            for (var i = 0; i < recent.Count; i++)
            {
                if (recent[i] == def) chance *= QuestTuning.RecentStoryWeightFactors[i];
            }
        }

        return chance;
    }

    // IncidentWorker_TraderCaravanArrival.TraderKindCommonality: the kind's faction, Ideology's slaver rule, a required permit, then CalculatedCommonality.
    private static float CaravanCommonality(TraderKindDef kind, Map map, Faction faction)
    {
        if (kind.faction != null && faction.def != kind.faction) return 0f;
        if (ModsConfig.IdeologyActive && faction.ideos != null && kind.category == IncidentWorker_TraderCaravanArrival.SlaverTraderKindCategory
            && faction.ideos.AllIdeos.Any(i => !i.IdeoApprovesOfSlavery())) return 0f;
        if (kind.permitRequiredForTrading != null && !map.mapPawns.FreeColonists.Any(p => p.royalty != null && p.royalty.HasPermit(kind.permitRequiredForTrading, faction))) return 0f;
        return kind.CalculatedCommonality;
    }

}
