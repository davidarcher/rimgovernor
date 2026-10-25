using RimGovernor.Brain.Host;
using RimGovernor.Brain.Observation;
using RimGovernor.Brain.PlayerApi;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// Goal ETA and marginal value from the tier-2 colony model (#3505), on the devilstrand worked case of the forecast note: a worn-duster
/// goal is a terminal of the research, sow, grow, harvest and craft chain.
/// </summary>
public sealed class GoalEtaTests
{
    private const string Duster = "Apparel_Duster";

    private static readonly string[] Wearers = ["Anna", "Bo"];

    private static GoalRecord Worn(Importance importance = Importance.Normal) => new() { Id = 7, Kind = GoalKind.WornCount, Target = Duster, Qualifier = ColonyModelFixtures.Cloth, Count = 1, Importance = importance };

    private static GoalsFacts Facts(params GoalRecord[] goals) => new()
    {
        Goals = goals,
        Stock = [],
        Rounds = new RoundsFacts { Gear = Known(ColonyModelFixtures.NoGear() with { Pawns = [new GearPawn { Pawn = new("Anna"), LoadoutModel = new GearModel.Modeled(new GearLoadoutInput { Threat = ColonyModelFixtures.NoThreat, Worn = [] }) }] }) },
        StuffResearch = new Dictionary<string, IReadOnlyList<string>> { [ColonyModelFixtures.Cloth] = [ColonyModelFixtures.Project] },
    };

    private static ForecastCoupledContext Context()
    {
        var pawns = Wearers.Select(name => ForecastValueTests.HarmPawn() with { Pawn = name }).ToArray();
        var outfits = Wearers.ToDictionary(name => name, _ => ForecastValueTests.Cloth, StringComparer.Ordinal);
        return ForecastValueTests.Context(pawns[0], ForecastValueTests.HarmTables(), ForecastValueTests.RaidDay(), outfits) with { Needs = NeedsFixtures.Empty with { Pawns = [.. pawns] } };
    }

    private static GearOption BillDuster() => ForecastValueTests.Duster(0.8) with
    {
        ID = "bill/duster", Definition = new Resource(Duster), Stuff = new Resource(ColonyModelFixtures.Cloth), Source = GearSource.Bill,
    };

    private static ProjectMarginals Run(ColonyModelInput input, GoalPath path) => ProjectMarginalValue.Of(
        input, new ColonyModelPolicy { MedicineTendsPerDay = 0.0, Projects = path.Graph },
        new ColonyValue(Context(), [BillDuster()], CalendarRules.YearDays, default, new ItemFacts()), [path.Terminal!]);

    // The completion day of the ProjectGraphTests hand calculation: 600 research points left, 12 cells of 400 sow work, growth to 0.65 of
    // 22.5 days through the 12 C and 8 C twelfths, 12 cells of 200 harvest work in 9.5 daylight hours, 10,000 work of tailoring by two pawns.
    private static double WorkedTableCraftDay()
    {
        const double FreeHours = 14.1;
        const double WorkTicksPerDay = FreeHours * 2500;
        const double FullGrowth = 0.55 / 22.5;
        var research = 600 / (WorkTicksPerDay * 0.00825);
        var sow = research + (4800 / WorkTicksPerDay);
        var grown = ((20.2 - sow) * FullGrowth) + (5 * FullGrowth * 0.8);
        var grow = 25.2 + ((0.65 - grown) / FullGrowth);
        var harvest = grow + (2400 / (9.5 * 2500));
        return harvest + (10_000 / (2 * WorkTicksPerDay));
    }

    private static GoalPath DevilstrandPath(ColonyModelInput input, GoalRecord? goal = null) => GoalPaths.Of(goal ?? Worn(), Facts(goal ?? Worn()), input);

