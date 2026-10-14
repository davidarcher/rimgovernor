using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using static RimGovernor.Brain.Policy.PolicyFunctions;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class ForecastSurvivalNeedsTests
{
    private static readonly RimGovernor.Brain.Policy.PawnId Builder = new("builder");
    private static readonly RimGovernor.Brain.Policy.PawnId Helper = new("helper");
    private static ColonyForecastRequest Frame(double foodTicks = 1500, double temperature = -20, double comfort = 0) => new()
    {
        Origin = new("test", 42), HorizonDays = 2000.0 / GameTime.TicksPerDay,
        Demand = new DerivedDemand { Work = new ForecastWorkInputs { Tick = new(42),
            Pawns = Known(EquatableArray.From(new[] {
                new WorkPawn { Id = Builder, Body = Known(new PawnBody { CellTemperatureC = Known(temperature),
                    ComfortableMinC = Known(comfort), Hypothermia = Known(0.335) }) },
                new WorkPawn { Id = Helper, Body = Known(new PawnBody { CellTemperatureC = Known(20.0),
                    ComfortableMinC = Known(0.0), Hypothermia = Known(0.0) }) },
            })) } },
        Facts = new ColonyForecastFacts { Food = new FoodSupply { Complete = Known(true),
            Consumers = [new() { Id = Builder, NutritionPerDay = Known(1.0) }, new() { Id = Helper, NutritionPerDay = Known(1.0) }],
            Stocks = [new() { Id = "food", Nutrition = Known(2 * foodTicks / GameTime.TicksPerDay),
                Holder = Known(default(RimGovernor.Brain.Policy.PawnId)), Perishable = Known(false), Eaters = [Builder, Helper] }] } },
    };
    // Work/travel estimates remain supplied. The changed inputs below are observed native-shaped facts.
    private static ForecastActivityProblem Work() => new()
    {
        Activities = [new("deliver", "site", "site", []), new("build", "site", "site", ["deliver"]), new("harvest", "site", "site", [])],
        Pawns = [new("builder", "site", Known(EquatableArray.From(new[] { new ForecastTimeWindow(200, 1000) { StartLocation = Known("site") } })), Known(1.0)),
            new("helper", "site", Known(EquatableArray.From(new[] { new ForecastTimeWindow(0, 1000) })), Known(1.0))],
        Options = [new("deliver", "helper", Known(200.0), Known(0.0)), new("build", "builder", Known(600.0), Known(0.0)),
            new("harvest", "helper", Known(900.0), Known(0.0))],
        Deadline = Known(2000.0), AlternativeLaborCost = Known(1900.0), ExpansionBudget = 4096,
    };
    private static ForecastActivityRelief[] Effects() =>
        [new("shelter", "cold/builder", "build", Known(true)), new("food", "food/builder", "harvest", Known(true))];

    [Fact]
    public void ObservedFoodStockReversesTheChoiceWithoutInventedLossWeights()
    {
        var coldFirst = CompareSurvivalActivities(Frame(), Work(), Effects());
        var foodFirst = CompareSurvivalActivities(Frame(950), Work(), Effects());
        Assert.Equal(["shelter"], coldFirst.Selected);
        Assert.Equal(["food"], foodFirst.Selected);
        Assert.False(coldFirst.SearchTruncated);
        Assert.False(coldFirst.NetBenefit.IsKnown);
        Assert.Equal("helper", coldFirst.Plan!.Allocations.Single(a => a.Activity == "deliver").Pawn);
    }

    [Fact]
    public void WarmerTemperatureOrBetterWornInsulationRemovesColdDeadline()
    {
        Assert.Equal(["food"], CompareSurvivalActivities(Frame(temperature: 20), Work(), Effects()).Selected);
        Assert.Equal(["food"], CompareSurvivalActivities(Frame(comfort: -25), Work(), Effects()).Selected);
        var warm = SurvivalNeedsOf(Frame(temperature: 20));
        Assert.False(warm.Needs.Single(n => n.Id == "cold/builder").Window.Value.Due);
    }

    [Fact]
    public void FoodAllocationIncludesOtherEatersAndRot()
    {
        var frame = Frame();
        var supply = frame.Facts.Food;
        var faster = supply with { Consumers = [supply.Consumers[0], supply.Consumers[1] with { NutritionPerDay = Known(3.0) }] };
        var needs = SurvivalNeedsOf(frame with { Facts = frame.Facts with { Food = faster } });
        Assert.Equal(750, needs.Needs.Single(n => n.Id == "food/builder").Window.Value.Ticks, 6);
        var rotting = supply with { Stocks = [supply.Stocks[0] with { Perishable = Known(true), RotTicks = Known(950L) }] };
        var result = CompareSurvivalActivities(frame with { Facts = frame.Facts with { Food = rotting } }, Work(), Effects());
        Assert.Equal(["food"], result.Selected);
    }

    [Fact]
    public void UnknownFoodAndBodyNeverBecomeAZeroDeadlineOrFreeBenefit()
    {
        var frame = Frame();
        var unknown = frame with { Facts = frame.Facts with { Food = new() },
            Demand = frame.Demand with { Work = frame.Demand.Work with { Pawns = Known(EquatableArray.From(
                frame.Demand.Work.Pawns.Value.Select(p => p with { Body = default }))) } } };
        var result = CompareSurvivalActivities(unknown, Work(), Effects());
        Assert.Empty(result.Selected);
        Assert.Contains("unknown_survival_food", result.Findings);
        Assert.Contains("unknown_need_window:cold/builder", result.Findings);
    }

    [Fact]
    public void FinishingRoofDoesNotAutomaticallyProveThermalRelief()
    {
        var effects = Effects();
        effects[0] = effects[0] with { PreventsShortfall = default };
        var result = CompareSurvivalActivities(Frame(), Work(), effects);
        Assert.Equal(["food"], result.Selected);
        Assert.Contains("unknown_relief_effect:shelter", result.Findings);
    }

    [Fact]
    public void LaborCeilingAndUnknownCeilingCanRetainPostponement()
    {
        var expensive = CompareSurvivalActivities(Frame(), Work() with { AlternativeLaborCost = Known(700.0) }, Effects());
        Assert.Empty(expensive.Selected);
        Assert.Contains("labor_ceiling", expensive.Findings);
        var unknown = CompareSurvivalActivities(Frame(), Work() with { AlternativeLaborCost = default }, Effects());
        Assert.Empty(unknown.Selected);
        Assert.Contains("unknown_labor_ceiling", unknown.Findings);
    }

    [Fact]
    public void AlreadySeriousColdIsImmediateEvenInAWarmCellAndLateWorkGetsNoPreventionCredit()
    {
        var frame = Frame(temperature: 20);
        var pawns = frame.Demand.Work.Pawns.Value.Select(p => p.Id == Builder
            ? p with { Body = Known(p.Body.Value with { Hypothermia = Known(HypothermiaSerious) }) } : p);
        frame = frame with { Demand = frame.Demand with { Work = frame.Demand.Work with { Pawns = Known(EquatableArray.From(pawns)) } } };
        Assert.Equal(0, SurvivalNeedsOf(frame).Needs.Single(n => n.Id == "cold/builder").Window.Value.Ticks);
        var result = CompareSurvivalActivities(frame, Work(), Effects());
        Assert.Equal(["food"], result.Selected);
        Assert.Contains("outcome_deadline", result.Findings);
    }

    [Fact]
    public void DeadlineComparisonKeepsBudgetAndOrderingDeterministic()
    {
        var problem = Work() with { ExpansionBudget = 10 };
        var first = CompareSurvivalActivities(Frame(), problem, Effects());
        var second = CompareSurvivalActivities(Frame(), problem, Effects().Reverse().ToArray());
        Assert.True(first.SearchTruncated);
        Assert.Equal(10, first.Expansions);
        Assert.Equal(first.Selected, second.Selected);
        Assert.Equal(first.Findings, second.Findings);
    }

    [Fact]
    public void SufficientFoodAndWarmthRetainPostponement()
    {
        var result = CompareSurvivalActivities(Frame(3000, 20), Work(), Effects());
        Assert.Empty(result.Selected);
        Assert.Null(result.Plan);
    }

    [Fact]
    public void FrameAndHorizonMismatchAreRejected()
    {
        Assert.Throws<DomainException>(() => SurvivalNeedsOf(Frame() with { Origin = new("test", 43) }));
        Assert.Throws<DomainException>(() => CompareSurvivalActivities(Frame(), Work() with { Deadline = Known(100.0) }, Effects()));
    }
}
