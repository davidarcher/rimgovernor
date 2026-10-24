using System.Diagnostics;
using RimGovernor.Brain.Policy;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

// #3529: the tier-2 colony model's stock-flow event loop.
public sealed class ColonyModelTests
{
    // Four workers, each falling 0.00002 nutrition a tick: 1.2 a day each, 4.8 a day together.
    private const double ColonyConsumption = 4.8;

    private static ModelPawn Worker(string name) => new()
    {
        Pawn = name,
        Needs = EquatableArray.From(new[]
        {
            new ModelNeed { Kind = "rest", Fall = 0.00001, Gain = 0.00002, Threshold = 0.28 },
            new ModelNeed { Kind = "food", Fall = 0.00002, Threshold = 0.3, GainPerUse = Known(0.6), ServiceTicks = Known(400.0), ApproachTicks = Known(100.0), Gain = 0 },
            new ModelNeed { Kind = "joy", Fall = 0.00001, Gain = 0.00015, Threshold = 0.3 },
        }),
        Skills = Known(EquatableArray.From(new[] { new WorkSkill { Name = "Plants", Level = 10 } })),
        ComfortableMin = Known(16.0),
        ComfortableMax = Known(26.0),
    };

    private static readonly string[] Names = ["Anna", "Bo", "Cy", "Di"];

    private static ColonyModelInput Colony(double nutrition)
    {
        var input = ColonyModelFixtures.Devilstrand();
        var pawns = EquatableArray.From(Names.Select(Worker));
        return input with
        {
            Pawns = Known(pawns),
            Stocks = new ModelStocks { Nutrition = Known(nutrition), Materials = Known(EquatableArray.From(new[] { new Amount(new Resource("Steel"), 120) })), Medicine = Known(EquatableArray.From(new[] { new Amount(new Resource("MedicineHerbal"), 13) })) },
        };
    }

    private static PawnCycle[][] Cycles(ColonyModelInput input) => [.. input.Pawns.Value.Select(p => PawnCycle.Year(input, p).ToArray())];

    private static ColonyTrajectory Run(ColonyModelInput input, ColonyModelPolicy? policy = null) => ColonyModel.Run(input, Cycles(input), policy ?? new ColonyModelPolicy { MedicineTendsPerDay = Known(0.0) });

    private static double Lead(ColonyModelInput input) =>
        CalendarRules.HarvestGapDays(input.Calendar, Known(EquatableArray.From(Array.Empty<DisasterCondition>()))).Value;

    [Fact]
    public void BetweenEventsEachStockChangesByItsIntegratedFlow()
    {
        var input = Colony(1_000);
        var policy = new ColonyModelPolicy
        {
            MedicineTendsPerDay = Known(0.1),
            FixedProduction = EquatableArray.From(new[] { 2.0, 1.5, 0, 3 }),
            Scheduled = EquatableArray.From(new[] { new ScheduledEffect { Day = 7, Kind = ModelEventKind.Harvest, Id = "h", Stock = ModelStock.Nutrition, Amount = 40 } }),
        };
        var rates = new[] { 2.0 - ColonyConsumption, 1.5, -0.1, 3 };

        var trajectory = Run(input, policy);

        Assert.False(trajectory.Truncated);
        var previous = (Day: 0.0, Levels: new[] { 1_000.0, 120, 13, 0 });
        foreach (var e in trajectory.Events)
        {
            for (var s = 0; s < rates.Length; s++)
            {
                var lump = e.Kind == ModelEventKind.Harvest && s == (int)ModelStock.Nutrition ? 40 : 0;
                Assert.Equal(previous.Levels[s] + rates[s] * (e.Day - previous.Day) + lump, e.Levels[s], 6);
            }

            previous = (e.Day, [.. e.Levels]);
        }

        Assert.Equal(60, previous.Day);
    }

    [Fact]
    public void SimultaneousEventsFireByKindThenIdWhateverTheInputOrder()
    {
        var input = Colony(1_000);
        ScheduledEffect Effect(ModelEventKind kind, string id) => new() { Day = 7, Kind = kind, Id = id, Stock = ModelStock.Materials, Amount = 1 };
        var effects = new[] { Effect(ModelEventKind.Harvest, "b"), Effect(ModelEventKind.ProjectCompletion, "z"), Effect(ModelEventKind.Harvest, "a") };

        var forward = Run(input, new ColonyModelPolicy { MedicineTendsPerDay = Known(0.0), Scheduled = EquatableArray.From(effects) });
        var reversed = Run(input, new ColonyModelPolicy { MedicineTendsPerDay = Known(0.0), Scheduled = EquatableArray.From(effects.Reverse()) });

        Assert.Equal(forward, reversed);
        Assert.Equal(forward, Run(input, new ColonyModelPolicy { MedicineTendsPerDay = Known(0.0), Scheduled = EquatableArray.From(effects) }));
        Assert.Equal(["z", "a", "b"], forward.Events.Where(e => e.Day == 7).Select(e => e.Id));
    }

