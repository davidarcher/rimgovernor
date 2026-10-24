using System.Reflection;
using static RimGovernor.Brain.Domain.Fact;
using RimGovernor.Brain.Bridge;
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
        var schedule = Of(Table(), storyteller, 30.0);

        Assert.True(schedule.Assessed);
        Assert.Empty(schedule.Findings);
        Assert.InRange(schedule.MajorThreatsPerDay, 0.9 / daysPerThreat, 1.1 / daysPerThreat);
    }

    [Fact]
    public void ACycleAndARandomMainWaitForTheirMinDaysPassed()
    {
        Assert.Equal(0, Of(Table(), "Cassandra", 10.9).MajorThreatsPerDay);
        Assert.True(Of(Table(), "Cassandra", 11.0).MajorThreatsPerDay > 0);
        Assert.Equal(0, Of(Table(), "Phoebe", 12.0).MajorThreatsPerDay);
        Assert.Equal(0, Of(Table(), "Randy", 0.5).MajorThreatsPerDay);
    }

    [Fact]
    public void EveryThreatBeforeDay20IsARaidAndHalfAreAfter()
    {
        Assert.Equal(1, Of(Table(), "Cassandra", 19.9).RaidFraction);
        Assert.Equal(0.5, Of(Table(), "Cassandra", 20.0).RaidFraction);
    }

    [Fact]
    public void AnAcceptCurveScalesTheCycleByTheDaysPassed()
    {
        var cycle = Cycle(0, 10, 0, 2, 2);
        cycle.acceptFractionByDaysPassedCurve = new SimpleCurve([new CurvePoint(10, 0), new CurvePoint(30, 1)]);
        var table = Catalog(Teller("Curved", cycle)).ThreatSchedule();

        Assert.Equal(0, Of(table, "Curved", 5.0).MajorThreatsPerDay);
        Assert.Equal(0.1, Of(table, "Curved", 20.0).MajorThreatsPerDay, 1e-9);
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
        Assert.Equal(["storyteller_unobserved"], P.ThreatSchedule.Unobserved("storyteller_unobserved").Findings);
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
        var table = Catalog(Teller("Assembled", new StorytellerCompProperties_ClassicIntro(), cycle, main, mtb, raid, visit, beacon,
            new StorytellerCompProperties_Disease { category = Misc })).ThreatSchedule();

        var schedule = Of(table, "Assembled", 30.0);

        Assert.True(schedule.Assessed);
        Assert.Empty(schedule.Findings);
        Assert.Equal((0.1 * 0.5) + 0.25 + (1 / 15.0) + 0.05, schedule.MajorThreatsPerDay, 9);
        // The threat-points curve reads the observed raid points: at 1000 its accept is 0 and the cycle adds nothing.
        Assert.Equal(0.25 + (1 / 15.0) + 0.05, P.ThreatSchedule.Of(table, "Assembled", 30.0, 1000).MajorThreatsPerDay, 9);
    }

    [Fact]
    public void ACompClassTheDecoderDoesNotKnowIsANamedFindingThatAddsNoThreats()
    {
        var table = Catalog(Teller("Modded", Cycle(0, 5, 5, 1, 1), new StorytellerCompProperties_Test())).ThreatSchedule();

        var schedule = Of(table, "Modded", 30.0);

        Assert.True(schedule.Assessed);
        Assert.Equal(0.1, schedule.MajorThreatsPerDay, 1e-9);
        Assert.Equal(["threat_comp_unmodelled:" + nameof(StorytellerCompProperties_Test)], schedule.Findings);
    }

    [Fact]
    public void AProgressScoreCycleAndAHomeMapThreatsGeneratorAreNamedAndAddNoThreats()
    {
        var cycle = Cycle(0, 5, 5, 1, 1);
        cycle.acceptPercentFactorPerProgressScoreCurve = new SimpleCurve([new CurvePoint(0, 1), new CurvePoint(10, 2)]);

        var schedule = Of(Catalog(Teller("Scored", cycle, new StorytellerCompProperties_ThreatsGenerator())).ThreatSchedule(), "Scored", 30.0);

        Assert.True(schedule.Assessed);
        Assert.Equal(0, schedule.MajorThreatsPerDay);
        Assert.Equal(["threat_comp_unmodelled:" + nameof(StorytellerCompProperties_OnOffCycle) + ".acceptPercentFactorPerProgressScoreCurve,"
            + nameof(StorytellerCompProperties_ThreatsGenerator)], schedule.Findings);
    }

    [Fact]
    public void CompsThatDoNotTargetTheHomeMapAndNonThreatCyclesAreLeftOut()
    {
        var beacon = Cycle(0, 1, 0, 1, 1);
        beacon.allowedTargetTags = [RaidBeacon];
        var caravanMtb = new StorytellerCompProperties_CategoryIndividualMTBByBiome { category = ThreatBig, allowedTargetTags = [Caravan] };
        var misc = Cycle(0, 7, 8, 1, 1);
        SetCategory(misc, Misc);
        var table = Catalog(Teller("Mixed", Cycle(0, 5, 5, 1, 1), beacon, caravanMtb, misc)).ThreatSchedule();

        Assert.Equal(0.1, Of(table, "Mixed", 30.0).MajorThreatsPerDay, 1e-9);
    }

    internal sealed class StorytellerCompProperties_Test : StorytellerCompProperties;

    /// <summary>The schedule of <paramref name="storyteller"/> at <paramref name="days"/> and 500 threat points.</summary>
    private static P.ThreatSchedule Of(P.ThreatScheduleTable table, string storyteller, double days) =>
        P.ThreatSchedule.Of(table, storyteller, days, 500.0);

    private static P.ThreatScheduleTable Table() => Catalog(Cassandra(), Phoebe(), Randy()).ThreatSchedule();

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
