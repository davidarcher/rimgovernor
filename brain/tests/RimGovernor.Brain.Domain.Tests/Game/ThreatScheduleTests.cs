using System.Reflection;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.GameFacts.Observations;
using RimWorld;
using Verse;
using Xunit;
using P = RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Domain.Tests.Game;

// #3539: the threat schedule decoded from the storyteller comps, the three Core storytellers shaped as 1.7 states them
// (Data/Core/Defs/Storyteller/Storytellers.xml). The design doc's rates are the oracle, not the implementation:
// Cassandra about one major threat per 7.1 days, Phoebe per 16, Randy per 9. #3859: any storyteller is decoded from its comps by
// class; a comp the decoder does not model is a named finding that adds no threats.
public sealed class ThreatScheduleTests
{
    internal static readonly IncidentCategoryDef ThreatBig = new() { defName = nameof(IncidentCategoryDefOf.ThreatBig) };
    private static readonly IncidentCategoryDef Misc = new() { defName = "Misc" };
    private static readonly IncidentTargetTagDef Home = new() { defName = nameof(IncidentTargetTagDefOf.Map_PlayerHome) };
    private static readonly IncidentTargetTagDef RaidBeacon = new() { defName = "Map_RaidBeacon" };
    private static readonly IncidentTargetTagDef Caravan = new() { defName = "Caravan" };

    [Theory]
    [InlineData("Cassandra", 7.1)]
    [InlineData("Phoebe", 16)]
    [InlineData("Randy", 9)]
    public void TheCoreStorytellersGiveTheDesignDocRates(string storyteller, double daysPerThreat)
    {
        var schedule = Of(Table(), storyteller, 30.0, horizon: 600);

        Assert.True(schedule.Assessed);
        if (storyteller == "Randy") Assert.Equal(P.RandomMainProcess.Conditions, schedule.Findings);
        else Assert.Empty(schedule.Findings);
        Assert.InRange(schedule.MajorThreatsPerDay, 0.9 / daysPerThreat, 1.1 / daysPerThreat);
    }

    [Fact]
    public void ACycleAndARandomMainWaitForTheirMinDaysPassed()
    {
        Assert.Equal(0, Of(Table(), "Cassandra", 10.9, horizon: 0.1).MajorThreatsPerDay);
        Assert.True(Of(Table(), "Cassandra", 11.0).MajorThreatsPerDay > 0);
        Assert.Equal(0, Of(Table(), "Phoebe", 12.0, horizon: 0.1).MajorThreatsPerDay);
        Assert.Equal(0, Of(Table(), "Randy", 0.5, horizon: 0.5).MajorThreatsPerDay);
        Assert.True(Of(Table(), "Randy", 0.5, horizon: 1).MajorThreatsPerDay > 0);
    }

    [Fact]
    public void EveryThreatBeforeDay20IsARaidAndHalfAreAfter()
    {
        Assert.InRange(Of(Table(), "Cassandra", 19.9).RaidFraction, 0.5, 1);
        Assert.Equal(0.5, Of(Table(), "Cassandra", 20.0).RaidFraction);
    }

    [Fact]
    public void AnAcceptCurveScalesTheCycleByTheDaysPassed()
    {
        var cycle = Cycle(0, 10, 0, 2, 2);
        cycle.acceptFractionByDaysPassedCurve = new SimpleCurve([new CurvePoint(10, 0), new CurvePoint(30, 1)]);
        var table = IncidentCycleTests.Table(Teller("Curved", cycle));

        Assert.Equal(0, Of(table, "Curved", 5.0, horizon: 5).MajorThreatsPerDay);
        Assert.InRange(Of(table, "Curved", 20.0).MajorThreatsPerDay, 0.1, 0.2);
        Assert.Equal(0.2, Of(table, "Curved", 40.0).MajorThreatsPerDay, 1e-9);
    }

