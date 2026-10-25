using RimWorld;
using Verse;
using P = RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Bridge;

// The disease tables of the harm model's illness forecast (#3824 of #3650), read by P.IllnessSchedule and P.IllnessForecast:
//   - each StorytellerDef's StorytellerCompProperties_Disease comps of the DiseaseHuman category that run on the home map (their
//     minDaysPassed), matched by exact class as the threat decoder does (Catalog/Storyteller.cs). A home-map comp that draws
//     DiseaseHuman otherwise (an OnOffCycle, a RandomMain weight, a CategoryMTB, CategoryIndividualMTBByBiome or SingleMTB of the
//     category, #3860) is decoded as a draw comp; a comp class the decoder does not know is named unmodelled and sends no
//     incidents (#3859);
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
            PopulationChanceByIntent = CurvePoints(StorytellerComp.IncreasesPopChanceByPopIntentCurve),
        };
    }

    private P.StorytellerDiseases StorytellerDiseases(StorytellerDef def)
    {
        const string human = nameof(IncidentCategoryDefOf.DiseaseHuman);
        const string big = nameof(IncidentCategoryDefOf.ThreatBig);
        List<double> comps = [];
        List<P.DiseaseDrawComp> draws = [];
        List<P.DiseaseBiomeComp> biomes = [];
        SortedSet<string> unmodelled = new(StringComparer.Ordinal);
        foreach (var (comp, kind) in HomeMapComps(def))
        {
            switch (comp)
            {
                case StorytellerCompProperties_Disease disease when kind == nameof(StorytellerCompProperties_Disease):
                    if (disease.category?.defName == human)
                    {
                        comps.Add(Decimal32(disease.minDaysPassed));
                    }

                    break;
                case StorytellerCompProperties_OnOffCycle cycle when kind == nameof(StorytellerCompProperties_OnOffCycle):
                    // Before forceRaidEnemyBeforeDaysPassed an on-off cycle sends raids, not the category's incidents.
                    var start = Math.Max(Decimal32(cycle.minDaysPassed), Decimal32(cycle.forceRaidEnemyBeforeDaysPassed));
                    if (cycle.incident is null ? cycle.IncidentCategory?.defName == human : cycle.incident.category?.defName == human)
                    {
                        draws.Add(new P.DiseaseDrawComp(P.DiseaseDrawRule.OnOffCycle, start) { Cycle = CycleComp(cycle, def.comps.IndexOf(cycle)), Incident = cycle.incident?.defName ?? "" });
                    }

                    break;
                case StorytellerCompProperties_RandomMain random when kind == nameof(StorytellerCompProperties_RandomMain):
                    var entries = random.categoryWeights.Seq().Where(e => e.category is not null && e.weight > 0).ToList();
                    if (entries.Any(e => e.category.defName == human))
                    {
                        draws.Add(new P.DiseaseDrawComp(P.DiseaseDrawRule.RandomMain, Decimal32(random.minDaysPassed))
                        {
                            MtbDays = Decimal32(random.mtbDays),
                            CategoryWeight = entries.Where(e => e.category.defName == human).Sum(e => Decimal32(e.weight)),
                            OtherWeight = entries.Where(e => e.category.defName != human && e.category.defName != big).Sum(e => Decimal32(e.weight)),
                            ThreatBigWeight = entries.Where(e => e.category.defName == big).Sum(e => Decimal32(e.weight)),
                            MaxThreatBigIntervalDays = Decimal32(random.maxThreatBigIntervalDays),
                            MinPopulationIntentFactor = Decimal32(random.minIncChancePopulationIntentFactor),
                        });
                    }

                    break;
                case StorytellerCompProperties_CategoryMTB mtb when kind == nameof(StorytellerCompProperties_CategoryMTB):
                    if (mtb.category?.defName == human)
                    {
                        draws.Add(new P.DiseaseDrawComp(P.DiseaseDrawRule.CategoryMtb, Decimal32(mtb.minDaysPassed))
                        {
                            MtbDays = Decimal32(mtb.mtbDays),
                            MtbFactorByDaysPassed = CurvePoints(mtb.mtbDaysFactorByDaysPassedCurve),
                            MinPopulationIntentFactor = Decimal32(mtb.minIncChancePopulationIntentFactor),
                        });
                    }

                    break;
                case StorytellerCompProperties_CategoryIndividualMTBByBiome biome when kind == nameof(StorytellerCompProperties_CategoryIndividualMTBByBiome):
                    if (biome.category?.defName == human)
                    {
                        biomes.Add(new P.DiseaseBiomeComp(Decimal32(biome.minDaysPassed), DiseaseBiomeIncidents(biome.category)));
                    }

                    break;
                case StorytellerCompProperties_SingleMTB single when kind == nameof(StorytellerCompProperties_SingleMTB):
                    if (single.incident?.category?.defName == human)
                    {
                        draws.Add(new P.DiseaseDrawComp(P.DiseaseDrawRule.SingleMtb, Decimal32(single.minDaysPassed)) { Incident = single.incident.defName, MtbDays = Decimal32(single.mtbDays) });
                    }

                    break;
                case StorytellerCompProperties_ThreatsGenerator when kind == nameof(StorytellerCompProperties_ThreatsGenerator):
                    // ThreatsGenerator.GetPossibleIncidents sends RaidEnemy and MechCluster only.
                    break;
                default:
                    if (!SendsNoScheduledMajorThreat.Contains(kind))
                    {
                        unmodelled.Add(kind);
                    }

                    break;
            }
        }

        return new P.StorytellerDiseases { MinDaysPassed = comps, Draws = draws, BiomeComps = biomes, Unmodelled = [.. unmodelled] };
    }

    // A CategoryIndividualMTBByBiome comp's DiseaseHuman incidents of the table (#3860), per biome: the first mtbDaysByBiome row of
    // the biome (the game's List.Find), with its MTB.
    private Dictionary<string, IReadOnlyList<(string Incident, double MtbDays)>> DiseaseBiomeIncidents(IncidentCategoryDef category)
    {
        var byBiome = new Dictionary<string, List<(string, double)>>(StringComparer.Ordinal);
        foreach (var incident in Rows<IncidentDef>().Values.Where(d => d.category == category && d.diseaseIncident is not null && d.mtbDaysByBiome is not null)
            .OrderBy(d => d.defName, StringComparer.Ordinal))
        {
            foreach (var row in incident.mtbDaysByBiome.Where(r => r?.biome is not null).GroupBy(r => r.biome.defName, StringComparer.Ordinal).Select(g => g.First()))
            {
                if (!byBiome.TryGetValue(row.biome.defName, out var list))
                {
                    byBiome[row.biome.defName] = list = [];
                }

                list.Add((incident.defName, Decimal32(row.mtbDays)));
            }
        }

        return byBiome.ToDictionary(e => e.Key, e => (IReadOnlyList<(string Incident, double MtbDays)>)e.Value, StringComparer.Ordinal);
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
            ChangesPopulation = def.populationEffect != IncidentPopulationEffect.None,
            IntentOffset = def.populationEffect switch
            {
                IncidentPopulationEffect.IncreaseHard => IncreaseHardIntentOffset,
                IncidentPopulationEffect.IncreaseMedium => IncreaseMediumIntentOffset,
                IncidentPopulationEffect.IncreaseEasy => IncreaseEasyIntentOffset,
                _ => 0,
            },
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
        }

        // TendTicksBase and TendTicksFull are the comp's own getters (baseTendDurationHours, tendOverlapHours are private); a
        // permanent tend (TendIsPermanent) is never due again.
        var tend = comps.FirstOrDefault(c => c.GetType() == typeof(HediffCompProperties_TendDuration)) as HediffCompProperties_TendDuration;
        P.TendCourse? tendCourse = tend is null ? null : new P.TendCourse(
            Decimal32(tend.severityPerDayTended),
            tend.TendIsPermanent ? double.PositiveInfinity : (double)tend.TendTicksBase / GenDate.TicksPerDay,
            tend.TendIsPermanent ? double.PositiveInfinity : (double)tend.TendTicksFull / GenDate.TicksPerDay,
            tend.disappearsAtTotalTendQuality);

        var destroys = comps.Any(c => c.GetType() == typeof(HediffCompProperties_DestroyOrgan));
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
            Parts = DiseaseParts(hediff, parts, body, destroys, unmodelled, unpriced),
            DestroysOrgan = destroys,
            Unmodelled = [.. unmodelled],
            Unpriced = [.. unpriced],
        };
    }

    // The part tags whose efficiency (PawnCapacityUtility.CalculateTagEfficiency, the mean over the body's parts with the tag) a
    // capacity worker multiplies in: PawnCapacityWorker_Breathing, _BloodPumping and _BloodFiltration.
    private static readonly Dictionary<string, string> OrganTagCapacities = new(StringComparer.Ordinal)
    {
        [nameof(BodyPartTagDefOf.BreathingSource)] = nameof(PawnCapacityDefOf.Breathing),
        [nameof(BodyPartTagDefOf.BloodPumpingSource)] = nameof(PawnCapacityDefOf.BloodPumping),
        [nameof(BodyPartTagDefOf.BloodFiltrationKidney)] = nameof(PawnCapacityDefOf.BloodFiltration),
        [nameof(BodyPartTagDefOf.BloodFiltrationLiver)] = nameof(PawnCapacityDefOf.BloodFiltration),
        [nameof(BodyPartTagDefOf.BloodFiltrationSource)] = nameof(PawnCapacityDefOf.BloodFiltration),
    };

    // The incident's affected part defs (#3830). Hediff.PostMake: a def with onlyLifeThreateningTo threatens only a part of those
    // defs; HediffGiverUtility.TryApply picks the part among the pawn's affected parts by coverageAbs (the policy draws it from the
    // pawn's read). A part's tag is priced only when every Human part carrying it is an affected one, so the pawn's read holds the
    // whole tag mean.
    private List<P.DiseasePart> DiseaseParts(HediffDef hediff, List<BodyPartDef>? parts, BodyDef? body, bool destroys,
        SortedSet<string> unmodelled, SortedSet<string> unpriced)
    {
        if (parts is not { Count: > 0 })
        {
            if (hediff.onlyLifeThreateningTo is not null)
            {
                unmodelled.Add("onlyLifeThreateningToWithoutParts");
            }

            return [];
        }

        var records = new List<BodyPartRecord>();
        var stack = new Stack<BodyPartRecord>();
        if (body?.corePart is { } core)
        {
            stack.Push(core);
        }

        while (stack.Count > 0)
        {
            var record = stack.Pop();
            records.Add(record);
            foreach (var child in record.parts.Seq())
            {
                stack.Push(child);
            }
        }

        var rows = new List<P.DiseasePart>();
        foreach (var part in parts.Where(p => p is not null).DistinctBy(p => p.defName))
        {
            var tags = part.tags.Seq().Where(t => t is not null).Select(t => t.defName).Distinct(StringComparer.Ordinal).ToArray();
            var tag = tags.Length == 1 && OrganTagCapacities.ContainsKey(tags[0])
                && records.All(r => r.def is null || !r.def.tags.Seq().Any(t => t?.defName == tags[0]) || parts.Contains(r.def))
                ? tags[0] : "";
            var capacity = tag.Length == 0 ? null : DefRow<PawnCapacityDef>(OrganTagCapacities[tag]);
            tag = capacity is null ? "" : tag;
            if (capacity is null && (destroys || hediff.stages.Seq().Any(s => s.partEfficiencyOffset != 0)))
            {
                unpriced.Add("disease_part_capacity_unmodelled:" + hediff.defName + ":" + part.defName);
            }

            rows.Add(new P.DiseasePart
            {
                Part = part.defName,
                Threatened = hediff.onlyLifeThreateningTo is null || hediff.onlyLifeThreateningTo.Contains(part),
                Tag = tag,
                Capacity = capacity?.defName ?? "",
                Lethal = capacity is { lethalFlesh: true },
                MinForCapable = capacity is null ? 0 : Decimal32(capacity.minForCapable),
                Recipes = destroys ? ReplacementTiers(part) : new Dictionary<string, double>(),
            });
        }

        return rows;
    }

    // The install recipes that put a part back on a missing part of the def (Recipe_InstallNaturalBodyPart and
    // Recipe_InstallArtificialBodyPart list a missing part among GetPartsToApplyOn), by their tier.
    private Dictionary<string, double> ReplacementTiers(BodyPartDef part) =>
        Rows<RecipeDef>().Values
            .Where(r => r.appliedOnFixedBodyParts.Seq().Contains(part) && r.workerClass is { } worker
                && (typeof(Recipe_InstallNaturalBodyPart).IsAssignableFrom(worker) || typeof(Recipe_InstallArtificialBodyPart).IsAssignableFrom(worker)))
            .ToDictionary(r => r.defName, r => PartTier(r.defName), StringComparer.Ordinal);
}
