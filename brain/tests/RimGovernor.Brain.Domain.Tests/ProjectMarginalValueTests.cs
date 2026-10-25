using System.Diagnostics;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

// #3531: the marginal value of project progress by perturbation, and the tier-1 investment credit it feeds. The devilstrand chain of
// ProjectGraphTests (research, sow, grow, harvest, craft the duster) is priced by the harm model: every hour the chain shifts earlier is
// an hour more of the duster's daily harm avoided.
public sealed class ProjectMarginalValueTests(ITestOutputHelper output)
{
    [Fact]
    public void MissingHungerTableFailsPricing()
    {
        var context = Context();
        var value = new ColonyValue(context with { CapacityPenalties = context.CapacityPenalties with { Malnutrition = new() } },
            [], CalendarRules.YearDays, default, new ItemFacts());
        Assert.Equal("capacity penalty table:Malnutrition", Assert.Throws<RequiredFactException>(() => value.Price(new ColonyTrajectory())).Fact);
    }

    private const double Hour = GameTime.TicksPerHour;

    // Anna researches and Bo sows and harvests; both craft. A pawn has 14.1 free hours a day, so an hour of work is 1 / 14.1 of a day.
    private const double HourInDays = 1 / 14.1;

    private static readonly string[] Wearers = ["Anna", "Bo", "Cy", "Di"];

    private static GearOption BillDuster() => ForecastValueTests.Duster(0.8) with { Origin = new GearOptionOrigin.Observed((((GearOptionOrigin.Observed)(ForecastValueTests.Duster(0.8)).Origin).Value) with { Id = "bill/duster", DefName = "Apparel_Duster", Stuff = ColonyModelFixtures.Cloth, Source = GearSource.Bill }) };

    private static ForecastCoupledContext Context(HarmInputs? harm = null, int wearers = 2)
    {
        var pawns = Wearers.Take(wearers).Select(name => ForecastValueTests.HarmPawn() with { Pawn = name }).ToArray();
        var outfits = Wearers.Take(wearers).ToDictionary(name => name, _ => ForecastValueTests.Cloth, StringComparer.Ordinal);
        return ForecastValueTests.Context(pawns[0], ForecastValueTests.HarmTables(), harm ?? ForecastValueTests.RaidDay(), outfits) with
        {
            Needs = NeedsFixtures.Empty with { Pawns = [.. pawns] },
        };
    }

    private static ColonyModelPolicy Policy(ProjectGraph graph) => new() { MedicineTendsPerDay = 0.0, Projects = graph };

    private static ProjectMarginals Marginals(ColonyModelInput input, ProjectGraph graph, ForecastCoupledContext? context = null) =>
        ProjectMarginalValue.Of(input, Policy(graph), new ColonyValue(context ?? Context(), [BillDuster()], CalendarRules.YearDays, input.Pawns, new ItemFacts()));

    [Fact]
    public void AnHourOfResearchOrSowingIsWorthTheDustersDailyHarmAvoidedForTheHourTheChainShiftsEarlier()
    {
        var input = ProjectGraphTests.Colony();
        var marginals = Marginals(input, ProjectGraphTests.Chain(input));

        var craft = marginals.Trajectory.Events.Single(e => e.Id == "craft:" + ColonyModelFixtures.Recipe).Day;
        Assert.True(double.IsFinite(marginals.Value));
        Assert.True(marginals.Value > 0);
        Assert.DoesNotContain(ColonyValue.ApparelHarmUnassessed, marginals.Findings);
        // The chain's value is the duster's harm avoided a day times the days it is worn; an hour of the chain's bottleneck work moves every
        // later completion an hour of that pawn's day earlier, so it is worth that many days of the same daily harm.
        var perDay = marginals.Value / (CalendarRules.YearDays - craft);
        var expected = perDay * HourInDays;
        Assert.Equal(expected, marginals.PerHour["research:Devilstrand"], expected * 0.02);
        Assert.Equal(expected, marginals.PerHour["sow:zone1"], expected * 0.02);
        Assert.Equal(marginals.PerHour["research:Devilstrand"] / (ProjectGraph.ResearchPointsPerWorkTick * Hour), marginals.PerUnit["Devilstrand"], 12);
        // No pawn here has a tend quality curve, so the raid tending medicine buys is unpriced: a finding, not a zero (#3635).
        Assert.Equal(["Devilstrand", "sow:zone1"], marginals.PerUnit.Keys.Order());
        Assert.Contains(ColonyValue.MedicineSkillValueUnassessed + ":Anna", marginals.Findings);
        // Growth, harvest and crafting take no tier-1 task, so they are not valued.
        Assert.DoesNotContain(marginals.PerHour.Keys, id => id.StartsWith("grow:", StringComparison.Ordinal) || id.StartsWith("harvest:", StringComparison.Ordinal) || id.StartsWith("craft:", StringComparison.Ordinal));
    }