    [Fact]
    public void GoalNotCompletedInTheRunHasNoCompletionDate()
    {
        var input = ProjectGraphTests.Colony();
        var path = DevilstrandPath(input);
        var value = new ColonyValue(Context(), [BillDuster()], 1, default, new ItemFacts());
        var marginals = ProjectMarginalValue.Of(input,
            new ColonyModelPolicy { MedicineTendsPerDay = 0, HorizonDays = 1, Projects = path.Graph }, value, [path.Terminal!]);
        var projection = marginals.Goals[path.Terminal!.Goal];
        Assert.IsType<ModelEventTime.NotReached>(projection.CompletionDay);
        Assert.False(projection.Priced);
        Assert.Equal(0, value.GoalValue(path.Terminal, projection.CompletionDay));
    }

    [Fact]
    public void ADevilstrandDusterGoalReportsTheDayTheTailoringFinishesAfterResearchSowingAndGrowth()
    {
        var input = ProjectGraphTests.Colony();
        var path = DevilstrandPath(input);

        Assert.Equal("", path.Unmodelled);
        Assert.Equal(["research:Devilstrand", "sow:zone1", "grow:zone1", "harvest:zone1", "craft:Make_Apparel_Duster"], path.Graph.Nodes.Select(n => n.Id));
        Assert.Equal(["craft:Make_Apparel_Duster"], path.Terminal!.Nodes);

        var marginals = Run(input, path);
        var projection = marginals.Goals[path.Terminal.Goal];

        Assert.Equal(WorkedTableCraftDay(), Assert.IsType<ModelEventTime.AtDay>(projection.CompletionDay).Day, 6);
        Assert.InRange(Assert.IsType<ModelEventTime.AtDay>(projection.CompletionDay).Day, 29, 31);
        Assert.True(projection.Priced);
    }

    [Fact]
    public void AnHourOfResearchOrSowingIsWorthWhatItAdvancesTheGoalByAndTheNodesBehindThemAreNot()
    {
        var input = ProjectGraphTests.Colony();
        var path = DevilstrandPath(input);
        var projection = Run(input, path).Goals[path.Terminal!.Goal];

        // The goal stands from its completion to the horizon, so each day earlier is worth its worth over the horizon.
        var perDay = 2.0 / CalendarRules.YearDays;
        var hourInDays = 1 / 14.1;
        Assert.Equal(["Devilstrand", "sow:zone1"], projection.PerUnit.Keys.Order());
        Assert.Equal(perDay * hourInDays, projection.PerUnit["Devilstrand"] * ProjectGraph.ResearchPointsPerWorkTick * GameTime.TicksPerHour, perDay * hourInDays * 0.02);
        // An hour of sowing is 2,500 of 4,800 work: 12 cells x 2,500 / 4,800 cells.
        Assert.Equal(perDay * hourInDays, projection.PerUnit["sow:zone1"] * GameTime.TicksPerHour * 12 / 4800, perDay * hourInDays * 0.05);
    }

    [Fact]
    public void ADeadlineJustBeforeTheEtaRaisesTheValueOfResearchHoursToday()
    {
        var input = ProjectGraphTests.Colony();
        double Research(int? deadline)
        {
            var goal = Worn() with { DeadlineDay = GoalDeadline.Of(deadline) };
            var path = DevilstrandPath(input, goal);
            return Run(input, path).Goals[path.Terminal!.Goal].PerUnit["Devilstrand"];
        }

        var open = Research(null);
        var late = Research(27);
        var onTime = Research(40);

        // On track for its deadline an hour only moves the goal along; once the goal would miss it every day earlier also saves the decay.
        Assert.Equal(open, onTime, 12);
        Assert.True(late > open * 1.5, $"{late} against {open}");
        Assert.True(Research(0) > 0);
    }

