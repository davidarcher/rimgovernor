using System.Reflection;
using RimGovernor.Brain.Bridge;
using RimWorld;
using Verse;
using Xunit;
using P = RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Domain.Tests.Game;

// #3824: the disease tables decoded from Core-shaped defs (Storytellers.xml, Biomes, Incidents_Map_Disease.xml): the storytellers'
// DiseaseHuman comps, a biome's MTB and commonalities, and each DiseaseHuman incident's rules and its hediff's course.
public sealed class DiseaseTableTests
{
    internal static readonly IncidentCategoryDef DiseaseHuman = new() { defName = nameof(IncidentCategoryDefOf.DiseaseHuman) };
    private static readonly IncidentCategoryDef DiseaseAnimal = new() { defName = "DiseaseAnimal" };
    private static readonly IncidentCategoryDef ThreatBig = new() { defName = nameof(IncidentCategoryDefOf.ThreatBig) };
    private static readonly IncidentTargetTagDef Home = new() { defName = nameof(IncidentTargetTagDefOf.Map_PlayerHome) };

    private static readonly BodyPartDef Torso = new() { defName = "Torso" };
    private static readonly BodyPartDef Heart = new() { defName = "Heart" };
    private static readonly BodyPartDef Lung = new() { defName = "Lung" };
    private static readonly BodyPartDef Kidney = new() { defName = "Kidney" };

    [Fact]
    public void TheCoreStorytellersSendDiseaseFromTheirMinDaysPassed()
    {
        var table = Table();

        Assert.Equal([9.0], table.Storytellers["Cassandra"].MinDaysPassed);
        Assert.Equal([12.0], table.Storytellers["Phoebe"].MinDaysPassed);
        Assert.Equal([0.0], table.Storytellers["Randy"].MinDaysPassed);
        Assert.All(table.Storytellers.Values, s => Assert.Empty(s.Unmodelled));
    }

    [Fact]
    public void ARandomMainWeightOnDiseaseHumanIsUnmodelled()
    {
        var main = new StorytellerCompProperties_RandomMain
        {
            mtbDays = 1, categoryWeights = [new IncidentCategoryEntry { category = DiseaseHuman, weight = 1 }],
        };
        var table = Catalog(new StorytellerDef { defName = "Modded", comps = [main] }).DiseaseTable();

        Assert.Equal([nameof(StorytellerCompProperties_RandomMain)], table.Storytellers["Modded"].Unmodelled);
    }

    [Fact]
    public void ABiomeGivesItsMtbAndTheCommonalityOfItsOwnAndTheIncidentsRecords()
    {
        var biome = Table().Biomes["TemperateForest"];

        Assert.Equal(60, biome.MtbDays);
        Assert.Equal(100, biome.Commonality["Disease_Flu"]);
        Assert.Equal(30, biome.Commonality["Disease_GutWorms"]);
        Assert.False(biome.Commonality.ContainsKey("Disease_OrganDecay"));
    }

    [Fact]
    public void AnIncidentCarriesItsVictimRulesAndItsHediffsCourse()
    {
        var flu = Table().Incidents["Disease_Flu"];

        Assert.True(flu.OnHomeMap);
        Assert.Equal(0.2, flu.FractionMin, 6);
        Assert.Equal(0.5, flu.FractionMax, 6);
        Assert.Equal(99999, flu.MaxVictims);
        Assert.Empty(flu.Course.Unmodelled);
        Assert.Equal(1, flu.Course.Table.LethalSeverity);
        Assert.Equal(0.2238, flu.Course.Table.Immunizable!.SeverityPerDayNotImmune, 6);
        Assert.Equal(1, flu.Course.LethalPartShare);
        Assert.NotNull(flu.Course.Tend);
        Assert.Equal(12 * 2500.0 / 60000, flu.Course.Tend!.FirstRetendDays, 9);
        Assert.Equal((12 + 3) * 2500.0 / 60000, flu.Course.Tend.RetendDays, 9);
        Assert.Equal(-1, flu.Course.Tend.CureAtTotalQuality);
        Assert.True(flu.Course.Valid());

        var worms = Table().Incidents["Disease_GutWorms"];
        Assert.Equal(3, worms.Course.Tend!.CureAtTotalQuality);
    }

    [Fact]
    public void OrganDecayKillsOnlyOnTheHeartsShareOfTheAffectedPartsAndDestroysTheOrgan()
    {
        var decay = Table().Incidents["Disease_OrganDecay"];

        // Heart 0.02 of heart, two lungs at 0.025 and two kidneys at 0.017.
        Assert.Equal(0.02 / (0.02 + 0.05 + 0.034), decay.Course.LethalPartShare, 6);
        Assert.True(decay.Course.DestroysOrgan);
        Assert.Equal(1, decay.MaxVictims);
        Assert.Equal(0.0166, decay.Course.PerDayMin, 6);
        Assert.Equal(0.0334, decay.Course.PerDayMax, 6);
        Assert.Contains("disease_part_efficiency_unpriced:OrganDecay", decay.Course.Unpriced);
    }

