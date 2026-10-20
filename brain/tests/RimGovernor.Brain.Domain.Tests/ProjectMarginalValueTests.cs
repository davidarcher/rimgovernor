using System.Diagnostics;
using RimGovernor.Brain.Policy;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

// #3531: the marginal value of project progress by perturbation, and the tier-1 investment credit it feeds. The devilstrand chain of
// ProjectGraphTests (research, sow, grow, harvest, craft the duster) is priced by the harm model: every hour the chain shifts earlier is
// an hour more of the duster's daily harm avoided.
public sealed class ProjectMarginalValueTests(ITestOutputHelper output)
{
    private const double Hour = GameTime.TicksPerHour;

    // Anna researches and Bo sows and harvests; both craft. A pawn has 14.1 free hours a day, so an hour of work is 1 / 14.1 of a day.
    private const double HourInDays = 1 / 14.1;

    private static readonly string[] Wearers = ["Anna", "Bo", "Cy", "Di"];

    private static GearOption BillDuster() => ForecastValueTests.Duster(0.8) with
    {
        ID = "bill/duster", Definition = new Resource("Apparel_Duster"), Stuff = new Resource(ColonyModelFixtures.Cloth), Source = GearSource.Bill,
    };

    private static ForecastCoupledContext Context(HarmInputs? harm = null, int wearers = 2)
    {
        var pawns = Wearers.Take(wearers).Select(name => ForecastValueTests.HarmPawn() with { Pawn = name }).ToArray();
        var outfits = Wearers.Take(wearers).ToDictionary(name => name, _ => ForecastValueTests.Cloth, StringComparer.Ordinal);
        return ForecastValueTests.Context(pawns[0], ForecastValueTests.HarmTables(), harm ?? ForecastValueTests.RaidDay(), outfits) with
        {
            Needs = new() { Pawns = [.. pawns] },
        };
    }

    private static ColonyModelPolicy Policy(ProjectGraph graph) => new() { MedicineTendsPerDay = Known(0.0), Projects = graph };

    private static ProjectMarginals Marginals(ColonyModelInput input, ProjectGraph graph, ForecastCoupledContext? context = null) =>
        ProjectMarginalValue.Of(input, Policy(graph), new ColonyValue(context ?? Context(), [BillDuster()], CalendarRules.YearDays, default, new ItemFacts()));

    [Fact]
    public void AnHourOfResearchOrSowingIsWorthTheDustersDailyHarmAvoidedForTheHourTheChainShiftsEarlier()
    {
        var input = ProjectGraphTests.Colony();
        var marginals = Marginals(input, ProjectGraphTests.Chain(input));

        var craft = marginals.Trajectory.Events.Single(e => e.Id == "craft:" + ColonyModelFixtures.Recipe).Day;
        Assert.True(marginals.Value.IsKnown);
        Assert.True(marginals.Value.Value > 0);
        Assert.DoesNotContain(ColonyValue.ApparelHarmUnassessed, marginals.Findings);
        // The chain's value is the duster's harm avoided a day times the days it is worn; an hour of the chain's bottleneck work moves every
        // later completion an hour of that pawn's day earlier, so it is worth that many days of the same daily harm.
        var perDay = marginals.Value.Value / (CalendarRules.YearDays - craft);
        var expected = perDay * HourInDays;
        Assert.Equal(expected, marginals.PerHour["research:Devilstrand"], expected * 0.02);
        Assert.Equal(expected, marginals.PerHour["sow:zone1"], expected * 0.02);
        Assert.Equal(marginals.PerHour["research:Devilstrand"] / (ProjectGraph.ResearchPointsPerWorkTick * Hour), marginals.PerUnit["Devilstrand"], 12);
        // No doctor census is given, so the raid tending medicine buys is unpriced: a finding, not a zero (#3635).
        Assert.Equal(["Devilstrand", "sow:zone1"], marginals.PerUnit.Keys.Order());
        Assert.Contains(ColonyValue.MedicineSkillValueUnassessed + ":pawns_unknown", marginals.Findings);
        // Growth, harvest and crafting take no tier-1 task, so they are not valued.
        Assert.DoesNotContain(marginals.PerHour.Keys, id => id.StartsWith("grow:", StringComparison.Ordinal) || id.StartsWith("harvest:", StringComparison.Ordinal) || id.StartsWith("craft:", StringComparison.Ordinal));
    }

