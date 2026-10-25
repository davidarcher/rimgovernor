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
        Assert.Equal((0, 20d), (decoded.Draws[0].Cycle!.CompIndex, decoded.Draws[0].Cycle!.ForceRaidBeforeDays));
        var main = decoded.Draws[1];
        Assert.Equal(1, main.CompIndex);
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
        // Native acceptance above one retains the drawn hits; it never invents extra incidents.
        Assert.Equal(0.1, Schedule([cycle], Reads(flu: 1), progress: 5).IncidentsPerDay(30), 12);
        Assert.InRange(Schedule([cycle], Reads(flu: 1), noTree: true).IncidentsPerDay(30), 4 / 30.0, 5 / 30.0);
    }

    [Fact]
    public void ARandomMainDrawsDiseaseAmongItsCategoriesWithThreatBigForcedPastItsInterval()
    {
        var schedule = Schedule(RandomMain(4), Reads(flu: 1));
        Assert.All(RandomMainProcess.Conditions, condition => Assert.Contains(condition, schedule.Findings));

        // Before forcing, one 1000-tick native roll chooses DiseaseHuman's weight 1 of 6.5.
        var flu = Assert.Single(schedule.Incidents);
        Assert.Equal("Disease_Flu", flu.Incident);
        var first = Assert.Single(flu.Occurrences(1001.0 / 60000));
        Assert.Equal(1000.0 / 60000, first.Day);
        Assert.Equal(1000.0 / (4 * 60000) / 6.5, first.Chance, 12);
        Assert.Equal(0, schedule.IncidentsPerDay(1000.0 / 60000));
        Assert.InRange(schedule.IncidentsPerDay(30), 0, 1 / 26.0);
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

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void EachCompGatesAtItsOwnStartAndRetainsItsOwnRate(int kind)
    {
        StorytellerCompProperties Comp(float start)
        {
            StorytellerCompProperties comp = kind switch
            {
                0 => new StorytellerCompProperties_Disease { category = DiseaseTableTests.DiseaseHuman },
                1 => Mtb(30),
                2 => Single("Disease_Flu", 50),
                3 => Biome(),
                4 => Cycle(start, 5, 5, 1, 1),
                _ => RandomMain(4),
            };
            comp.minDaysPassed = start;
            return comp;
        }

        static void Gate(IncidentDef def)
        {
            if (def.defName == "Disease_Flu") def.earliestDay = 30;
        }

        // The comps start strictly after days 29.5 and 30; flu's earliestDay gate opens inclusively at day 30.
        // Today is day 20, so only the first comp may emit flu exactly 10 days from now.
        var schedule = Schedule([Comp(29.5f), Comp(30)], Reads(flu: 3, worms: 1), configure: Gate);
        var atStart = Schedule([Comp(30)], Reads(flu: 3, worms: 1),
            facts => facts with { Disease = facts.Disease with { DaysSinceSettle = 30 } }, Gate);
        var flu = schedule.Incidents.Single(i => i.Incident == "Disease_Flu");

        Assert.True(schedule.Assessed);
        Assert.Contains("disease_future_state_unprojected", schedule.Findings);
        Assert.DoesNotContain("disease_future_state_unprojected", atStart.Findings);
        if (kind == 5)
        {
            Assert.Empty(flu.Rates);
            Assert.NotEmpty(flu.Processes);
            Assert.Equal(0, flu.PerDay(10));
            Assert.Equal(10, flu.Occurrences(30).First().Day);
            Assert.All(flu.Occurrences(30), occurrence => Assert.True(occurrence.Day >= 10));
            var startedFlu = atStart.Incidents.Single(i => i.Incident == "Disease_Flu");
            Assert.Empty(startedFlu.Occurrences(1000.0 / 60000));
            Assert.Equal(1000.0 / 60000, Assert.Single(startedFlu.Occurrences(1001.0 / 60000)).Day);
            Assert.True(flu.PerDay(30) > 0);
            return;
        }
        if (kind == 4)
        {
            Assert.Empty(flu.Rates);
            Assert.NotEmpty(flu.Scheduled);
            Assert.All(flu.Scheduled, timing => Assert.True(timing.From >= 10));
            Assert.Equal(0, flu.PerDay(10));
            Assert.Equal(flu.Occurrences(30).Sum(e => e.Chance) / 30, flu.PerDay(30), 12);
            return;
        }
        var rate = Assert.Single(flu.Rates);
        Assert.Equal(10, rate.StartDay);
        Assert.Equal(atStart.Incidents.Single(i => i.Incident == "Disease_Flu").PerDay(30), rate.PerDay, 12);
        Assert.Equal(0, flu.PerDay(10));
        Assert.Equal(rate.PerDay * 20 / 30, flu.PerDay(30), 12);
        if (kind == 1)
        {
            // Reweight separately: the early comp has only worms; the late comp draws flu 3: worms 1.
            var worms = schedule.Incidents.Single(i => i.Incident == "Disease_GutWorms");
            Assert.Equal([(9.5, 1 / 30.0), (10.0, 1 / 120.0)], worms.Rates);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DayCurvesUseTheFutureStartOrTodayForAnAlreadyStartedComp(bool cycle)
    {
        StorytellerCompProperties Comp(float start)
        {
            var curve = new SimpleCurve([new Verse.CurvePoint(20, 1), new Verse.CurvePoint(40, 3)]);
            if (cycle)
            {
                var comp = Cycle(start, 5, 5, 1, 1);
                comp.acceptFractionByDaysPassedCurve = curve;
                return comp;
            }

            var mtb = Mtb(30);
            mtb.minDaysPassed = start;
            mtb.mtbDaysFactorByDaysPassedCurve = curve;
            return mtb;
        }

        var schedule = Schedule([Comp(0), Comp(30)], Reads(flu: 1));
        var rates = schedule.Incidents.Single().Rates;
        if (cycle)
        {
            Assert.Empty(rates);
            Assert.Equal(5 / 30.0, schedule.Incidents.Single().PerDay(30), 12);
            return;
        }
        Assert.Equal(2, rates.Count);
        Assert.Equal(cycle ? 0.1 : 1 / 30.0, rates[0].PerDay, 12);
        Assert.Equal(cycle ? 0.2 : 1 / 60.0, rates[1].PerDay, 12);
        Assert.Equal(schedule.Incidents.Single().PerDay(30), rates[0].PerDay + rates[1].PerDay * 20 / 30, 12);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void FutureStartsKeepTheNamedObservedPopulationVictimAndNativeGates(int gate)
    {
        var comp = Mtb(30);
        comp.minDaysPassed = 30;
        var reads = Reads(flu: 3, worms: 1);
        if (gate == 0) reads[0] = reads[0] with { Blocked = ["scenario"] };
        if (gate == 1) reads[0] = reads[0] with { Potential = 0 };
        var schedule = Schedule([comp], reads, configure: def =>
        {
            if (def.defName == "Disease_Flu")
            {
                def.earliestDay = 30;
                if (gate == 2) def.minPopulation = 4;
            }
        });

        Assert.Equal(["Disease_GutWorms"], schedule.Incidents.Select(i => i.Incident));
        Assert.Equal(["disease_future_state_unprojected"], schedule.Findings);
        Assert.Equal(1 / 30.0 * 20 / 30, schedule.IncidentsPerDay(30), 12);
    }
    private static DiseaseIncidentRead[] Reads(double flu = 0, double worms = 0) =>
    [
        new DiseaseIncidentRead("Disease_Flu", 3) { Weight = flu },
        new DiseaseIncidentRead("Disease_GutWorms", 3) { Weight = worms },
        new DiseaseIncidentRead("Disease_OrganDecay", 3),
    ];

    // The review's illness inputs for these storyteller comps, on a day-20 colony.
    private static IllnessSchedule Schedule(StorytellerCompProperties comp, DiseaseIncidentRead[] reads, Func<ThreatFacts, ThreatFacts>? with = null)
        => Schedule([comp], reads, with);

    private static IllnessSchedule Schedule(StorytellerCompProperties[] comps, DiseaseIncidentRead[] reads,
        Func<ThreatFacts, ThreatFacts>? with = null, Action<IncidentDef>? configure = null, double progress = 0, bool noTree = false)
    {
        var facts = ColonyFactsCoreTests.QuietThreat with { Disease = IllnessForecastTests.Facts(20, reads) with { Storyteller = "Modded" } };
        var catalog = Catalog(Teller(comps), configure);
        facts = with?.Invoke(facts) ?? facts;
        facts = facts with { DaysPassed = facts.Disease.DaysSinceSettle };
        var table = catalog.DiseaseTable();
        facts = facts with { Cycles = [.. comps.OfType<StorytellerCompProperties_OnOffCycle>().Select(c => IncidentCycleTests.Read(c, Array.IndexOf(comps, c), facts, progress: progress, noTree: noTree))] };
        if (comps.Any(c => c is StorytellerCompProperties_RandomMain))
        {
            var now = (int)(facts.Disease.DaysSinceSettle * 60000);
            var mains = comps.Select((c, index) => (Comp: c as StorytellerCompProperties_RandomMain, Index: index)).Where(c => c.Comp != null).ToArray();
            facts = facts with { Trade = facts.Trade with
            {
                DaysSinceSettle = facts.Disease.DaysSinceSettle,
                Clock = new(now, now, now, 60, EquatableArray.From(mains.Select(c => new RandomMainIntervalRead(c.Index, c.Comp!.minDaysPassed, 1000.0 / (c.Comp.mtbDays * 60000), (int)(c.Comp.maxThreatBigIntervalDays * 60000), 0))), []),
                Categories = EquatableArray.From(mains.SelectMany(c => new IncidentCategoryRead[]
                {
                    new(c.Index, "ThreatBig", [new(1, [new("Raid", 1, false)])]) { Weight = 2, ThreatBig = true },
                    new(c.Index, "Misc", [new(1, [new("MiscIncident", 1, false)])]) { Weight = 3.5 },
                    new(c.Index, "DiseaseHuman", [new(1, EquatableArray.From(reads.Where(r => r.Blocked.Count == 0 && r.Potential > 0 && r.Weight > 0)
                        .Select(r => new UsableIncidentWeight(r.Incident, r.Weight, false) { EarliestDay = table.Incidents[r.Incident].EarliestDay })))]) { Weight = 1 },
                })),
            } };
        }
        return HarmInputs.Of(new ReviewFacts { DiseaseTable = table, Census = ColonyFactsCoreTests.Snapshot() with { Threat = new() { Observed = facts } } }).Illness;
    }

    private static StorytellerDiseases Decode(params StorytellerCompProperties[] comps) => Catalog(Teller(comps)).DiseaseTable().Storytellers["Modded"];

    private static StorytellerDef Teller(params StorytellerCompProperties[] comps) => new() { defName = "Modded", comps = [.. comps] };

    private static DefinitionCatalog Catalog(StorytellerDef teller, Action<IncidentDef>? configure = null)
    {
        var defs = DiseaseTableTests.CoreDefs();
        foreach (var incident in defs.OfType<IncidentDef>()) configure?.Invoke(incident);
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
