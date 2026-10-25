using System.Reflection;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.GameFacts.Observations;
using RimWorld;
using Verse;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests.Game;

// #3860: the comps other than StorytellerComp_Disease that draw DiseaseHuman incidents, decoded from their props by comp class and
// priced through the same catalog and review inputs as the disease comp (the production way, #3822). The oracle is the game's
// formulas: OnOffCycle, RandomMain, CategoryMTB, SingleMTB and CategoryIndividualMTBByBiome draw by IncidentChanceFinal weights.
public sealed class DiseaseDrawCompTests
{
    private static readonly IncidentCategoryDef Misc = new() { defName = "Misc" };
    private static readonly IncidentCategoryDef ThreatBig = new() { defName = nameof(IncidentCategoryDefOf.ThreatBig) };

    [Fact]
    public void EachDrawCompIsDecodedByClassWithItsDrawInputs()
    {
        var cycle = Cycle(2, 5, 5, 1, 1);
        cycle.forceRaidEnemyBeforeDaysPassed = 20;
        cycle.acceptPercentFactorPerProgressScoreCurve = new SimpleCurve([new Verse.CurvePoint(0, 1), new Verse.CurvePoint(10, 2)]);
        cycle.onDaysNoTreeConnectors = 3;
        var decoded = Decode(cycle, RandomMain(4), Mtb(30), Single("Disease_Flu", 50), Biome(), new StorytellerCompProperties_SingleMTB { mtbDays = 9, incident = new IncidentDef { defName = "Other", category = Misc } });

        Assert.Empty(decoded.Unmodelled);
        Assert.Equal([DiseaseDrawRule.OnOffCycle, DiseaseDrawRule.RandomMain, DiseaseDrawRule.CategoryMtb, DiseaseDrawRule.SingleMtb], decoded.Draws.Select(d => d.Rule));
        // Before forceRaidEnemyBeforeDaysPassed an on-off cycle's incidents are raids.
        Assert.Equal(20, decoded.Draws[0].MinDaysPassed);
        Assert.Equal([1.0, 2.0], decoded.Draws[0].Cycle!.AcceptByProgressScore.Select(p => p.Factor));
        Assert.Equal(3, decoded.Draws[0].Cycle!.OnDaysNoTreeConnectors);
        var main = decoded.Draws[1];
        Assert.Equal((4.0, 1.0, 3.5, 2.0), (main.MtbDays, main.CategoryWeight, main.OtherWeight, main.ThreatBigWeight));
        Assert.Equal(13, main.MaxThreatBigIntervalDays);
        Assert.Equal(30, decoded.Draws[2].MtbDays);
        Assert.Equal(("Disease_Flu", 50.0), (decoded.Draws[3].Incident, decoded.Draws[3].MtbDays));
        Assert.Equal(40.0, decoded.BiomeComps[0].ByBiome["TemperateForest"].Single(i => i.Incident == "Disease_Flu").MtbDays);
    }

    [Fact]
    public void ACategoryMtbDrawsEachOpenIncidentByWeightAtOneOverItsMtb()
    {
        var schedule = Schedule(Mtb(30), Reads(flu: 3, worms: 1));

        Assert.True(schedule.Assessed);
        Assert.Equal(1 / 30.0, schedule.IncidentsPerDay(30), 12);
        Assert.Equal(0.75, schedule.Share(schedule.Incidents.Single(i => i.Incident == "Disease_Flu"), 30), 12);
        Assert.Equal(["Disease_Flu", "Disease_GutWorms"], schedule.Incidents.Select(i => i.Incident));
    }

    [Fact]
    public void AnOnOffCycleSendsItsCountAtTheProgressAndTreeConnectionTheColonyReads()
    {
        var cycle = Cycle(0, 5, 5, 1, 1);
        cycle.acceptPercentFactorPerProgressScoreCurve = new SimpleCurve([new Verse.CurvePoint(0, 1), new Verse.CurvePoint(10, 2)]);
        cycle.onDaysNoTreeConnectors = 2;

        Assert.Equal(0.1, Schedule(cycle, Reads(flu: 1)).IncidentsPerDay(30), 12);
        Assert.Equal(0.15, Schedule(cycle, Reads(flu: 1), facts => facts with { ProgressScore = 5 }).IncidentsPerDay(30), 12);
        Assert.Equal(60 / (120.0 + 300), Schedule(cycle, Reads(flu: 1), facts => facts with { NoTreeConnection = true }).IncidentsPerDay(30), 12);
    }

    [Fact]
    public void ARandomMainDrawsDiseaseAmongItsCategoriesWithThreatBigForcedPastItsInterval()
    {
        var schedule = Schedule(RandomMain(4), Reads(flu: 1));

        // 1 event per 4 days; DiseaseHuman weight 1 of 1 + 3.5 + 2 with ThreatBig redrawn past 13 days.
        var share = 2 / (2 + 4.5);
        var events = 1 + ((1 - share) * -double.ExpM1(-0.25 * share * 13) / share);
        var notThreatBig = 1 - (1 / events);
        Assert.Equal(0.25 * notThreatBig * 1 / 4.5, schedule.IncidentsPerDay(30), 12);
    }