    [Fact]
    public void ASixtyDayRunOfFourWorkersIsAFewEventsAndFast()
    {
        var input = Colony(100);
        var cycles = Cycles(input);
        var policy = new ColonyModelPolicy { MedicineTendsPerDay = Known(0.0) };
        ColonyModel.Run(input, cycles, policy);

        var clock = Stopwatch.StartNew();
        var trajectory = ColonyModel.Run(input, cycles, policy);
        clock.Stop();

        Assert.True(trajectory.Events.Count < 4_096);
        Assert.False(trajectory.Truncated);
        Assert.Equal(ModelEventKind.HorizonEnd, trajectory.Events[^1].Kind);
        Assert.True(clock.Elapsed < TimeSpan.FromMilliseconds(10), $"{clock.Elapsed.TotalMilliseconds} ms");
    }

    [Fact]
    public void TheRunwayIsTheStockOverConsumptionLessTheLeadDays()
    {
        var input = Colony(100);
        var lead = Lead(input);
        Assert.InRange(lead, 0, 15);

        var trajectory = Run(input);

        Assert.Equal(100 / ColonyConsumption - lead, trajectory.FoodRunwayDay!.Value, 1);
        var runway = trajectory.Events.Single(e => e.Kind == ModelEventKind.FoodRunway);
        Assert.Equal(trajectory.FoodRunwayDay!.Value, runway.Day, 9);
        Assert.Equal(ColonyConsumption * lead, runway.Levels[(int)ModelStock.Nutrition], 6);
    }

    [Fact]
    public void ARunPastTheEventBudgetIsTruncatedWithItsPartialTrajectory()
    {
        var trajectory = Run(Colony(1_000), new ColonyModelPolicy { MedicineTendsPerDay = Known(0.0), MaxEvents = 3 });

        Assert.True(trajectory.Truncated);
        Assert.Equal(3, trajectory.Events.Count);
        Assert.DoesNotContain(trajectory.Events, e => e.Kind == ModelEventKind.HorizonEnd);
    }

    [Fact]
    public void ANutritionStockAtZeroStarvesTheColonyForTheRestOfTheHorizon()
    {
        var trajectory = Run(Colony(4.8));

        var zero = trajectory.Events.Single(e => e.Kind == ModelEventKind.StockZero);
        Assert.Equal(1.0, zero.Day, 9);
        Assert.Equal(nameof(ModelStock.Nutrition), zero.Id);
        var span = Assert.Single(trajectory.Starvation);
        Assert.Equal(1.0, span.Start, 9);
        Assert.Equal(60.0, span.End, 9);
    }

    [Fact]
    public void ProductionAboveConsumptionNeverStarvesOrCrossesTheRunway()
    {
        var trajectory = Run(Colony(10), new ColonyModelPolicy { MedicineTendsPerDay = Known(0.0), FixedProduction = EquatableArray.From(new[] { ColonyConsumption + 1 }) });

        Assert.Empty(trajectory.Starvation);
        Assert.Null(trajectory.FoodRunwayDay);
    }

    [Fact]
    public void UnknownRatesAreReportedNeverDefaulted()
    {
        var input = Colony(100);
        var noFall = input with { Pawns = Known(EquatableArray.From(input.Pawns.Value.Select((p, i) => i == 0 ? p with { Needs = default } : p))) };

        var trajectory = Run(noFall, new ColonyModelPolicy());

        Assert.Contains("medicine_tend_rate_unknown", trajectory.Findings);
        Assert.Contains("food_fall_unknown:Anna", trajectory.Findings);
        Assert.Contains(ColonyTrajectory.MoodUnassessedFinding, trajectory.Findings);
        Assert.Equal(100 / 3.6, trajectory.Events.Single(e => e.Kind == ModelEventKind.StockZero).Day, 9);
    }

    [Fact]
    public void AnInputWithoutPawnsRunsNothing()
    {
        var trajectory = ColonyModel.Run(new ColonyModelInput(), [], new ColonyModelPolicy());

        Assert.Empty(trajectory.Events);
        Assert.Contains("colony_model_input_unknown", trajectory.Findings);
    }
}
