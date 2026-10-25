using RimWorld;
using Verse;
using P = RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Bridge;

// Every StorytellerDef decoded from its comps by comp class (#3539 of #3471, #3859 of #3650), read by P.ThreatSchedule here and by
// P.IllnessSchedule (Catalog/Disease.cs). A comp counts when it can fire on the player's home map (its allowed target tags are
// empty or name Map_PlayerHome, and it does not disallow it: Cassandra's ThreatsGenerator and caravan comps target raid beacons
// and caravans). A comp is matched by its exact props class, so a mod's subclass is a class the decoder does not know. The
// threat decoder models:
//   - StorytellerCompProperties_OnOffCycle whose IncidentCategory is ThreatBig (incident.category, else category): a cycle, its
//     accept fraction by days passed, threat points and progress score, and its tree-connector days (#3860);
//   - StorytellerCompProperties_ThreatsGenerator (#3860): its cycle, the allowed threats (RaidEnemy, MechCluster) and the difficulty's
//     threatScale, plus its explicit/scaled/minimum points (the count curve is mirrored in P.ThreatSchedule);
//   - StorytellerCompProperties_CategoryIndividualMTBByBiome of ThreatBig (#3860): each such incident's mtbDaysByBiome rows;
//   - StorytellerCompProperties_RandomMain: an MTB comp with the ThreatBig share of its categoryWeights;
//   - StorytellerCompProperties_CategoryMTB of ThreatBig: an MTB comp with its mtbDaysFactorByDaysPassedCurve;
//   - StorytellerCompProperties_SingleMTB of a ThreatBig incident that targets the home map: an MTB comp;
//   - StorytellerCompProperties_FactionInteraction: nothing (faction visits, traders, ally help), unmodelled of a ThreatBig incident.
// A class listed as sending no scheduled major threat (quests, diseases, ...) adds nothing. The trader decoder (#3869, #3909) reads
// the same comps for trader visits (TradeSchedule). Every other home-map
// comp is named unmodelled and adds no threats: a comp class the decoder does not know, and a ThreatsGenerator with an explicit
// faction. 1.7 has no StorytellerCompProperties_ThreatCycle.

public sealed partial class DefinitionCatalog
{
    private static readonly HashSet<string> SendsNoScheduledMajorThreat = new(StringComparer.Ordinal)
    {
        nameof(StorytellerCompProperties_ClassicIntro), nameof(StorytellerCompProperties_DeepDrillInfestation), nameof(StorytellerCompProperties_Disease),
        nameof(StorytellerCompProperties_DissolutionTriggered), nameof(StorytellerCompProperties_GauranlenPodSpawn),
        nameof(StorytellerCompProperties_ImportantQuest), nameof(StorytellerCompProperties_MechanitorComplexQuest), nameof(StorytellerCompProperties_MonolithMigration),
        nameof(StorytellerCompProperties_NoxiousHaze), nameof(StorytellerCompProperties_RandomEpicQuest), nameof(StorytellerCompProperties_RandomQuest),
        nameof(StorytellerCompProperties_RefiringUniqueQuest), nameof(StorytellerCompProperties_ShipChunkDrop), nameof(StorytellerCompProperties_SingleOnceFixed),
        nameof(StorytellerCompProperties_Triggered), nameof(StorytellerCompProperties_WorkSite),
    };

    /// <summary>The threat comps of every storyteller def; empty (every storyteller unread) when the catalog has no StorytellerDef.</summary>
    public P.ThreatScheduleTable ThreatSchedule() => _threatSchedule.Value;

    private P.ThreatScheduleTable BuildThreatSchedule() => new()
    {
        Storytellers = Rows<StorytellerDef>().ToDictionary(row => row.Key, row => StorytellerThreats(row.Value), StringComparer.Ordinal),
    };