    [Fact]
    public void ASingleMtbFiresItsIncidentOnlyWhileItsWorkerCanFire()
    {
        Assert.Equal(1 / 50.0, Schedule(Single("Disease_Flu", 50), Reads(flu: 1, worms: 1)).IncidentsPerDay(30), 12);
        // A single incident ignores the weights: a weight of 0 still fires it.
        Assert.Equal(["Disease_GutWorms"], Schedule(Single("Disease_GutWorms", 50), Reads(flu: 1)).Incidents.Select(i => i.Incident));
        Assert.Equal(0, Schedule(Single("Disease_Flu", 50), [new DiseaseIncidentRead("Disease_Flu", 3) { Blocked = ["scenario"], Weight = 1 }]).IncidentsPerDay(30));
    }

    [Fact]
    public void ACategoryIndividualMtbByBiomeFiresEachIncidentOfTheColonysBiomeAtItsOwnMtb()
    {
        var schedule = Schedule(Biome(), Reads(flu: 1, worms: 1));

        Assert.Equal(1 / 40.0, schedule.IncidentsPerDay(30), 12);
        Assert.Equal(["Disease_Flu"], schedule.Incidents.Select(i => i.Incident));
        Assert.Empty(Schedule(Biome(), Reads(flu: 1), facts => facts with { Disease = facts.Disease with { Biome = "AridShrubland" } }).Incidents);
    }

    private static DiseaseIncidentRead[] Reads(double flu = 0, double worms = 0) =>
    [
        new DiseaseIncidentRead("Disease_Flu", 3) { Weight = flu },
        new DiseaseIncidentRead("Disease_GutWorms", 3) { Weight = worms },
        new DiseaseIncidentRead("Disease_OrganDecay", 3),
    ];

    // The review's illness inputs for a storyteller of just this draw comp, on a day-20 colony.
    private static IllnessSchedule Schedule(StorytellerCompProperties comp, DiseaseIncidentRead[] reads, Func<ThreatFacts, ThreatFacts>? with = null)
    {
        var facts = ColonyFactsCoreTests.QuietThreat with { Disease = IllnessForecastTests.Facts(20, reads) with { Storyteller = "Modded" } };
        var catalog = Catalog(Teller(comp));
        return HarmInputs.Of(new ReviewFacts { DiseaseTable = catalog.DiseaseTable(), Census = ColonyFactsCoreTests.Snapshot() with { Threat = new() { Observed = with?.Invoke(facts) ?? facts } } }).Illness;
    }

    private static StorytellerDiseases Decode(params StorytellerCompProperties[] comps) => Catalog(Teller(comps)).DiseaseTable().Storytellers["Modded"];

    private static StorytellerDef Teller(params StorytellerCompProperties[] comps) => new() { defName = "Modded", comps = [.. comps] };

    private static DefinitionCatalog Catalog(StorytellerDef teller)
    {
        var defs = DiseaseTableTests.CoreDefs();
        defs.OfType<IncidentDef>().Single(d => d.defName == "Disease_Flu").mtbDaysByBiome = [new MTBByBiome { biome = defs.OfType<BiomeDef>().Single(b => b.defName == "TemperateForest"), mtbDays = 40 }];
        return DiseaseTableTests.Catalog([.. defs, teller]);
    }

    private static StorytellerCompProperties_OnOffCycle Cycle(float minDays, float on, float off, float min, float max)
    {
        var cycle = new StorytellerCompProperties_OnOffCycle { minDaysPassed = minDays, onDays = on, offDays = off, numIncidentsRange = new FloatRange(min, max) };
        typeof(StorytellerCompProperties_OnOffCycle).GetField("category", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(cycle, DiseaseTableTests.DiseaseHuman);
        return cycle;
    }

    private static StorytellerCompProperties_RandomMain RandomMain(float mtbDays) => new()
    {
        mtbDays = mtbDays, maxThreatBigIntervalDays = 13,
        categoryWeights =
        [
            new IncidentCategoryEntry { category = DiseaseTableTests.DiseaseHuman, weight = 1 }, new IncidentCategoryEntry { category = Misc, weight = 3.5f },
            new IncidentCategoryEntry { category = ThreatBig, weight = 2 },
        ],
    };

    private static StorytellerCompProperties_CategoryMTB Mtb(float mtbDays) => new() { category = DiseaseTableTests.DiseaseHuman, mtbDays = mtbDays };

    private static StorytellerCompProperties_SingleMTB Single(string incident, float mtbDays) =>
        new() { mtbDays = mtbDays, incident = DiseaseTableTests.CoreDefs().OfType<IncidentDef>().Single(d => d.defName == incident) };

    private static StorytellerCompProperties_CategoryIndividualMTBByBiome Biome() => new() { category = DiseaseTableTests.DiseaseHuman };
}
