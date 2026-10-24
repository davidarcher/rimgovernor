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
//   - StorytellerCompProperties_SingleMTB of a ThreatBig incident that targets the home map: an MTB comp.
// A class listed as sending no scheduled major threat (quests, diseases, faction visits, ...) adds nothing. Every other home-map
// comp is named unmodelled and adds no threats: a comp class the decoder does not know, a ThreatBig OnOffCycle that scales its
// accept fraction by the progress score or swaps its days without a tree-connection meme, a ThreatsGenerator, and a
// CategoryIndividualMTBByBiome of ThreatBig. 1.7 has no StorytellerCompProperties_ThreatCycle.

public sealed partial class DefinitionCatalog
{
    private static readonly HashSet<string> SendsNoScheduledMajorThreat = new(StringComparer.Ordinal)
    {
        nameof(StorytellerCompProperties_ClassicIntro), nameof(StorytellerCompProperties_DeepDrillInfestation), nameof(StorytellerCompProperties_Disease),
        nameof(StorytellerCompProperties_DissolutionTriggered), nameof(StorytellerCompProperties_FactionInteraction), nameof(StorytellerCompProperties_GauranlenPodSpawn),
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