    [Fact]
    public void AnUnreadStorytellerOrInputIsUnassessedNamingIt()
    {
        var table = Table();
        var ghost = Of(table, "Ghost", 30.0);

        Assert.False(ghost.Assessed);
        Assert.Equal(["storyteller_unknown:Ghost"], ghost.Findings);
        Assert.Equal(["days_passed_invalid"], Of(table, "Cassandra", -1).Findings);
        Assert.Equal(["storyteller_unknown:Cassandra"], Of(new P.ThreatScheduleTable(), "Cassandra", 30.0).Findings);
    }

    [Fact]
    public void AStorytellerAssembledFromVanillaCompsIsModelled()
    {
        // An on-off cycle of 1 threat per 10 days at half its accept by threat points (500 of 0 to 1000), a random main of 1 per
        // 2 days half ThreatBig, a ThreatBig CategoryMTB of 10 days times 1.5 at day 30, and a raid SingleMTB of 20 days; a
        // Misc SingleMTB, a disease comp and a raid-beacon generator send no home-map threat.
        var cycle = Cycle(0, 5, 5, 1, 1);
        cycle.acceptPercentFactorPerThreatPointsCurve = new SimpleCurve([new CurvePoint(0, 1), new CurvePoint(1000, 0)]);
        var main = new StorytellerCompProperties_RandomMain { mtbDays = 2, categoryWeights = [Entry(nameof(IncidentCategoryDefOf.ThreatBig), 1), Entry("Misc", 1)] };
        var mtb = new StorytellerCompProperties_CategoryMTB
        {
            category = ThreatBig, mtbDays = 10, mtbDaysFactorByDaysPassedCurve = new SimpleCurve([new CurvePoint(0, 1), new CurvePoint(60, 2)]),
        };
        var raid = new StorytellerCompProperties_SingleMTB { mtbDays = 20, incident = new IncidentDef { defName = "RaidEnemy", category = ThreatBig, targetTags = [Home] } };
        var visit = new StorytellerCompProperties_SingleMTB { mtbDays = 1, incident = new IncidentDef { defName = "Visit", category = Misc, targetTags = [Home] } };
        var beacon = new StorytellerCompProperties_ThreatsGenerator { allowedTargetTags = [RaidBeacon] };
        var table = IncidentCycleTests.Table(Teller("Assembled", new StorytellerCompProperties_ClassicIntro(), cycle, main, mtb, raid, visit, beacon,
            new StorytellerCompProperties_Disease { category = Misc }));

        var schedule = Of(table, "Assembled", 30.0);

        Assert.True(schedule.Assessed);
        Assert.Equal(P.RandomMainProcess.Conditions, schedule.Findings);
        var observed = IncidentCycleTests.Read(cycle, 1, ColonyFactsCoreTests.QuietThreat with { DaysPassed = 30, RaidPoints = 500 });
        Assert.Equal(observed.Days.Count / 60.0 + 0.25 + (1 / 15.0) + 0.05, schedule.MajorThreatsPerDay, 9);
        // The threat-points curve reads the observed raid points: at 1000 its accept is 0 and the cycle adds nothing.
        Assert.Equal(0.25 + (1 / 15.0) + 0.05, Of(table, "Assembled", 30.0, points: 1000).MajorThreatsPerDay, 9);
    }

    [Fact]
    public void ACompClassTheDecoderDoesNotKnowIsANamedFindingThatAddsNoThreats()
    {
        var table = IncidentCycleTests.Table(Teller("Modded", Cycle(0, 5, 5, 1, 1), new StorytellerCompProperties_Test()));

        var schedule = Of(table, "Modded", 30.0);

        Assert.True(schedule.Assessed);
        Assert.Equal(0.1, schedule.MajorThreatsPerDay, 1e-9);
        Assert.Equal(["threat_comp_unmodelled:" + nameof(StorytellerCompProperties_Test)], schedule.Findings);
    }

    [Fact]
    public void AProgressScoreCurveScalesTheCycleByTheColonysProgress()
    {
        // StorytellerUtility.GetProgressScore: the accept factor is 1 at 0 and 2 at 10, so 1.5 at 5.
        var cycle = Cycle(0, 5, 5, 1, 1);
        cycle.acceptPercentFactorPerProgressScoreCurve = new SimpleCurve([new CurvePoint(0, 1), new CurvePoint(10, 2)]);
        var table = IncidentCycleTests.Table(Teller("Scored", cycle));

        Assert.Equal(0.1, Of(table, "Scored", 30.0, progress: 0).MajorThreatsPerDay, 1e-9);
        Assert.Equal(0.1, Of(table, "Scored", 30.0, progress: 5).MajorThreatsPerDay, 1e-9);
        Assert.Equal(0.1, Of(table, "Scored", 30.0, progress: 25).MajorThreatsPerDay, 1e-9);
    }

