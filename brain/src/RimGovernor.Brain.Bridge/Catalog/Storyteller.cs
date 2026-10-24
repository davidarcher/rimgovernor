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
//     accept fraction by days passed and by threat points;
//   - StorytellerCompProperties_RandomMain: an MTB comp with the ThreatBig share of its categoryWeights;
//   - StorytellerCompProperties_CategoryMTB of ThreatBig: an MTB comp with its mtbDaysFactorByDaysPassedCurve;
//   - StorytellerCompProperties_SingleMTB of a ThreatBig incident that targets the home map: an MTB comp;
//   - StorytellerCompProperties_FactionInteraction: nothing (faction visits, traders, ally help), unmodelled of a ThreatBig incident.
// A class listed as sending no scheduled major threat (quests, diseases, ...) adds nothing. The trader decoder (#3869) reads the
// same comps for trader visits (TradeSchedule). Every other home-map
// comp is named unmodelled and adds no threats: a comp class the decoder does not know, a ThreatBig OnOffCycle that scales its
// accept fraction by the progress score or swaps its days without a tree-connection meme, a ThreatsGenerator, and a
// CategoryIndividualMTBByBiome of ThreatBig. 1.7 has no StorytellerCompProperties_ThreatCycle.

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

    private static P.StorytellerThreats StorytellerThreats(StorytellerDef def)
    {
        const string big = nameof(IncidentCategoryDefOf.ThreatBig);
        List<P.ThreatCycleComp> cycles = [];
        List<P.ThreatMtbComp> mtbs = [];
        SortedSet<string> unmodelled = new(StringComparer.Ordinal);
        foreach (var (comp, kind) in HomeMapComps(def))
        {
            switch (comp)
            {
                case StorytellerCompProperties_OnOffCycle cycle when kind == nameof(StorytellerCompProperties_OnOffCycle):
                    if (cycle.IncidentCategory?.defName != big)
                    {
                        break;
                    }

                    if (cycle.acceptPercentFactorPerProgressScoreCurve is not null)
                    {
                        unmodelled.Add(kind + "." + nameof(cycle.acceptPercentFactorPerProgressScoreCurve));
                        break;
                    }

                    if (cycle.onDaysNoTreeConnectors != 0 || cycle.offDaysNoTreeConnectors != 0)
                    {
                        unmodelled.Add(kind + "." + nameof(cycle.onDaysNoTreeConnectors));
                        break;
                    }

                    cycles.Add(new P.ThreatCycleComp(
                        Decimal32(cycle.minDaysPassed), Decimal32(cycle.onDays), Decimal32(cycle.offDays),
                        Decimal32(cycle.numIncidentsRange.min), Decimal32(cycle.numIncidentsRange.max), CurvePoints(cycle.acceptFractionByDaysPassedCurve))
                    {
                        AcceptByThreatPoints = CurvePoints(cycle.acceptPercentFactorPerThreatPointsCurve),
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
                        unmodelled.Add(kind);
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

        return new P.StorytellerThreats { Cycles = cycles, Mtbs = mtbs, Unmodelled = [.. unmodelled] };
    }

    // The trader decoder (#3869): which comp classes send no trader visit. A comp class outside it and the trader decode below is
    // named unmodelled.
    private static readonly HashSet<string> SendsNoTrader = new(SendsNoScheduledMajorThreat, StringComparer.Ordinal)
    {
        nameof(StorytellerCompProperties_CategoryIndividualMTBByBiome), nameof(StorytellerCompProperties_ThreatsGenerator),
    };

    /// <summary>The trader comps of every storyteller def (#3869); empty (every storyteller unread) when the catalog has no StorytellerDef.</summary>
    public P.TradeScheduleTable TradeSchedule() => (_tradeSchedule ??= new(BuildTradeSchedule)).Value;

    private Lazy<P.TradeScheduleTable>? _tradeSchedule;

    private P.TradeScheduleTable BuildTradeSchedule() => new()
    {
        Storytellers = Rows<StorytellerDef>().ToDictionary(row => row.Key, row => StorytellerTrade(row.Value), StringComparer.Ordinal),
    };

    // The trader decoder models:
    //   - StorytellerCompProperties_FactionInteraction of a trader incident (StorytellerComp_FactionInteraction): baseIncidentsPerYear
    //     over the 60-day year (IncidentCycleUtility.IncidentCountThisInterval) from minDaysPassed, scaled by AllyIncidentFraction
    //     (fullAlliesOnly), none below minWealth; a minDanger gate is not modelled;
    //   - StorytellerCompProperties_OnOffCycle of a trader incident: its cycle (StorytellerComp_OnOffCycle; before
    //     forceRaidEnemyBeforeDaysPassed its incidents are raids);
    // each from the incident's earliestDay too. A trader incident's comp is a trader comp when the incident's worker class is exactly
    // IncidentWorker_TraderCaravanArrival or IncidentWorker_OrbitalTraderArrival. Named unmodelled: a FactionInteraction or OnOffCycle
    // of another trader-like worker (a visitor group, a subclass), an OnOffCycle trader comp with a progress-score curve or
    // tree-connector days, an OnOffCycle by category, a RandomMain or CategoryMTB drawing from the OrbitalVisitor or FactionArrival
    // categories, a SingleMTB of a trader incident, and a comp class the decoder does not know.
    private static P.StorytellerTrade StorytellerTrade(StorytellerDef def)
    {
        string[] traderCategories = ["OrbitalVisitor", "FactionArrival"];
        List<P.TradeVisitComp> comps = [];
        SortedSet<string> unmodelled = new(StringComparer.Ordinal);
        foreach (var (comp, kind) in HomeMapComps(def))
        {
            switch (comp)
            {
                case StorytellerCompProperties_FactionInteraction interaction when kind == nameof(StorytellerCompProperties_FactionInteraction):
                    if (TraderIncident(interaction.incident, kind, unmodelled) is not { } caravan)
                    {
                        break;
                    }

                    if (interaction.minDanger != StoryDanger.None)
                    {
                        unmodelled.Add(kind + "." + nameof(interaction.minDanger));
                        break;
                    }

                    comps.Add(new P.TradeVisitComp(caravan.defName, caravan.workerClass == typeof(IncidentWorker_OrbitalTraderArrival), Math.Max(Decimal32(interaction.minDaysPassed), Decimal32(caravan.earliestDay)))
                    {
                        IncidentsPerDay = Decimal32(interaction.baseIncidentsPerYear) / GenDate.DaysPerYear,
                        FullAlliesOnly = interaction.fullAlliesOnly,
                        MinWealth = Decimal32(interaction.minWealth),
                    });
                    break;
                case StorytellerCompProperties_OnOffCycle cycle when kind == nameof(StorytellerCompProperties_OnOffCycle):
                    if (cycle.incident is null)
                    {
                        if (traderCategories.Contains(cycle.IncidentCategory?.defName))
                        {
                            unmodelled.Add(kind + ":" + cycle.IncidentCategory!.defName);
                        }

                        break;
                    }

                    if (TraderIncident(cycle.incident, kind, unmodelled) is not { } trader)
                    {
                        break;
                    }

                    if (cycle.acceptPercentFactorPerProgressScoreCurve is not null || cycle.onDaysNoTreeConnectors != 0 || cycle.offDaysNoTreeConnectors != 0)
                    {
                        unmodelled.Add(kind + ":" + trader.defName);
                        break;
                    }

                    var from = Math.Max(Math.Max(Decimal32(cycle.minDaysPassed), Decimal32(cycle.forceRaidEnemyBeforeDaysPassed)), Decimal32(trader.earliestDay));
                    comps.Add(new P.TradeVisitComp(trader.defName, trader.workerClass == typeof(IncidentWorker_OrbitalTraderArrival), from)
                    {
                        Cycle = new P.ThreatCycleComp(Decimal32(cycle.minDaysPassed), Decimal32(cycle.onDays), Decimal32(cycle.offDays),
                            Decimal32(cycle.numIncidentsRange.min), Decimal32(cycle.numIncidentsRange.max), CurvePoints(cycle.acceptFractionByDaysPassedCurve))
                        {
                            AcceptByThreatPoints = CurvePoints(cycle.acceptPercentFactorPerThreatPointsCurve),
                        },
                    });
                    break;
                case StorytellerCompProperties_RandomMain random when kind == nameof(StorytellerCompProperties_RandomMain):
                    foreach (var category in random.categoryWeights.Seq().Where(e => e.category is not null && e.weight > 0 && traderCategories.Contains(e.category.defName)))
                    {
                        unmodelled.Add(kind + ":" + category.category.defName);
                    }

                    break;
                case StorytellerCompProperties_CategoryMTB mtb when kind == nameof(StorytellerCompProperties_CategoryMTB):
                    if (traderCategories.Contains(mtb.category?.defName))
                    {
                        unmodelled.Add(kind + ":" + mtb.category!.defName);
                    }

                    break;
                case StorytellerCompProperties_SingleMTB single when kind == nameof(StorytellerCompProperties_SingleMTB):
                    if (TraderIncident(single.incident, kind, unmodelled) is not null)
                    {
                        unmodelled.Add(kind + ":" + single.incident.defName);
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

    // The comp's incident when its worker class is exactly a trader worker; a trader-like worker of another class (a visitor
    // group, a subclass of either) is named unmodelled; any other incident sends no trader.
    private static IncidentDef? TraderIncident(IncidentDef? incident, string kind, SortedSet<string> unmodelled)
    {
        if (incident?.workerClass is not { } worker)
        {
            return null;
        }

        // IncidentWorker_VisitorGroup sends a trader with its TraderChance, and a mod's subclass draws its own way.
        if (worker == typeof(IncidentWorker_TraderCaravanArrival) || worker == typeof(IncidentWorker_OrbitalTraderArrival))
        {
            return incident;
        }

        if (typeof(IncidentWorker_TraderCaravanArrival).IsAssignableFrom(worker) || typeof(IncidentWorker_OrbitalTraderArrival).IsAssignableFrom(worker)
            || typeof(IncidentWorker_VisitorGroup).IsAssignableFrom(worker))
        {
            unmodelled.Add(kind + ":" + incident.defName);
        }

        return null;
    }

    /// <summary>The def's comps that run on the player's home map, each with its exact props class name.</summary>
    private static IEnumerable<(StorytellerCompProperties Comp, string Kind)> HomeMapComps(StorytellerDef def) =>
        def.comps.Seq().Where(c => c is not null && OnHomeMap(c)).Select(c => (c, c.GetType().Name));

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