    [Fact]
    public void APractisedSkillWithoutADoctorCensusIsUnpricedNotZero()
    {
        var input = ProjectGraphTests.Colony();
        input = input with
        {
            Pawns = Known(EquatableArray.From(new[] { SkillStockTests.Practitioner(input.Pawns.Value[0], SkillStockTests.Mechanics(0, 0)), input.Pawns.Value[1] })),
            Practice = Known(EquatableArray.From(new[] { SkillStockTests.Slot() })),
        };
        var marginals = Marginals(input, ProjectGraphTests.Chain(input).Then(ProjectGraph.Practice(input)));

        Assert.True(marginals.Trajectory.Events[^1].Levels[(int)ModelStock.Medicine] < 13);
        Assert.DoesNotContain("skill/Anna/Medicine", marginals.PerUnit.Keys);
        Assert.Contains(ColonyValue.MedicineSkillValueUnassessed + ":pawns_unknown", marginals.Findings);
        Assert.True(marginals.PerHour["research:Devilstrand"] > 0);
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
        var input = ProjectGraphTests.Colony() with { Stocks = ProjectGraphTests.Colony().Stocks with { Nutrition = Known(4.8) } };
        var context = Context();
        var starving = ProjectMarginalValue.Of(input, Policy(new ProjectGraph()), new ColonyValue(context, [], CalendarRules.YearDays, default, new ItemFacts()));
        var fed = ProjectMarginalValue.Of(ProjectGraphTests.Colony(), Policy(new ProjectGraph()), new ColonyValue(context, [], CalendarRules.YearDays, default, new ItemFacts()));

        Assert.Equal(0, fed.Value.Value);
        var died = 2 + (1 / ForecastValue.MalnutritionSeverityPerDay);
        var perPawn = CalendarRules.YearDays - died + context.Value.DeathTerminalValue;
        Assert.True(starving.Value.Value < -2 * perPawn);
        Assert.Contains(starving.Findings, f => f.StartsWith("lethal_severity_reached:Malnutrition:", StringComparison.Ordinal));
    }

    [Fact]
    public void TheCreditOfAResearchBlockRanksItAboveIdleLaborAndBelowUrgentFoodWhoseFloorHoldsFirst()
    {
        var input = ProjectGraphTests.Colony();
        var marginals = Marginals(input, ProjectGraphTests.Chain(input));
        var hour = ProjectGraph.ResearchPointsPerWorkTick * Hour;
        var context = new ForecastCoupledContext
        {
            Activities = new() { Activities = [new("research/Anna/bench", "bench", "bench", []) { ProjectProgress = new("Devilstrand", ProjectGraph.ResearchPointsPerWorkTick) }] },
            Needs = new(), Resources = ForecastValueTests.Resources(), Investment = marginals.PerUnit,
        };

        ForecastValueResult Ledger(double researchTicks) => ForecastValue.Of(
            new(new(researchTicks == 0 ? Array.Empty<ForecastActivityAllocation>() : [new("research/Anna/bench", "Anna", 0, 0, 0, researchTicks, 0, 0, researchTicks, 0, 0)], researchTicks, 0),
                Known(true), new Dictionary<string, ForecastNeedsResult>(), []),
            context, GameTime.TicksPerDay, new(new([], 0, 0), Known(true), new Dictionary<string, ForecastNeedsResult>(), []));

        ForecastConsequenceVector Vector(ForecastValueResult ledger, double foodExcess) => new(
            EquatableArray.From(Enumerable.Repeat(Known(0d), 5).ToArray()), EquatableArray.From(Enumerable.Repeat(Known(0d), 6).ToArray()),
            Known(0d), Known(0d), 0, 0, [])
        { Value = new(ledger, new()) { UrgentZero = new(true, foodExcess) } };

        var research = Ledger(Hour);
        var idle = Ledger(0);
        // One hour of the pawn's work adds exactly the project's marginal value: a gain, so a negative loss, and the ledger logs it.
        Assert.Equal(-marginals.PerHour["research:Devilstrand"], research.Investment.Value, 12);
        Assert.Equal(0, idle.Investment.Value);
        Assert.True(research.Investment.Value < -context.Value.TieResolution);
        Assert.Equal(hour * marginals.PerUnit["Devilstrand"], -research.Investment.Value, 12);

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
            Pawns = Known(EquatableArray.From(new[]
            {
                ProjectGraphTests.Pawn("Anna", 1, null), ProjectGraphTests.Pawn("Bo", null, 1), ProjectGraphTests.Pawn("Cy", 1, 1), ProjectGraphTests.Pawn("Di", 1, 1),
            })),
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
