using RimWorld;
using Verse;
using P = RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Bridge;

// The disease tables of the harm model's illness forecast (#3824 of #3650), read by P.IllnessSchedule and P.IllnessForecast:
//   - each StorytellerDef's StorytellerCompProperties_Disease comps of the DiseaseHuman category that run on the home map (their
//     minDaysPassed). A home-map comp that can send DiseaseHuman otherwise (an OnOffCycle, a RandomMain weight, a CategoryMTB or
//     CategoryIndividualMTBByBiome of the category, or a comp class the threat decoder does not know) is unmodelled;
//   - each BiomeDef's diseaseMtbDays and BiomeDef.CommonalityOfDisease's table: the biome's diseases records, then the
//     incidents' diseaseBiomeRecords naming the biome (the first record of an incident wins, as the game's Dictionary.Add would
//     refuse a second). The game's lazy cache is never filled: the records are read directly;
//   - each DiseaseHuman IncidentDef with a diseaseIncident: its victim rules and gates, and its hediff's course.

public sealed partial class DefinitionCatalog
{
    // The hediff comps a disease course models; any other comp class makes the course unmodelled.
    private static readonly HashSet<string> ModelledDiseaseComps = new(StringComparer.Ordinal)
    {
        nameof(HediffCompProperties_Immunizable), nameof(HediffCompProperties_TendDuration), nameof(HediffCompProperties_SeverityPerDay),
        nameof(HediffCompProperties_Disappears), nameof(HediffCompProperties_DestroyOrgan), nameof(HediffCompProperties_Discoverable),
    };

    /// <summary>The disease tables; empty when the catalog has no such defs, which leaves the illness unassessed.</summary>
    public P.DiseaseTable DiseaseTable() => _diseaseTable.Value;

    private P.DiseaseTable BuildDiseaseTable()
    {
        var incidents = Rows<IncidentDef>().Values
            .Where(d => d.category?.defName == nameof(IncidentCategoryDefOf.DiseaseHuman) && d.diseaseIncident is not null)
            .OrderBy(d => d.defName, StringComparer.Ordinal).ToArray();
        var body = DefRow<BodyDef>(nameof(BodyDefOf.Human));
        return new P.DiseaseTable
        {
            Storytellers = Rows<StorytellerDef>().ToDictionary(row => row.Key, row => StorytellerDiseases(row.Value), StringComparer.Ordinal),
            Biomes = Rows<BiomeDef>().ToDictionary(row => row.Key, row => DiseaseBiome(row.Value, incidents), StringComparer.Ordinal),
            Incidents = incidents.ToDictionary(d => d.defName, d => DiseaseIncident(d, body), StringComparer.Ordinal),
        };
    }

    private static P.StorytellerDiseases StorytellerDiseases(StorytellerDef def)
    {
        const string human = nameof(IncidentCategoryDefOf.DiseaseHuman);
        List<double> comps = [];
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
                case StorytellerCompProperties_Disease disease when kind == nameof(StorytellerCompProperties_Disease):
                    if (disease.category?.defName == human)
                    {
                        comps.Add(Decimal32(disease.minDaysPassed));
                    }

                    break;
                case StorytellerCompProperties_OnOffCycle cycle when kind == nameof(StorytellerCompProperties_OnOffCycle):
                    if (cycle.IncidentCategory?.defName == human)
                    {
                        unmodelled.Add(kind);
                    }

                    break;
                case StorytellerCompProperties_RandomMain random when kind == nameof(StorytellerCompProperties_RandomMain):
                    if (random.categoryWeights.Seq().Any(e => e.category?.defName == human && e.weight > 0))
                    {
                        unmodelled.Add(kind);
                    }

                    break;
                case StorytellerCompProperties_CategoryMTB mtb:
                    if (mtb.category?.defName == human)
                    {
                        unmodelled.Add(kind);
                    }

                    break;
                case StorytellerCompProperties_CategoryIndividualMTBByBiome biome:
                    if (biome.category?.defName == human)
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

        return new P.StorytellerDiseases { MinDaysPassed = comps, Unmodelled = [.. unmodelled] };
    }

    private static P.DiseaseBiome DiseaseBiome(BiomeDef biome, IReadOnlyList<IncidentDef> incidents)
    {
        var commonality = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var record in GameMembers.BiomeDiseases(biome).Seq())
        {
            if (record?.diseaseInc is { } incident)
            {
                commonality.TryAdd(incident.defName, Decimal32(record.commonality));
            }
        }

        foreach (var incident in incidents)
        {
            foreach (var record in incident.diseaseBiomeRecords.Seq())
            {
                if (record?.biome == biome)
                {
                    commonality.TryAdd((record.diseaseInc ?? incident).defName, Decimal32(record.commonality));
                }
            }
        }

