using RimGovernor.Brain.Domain;
using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class NativeLaborCapacityTests
{
    private static PawnState Worker() => StockArrivalTestFacts.Crafter("worker", 10, 1) with
    {
        Settings = StockArrivalTestFacts.Crafter("worker", 10, 1).Settings with
        { Schedule = EquatableArray.From(Enumerable.Range(0, 24).Select(h => new TimetableSlot { Hour = (uint)h, AssignmentDefName = PawnRules.ScheduleWork })) },
    };
    private static NeedsForecastPawn Needs() => NeedsFixtures.Pawn("worker") with
    {
        Needs = [NeedsFixtures.Rate("food", .8, .00001, .3), NeedsFixtures.Rate("rest", .8, .0001, .3)],
        Targets = [NeedsFixtures.Food("meal", "meal", 20, 400, 100, .5), NeedsFixtures.Service("bed", "rest", 0, .0002)],
    };
    private static double Hours(PawnState worker, NeedsForecastPawn pawn) => Assert.IsType<ForecastTerm.Assessed>(
        NativeLaborCapacity.HoursPerDay(worker, NeedsFixtures.Read(0, pawn), [WorkType.Crafting])).Value;

    [Fact]
    public void TwentyFourWorkSlotsCannotFinanceTwentyFourHoursOfProduction()
    {
        var hours = Hours(Worker(), Needs());
        // Eight hours of rest and 6/7 meal services a day, each 500 ticks including approach.
        Assert.Equal(24 - 8 - 6d / 7 * .2, hours, 9);
        var hungry = Needs() with { Needs = [Needs().Needs[0] with { Level = .1 }, Needs().Needs[1]] };
        Assert.True(Hours(Worker(), hungry) < hours);
        var furtherMeal = Needs() with { Targets = [Needs().Targets[0] with { TravelTicks = 1400 }, Needs().Targets[1]] };
        Assert.True(Hours(Worker(), furtherMeal) < hours);
        var exhausted = Needs() with { Needs = [Needs().Needs[0], Needs().Needs[1] with { Level = .1 }] };
        Assert.True(Hours(Worker(), exhausted) < hours);
    }

    [Fact]
    public void ObservedContinuousServiceDebitsItsRemainingTimeEvenAboveTheTrigger()
    {
        var needs = Needs() with { CurrentService = new("", "", 0, "bed", "rest", new(0, 0), GameTime.TicksPerHour, .0002) { Unsupported = [] } };
        Assert.Equal(Hours(Worker(), Needs()) - 1, Hours(Worker(), needs), 9);
    }

    [Fact]
    public void MissingNeedsAndUnavailableServiceDoNotBecomeFreeLabor()
    {
        Assert.Equal(new ForecastTerm.Unassessed("labor_needs_unread:worker"),
            NativeLaborCapacity.HoursPerDay(Worker(), null, [WorkType.Crafting]));
        var noMeal = Needs() with { Targets = [Needs().Targets[1]] };
        Assert.Equal(new ForecastTerm.Unassessed("labor_meal_service_unavailable:worker"),
            NativeLaborCapacity.HoursPerDay(Worker(), NeedsFixtures.Read(0, noMeal), [WorkType.Crafting]));
    }

    [Fact]
    public void UsefulNativeWorkIsPotentialForItsOwnTypeAndUnavailableForAnotherWithoutADuration()
    {
        var worker = Worker();
        worker = worker with { Job = worker.Job with { DefName = "native-cooking", WorkTypeDefName = "Cooking" } };
        var read = NeedsFixtures.Read(0, Needs());
        Assert.Equal(new ForecastTerm.Unassessed("labor_native_work_remaining:worker:native-cooking"),
            NativeLaborCapacity.HoursPerDay(worker, read, [WorkType.Crafting]));
        Assert.IsType<ForecastTerm.Assessed>(NativeLaborCapacity.HoursPerDay(worker, read, [WorkType.Cooking]));
        // A throughput estimate for that same work never grants free shared admission capacity.
        var work = PawnFixtures.Worker("worker") with { Observed = worker };
        Assert.True(ForecastCurrentWork.ForecastCurrentWorkOf(work).Occupied);
        Assert.IsType<ForecastWorkRemaining.Untimed>(ForecastCurrentWork.ForecastCurrentWorkOf(work).Remaining);
    }

    [Fact]
    public void ActualCareWithUnknownRemainingTimeDoesNotFinanceProduction()
    {
        var baby = new BabyRead
        {
            Pawn = "baby", Care = new() { Active = new("worker", BabyCareKind.Carry, new(0, 0), new(0, 0),
                new ServicePositionRead.Located(new(0, 0)), ActivitySocialMode.Normal), Food = null, Play = null, Opportunities = [] },
            CribMood = null, Fits = [], Play = null, InNursery = false, Hearers = [], SleepHearers = [],
            SleepWithoutNursery = new("", false, true, .00003, 0, 21),
        };
        var read = NeedsFixtures.Read(0, Needs()) with { Babies = NeedsFixtures.NoBabies with { Babies = [baby] } };
        Assert.Equal(new ForecastTerm.Unassessed("labor_active_care_remaining:worker"),
            NativeLaborCapacity.HoursPerDay(Worker(), read, [WorkType.Crafting]));
    }

    [Fact]
    public void CraftArrivalUsesConditionalHoursAndNamesAnUnavailableSource()
    {
        StockRecipe[] recipes = [new()
        {
            Recipe = "make", Product = new("product"), Batch = 1, WorkAmount = GameTime.TicksPerHour,
            WorkSpeedStat = "GeneralLaborSpeed", BenchSpeedStat = StockArrivalRules.BenchSpeedStat,
            Benches = [new("FabricationBench", WorkType.Crafting)], Skills = [], Ingredients = [[new(new("input"), 1)]],
        }];
        BillStackRow[] benches = [StockArrivalTestFacts.Bench("FabricationBench", true, 1)];
        var stock = new Dictionary<Resource, long> { [new("input")] = 10 };
        var arrival = StockArrivalRules.Craft(recipes, benches, [Worker()], stock, NeedsFixtures.Read(0, Needs()));
        Assert.Equal(Hours(Worker(), Needs()), Assert.Single(arrival.Sources).PerDay);
        Assert.True(arrival.Sources[0].PerDay < 24);
        var unavailable = StockArrivalRules.Craft(recipes, benches, [Worker()], stock, null);
        Assert.Empty(unavailable.Sources);
        Assert.Contains("labor_needs_unread:worker", unavailable.Findings);
    }

    [Fact]
    public void BenchSizingReadsNeedsAndCapabilityInsteadOfCountingSlots()
    {
        var pawn = PawnFixtures.Worker("worker") with { Observed = Worker(), Work = PawnFixtures.WorkTable(true, [WorkPriority.Planned(WorkType.Crafting, 1)]) };
        WorkRequirement[] requirement = [new() { Work = WorkType.Crafting, Skill = "Crafting", Minimum = 8 }];
        Assert.True(WorkRules.TryDispatchWorkers([pawn], requirement, "GeneralLaborSpeed", NeedsFixtures.Read(0, Needs()), out var workers));
        Assert.Equal(Hours(Worker(), Needs()), Assert.Single(workers).WorkHoursPerDay);
        Assert.False(WorkRules.TryDispatchWorkers([pawn], requirement, "GeneralLaborSpeed", null, out _));
        Assert.True(WorkRules.TryDispatchWorkers([pawn], [requirement[0] with { Minimum = 11 }], "GeneralLaborSpeed", NeedsFixtures.Read(0, Needs()), out var incapable));
        Assert.Empty(incapable);
    }
}