    [Fact]
    public void AGoalWithNoModelledPathKeepsItsFlatValueAndHasNoEta()
    {
        var input = ProjectGraphTests.Colony();
        var stock = new GoalRecord { Id = 8, Kind = GoalKind.StockCount, Target = "Steel", Count = 500 };
        var noRecipe = Worn() with { Target = "Apparel_Parka" };
        var noStuff = Worn() with { Qualifier = "" };

        Assert.Equal("no_tier2_path:stock-count", GoalPaths.Of(stock, Facts(stock), input).Unmodelled);
        Assert.Equal("no_recipe:Apparel_Parka", GoalPaths.Of(noRecipe, Facts(noRecipe), input).Unmodelled);
        Assert.Equal("stuff_unnamed", GoalPaths.Of(noStuff, Facts(noStuff), input).Unmodelled);
        Assert.Null(GoalPaths.Of(stock, Facts(stock), input).Terminal);

        // Tier 2 prices nothing it has no terminal for: the flat share stands.
        var flat = new ForecastGoalValue("goals/research-project/1", Importance.Normal, new GoalDeadline.Open()) { Research = [new("Smithing", 0.001)] };
        Assert.Equal(flat, Assert.Single(GoalRules.Priced([flat], new Dictionary<string, GoalProjection>())));
        Assert.Equal(flat, Assert.Single(GoalRules.Priced([flat], new Dictionary<string, GoalProjection> { [flat.Goal] = new() })));

        var status = GoalRules.Evaluate(stock, Facts(stock));
        Assert.IsType<GoalEta.NoEstimate>(status.Eta);
    }

    [Fact]
    public void AGoalTierTwoPricedIsPaidPerUnitOfProgressInsteadOfItsFlatShare()
    {
        var flat = new ForecastGoalValue("goals/research-project/1", Importance.Normal, new GoalDeadline.Open()) { Research = [new("Devilstrand", 1.0 / 600)] };
        var projection = new GoalProjection { Priced = true, PerUnit = new Dictionary<string, double> { ["Devilstrand"] = 0.0004 } };
        var priced = Assert.Single(GoalRules.Priced([flat], new Dictionary<string, GoalProjection> { [flat.Goal] = projection }));

        Assert.True(priced.Priced);
        Assert.Equal(new ForecastGoalUnit("Devilstrand", 0.0004), Assert.Single(priced.Units));

        var context = new ForecastCoupledContext
        {
            Activities = new() { Deadline = 0, Activities = [new("research/Anna/bench", "bench", "bench", []) { ProjectProgress = new("Devilstrand", 1) }] },
            Needs = NeedsFixtures.Empty, Resources = ForecastValueTests.Resources(),
        };
        ForecastValueResult Ledger(ForecastGoalValue goal) => ForecastValue.Of(
            new(new([new("research/Anna/bench", "Anna", 0, 0, 0, 2500, 0, 0, 2500, 0, 0)], 2500, 0), true, new Dictionary<string, ForecastNeedsResult>(), []),
            context with { GoalValues = [goal] }, GameTime.TicksPerDay, new(new([], 0, 0), true, new Dictionary<string, ForecastNeedsResult>(), []));

        // The flat share of 2,500 of 600 points caps at the whole goal's worth; the priced goal pays 2,500 points at its marginal value.
        Assert.Equal(-2.0, Ledger(flat).Goals, 9);
        Assert.Equal(-1.0, Ledger(priced).Goals, 9);
    }

    [Fact]
    public void AGoalsEstimatedDayIsDerivedIntoItsStatusAndShownOnThePursuingRow()
    {
        var goal = new GoalRecord { Id = 3, Kind = GoalKind.ResearchProject, Target = "Smithing" };
        var facts = new GoalsFacts
        {
            Goals = [goal],
            Research = new ResearchFacts { Projects = [new("Smithing")], Finished = [] },
            Tier = Known(TechTier.Camp),
            Etas = new Dictionary<string, int> { [GoalRules.NeedOf(goal).Value] = 45 },
        };

        var status = GoalRules.Evaluate(goal, facts);
        Assert.Equal(GoalEta.On(45), status.Eta);
        Assert.IsType<GoalEta.NoEstimate>(GoalRules.Evaluate(goal, facts with { Etas = new Dictionary<string, int>() }).Eta);

        var record = new GoalsRecord { World = new(new("colony"), new("load"), new(1)) }.WithNewGoal(goal);
        var stored = Assert.Single(record.Goals);
        var row = GoalsViewModel.From(new GoalsSnapshot(record, [status with { Goal = stored }], [])).Groups.Single().Rows.Single();
        Assert.Equal(("Pursuing", "economy/research, est. day 45"), (row.State, row.Detail));
    }
}