    private P.StorytellerThreats StorytellerThreats(StorytellerDef def)
    {
        const string big = nameof(IncidentCategoryDefOf.ThreatBig);
        List<P.ThreatCycleComp> cycles = [];
        List<P.ThreatMtbComp> mtbs = [];
        List<P.ThreatGeneratorComp> generators = [];
        List<P.ThreatBiomeComp> biomes = [];
        SortedSet<string> unmodelled = new(StringComparer.Ordinal);
        foreach (var (comp, kind) in HomeMapComps(def))
        {
            switch (comp)
            {
                case StorytellerCompProperties_OnOffCycle cycle when kind == nameof(StorytellerCompProperties_OnOffCycle):
                    if (cycle.IncidentCategory?.defName == big)
                    {
                        cycles.Add(CycleComp(cycle, def.comps.IndexOf(cycle)));
                    }

                    break;
                case StorytellerCompProperties_ThreatsGenerator generator when kind == nameof(StorytellerCompProperties_ThreatsGenerator):
                    var parms = generator.parms;
                    if (parms is null || parms.faction is not null)
                    {
                        // Explicit-faction generators need a faction-specific composition read.
                        unmodelled.Add(kind);
                        break;
                    }

                    List<string> allowed = [];
                    if ((parms.allowedThreats & AllowedThreatsGeneratorThreats.Raids) != 0)
                    {
                        allowed.Add(nameof(IncidentDefOf.RaidEnemy));
                    }

                    if ((parms.allowedThreats & AllowedThreatsGeneratorThreats.MechClusters) != 0)
                    {
                        allowed.Add(nameof(IncidentDefOf.MechCluster));
                    }

                    generators.Add(new P.ThreatGeneratorComp(allowed, nameof(IncidentDefOf.RaidEnemy))
                    {
                        CompIndex = def.comps.IndexOf(generator),
                        PointBasis = parms.threatPoints is { } points ? new P.GeneratorPoints.Fixed(Decimal32(points))
                            : new P.GeneratorPoints.Current(Decimal32(parms.currentThreatPointsFactor)),
                        MinimumPoints = parms.minThreatPoints is { } minimum ? Decimal32(minimum) : double.NegativeInfinity,
                    });
                    break;
                case StorytellerCompProperties_RandomMain random when kind == nameof(StorytellerCompProperties_RandomMain):
                    var weights = random.categoryWeights.Seq().Where(e => e.category is not null).ToList();
                    var total = weights.Sum(e => (double)e.weight);
                    var share = weights.Where(e => e.category.defName == big).Sum(e => (double)e.weight);
                    mtbs.Add(new P.ThreatMtbComp(Decimal32(random.minDaysPassed), Decimal32(random.mtbDays), total > 0 ? share / total : 0));
                    break;
                case StorytellerCompProperties_CategoryMTB mtb when kind == nameof(StorytellerCompProperties_CategoryMTB):
                    if (mtb.category?.defName == big)
                    {
                        mtbs.Add(new P.ThreatMtbComp(Decimal32(mtb.minDaysPassed), Decimal32(mtb.mtbDays), 1) { MtbFactorByDaysPassed = CurvePoints(mtb.mtbDaysFactorByDaysPassedCurve) });
                    }

                    break;
                case StorytellerCompProperties_SingleMTB single when kind == nameof(StorytellerCompProperties_SingleMTB):
                    // StorytellerComp_SingleMTB fires its incident only on a target the incident allows.
                    if (single.incident?.category?.defName == big && single.incident.targetTags.Seq().Any(t => t?.defName == nameof(IncidentTargetTagDefOf.Map_PlayerHome)))
                    {
                        mtbs.Add(new P.ThreatMtbComp(Decimal32(single.minDaysPassed), Decimal32(single.mtbDays), 1));
                    }

                    break;
                case StorytellerCompProperties_FactionInteraction interaction when kind == nameof(StorytellerCompProperties_FactionInteraction):
                    // Faction visits, traders and ally help (#3869 decodes the traders); one of a ThreatBig incident is not modelled.
                    if (interaction.incident?.category?.defName == big)
                    {
                        unmodelled.Add(kind);
                    }

                    break;
                case StorytellerCompProperties_CategoryIndividualMTBByBiome biome when kind == nameof(StorytellerCompProperties_CategoryIndividualMTBByBiome):
                    if (biome.category?.defName == big)
                    {
                        biomes.Add(new P.ThreatBiomeComp(Decimal32(biome.minDaysPassed), BiomeIncidents(biome.category)));
                    }

                    break;
                default:
                    if (!SendsNoScheduledMajorThreat.Contains(kind))
                    {
                        unmodelled.Add(kind);
                    }

                    break;
            }
        }

        return new P.StorytellerThreats { Cycles = cycles, Mtbs = mtbs, Generators = generators, Biomes = biomes, Unmodelled = [.. unmodelled] };
    }