        return new P.DiseaseBiome(Decimal32(biome.diseaseMtbDays), commonality);
    }

    private P.DiseaseIncidentRules DiseaseIncident(IncidentDef def, BodyDef? body)
    {
        const string home = nameof(IncidentTargetTagDefOf.Map_PlayerHome);
        return new P.DiseaseIncidentRules
        {
            Incident = def.defName,
            FractionMin = Decimal32(def.diseaseVictimFractionRange.min),
            FractionMax = Decimal32(def.diseaseVictimFractionRange.max),
            MaxVictims = def.diseaseMaxVictims,
            EarliestDay = def.earliestDay,
            MinPopulation = def.minPopulation,
            AllowedBiomes = [.. def.allowedBiomes.Seq().Where(b => b is not null).Select(b => b.defName)],
            DisallowedBiomes = [.. def.disallowedBiomes.Seq().Where(b => b is not null).Select(b => b.defName)],
            OnHomeMap = def.targetTags.Seq().Any(t => t?.defName == home),
            Course = DiseaseCourse(def.diseaseIncident, def.diseasePartsToAffect, body),
        };
    }

    /// <summary>The course of <paramref name="hediff"/> given to <paramref name="parts"/> (null: the whole body) of the Human <paramref name="body"/>.</summary>
    private P.DiseaseCourse DiseaseCourse(HediffDef hediff, List<BodyPartDef>? parts, BodyDef? body)
    {
        SortedSet<string> unmodelled = new(StringComparer.Ordinal);
        SortedSet<string> unpriced = new(StringComparer.Ordinal);
        var comps = hediff.comps.Seq().Where(c => c is not null).ToArray();
        foreach (var comp in comps)
        {
            var kind = comp.GetType().Name;
            if (!ModelledDiseaseComps.Contains(kind))
            {
                unmodelled.Add(kind);
            }
        }

        var perDay = comps.FirstOrDefault(c => c.GetType() == typeof(HediffCompProperties_SeverityPerDay)) as HediffCompProperties_SeverityPerDay;
        if (perDay is { reverseSeverityChangeChance: > 0 })
        {
            unmodelled.Add("reverseSeverityChangeChance");
        }

        var immunizable = comps.FirstOrDefault(c => c.GetType() == typeof(HediffCompProperties_Immunizable)) as HediffCompProperties_Immunizable;
        if (immunizable is { severityFactorsFromHediffs.Count: > 0 })
        {
            unpriced.Add("disease_severity_factors_unpriced:" + hediff.defName);
        }

        if (comps.FirstOrDefault(c => c.GetType() == typeof(HediffCompProperties_Disappears)) is HediffCompProperties_Disappears { requiredMentalState: not null })
        {
            unmodelled.Add("disappearsRequiredMentalState");
        }

        if (hediff.hediffGivers is { Count: > 0 })
        {
            unmodelled.Add("hediffGivers");
        }

        foreach (var stage in hediff.stages.Seq())
        {
            if (stage.deathMtbDays > 0 || stage.hediffGivers is { Count: > 0 })
            {
                unmodelled.Add("stageDeathOrGivers");
            }

            if (stage.partEfficiencyOffset != 0)
            {
                unpriced.Add("disease_part_efficiency_unpriced:" + hediff.defName);
            }
        }

        // TendTicksBase and TendTicksFull are the comp's own getters (baseTendDurationHours, tendOverlapHours are private); a
        // permanent tend (TendIsPermanent) is never due again.
        var tend = comps.FirstOrDefault(c => c.GetType() == typeof(HediffCompProperties_TendDuration)) as HediffCompProperties_TendDuration;
        P.TendCourse? tendCourse = tend is null ? null : new P.TendCourse(
            Decimal32(tend.severityPerDayTended),
            tend.TendIsPermanent ? double.PositiveInfinity : (double)tend.TendTicksBase / GenDate.TicksPerDay,
            tend.TendIsPermanent ? double.PositiveInfinity : (double)tend.TendTicksFull / GenDate.TicksPerDay,
            tend.disappearsAtTotalTendQuality);

        return new P.DiseaseCourse
        {
            Table = PenaltyTable(hediff.defName),
            MinSeverity = Decimal32(hediff.minSeverity),
            MaxSeverity = hediff.maxSeverity >= float.MaxValue ? double.PositiveInfinity : Decimal32(hediff.maxSeverity),
            PerDayMin = perDay is null ? 0 : Decimal32(perDay.severityPerDay) + Decimal32(perDay.severityPerDayRange.min),
            PerDayMax = perDay is null ? 0 : Decimal32(perDay.severityPerDay) + Decimal32(perDay.severityPerDayRange.max),
            NotImmuneFactorMin = immunizable is null ? 1 : Decimal32(immunizable.severityPerDayNotImmuneRandomFactor.min),
            NotImmuneFactorMax = immunizable is null ? 1 : Decimal32(immunizable.severityPerDayNotImmuneRandomFactor.max),
            Tendable = hediff.tendable,
            Tend = tendCourse,
            LethalPartShare = LethalPartShare(hediff, parts, body, unmodelled),
            DestroysOrgan = comps.Any(c => c.GetType() == typeof(HediffCompProperties_DestroyOrgan)),
            Unmodelled = [.. unmodelled],
            Unpriced = [.. unpriced],
        };
    }

    // Hediff.PostMake: a def with onlyLifeThreateningTo threatens only a part of those defs. HediffGiverUtility.TryApply picks the
    // part among the affected defs' parts by coverageAbs, so the share is the threatened parts' coverage of the affected parts'.
    private static double LethalPartShare(HediffDef hediff, List<BodyPartDef>? parts, BodyDef? body, SortedSet<string> unmodelled)
    {
        if (hediff.onlyLifeThreateningTo is null)
        {
            return 1;
        }

        if (parts is not { Count: > 0 } || body?.corePart is null)
        {
            unmodelled.Add("onlyLifeThreateningToWithoutParts");
            return 0;
        }

        double all = 0, threatened = 0;
        var stack = new Stack<BodyPartRecord>();
        stack.Push(body.corePart);
        while (stack.Count > 0)
        {
            var record = stack.Pop();
            if (parts.Contains(record.def))
            {
                all += Decimal32(record.coverageAbs);
                threatened += hediff.onlyLifeThreateningTo.Contains(record.def) ? Decimal32(record.coverageAbs) : 0;
            }

            foreach (var child in record.parts.Seq())
            {
                stack.Push(child);
            }
        }

        if (!(all > 0))
        {
            unmodelled.Add("affectedPartsUncovered");
            return 0;
        }

        return threatened / all;
    }
}