    [Fact]
    public void AHediffCompTheCourseDoesNotModelMakesItUnmodelled()
    {
        var hediff = Hediff("Odd", 1, 0.1f, new HediffCompProperties_Infecter());
        var table = Catalog(hediff, Incident("Disease_Odd", hediff)).DiseaseTable();

        Assert.Equal([nameof(HediffCompProperties_Infecter)], table.Incidents["Disease_Odd"].Course.Unmodelled);
    }

    internal static P.DiseaseTable Table() => Catalog(CoreDefs()).DiseaseTable();

    internal static Def[] CoreDefs()
    {
        var flu = Hediff("Flu", 1, 0.2f,
            new HediffCompProperties_Immunizable { immunityPerDaySick = 0.2388f, severityPerDayNotImmune = 0.2238f, severityPerDayImmune = -0.4947f },
            Tend(12, -0.0773f, -1));
        flu.tendable = true;
        var worms = Hediff("GutWorms", 0, 0.5f, Tend(48, 0, 3));
        worms.tendable = true;
        var decay = Hediff("OrganDecay", 1, 0.001f,
            new HediffCompProperties_SeverityPerDay { severityPerDayRange = new FloatRange(0.0166f, 0.0334f) }, new HediffCompProperties_DestroyOrgan());
        decay.maxSeverity = 1;
        decay.onlyLifeThreateningTo = [Heart];
        decay.stages[0].partEfficiencyOffset = -0.1f;

        var fluIncident = Incident("Disease_Flu", flu);
        var wormsIncident = Incident("Disease_GutWorms", worms);
        var decayIncident = Incident("Disease_OrganDecay", decay);
        decayIncident.diseaseMaxVictims = 1;
        decayIncident.diseasePartsToAffect = [Heart, Lung, Kidney];

        var forest = new BiomeDef { defName = "TemperateForest", diseaseMtbDays = 60 };
        GameMembers.BiomeDiseases(forest) = [new BiomeDiseaseRecord { diseaseInc = fluIncident, commonality = 100 }];
        wormsIncident.diseaseBiomeRecords = [new BiomeDiseaseRecord { biome = forest, commonality = 30 }];

        return
        [
            Teller("Cassandra", Disease(DiseaseHuman, 9), Disease(DiseaseAnimal, 9), Cycle()),
            Teller("Phoebe", Disease(DiseaseHuman, 12), Cycle()),
            Teller("Randy", Disease(DiseaseHuman, 0)),
            flu, worms, decay, fluIncident, wormsIncident, decayIncident, forest, HumanBody(),
        ];
    }

    private static BodyDef HumanBody()
    {
        static BodyPartRecord Part(BodyPartDef def, float coverage) => new() { def = def, coverageAbs = coverage };
        var torso = Part(Torso, 0.3f);
        torso.parts = [Part(Heart, 0.02f), Part(Lung, 0.025f), Part(Lung, 0.025f), Part(Kidney, 0.017f), Part(Kidney, 0.017f)];
        return new BodyDef { defName = nameof(BodyDefOf.Human), corePart = torso };
    }

    internal static HediffDef Hediff(string name, float lethal, float initial, params HediffCompProperties[] comps) => new()
    {
        defName = name, lethalSeverity = lethal, initialSeverity = initial, comps = [.. comps], stages = [new HediffStage { minSeverity = 0 }],
    };

    internal static HediffCompProperties_TendDuration Tend(float baseHours, float severityPerDayTended, int cureAt)
    {
        var tend = new HediffCompProperties_TendDuration { severityPerDayTended = severityPerDayTended, disappearsAtTotalTendQuality = cureAt };
        typeof(HediffCompProperties_TendDuration).GetField("baseTendDurationHours", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(tend, baseHours);
        return tend;
    }

    internal static IncidentDef Incident(string name, HediffDef hediff) => new()
    {
        defName = name, category = DiseaseHuman, diseaseIncident = hediff, targetTags = [Home], diseaseVictimFractionRange = new FloatRange(0.2f, 0.5f),
    };

    private static StorytellerCompProperties_Disease Disease(IncidentCategoryDef category, float minDays) => new() { category = category, minDaysPassed = minDays };

    private static StorytellerCompProperties_OnOffCycle Cycle()
    {
        var cycle = new StorytellerCompProperties_OnOffCycle { minDaysPassed = 11, onDays = 4.6f, offDays = 6, numIncidentsRange = new FloatRange(1, 2) };
        typeof(StorytellerCompProperties_OnOffCycle).GetField("category", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(cycle, ThreatBig);
        return cycle;
    }

    private static StorytellerDef Teller(string name, params StorytellerCompProperties[] comps) => new() { defName = name, comps = [.. comps] };

    internal static DefinitionCatalog Catalog(params Def[] defs) =>
        new([new ThingDef { defName = "Silver" }, new TerrainDef { defName = "Soil" }, .. defs], DefStatTable.Empty, new CatalogDerived("Silver", "Wort", 10f));
}