    [Fact]
    public void ASkillOfAPawnWithoutATendQualityCurveIsUnpricedNotZero()
    {
        var input = ProjectGraphTests.Colony();
        input = input with
        {
            Pawns = EquatableArray.From(new[] { SkillStockTests.WithMedicine(input.Pawns[0], SkillStockTests.Mechanics(0, 0), "Plants"), input.Pawns[1] }),
        };
        var marginals = Marginals(input, ProjectGraphTests.Chain(input));

        Assert.DoesNotContain("skill/Anna/Medicine", marginals.PerUnit.Keys);
        Assert.Contains(ColonyValue.MedicineSkillValueUnassessed + ":Anna", marginals.Findings);
        Assert.True(marginals.PerHour["research:Devilstrand"] > 0);
    }

    [Fact]
    public void ASkillNoPriceReadsIsWorthNothingWithoutARun()
    {
        // #3664: a run per stock cost a tenth of a review; only a stock the value reads is run one level higher.
        var input = ProjectGraphTests.Colony();
        input = input with
        {
            Pawns = EquatableArray.From(new[] { SkillStockTests.WithMedicine(ProjectGraphTests.Pawn("Anna", null, null), SkillStockTests.Mechanics(3, 0), "Cooking", "Plants") }),
        };
        var valuation = new SkillStockTests.LevelDays(CalendarRules.YearDays);

        var marginals = ProjectMarginalValue.Of(input, Policy(new ProjectGraph()), valuation);

        Assert.True(marginals.PerUnit["skill/Anna/Medicine"] > 0);
        Assert.Equal(0, marginals.PerUnit["skill/Anna/Cooking"]);
        Assert.Equal(0, marginals.PerUnit["skill/Anna/Plants"]);
        // The baseline, Medicine one level higher and the colony's one medicine kind one unit more: Cooking and Plants are not run.
        Assert.Equal(3, valuation.Priced);
    }

    [Fact]
    public void EveryStockCarriesTheXpAndDaysOfItsNextLevelWhetherOrNotThePriceReadsIt()
    {
        // #3912: the work speed a level adds is priced in tier 1 for every skill, so tier 2 hands over each stock's next level and
        // the days of the horizon a raise of it lasts, not only the stocks the harm price reads.
        var input = ProjectGraphTests.Colony() with
        {
            Pawns = EquatableArray.From(new[] { SkillStockTests.WithMedicine(ProjectGraphTests.Pawn("Anna", null, null), SkillStockTests.Mechanics(3, 0), "Cooking", "Plants") }),
        };
        var policy = Policy(new ProjectGraph());

        var marginals = ProjectMarginalValue.Of(input, policy, new SkillStockTests.LevelDays(CalendarRules.YearDays));

        Assert.Equal(["skill/Anna/Cooking", "skill/Anna/Medicine", "skill/Anna/Plants"], marginals.SkillRaises.Keys);
        Assert.All(marginals.Trajectory.Skills, track =>
        {
            var raise = marginals.SkillRaises[track.Key];
            Assert.Equal(track.NextLevel is SkillNextLevel.Takes { Xp: var xp } ? xp : 0, raise.Xp);
            Assert.InRange(raise.Days, 0, policy.HorizonDays);
        });
    }
    [Fact]
    public void ColonyValueReadsADoctorsTendSkillOnlyWhileRaidsAreExpected()
    {
        var curve = new SkillStatCurve(ColonyValue.TendQualityStat, "Medicine", EquatableArray.From(new[] { new SkillStatLevel(0, 0.2) }), "test");
        var anna = ProjectGraphTests.Pawn("Anna", null, null) with { SkillStatCurves = EquatableArray.From(new[] { curve }) };
        ColonyValue Value(HarmInputs harm) => new(Context(harm, wearers: 1), [], CalendarRules.YearDays, EquatableArray.From(new[] { anna }), new ItemFacts());
        SkillTrack Track(string pawn, string skill) => new() { Key = ProjectGraph.SkillKey(pawn, skill), Pawn = pawn, Skill = skill, NextLevel = new SkillNextLevel.Top() };

        var raids = Value(ForecastValueTests.RaidDay());
        Assert.True(raids.Reads(Track("Anna", "Medicine")));
        Assert.False(raids.Reads(Track("Anna", "Cooking")));
        Assert.False(raids.Reads(Track("Bo", "Medicine")));
        Assert.False(Value(ForecastValueTests.RaidDay(raidsPerDay: 0)).Reads(Track("Anna", "Medicine")));
    }

