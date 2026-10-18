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
// Cassandra about one major threat per 7.1 days, Phoebe per 16, Randy per 9.
public sealed class ThreatScheduleTests
{
    private static readonly IncidentCategoryDef ThreatBig = new() { defName = nameof(IncidentCategoryDefOf.ThreatBig) };
    private static readonly IncidentCategoryDef Misc = new() { defName = "Misc" };
    private static readonly IncidentTargetTagDef RaidBeacon = new() { defName = "Map_RaidBeacon" };
    private static readonly IncidentTargetTagDef Caravan = new() { defName = "Caravan" };

    [Theory]
    [InlineData("Cassandra", 7.1)]
    [InlineData("Phoebe", 16)]
    [InlineData("Randy", 9)]
    public void TheCoreStorytellersGiveTheDesignDocRates(string storyteller, double daysPerThreat)
    {
        var schedule = P.ThreatSchedule.Of(Table(), Known(storyteller), Known(30.0));

        Assert.Empty(schedule.Findings);
        Assert.InRange(schedule.MajorThreatsPerDay.Value, 0.9 / daysPerThreat, 1.1 / daysPerThreat);
    }

    [Fact]
    public void ACycleAndARandomMainWaitForTheirMinDaysPassed()
    {
        Assert.Equal(0, P.ThreatSchedule.Of(Table(), Known("Cassandra"), Known(10.9)).MajorThreatsPerDay.Value);
        Assert.True(P.ThreatSchedule.Of(Table(), Known("Cassandra"), Known(11.0)).MajorThreatsPerDay.Value > 0);
        Assert.Equal(0, P.ThreatSchedule.Of(Table(), Known("Phoebe"), Known(12.0)).MajorThreatsPerDay.Value);
        Assert.Equal(0, P.ThreatSchedule.Of(Table(), Known("Randy"), Known(0.5)).MajorThreatsPerDay.Value);
    }

    [Fact]
    public void EveryThreatBeforeDay20IsARaidAndHalfAreAfter()
    {
        Assert.Equal(1, P.ThreatSchedule.Of(Table(), Known("Cassandra"), Known(19.9)).RaidFraction.Value);
        Assert.Equal(0.5, P.ThreatSchedule.Of(Table(), Known("Cassandra"), Known(20.0)).RaidFraction.Value);
    }

    [Fact]
    public void AnAcceptCurveScalesTheCycleByTheDaysPassed()
    {
        var cycle = Cycle(0, 10, 0, 2, 2);
        cycle.acceptFractionByDaysPassedCurve = new SimpleCurve([new CurvePoint(10, 0), new CurvePoint(30, 1)]);
        var table = Catalog(Teller("Curved", cycle)).ThreatSchedule();

        Assert.Equal(0, P.ThreatSchedule.Of(table, Known("Curved"), Known(5.0)).MajorThreatsPerDay.Value);
        Assert.Equal(0.1, P.ThreatSchedule.Of(table, Known("Curved"), Known(20.0)).MajorThreatsPerDay.Value, 1e-9);
        Assert.Equal(0.2, P.ThreatSchedule.Of(table, Known("Curved"), Known(40.0)).MajorThreatsPerDay.Value, 1e-9);
    }

    [Fact]
    public void AnUnknownStorytellerOrUnobservedInputIsUnknownWithAFinding()
    {
        var table = Table();
        var ghost = P.ThreatSchedule.Of(table, Known("Ghost"), Known(30.0));

        Assert.False(ghost.MajorThreatsPerDay.IsKnown);
        Assert.False(ghost.RaidFraction.IsKnown);
        Assert.Equal(["storyteller_unknown:Ghost"], ghost.Findings);
        Assert.Equal(["storyteller_unobserved"], P.ThreatSchedule.Of(table, Unknown<string>(), Known(30.0)).Findings);
        Assert.Equal(["days_passed_unobserved"], P.ThreatSchedule.Of(table, Known("Cassandra"), Unknown<double>()).Findings);
        Assert.Equal(["storyteller_unknown:Cassandra"], P.ThreatSchedule.Of(new P.ThreatScheduleTable(), Known("Cassandra"), Known(30.0)).Findings);
    }

    [Fact]
    public void ACompClassTheDecoderDoesNotKnowMakesTheRateUnknown()
    {
        var table = Catalog(Teller("Modded", Cycle(0, 5, 5, 1, 1), new StorytellerCompProperties_Test())).ThreatSchedule();

        var schedule = P.ThreatSchedule.Of(table, Known("Modded"), Known(30.0));

        Assert.False(schedule.MajorThreatsPerDay.IsKnown);
        Assert.Equal(["threat_comp_unmodelled:" + nameof(StorytellerCompProperties_Test)], schedule.Findings);
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

        Assert.Equal(0.1, P.ThreatSchedule.Of(table, Known("Mixed"), Known(30.0)).MajorThreatsPerDay.Value, 1e-9);
    }

    [Fact]
    public void ACycleThatScalesByThreatPointsIsUnmodelled()
    {
        var cycle = Cycle(0, 5, 5, 1, 1);
        cycle.acceptPercentFactorPerThreatPointsCurve = new SimpleCurve([new CurvePoint(0, 1), new CurvePoint(1000, 0)]);

        var schedule = P.ThreatSchedule.Of(Catalog(Teller("Scaled", cycle)).ThreatSchedule(), Known("Scaled"), Known(30.0));

        Assert.False(schedule.MajorThreatsPerDay.IsKnown);
    }

    private sealed class StorytellerCompProperties_Test : StorytellerCompProperties;

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

    private static StorytellerCompProperties_OnOffCycle Cycle(float minDays, float on, float off, float min, float max)
    {
        var cycle = new StorytellerCompProperties_OnOffCycle { minDaysPassed = minDays, onDays = on, offDays = off, numIncidentsRange = new FloatRange(min, max) };
        SetCategory(cycle, ThreatBig);
        return cycle;
    }

    // The category of an OnOffCycle is a private field the XML loader fills.
    private static void SetCategory(StorytellerCompProperties_OnOffCycle cycle, IncidentCategoryDef category) =>
        typeof(StorytellerCompProperties_OnOffCycle).GetField("category", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(cycle, category);

    private static StorytellerDef Teller(string name, params StorytellerCompProperties[] comps) => new() { defName = name, comps = [.. comps] };

    private static DefinitionCatalog Catalog(params Def[] defs) =>
        new(
            [new ThingDef { defName = "Silver" }, new TerrainDef { defName = "Soil" }, .. defs],
            DefStatTable.Empty,
            new CatalogDerived("Silver", "Wort", 10f));
}