    [Fact]
    public void ACycleSwapsToItsTreeConnectorDaysOnlyWithoutATreeConnection()
    {
        // onDaysNoTreeConnectors 2 and offDaysNoTreeConnectors 0 (unset): on 2 days, off the cycle's own 5.
        var cycle = Cycle(0, 5, 5, 1, 1);
        cycle.onDaysNoTreeConnectors = 2;
        var table = IncidentCycleTests.Table(Teller("Tree", cycle));

        Assert.Equal(0.1, Of(table, "Tree", 30.0).MajorThreatsPerDay, 1e-9);
        Assert.InRange(Of(table, "Tree", 30.0, noTree: true).MajorThreatsPerDay, 8 / 60.0, 9 / 60.0);
    }

    [Fact]
    public void AHomeMapThreatsGeneratorDrawsByWeightAmongTheThreatsThatCanFireScaledByTheThreatScale()
    {
        var generator = new StorytellerCompProperties_ThreatsGenerator
        {
            parms = new ThreatsGeneratorParams { allowedThreats = AllowedThreatsGeneratorThreats.All, onDays = 5, offDays = 5, numIncidentsRange = new FloatRange(1, 1) },
        };
        var table = IncidentCycleTests.Table(Teller("Generated", generator));
        ThreatIncidentRead[] both = [new("RaidEnemy", 7.4, true), new("MechCluster", 2.0, true)];

        var normal = Of(table, "Generated", 30.0, generator: both);
        Assert.True(normal.Assessed);
        Assert.Empty(normal.Findings);
        Assert.Equal(0.1, normal.MajorThreatsPerDay, 1e-9);
        Assert.Equal(7.4 / 9.4, normal.RaidFraction, 1e-9);
        // ThreatsGenerator.ThreatScaleToCountFactorCurve: 0.3 gives a factor of 0.5, and between 0.6 (0.8) and 1 (1) is linear.
        var halfCount = IncidentCycleTests.ReadNative(new() { Salt = generator.parms.randSeed, OnDays = 5, OffDays = 5, Min = .5f, Max = .5f }, 17, 23, 30 * 60000, 60);
        Assert.Equal(halfCount.Days.Count / 60d, Of(table, "Generated", 30.0, generator: both, scale: 0.3).MajorThreatsPerDay, 12);
        Assert.InRange(Of(table, "Generated", 30.0, generator: both, scale: 0.8).MajorThreatsPerDay, 0, 0.1);
        // A raid that cannot fire (no faction) leaves the mech cluster: every threat is one, none a raid.
        var noRaid = Of(table, "Generated", 30.0, generator: [new("RaidEnemy", 7.4, false), new("MechCluster", 2.0, true)]);
        Assert.Equal(0.1, noRaid.MajorThreatsPerDay, 1e-9);
        Assert.Equal(0, noRaid.RaidFraction);
        // None that can fire: no threat.
        Assert.Equal(0, Of(table, "Generated", 30.0, generator: [new("RaidEnemy", 7.4, false), new("MechCluster", 2.0, false)]).MajorThreatsPerDay);
        // The generator waits for its minDaysPassed.
        generator.minDaysPassed = 40;
        Assert.Equal(0, Of(IncidentCycleTests.Table(Teller("Generated", generator)), "Generated", 30.0, generator: both).Raids.Sum(r => r.At(10)));
    }

    [Fact]
    public void AThreatsGeneratorWhoseIncidentTheReadLacksIsARequiredFact()
    {
        var generator = new StorytellerCompProperties_ThreatsGenerator
        {
            parms = new ThreatsGeneratorParams { allowedThreats = AllowedThreatsGeneratorThreats.Raids, onDays = 5, offDays = 5, numIncidentsRange = new FloatRange(1, 1) },
        };
        var table = IncidentCycleTests.Table(Teller("Generated", generator));
        Assert.Throws<P.RequiredFactException>(() => Of(table, "Generated", 30.0));

    }