    [Fact]
    public void ACropThatCannotMatureBeforeTheColdIsWorthNothing()
    {
        var input = ProjectGraphTests.Colony(twelfth: 8);
        var marginals = Marginals(input, ProjectGraph.CropChain(input, ColonyModelFixtures.Crop, "zone1"));

        Assert.Contains("crop_will_not_mature:grow:zone1", marginals.Findings);
        Assert.Equal(0, marginals.PerHour["sow:zone1"]);
        Assert.Equal(0, marginals.PerUnit["sow:zone1"]);
    }

    [Fact]
    public void ApparelTheHarmModelCannotPriceIsNamedAndTheChainIsWorthNothingUntilItCan()
    {
        var input = ProjectGraphTests.Colony();
        var marginals = Marginals(input, ProjectGraphTests.Chain(input), Context(new HarmInputs()));

        Assert.Contains(ColonyValue.ApparelHarmUnassessed, marginals.Findings);
        Assert.Equal(0, marginals.PerHour["research:Devilstrand"]);
    }

    [Fact]
    public void StarvationCostsTheMalnutritionStagesAndALethalOneTheRestOfTheHorizonPlusTheTerminalValue()
    {
        // 4.8 nutrition against two pawns eating 1.2 a day each is gone in two days; the hunger after rises past the lethal severity in about 2.2.
        var input = ProjectGraphTests.Colony() with { Stocks = ProjectGraphTests.Colony().Stocks with { Nutrition = 4.8 } };
        var context = Context();
        var starving = ProjectMarginalValue.Of(input, Policy(new ProjectGraph()), new ColonyValue(context, [], CalendarRules.YearDays, input.Pawns, new ItemFacts()));
        var fed = ProjectMarginalValue.Of(ProjectGraphTests.Colony(), Policy(new ProjectGraph()), new ColonyValue(context, [], CalendarRules.YearDays, input.Pawns, new ItemFacts()));

        Assert.Equal(0, fed.Value);
        var died = 2 + (1 / ForecastValue.MalnutritionSeverityPerDay);
        var perPawn = CalendarRules.YearDays - died + context.Value.DeathTerminalValue;
        Assert.True(starving.Value < -2 * perPawn);
        Assert.Contains(starving.Findings, f => f.StartsWith("lethal_severity_reached:Malnutrition:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0.8)]
    [InlineData(1.2)]
    public void ColonyStarvationReachesLethalSeverityAtTheObservedPawnRate(double factor)
    {
        var pawn = ForecastValueTests.Pawn() with { MalnutritionRateFactor = factor };
        var context = ForecastValueTests.Context(pawn, ForecastValueTests.Tables() with
        {
            Malnutrition = new() { Known = true, Hediff = "Malnutrition", Lethality = new Lethality.Lethal(1), Stages = [new(0, 0, [])] },
        });
        var trajectory = new ColonyTrajectory { Starvation = [new() { Start = 0, End = 4 }] };
        var value = new ColonyValue(context, [], 4, [], new ItemFacts()).Price(trajectory);
        Assert.Equal(-(4 - 1 / (0.453 * factor) + context.Value.DeathTerminalValue), value.Value, 9);
    }

    [Fact]
    public void TheCreditOfAResearchBlockRanksItAboveIdleLaborAndBelowUrgentFoodWhoseFloorHoldsFirst()
    {
        var input = ProjectGraphTests.Colony();
        var marginals = Marginals(input, ProjectGraphTests.Chain(input));
        var hour = ProjectGraph.ResearchPointsPerWorkTick * Hour;
        var context = new ForecastCoupledContext
        {
            Activities = new() { Deadline = 0, Activities = [new("research/Anna/bench", "bench", "bench", []) { ProjectProgress = new("Devilstrand", ProjectGraph.ResearchPointsPerWorkTick) }] },
            Needs = NeedsFixtures.Empty, Resources = ForecastValueTests.Resources(), Investment = marginals.PerUnit,
        };

        ForecastValueResult Ledger(double researchTicks) => ForecastValue.Of(
            new(new(researchTicks == 0 ? Array.Empty<ForecastActivityAllocation>() : [new("research/Anna/bench", "Anna", 0, 0, 0, researchTicks, 0, researchTicks, 0, 0)], researchTicks, 0),
                true, new Dictionary<string, ForecastNeedsResult>(), []),
            context, GameTime.TicksPerDay, new(new([], 0, 0), true, new Dictionary<string, ForecastNeedsResult>(), []));

        ForecastConsequenceVector Vector(ForecastValueResult ledger, double foodExcess) => new(
            EquatableArray.From(Enumerable.Repeat<ForecastTerm>(new ForecastTerm.Assessed(0d), 5).ToArray()), EquatableArray.From(Enumerable.Repeat<ForecastTerm>(new ForecastTerm.Assessed(0d), 6).ToArray()),
            new ForecastTerm.Assessed(0d), new ForecastTerm.Assessed(0d), 0, [])
        { Value = new(ledger, new()) { UrgentZero = new(true, foodExcess) } };

        var research = Ledger(Hour);
        var idle = Ledger(0);
        // One hour of the pawn's work adds exactly the project's marginal value: a gain, so a negative loss, and the ledger logs it.
        Assert.Equal(-marginals.PerHour["research:Devilstrand"], research.Investment, 12);
        Assert.Equal(0, idle.Investment);
        Assert.True(research.Investment < -context.Value.TieResolution);
        Assert.Equal(hour * marginals.PerUnit["Devilstrand"], -research.Investment, 12);

        Assert.True(ForecastCoupledSelection.CompareConsequences(Vector(research, 0), Vector(idle, 0)) < 0);
        // Food that would reach zero outranks the credit whatever it is worth: the floors are read first.
        var food = Vector(idle, 0);
        var starved = Vector(research, 600);
        Assert.True(ForecastCoupledSelection.CompareConsequences(food, starved) < 0);
    }

    [Fact]
    public void TierTwoOnFourWorkersRunsWellInsideItsBudget()
    {
        var input = ProjectGraphTests.Colony();
        input = input with
        {
            Pawns = EquatableArray.From(new[]
            {
                ProjectGraphTests.Pawn("Anna", 1, null), ProjectGraphTests.Pawn("Bo", null, 1), ProjectGraphTests.Pawn("Cy", 1, 1), ProjectGraphTests.Pawn("Di", 1, 1),
            }),
        };
        var graph = ProjectGraphTests.Chain(input);
        var context = Context(wearers: 4);

        // One run warms the code; the measured one is what a review pays (tier 2 runs once per review).
        Marginals(input, graph, context);
        var clock = Stopwatch.StartNew();
        var marginals = Marginals(input, graph, context);
        clock.Stop();

        output.WriteLine($"tier 2, four workers, {graph.Nodes.Count} nodes, {marginals.PerHour.Count} valued, {marginals.Trajectory.Events.Count} events: {clock.Elapsed.TotalMilliseconds:0.0} ms");
        Assert.Equal(2, marginals.PerHour.Count);
        Assert.False(marginals.Trajectory.Truncated);
        // A hang guard, not the budget: the measured time above is what the landing report carries.
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"tier 2 took {clock.Elapsed.TotalMilliseconds:0} ms");
    }
}