    // An OnOffCycle comp's cycle with its accept curves (days passed, threat points, progress score) and tree-connector days.
    private static P.ThreatCycleComp CycleComp(StorytellerCompProperties_OnOffCycle cycle, int index) => new(index, Decimal32(cycle.forceRaidEnemyBeforeDaysPassed));

    // A CategoryIndividualMTBByBiome comp's incidents of the category (#3860): per biome, each incident that targets the player's home
    // map and has an mtbDaysByBiome row of it (the game takes the first row of the biome), with its MTB.
    private Dictionary<string, IReadOnlyList<P.ThreatBiomeIncident>> BiomeIncidents(IncidentCategoryDef category)
    {
        const string home = nameof(IncidentTargetTagDefOf.Map_PlayerHome);
        var byBiome = new Dictionary<string, List<P.ThreatBiomeIncident>>(StringComparer.Ordinal);
        foreach (var incident in Rows<IncidentDef>().Values.Where(d => d.category == category && d.mtbDaysByBiome is not null).OrderBy(d => d.defName, StringComparer.Ordinal))
        {
            if (!incident.targetTags.Seq().Any(t => t?.defName == home))
            {
                continue;
            }

            foreach (var row in incident.mtbDaysByBiome.Where(r => r?.biome is not null).GroupBy(r => r.biome.defName, StringComparer.Ordinal).Select(g => g.First()))
            {
                if (!byBiome.TryGetValue(row.biome.defName, out var list))
                {
                    byBiome[row.biome.defName] = list = [];
                }

                list.Add(new P.ThreatBiomeIncident(incident.defName, Decimal32(row.mtbDays), typeof(IncidentWorker_Raid).IsAssignableFrom(incident.workerClass)));
            }
        }

        return byBiome.ToDictionary(e => e.Key, e => (IReadOnlyList<P.ThreatBiomeIncident>)e.Value, StringComparer.Ordinal);
    }

    // The trader decoder (#3869): which comp classes send no trader visit. A comp class outside it and the trader decode below is
    // named unmodelled.
    private static readonly HashSet<string> SendsNoTrader = new(SendsNoScheduledMajorThreat, StringComparer.Ordinal)
    {
        nameof(StorytellerCompProperties_CategoryIndividualMTBByBiome), nameof(StorytellerCompProperties_ThreatsGenerator),
    };

    // StorytellerComp.IncidentChanceFactor_PopulationIntent's offsets to the population intent by IncidentPopulationEffect (inline
    // literals of its switch).
    private const double IncreaseHardIntentOffset = 0.4;

    private const double IncreaseMediumIntentOffset = 0;

    private const double IncreaseEasyIntentOffset = -0.4;

    /// <summary>The trader comps of every storyteller def (#3869, #3909); empty (every storyteller unread) when the catalog has no StorytellerDef.</summary>
    public P.TradeScheduleTable TradeSchedule() => (_tradeSchedule ??= new(BuildTradeSchedule)).Value;

    private Lazy<P.TradeScheduleTable>? _tradeSchedule;

    private P.TradeScheduleTable BuildTradeSchedule()
    {
        var incidents = Rows<IncidentDef>().Values.Where(d => d.category is not null).OrderBy(d => d.defName, StringComparer.Ordinal).ToList();
        var modelled = new Dictionary<string, P.TradeIncident>(StringComparer.Ordinal);
        foreach (var incident in incidents)
        {
            if (TradeWorkerOf(incident.workerClass) is { } worker)
            {
                modelled[incident.defName] = new P.TradeIncident(incident.category.defName, worker, incident.earliestDay)
                {
                    ChangesPopulation = incident.populationEffect != IncidentPopulationEffect.None,
                    IntentOffset = incident.populationEffect switch
                    {
                        IncidentPopulationEffect.IncreaseHard => IncreaseHardIntentOffset,
                        IncidentPopulationEffect.IncreaseMedium => IncreaseMediumIntentOffset,
                        IncidentPopulationEffect.IncreaseEasy => IncreaseEasyIntentOffset,
                        _ => 0,
                    },
                };
            }
        }

        // A trader category holds a trader-like incident (an exact trader worker or a subclass of one). It is drawn exactly when every
        // incident in it is of a modelled worker, its threat points are unbounded (a category comp scales the points it passes) and
        // the category does not split by the Anomaly chance; otherwise a comp drawing it is named unmodelled.
        var traderCategories = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var group in incidents.GroupBy(d => d.category.defName, StringComparer.Ordinal).Where(g => g.Any(d => TraderLike(d.workerClass))))
        {
            traderCategories[group.Key] = !group.First().category.canUseAnomalyChance && group.All(d => modelled.ContainsKey(d.defName)
                && d.minThreatPoints == float.MinValue && d.maxThreatPoints == float.MaxValue);
        }