    [Theory]
    [InlineData(700d, 100d, 3, 700)]
    [InlineData(200d, 800d, 3, 800)]
    [InlineData(null, null, 3, 1500)]
    [InlineData(null, 800d, 0.5, 800)]
    public void GeneratorPointsUseTheExplicitBudgetOtherwiseTheScaledCurrentPointsThenTheMinimum(double? points, double? minimum, double factor, double expected)
    {
        var generator = new StorytellerCompProperties_ThreatsGenerator
        {
            parms = new ThreatsGeneratorParams { allowedThreats = AllowedThreatsGeneratorThreats.Raids, onDays = 5, offDays = 5,
                numIncidentsRange = new FloatRange(1, 1), threatPoints = (float?)points, minThreatPoints = (float?)minimum, currentThreatPointsFactor = (float)factor },
        };
        var table = IncidentCycleTests.Table(Teller("Generated", generator));
        var result = Of(table, "Generated", 30, generator: [new("RaidEnemy", 1, true)]);
        Assert.Empty(result.Findings);
        Assert.Equal(expected, Assert.Single(result.Raids).Points);
        Assert.Equal(0.1, Assert.Single(result.Raids).At(60), 9);
        var review = new P.ReviewFacts
        {
            ThreatTable = table, DefenseCapacity = 1000,
            Census = ColonyFactsCoreTests.Snapshot() with
            {
                Threat = new() { Observed = IncidentCycleTests.Observe(table, ColonyFactsCoreTests.QuietThreat with
                {
                    Storyteller = "Generated", DaysPassed = 30, RaidPoints = 500, Generator = [new("RaidEnemy", 1, true)],
                }) },
            },
        };
        var priced = Assert.Single(P.HarmInputs.Of(review).ScheduledRaids);
        Assert.Equal(expected, priced.Raid.RaidPoints);
        Assert.Equal(1000, priced.Raid.Capacity);
        Assert.Equal(0.1, priced.Timing.PerDay(priced.PerDay, 60), 9);
    }

    [Fact]
    public void TwoGeneratorsKeepTheirOwnEligibilityAndPointsAndReadTheMapGenerationStart()
    {
        StorytellerCompProperties_ThreatsGenerator Generator(float points) => new()
        {
            parms = new ThreatsGeneratorParams { allowedThreats = AllowedThreatsGeneratorThreats.Raids, onDays = 5, offDays = 5,
                numIncidentsRange = new FloatRange(1, 1), threatPoints = points },
        };
        var table = IncidentCycleTests.Table(Teller("Generated", Generator(200), Generator(900)));
        var facts = ColonyFactsCoreTests.QuietThreat with
        {
            Storyteller = "Generated", DaysPassed = 30,
            Generator = [new("RaidEnemy", 1, false), new ThreatIncidentRead("RaidEnemy", 1, true) { CompIndex = 1 }],
        };
        facts = IncidentCycleTests.Observe(table, facts, generatorStart: 29);
        Assert.Equal(900, Assert.Single(P.ThreatSchedule.Of(table, facts).Raids).Points);
        Assert.Equal(0, P.ThreatSchedule.Of(table, IncidentCycleTests.Observe(table, facts, generatorStart: 31)).Raids.Sum(r => r.At(1)));
        Assert.Throws<P.RequiredFactException>(() => P.ThreatSchedule.Of(table, facts with { Generator = [new("RaidEnemy", 1, true)] }));
    }

