using RimWorld;
using Verse;
using P = RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Bridge;

// The threat comps of every StorytellerDef (#3539 of #3471), read by P.ThreatSchedule. A comp counts when it can fire on the
// player's home map (its allowed target tags are empty or name Map_PlayerHome, and it does not disallow it: Cassandra's
// ThreatsGenerator and caravan comps target raid beacons and caravans) and it is one of:
//   - StorytellerCompProperties_OnOffCycle whose IncidentCategory is ThreatBig (incident.category, else category): a cycle;
//   - StorytellerCompProperties_RandomMain: a random main with the ThreatBig share of its categoryWeights.
// Every other home-map comp is either a class listed as sending no scheduled major threat (quests, diseases, faction visits,
// ...) or unmodelled, which makes that storyteller's rate unknown: a mod's comp class, an OnOffCycle that scales its accept
// fraction by threat points or progress score, and a CategoryMTB that sends ThreatBig. 1.7 has no StorytellerCompProperties_ThreatCycle.

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

    /// <summary>The threat comps of every storyteller def; empty (every storyteller unknown) when the catalog has no StorytellerDef.</summary>
    public P.ThreatScheduleTable ThreatSchedule() => _threatSchedule.Value;

    private P.ThreatScheduleTable BuildThreatSchedule() => new()
    {
        Storytellers = Rows<StorytellerDef>().ToDictionary(row => row.Key, row => StorytellerThreats(row.Value), StringComparer.Ordinal),
    };

    private static P.StorytellerThreats StorytellerThreats(StorytellerDef def)
    {
        List<P.ThreatCycleComp> cycles = [];
        List<P.ThreatRandomComp> randoms = [];
        SortedSet<string> unmodelled = new(StringComparer.Ordinal);
        foreach (var comp in def.comps.Seq())
        {
            if (comp is null || !OnHomeMap(comp))
            {
                continue;
            }

            var kind = comp.GetType().Name;
            switch (comp)
            {
                case StorytellerCompProperties_OnOffCycle cycle when kind == nameof(StorytellerCompProperties_OnOffCycle):
                    if (cycle.IncidentCategory?.defName != nameof(IncidentCategoryDefOf.ThreatBig))
                    {
                        break;
                    }

                    if (cycle.acceptPercentFactorPerThreatPointsCurve is not null || cycle.acceptPercentFactorPerProgressScoreCurve is not null)
                    {
                        unmodelled.Add(kind);
                        break;
                    }

                    cycles.Add(new P.ThreatCycleComp(
                        Decimal32(cycle.minDaysPassed), Decimal32(cycle.onDays), Decimal32(cycle.offDays),
                        Decimal32(cycle.numIncidentsRange.min), Decimal32(cycle.numIncidentsRange.max),
                        [.. cycle.acceptFractionByDaysPassedCurve?.Points.Select(p => (Decimal32(p.x), Decimal32(p.y))) ?? []]));
                    break;
                case StorytellerCompProperties_RandomMain random when kind == nameof(StorytellerCompProperties_RandomMain):
                    var weights = random.categoryWeights.Seq().Where(e => e.category is not null).ToList();
                    var total = weights.Sum(e => (double)e.weight);
                    var big = weights.Where(e => e.category.defName == nameof(IncidentCategoryDefOf.ThreatBig)).Sum(e => (double)e.weight);
                    randoms.Add(new P.ThreatRandomComp(Decimal32(random.minDaysPassed), Decimal32(random.mtbDays), total > 0 ? big / total : 0));
                    break;
                case StorytellerCompProperties_CategoryMTB mtb:
                    if (mtb.category?.defName == nameof(IncidentCategoryDefOf.ThreatBig))
                    {
                        unmodelled.Add(kind);
                    }

                    break;
                case StorytellerCompProperties_CategoryIndividualMTBByBiome biome:
                    if (biome.category?.defName == nameof(IncidentCategoryDefOf.ThreatBig))
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

        return new P.StorytellerThreats { Cycles = cycles, Randoms = randoms, Unmodelled = [.. unmodelled] };
    }

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