        return new P.TradeScheduleTable
        {
            Storytellers = Rows<StorytellerDef>().ToDictionary(row => row.Key, row => StorytellerTrade(row.Value, modelled, traderCategories), StringComparer.Ordinal),
            Incidents = modelled,
            ChildVisitorFactions = Rows<FactionDef>().Values.Where(f => f.pawnGroupMakers.Seq()
                    .Any(m => m.kindDef?.defName == nameof(PawnGroupKindDefOf.Peaceful) && m.options.Seq().Any(o => o.kind?.pawnGroupDevelopmentStage is { } stage && stage != DevelopmentalStage.Adult)))
                .Select(f => f.defName).ToHashSet(StringComparer.Ordinal),
            PopulationChanceByIntent = Domain.EquatableArray.From(CurvePoints(StorytellerComp.IncreasesPopChanceByPopIntentCurve)),
        };
    }

    // The trader decoder models (#3869, #3909), each from minDaysPassed:
    //   - StorytellerCompProperties_FactionInteraction of a modelled incident (StorytellerComp_FactionInteraction): baseIncidentsPerYear
    //     over the 60-day year (IncidentCycleUtility.IncidentCountThisInterval), scaled by AllyIncidentFraction (fullAlliesOnly), none
    //     below minWealth or minDanger;
    //   - StorytellerCompProperties_OnOffCycle of a modelled incident, or of a trader category: its cycle (StorytellerComp_OnOffCycle;
    //     before forceRaidEnemyBeforeDaysPassed its incidents are raids), a category's incident by IncidentChanceFinal weight;
    //   - StorytellerCompProperties_RandomMain drawing a trader category: 1 ÷ mtbDays a day, its categoryWeights with ThreatBig apart
    //     (maxThreatBigIntervalDays) and its minIncChancePopulationIntentFactor;
    //   - StorytellerCompProperties_CategoryMTB of a trader category: 1 ÷ mtbDays over its mtbDaysFactorByDaysPassedCurve;
    //   - StorytellerCompProperties_SingleMTB of a modelled incident: 1 ÷ mtbDays;
    // a comp of one incident also from its earliestDay. A modelled incident's worker class is exactly IncidentWorker_TraderCaravanArrival,
    // _OrbitalTraderArrival, _VisitorGroup, _CaravanArrivalTributeCollector or _TravelerGroup. Named unmodelled: a comp of a
    // trader-like incident of another class (a subclass), a comp drawing a trader category the draw does not model, and a comp class the decoder does not know.
    private static P.StorytellerTrade StorytellerTrade(StorytellerDef def, Dictionary<string, P.TradeIncident> incidents, Dictionary<string, bool> traderCategories)
    {
        List<P.TradeVisitComp> comps = [];
        SortedSet<string> unmodelled = new(StringComparer.Ordinal);
        foreach (var (comp, kind) in HomeMapComps(def))
        {
            switch (comp)
            {
                case StorytellerCompProperties_FactionInteraction interaction when kind == nameof(StorytellerCompProperties_FactionInteraction):
                    if (TraderIncident(interaction.incident, kind, incidents, unmodelled) is not { } caravan)
                    {
                        break;
                    }

                    comps.Add(new P.TradeVisitComp(P.TradeCompRate.FactionInteraction, Math.Max(Decimal32(interaction.minDaysPassed), caravan.earliestDay))
                    {
                        CompIndex = def.comps.IndexOf(interaction),
                        Incident = caravan.defName,
                        FullAlliesOnly = interaction.fullAlliesOnly,
                        MinWealth = Decimal32(interaction.minWealth),
                        MinDanger = interaction.minDanger switch
                        {
                            StoryDanger.High => RimGovernor.Brain.GameFacts.Observations.StoryDangerLevel.High,
                            StoryDanger.Low => RimGovernor.Brain.GameFacts.Observations.StoryDangerLevel.Low,
                            _ => RimGovernor.Brain.GameFacts.Observations.StoryDangerLevel.None,
                        },
                    });
                    break;
                case StorytellerCompProperties_OnOffCycle cycle when kind == nameof(StorytellerCompProperties_OnOffCycle):
                    P.TradeVisitComp? cycled = null;
                    var start = Math.Max(Decimal32(cycle.minDaysPassed), Decimal32(cycle.forceRaidEnemyBeforeDaysPassed));
                    if (cycle.incident is null)
                    {
                        if (CategoryDraw(cycle.IncidentCategory?.defName, kind, P.TradeDrawRule.OnOffCycle, cycle, traderCategories, unmodelled) is { } draw)
                        {
                            cycled = new P.TradeVisitComp(P.TradeCompRate.OnOffCycle, start) { Draw = draw };
                        }
                    }
                    else if (TraderIncident(cycle.incident, kind, incidents, unmodelled) is { } trader)
                    {
                        cycled = new P.TradeVisitComp(P.TradeCompRate.OnOffCycle, Math.Max(start, trader.earliestDay)) { Incident = trader.defName };
                    }

                    if (cycled is null)
                    {
                        break;
                    }

                    comps.Add(cycled with { CompIndex = def.comps.IndexOf(cycle) });
                    break;
                case StorytellerCompProperties_RandomMain random when kind == nameof(StorytellerCompProperties_RandomMain):
                    const string big = nameof(IncidentCategoryDefOf.ThreatBig);
                    var entries = random.categoryWeights.Seq().Where(e => e.category is not null).ToList();
                    var drawn = entries.Where(e => e.weight > 0 && traderCategories.ContainsKey(e.category.defName)).Select(e => e.category.defName).Distinct().ToList();
                    if (drawn.Count == 0)
                    {
                        break;
                    }

                    if (drawn.Any(c => !traderCategories[c]))
                    {
                        unmodelled.UnionWith(drawn.Where(c => !traderCategories[c]).Select(c => kind + ":" + c));
                        break;
                    }

                    comps.Add(new P.TradeVisitComp(P.TradeCompRate.Mtb, Decimal32(random.minDaysPassed))
                    {
                        IncidentsPerDay = 1 / Decimal32(random.mtbDays),
                        Draw = new P.TradeCategoryDraw(P.TradeDrawRule.RandomMain)
                        {
                            Categories = Domain.EquatableArray.From(entries.Where(e => e.category.defName != big)
                                .Select(e => new P.TradeCategoryWeight(e.category.defName, Decimal32(e.weight), traderCategories.ContainsKey(e.category.defName)))),
                            ThreatBigWeight = entries.Where(e => e.category.defName == big).Sum(e => Decimal32(e.weight)),
                            MaxThreatBigIntervalDays = Decimal32(random.maxThreatBigIntervalDays),
                            MinPopulationIntentFactor = Decimal32(random.minIncChancePopulationIntentFactor),
                        },
                    });
                    break;
                case StorytellerCompProperties_CategoryMTB mtb when kind == nameof(StorytellerCompProperties_CategoryMTB):
                    if (CategoryDraw(mtb.category?.defName, kind, P.TradeDrawRule.CategoryMtb, mtb, traderCategories, unmodelled) is { } mtbDraw)
                    {
                        comps.Add(new P.TradeVisitComp(P.TradeCompRate.Mtb, Decimal32(mtb.minDaysPassed))
                        {
                            IncidentsPerDay = 1 / Decimal32(mtb.mtbDays),
                            MtbFactorByDaysPassed = Domain.EquatableArray.From(CurvePoints(mtb.mtbDaysFactorByDaysPassedCurve)),
                            Draw = mtbDraw,
                        });
                    }

                    break;
                case StorytellerCompProperties_SingleMTB single when kind == nameof(StorytellerCompProperties_SingleMTB):
                    if (TraderIncident(single.incident, kind, incidents, unmodelled) is { } once)
                    {
                        comps.Add(new P.TradeVisitComp(P.TradeCompRate.Mtb, Math.Max(Decimal32(single.minDaysPassed), once.earliestDay))
                        {
                            Incident = once.defName,
                            IncidentsPerDay = 1 / Decimal32(single.mtbDays),
                        });
                    }

                    break;
                default:
                    if (!SendsNoTrader.Contains(kind))
                    {
                        unmodelled.Add(kind);
                    }

                    break;
            }
        }

        return new P.StorytellerTrade { Comps = comps, Unmodelled = [.. unmodelled] };
    }

    // A one-category comp's draw when its category is a trader category: null for any other category; named unmodelled when the
    // trader category is not drawn exactly.
    private static P.TradeCategoryDraw? CategoryDraw(string? category, string kind, P.TradeDrawRule rule, StorytellerCompProperties comp,
        Dictionary<string, bool> traderCategories, SortedSet<string> unmodelled)
    {
        if (category is null || !traderCategories.TryGetValue(category, out var exact))
        {
            return null;
        }

        if (!exact)
        {
            unmodelled.Add(kind + ":" + category);
            return null;
        }

        return new P.TradeCategoryDraw(rule)
        {
            Categories = [new P.TradeCategoryWeight(category, 1, true)],
            MinPopulationIntentFactor = Decimal32(comp.minIncChancePopulationIntentFactor),
        };
    }

    // The comp's incident when its worker class is a modelled one; a trader-like worker of another class (a subclass) is named
    // unmodelled; any other incident sends no trader.
    private static IncidentDef? TraderIncident(IncidentDef? incident, string kind, Dictionary<string, P.TradeIncident> incidents, SortedSet<string> unmodelled)
    {
        if (incident?.workerClass is not { } worker)
        {
            return null;
        }

        if (incidents.TryGetValue(incident.defName, out var modelled))
        {
            // A traveler group sends no trader of its own; it only shares a category draw.
            return modelled.Worker == P.TradeWorker.Travelers ? null : incident;
        }

        if (TraderLike(worker))
        {
            unmodelled.Add(kind + ":" + incident.defName);
        }

        return null;
    }

    // The exact worker classes the trader decoder models.
    private static P.TradeWorker? TradeWorkerOf(Type? worker) =>
        worker == typeof(IncidentWorker_TraderCaravanArrival) ? P.TradeWorker.Caravan
        : worker == typeof(IncidentWorker_OrbitalTraderArrival) ? P.TradeWorker.Orbital
        : worker == typeof(IncidentWorker_VisitorGroup) ? P.TradeWorker.Visitors
        : worker == typeof(IncidentWorker_CaravanArrivalTributeCollector) ? P.TradeWorker.TributeCollector
        : worker == typeof(IncidentWorker_TravelerGroup) ? P.TradeWorker.Travelers
        : null;

    // A worker that may send a trader: a caravan, orbital or visitor worker or any subclass (a mod's draws its own way).
    private static bool TraderLike(Type? worker) => worker is not null && (typeof(IncidentWorker_TraderCaravanArrival).IsAssignableFrom(worker)
        || typeof(IncidentWorker_OrbitalTraderArrival).IsAssignableFrom(worker) || typeof(IncidentWorker_VisitorGroup).IsAssignableFrom(worker));

    /// <summary>The def's comps that run on the player's home map, each with its exact props class name.</summary>
    private static IEnumerable<(StorytellerCompProperties Comp, string Kind)> HomeMapComps(StorytellerDef def) =>
        def.comps.Seq().Where(c => c is not null && c.Enabled && OnHomeMap(c)).Select(c => (c, c.GetType().Name));

    private static List<(double, double)> CurvePoints(SimpleCurve? curve) => [.. curve?.Points.Select(p => (Decimal32(p.x), Decimal32(p.y))) ?? []];

    // The target tags a comp is run for: StorytellerComp is ticked for a target when its tags allow it (allowed empty means any).
    private static bool OnHomeMap(StorytellerCompProperties comp)
    {
        const string home = nameof(IncidentTargetTagDefOf.Map_PlayerHome);
        if (comp.disallowedTargetTags.Seq().Any(t => t?.defName == home))
        {
            return false;
        }

        var allowed = comp.allowedTargetTags;
        return allowed is null || allowed.Count == 0 || allowed.Any(t => t?.defName == home);
    }
}