    [Fact]
    public void ACategoryIndividualMtbByBiomeFiresEachThreatBigIncidentOfTheColonysBiomeAtItsMtb()
    {
        var tundra = new BiomeDef { defName = "Tundra" };
        var raid = new IncidentDef
        {
            defName = "BiomeRaid", category = ThreatBig, targetTags = [Home], workerClass = typeof(IncidentWorker_RaidEnemy),
            mtbDaysByBiome = [new MTBByBiome { biome = tundra, mtbDays = 20 }, new MTBByBiome { biome = new BiomeDef { defName = "Desert" }, mtbDays = 5 }],
        };
        var other = new IncidentDef
        {
            defName = "BiomeCluster", category = ThreatBig, targetTags = [Home], workerClass = typeof(IncidentWorker_MechCluster),
            mtbDaysByBiome = [new MTBByBiome { biome = tundra, mtbDays = 40 }],
        };
        var caravan = new IncidentDef { defName = "CaravanOnly", category = ThreatBig, targetTags = [Caravan], mtbDaysByBiome = [new MTBByBiome { biome = tundra, mtbDays = 1 }] };
        var comp = new StorytellerCompProperties_CategoryIndividualMTBByBiome { category = ThreatBig, minDaysPassed = 10 };
        var table = IncidentCycleTests.Table(Teller("Biomed", comp), raid, other, caravan);

        ThreatBiomeIncidentRead[] allowed = [new("BiomeRaid", true), new("BiomeCluster", true)];
        var tundraRate = Of(table, "Biomed", 30.0, biome: "Tundra", biomeIncidents: allowed);
        Assert.Equal((1 / 20.0) + (1 / 40.0), tundraRate.MajorThreatsPerDay, 1e-9);
        Assert.Equal((1 / 20.0) / ((1 / 20.0) + (1 / 40.0)), tundraRate.RaidFraction, 1e-9);
        Assert.Empty(tundraRate.Findings);
        Assert.Equal(1 / 5.0, Of(table, "Biomed", 30.0, biome: "Desert", biomeIncidents: allowed).MajorThreatsPerDay, 1e-9);
        Assert.Equal(0, Of(table, "Biomed", 30.0, biome: "Jungle").MajorThreatsPerDay);
        Assert.Equal(0, Of(table, "Biomed", 5.0, biome: "Tundra").MajorThreatsPerDay);

        // A fresh census owns the gate result. The normal harm-input wiring must see a blocked raid and a fireable cluster,
        // then observe the reverse on the next read, without a second worker decision or a stale catalog gate.
        var census = ColonyFactsCoreTests.Snapshot() with
        {
            Threat = new() { Observed = ColonyFactsCoreTests.QuietThreat with
            {
                Storyteller = "Biomed", DaysPassed = 30, Biome = "Tundra",
                BiomeIncidents = [new("BiomeRaid", false), new("BiomeCluster", true)],
            } },
        };
        var facts = new P.ReviewFacts { Census = census, ThreatTable = table };
        var blockedRaid = P.HarmInputs.Of(facts).Schedule;
        Assert.Equal(1 / 40.0, blockedRaid.MajorThreatsPerDay, 12);
        Assert.Equal(0, blockedRaid.RaidFraction);
        Assert.Empty(blockedRaid.Findings);
        var fresh = census with { Threat = new() { Observed = census.Threat.Observed with
        {
            BiomeIncidents = [new("BiomeRaid", true), new("BiomeCluster", false)],
        } } };
        var blockedCluster = P.HarmInputs.Of(facts with { Census = fresh }).Schedule;
        Assert.Equal(1 / 20.0, blockedCluster.MajorThreatsPerDay, 12);
        Assert.Equal(1, blockedCluster.RaidFraction);
        Assert.Empty(blockedCluster.Findings);
        var unread = census with { Threat = new() { Observed = census.Threat.Observed with { BiomeIncidents = [] } } };
        Assert.Equal("threat_biome_incident_unobserved:BiomeCluster", Assert.Throws<P.RequiredFactException>(() =>
            P.HarmInputs.Of(facts with { Census = unread })).Fact);
    }

    [Fact]
    public void CompsThatDoNotTargetTheHomeMapAndNonThreatCyclesAreLeftOut()
    {
        var beacon = Cycle(0, 1, 0, 1, 1);
        beacon.allowedTargetTags = [RaidBeacon];
        var caravanMtb = new StorytellerCompProperties_CategoryIndividualMTBByBiome { category = ThreatBig, allowedTargetTags = [Caravan] };
        var misc = Cycle(0, 7, 8, 1, 1);
        SetCategory(misc, Misc);
        var table = IncidentCycleTests.Table(Teller("Mixed", Cycle(0, 5, 5, 1, 1), beacon, caravanMtb, misc));

        Assert.Equal(0.1, Of(table, "Mixed", 30.0).MajorThreatsPerDay, 1e-9);
    }

