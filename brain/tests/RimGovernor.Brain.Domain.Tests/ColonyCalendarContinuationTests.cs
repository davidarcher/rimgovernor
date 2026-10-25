using O = RimGovernor.Contracts.Wire.Observations;
using RimGovernor.Contracts.Wire;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class ColonyCalendarContinuationTests
{
    [Fact]
    public void TheReviewFramePassesItsNativeClimateDirectlyIntoTheModel()
    {
        var reading = ColonyModelFixtures.Reading();
        var input = ColonyModelFixtures.ReadyOf(reading);

        Assert.Same(reading.Snapshot.FoodClimate, reading.Facts.Calendar);
        Assert.Same(reading.Snapshot.FoodClimate, input.Calendar);
    }

    private static ColonyModelInput Input(bool[] year, double nutrition) => ColonyModelFixtures.Devilstrand() with
    {
        Calendar = new O.FoodClimate
        {
            DayOfYear = 58, Season = "Winter", GrowingYear = EquatableArray.From(year),
            GrowingDays = year.Count(day => day), GrowingDaysRemaining = year.TakeWhile(day => day).Count(),
            GrowingDaysUntil = year.TakeWhile(day => !day).Count(),
            NonGrowingDays = year.SkipWhile(day => day).TakeWhile(day => !day).Count(),
            SowingNow = year.Length > 0 && year[0],
        },
        Climate = ColonyModelFixtures.Climate() with { CurrentTwelfth = 11, TicksToNextTwelfth = 2 * GameTime.TicksPerDay },
        Pawns = EquatableArray.From(new[] { new ModelPawn
        {
            Pawn = "Anna", Needs = EquatableArray.From(new[] { new ModelNeed { Kind = "food", Fall = 1d / GameTime.TicksPerDay, Gain = 0, Threshold = .3, Service = new ModelService.NoneObserved() } }),
            Skills = EquatableArray.From<O.Skill>([]),
            Consciousness = 1.0,
            Moving = 1.0,
            Manipulation = 1.0,
            Sight = 1.0,
            Talking = 1.0,
            ComfortableMin = 16.0,
            ComfortableMax = 26.0,
            ResearchSpeed = 1.0,
            PlantWorkSpeed = 1.0,
            Hunts = false,
        } }),
        Stocks = new ModelStocks { Nutrition = nutrition, Materials = EquatableArray.From<Amount>([]), Medicine = EquatableArray.From<Amount>([]) },
    };

    private static PawnCycle[][] Cycles() => [Enumerable.Range(0, 12).Select(t => new PawnCycle.Scheduled
    {
        Pawn = "Anna", Twelfth = t, IndoorHours = 0d, OutdoorHours = 0d,
        SleepHours = 24, EatingHours = 0, RecreationHours = 0, DaylightHours = 0, FreeHours = 0,
        OutdoorThermal = new(0, 0, 20, 20, 16, 26),
    }).ToArray()];

    private static ColonyModelPolicy Policy(double production, double end = 60) => new()
    {
        HorizonDays = end, MedicineTendsPerDay = 0d, FixedProduction = EquatableArray.From(new[] { production }),
    };

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(4.5)]
    [InlineData(5)]
    public void GrowingSeasonReserveRisesAcrossHandoffWithoutWaitingForAnotherModelEvent(double cut)
    {
        var input = Input(Enumerable.Range(0, 60).Select(d => d < 10 || d >= 30).ToArray(), 20);
        var policy = Policy(1);
        var whole = ColonyModel.Run(input, Cycles(), policy);
        var carried = ColonyModelState.Observed(input, policy) with { Day = cut };
        var resumed = ColonyModel.Run(input, carried, Cycles(), policy);
        // Twenty stored days meet the rising 23-day winter reserve after 5.021739 days.
        var expected = 9 - 30.5 * (1 - 20d / 23);
        Assert.Equal(expected, Assert.IsType<ModelEventTime.AtDay>(whole.FoodRunwayDay).Day, 9);
        Assert.Equal(expected, Assert.IsType<ModelEventTime.AtDay>(resumed.FoodRunwayDay).Day, 9);
        Assert.Equal(whole.Events.Where(e => e.Day > cut), resumed.Events);
        Assert.Equal(60, resumed.Events[^1].Day);
    }

    [Theory]
    [InlineData(.25)]
    [InlineData(.9)]
    public void DormantSeasonWaitShrinksWhileStocksFall(double cut)
    {
        var input = Input(Enumerable.Range(0, 60).Select(d => d >= 20).ToArray(), 24);
        var policy = Policy(-1);
        var whole = ColonyModel.Run(input, Cycles(), policy);
        var carried = ColonyModelState.Observed(input, policy) with { Day = cut, Nutrition = 24 - 2 * cut };
        var resumed = ColonyModel.Run(input, carried, Cycles(), policy);
        Assert.Equal(1, Assert.IsType<ModelEventTime.AtDay>(whole.FoodRunwayDay).Day, 9);
        Assert.Equal(Assert.IsType<ModelEventTime.AtDay>(whole.FoodRunwayDay).Day, Assert.IsType<ModelEventTime.AtDay>(resumed.FoodRunwayDay).Day, 9);
        var expected = whole.Events.Where(e => e.Day > cut).ToArray();
        Assert.Equal(expected.Length, resumed.Events.Count);
        foreach (var pair in expected.Zip(resumed.Events))
        {
            Assert.Equal(pair.First.Kind, pair.Second.Kind);
            Assert.Equal(pair.First.Day, pair.Second.Day, 9);
            Assert.Equal(pair.First.Levels[0], pair.Second.Levels[0], 9);
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(35)]
    [InlineData(59.5)]
    [InlineData(60)]
    [InlineData(60.5)]
    public void AnnualPatternAndTwelfthWrapKeepTheSameFutureCrossingAndEndDate(double cut)
    {
        var input = Input(Enumerable.Range(0, 60).Select(d => d < 35).ToArray(), 45);
        var policy = Policy(.7, 120);
        var whole = ColonyModel.Run(input, Cycles(), policy);
        var carried = ColonyModelState.Observed(input, policy) with { Day = cut, Nutrition = 45 - .3 * cut };
        var resumed = ColonyModel.Run(input, carried, Cycles(), policy);
        Assert.InRange(Assert.IsType<ModelEventTime.AtDay>(whole.FoodRunwayDay).Day, 60.5, 95);
        Assert.Equal(Assert.IsType<ModelEventTime.AtDay>(whole.FoodRunwayDay).Day, Assert.IsType<ModelEventTime.AtDay>(resumed.FoodRunwayDay).Day, 9);
        var expected = whole.Events.Where(e => e.Day > cut).ToArray();
        Assert.Equal(expected.Select(e => (e.Kind, e.Id)), resumed.Events.Select(e => (e.Kind, e.Id)));
        foreach (var pair in expected.Zip(resumed.Events))
        {
            Assert.Equal(pair.First.Day, pair.Second.Day, 9);
            Assert.Equal(pair.First.Levels[0], pair.Second.Levels[0], 9);
        }
        Assert.Equal(120, resumed.Events[^1].Day);
    }

    [Fact]
    public void TwoGrowingWindowsSurviveWireDecodeAndASecondSeasonHandoff()
    {
        var year = Enumerable.Range(0, 60).Select(d => d < 10 || d >= 20 && d < 30).ToArray();
        var wire = new RimGovernor.Contracts.Wire.Observations.FoodClimate
        {
            GrowingYear = EquatableArray.From(year), GrowingDays = 20, GrowingDaysRemaining = 10,
            GrowingDaysUntil = 0, NonGrowingDays = 10, DayOfYear = 58, SowingNow = true, Season = "Winter",
        };
        var decoded = WireJson.Deserialize<RimGovernor.Contracts.Wire.Observations.FoodClimate>(WireJson.Serialize(wire));
        Assert.Equal(year, decoded.GrowingYear);
        var input = Input(year, 25) with { Calendar = decoded };
        Assert.Same(decoded, input.Calendar);
        var policy = Policy(1);
        var whole = ColonyModel.Run(input, Cycles(), policy);
        var resumed = ColonyModel.Run(input, ColonyModelState.Observed(input, policy) with { Day = 20 }, Cycles(), policy);
        Assert.InRange(Assert.IsType<ModelEventTime.AtDay>(whole.FoodRunwayDay).Day, 20, 30);
        Assert.Equal(whole.FoodRunwayDay, resumed.FoodRunwayDay);
    }

    [Fact]
    public void PhaseInStartsBetweenEngineEventsAndCrossesBeforeTheNextTwelfth()
    {
        var input = Input(Enumerable.Range(0, 60).Select(d => d < 50).ToArray(), 1);
        var policy = Policy(1);
        var run = ColonyModel.Run(input, Cycles(), policy);
        Assert.Equal(28.5 + 20.5 / 13, Assert.IsType<ModelEventTime.AtDay>(run.FoodRunwayDay).Day, 9);
        var resumed = ColonyModel.Run(input, ColonyModelState.Observed(input, policy) with { Day = 28.5 }, Cycles(), policy);
        Assert.Equal(Assert.IsType<ModelEventTime.AtDay>(run.FoodRunwayDay).Day, Assert.IsType<ModelEventTime.AtDay>(resumed.FoodRunwayDay).Day, 9);
    }

    [Fact]
    public void MissingAnnualReadFails()
    {
        var input = Input([], 20);
        Assert.Equal("colony model growing year", Assert.Throws<RequiredFactException>(() => ColonyModel.Run(input, Cycles(), Policy(0))).Fact);
    }
}