    internal sealed class StorytellerCompProperties_Test : StorytellerCompProperties;

    /// <summary>The schedule of <paramref name="storyteller"/> at <paramref name="days"/> and 500 threat points, with the colony facts the comps read.</summary>
    private static P.ThreatSchedule Of(P.ThreatScheduleTable table, string storyteller, double days, double points = 500.0, double progress = 0,
        bool noTree = false, double scale = 1, string biome = "TemperateForest", ThreatIncidentRead[]? generator = null,
        ThreatBiomeIncidentRead[]? biomeIncidents = null, double horizon = 60) =>
        P.ThreatSchedule.Of(table, IncidentCycleTests.Observe(table, ColonyFactsCoreTests.QuietThreat with
        {
            Storyteller = storyteller, DaysPassed = days, RaidPoints = points,
            Biome = biome, Generator = [.. generator ?? []],
            BiomeIncidents = [.. biomeIncidents ?? []],
        }, horizon, scale: scale, progress: progress, noTree: noTree));

    private static P.ThreatScheduleTable Table() => IncidentCycleTests.Table(Cassandra(), Phoebe(), Randy());

    private static StorytellerDef Cassandra()
    {
        var cycle = Cycle(11, 4.6f, 6, 1, 2);
        cycle.minSpacingDays = 1.9f;
        cycle.forceRaidEnemyBeforeDaysPassed = 20;
        var beacon = new StorytellerCompProperties_ThreatsGenerator { allowedTargetTags = [RaidBeacon] };
        return Teller("Cassandra", new StorytellerCompProperties_ClassicIntro(), cycle, beacon, new StorytellerCompProperties_Disease { category = Misc });
    }

    private static StorytellerDef Phoebe() => Teller("Phoebe", new StorytellerCompProperties_ClassicIntro(), Cycle(13, 8, 8, 1, 1));

    private static StorytellerDef Randy()
    {
        var main = new StorytellerCompProperties_RandomMain
        {
            minDaysPassed = 1,
            mtbDays = 1.35f,
            categoryWeights =
            [
                Entry("Misc", 3.5f), Entry(nameof(IncidentCategoryDefOf.ThreatBig), 1.4f), Entry("OrbitalVisitor", 1.1f),
                Entry("FactionArrival", 2.4f), Entry("ThreatSmall", 0.6f), Entry("ShipChunkDrop", 0.22f),
            ],
        };
        return Teller("Randy", main);
    }

    private static IncidentCategoryEntry Entry(string category, float weight) =>
        new() { category = category == nameof(IncidentCategoryDefOf.ThreatBig) ? ThreatBig : new IncidentCategoryDef { defName = category }, weight = weight };

    internal static StorytellerCompProperties_OnOffCycle Cycle(float minDays, float on, float off, float min, float max)
    {
        var cycle = new StorytellerCompProperties_OnOffCycle { minDaysPassed = minDays, onDays = on, offDays = off, numIncidentsRange = new FloatRange(min, max) };
        SetCategory(cycle, ThreatBig);
        return cycle;
    }

    // The category of an OnOffCycle is a private field the XML loader fills.
    private static void SetCategory(StorytellerCompProperties_OnOffCycle cycle, IncidentCategoryDef category) =>
        typeof(StorytellerCompProperties_OnOffCycle).GetField("category", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(cycle, category);

    internal static StorytellerDef Teller(string name, params StorytellerCompProperties[] comps) => new() { defName = name, comps = [.. comps] };

    internal static DefinitionCatalog Catalog(params Def[] defs) =>
        new(
            [new ThingDef { defName = "Silver" }, new TerrainDef { defName = "Soil" }, .. defs],
            DefStatTable.Empty,
            new CatalogDerived("Silver", "Wort", 10f));
}
