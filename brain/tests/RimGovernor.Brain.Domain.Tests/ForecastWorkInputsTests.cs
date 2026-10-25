using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class ForecastWorkInputsTests
{
    private static ForecastCapacityPawn Pawn(string id = "cook-builder") => new()
    {
        Id = new RimGovernor.Brain.Domain.PawnId(id), State = ForecastPawnCapacityState.Available,
        Schedule = EquatableArray.From(Enumerable.Repeat(PawnRules.ScheduleAnything, 24)),
    };
    private static ForecastCapacityInput Capacity(ForecastCapacityPawn? pawn = null) => new()
    {
        Pawn = pawn ?? Pawn(), CurrentWork = new((pawn ?? Pawn()).Id, false, WorkType.None, new ForecastWorkRemaining.Timed(0.0)), Location = "home", HorizonTicks = 100, LocalHour = 12.0,
        OpportunityCostPerTick = 1.0, Occupancy = new ForecastProtection.Read([]),
        LocationAfterProtectedTime = "home",
    };
    private static ForecastOccupancy Busy(string id, double start, double end, ForecastOccupancyState state = ForecastOccupancyState.Active) =>
        new(id, "cook-builder", start, end, state);
    private static ForecastActivityProblem Jobs(params ForecastPawnCapacity[] pawns) => new()
    {
        Activities = [new("cook", "home", "home", []), new("build", "home", "home", [])],
        Pawns = pawns,
        Options = pawns.SelectMany(p => new[] { new ForecastActivityOption("cook", p.Pawn, 100.0, 0.0), new ForecastActivityOption("build", p.Pawn, 100.0, 0.0) }).ToArray(),
        Deadline = 100.0,
    };

    [Fact]
    public void ObservedCookBuilderHasOneBudgetAcrossWorkTypesWhileDisjointWorkersOverlap()
    {
        var one = ForecastCapacity.ForecastPawnCapacityOf(Capacity());
        Assert.Equal(100, one.Capacity.Windows.ReadWindows().Sum(w => w.End - w.Start));
        Assert.Empty(ForecastActivities.CompareActivityPlans(Jobs(one.Capacity)).Alternatives);
        var other = ForecastCapacity.ForecastPawnCapacityOf(Capacity(Pawn("other")));
        var selected = ForecastActivities.CompareActivityPlans(Jobs(one.Capacity, other.Capacity)).Alternatives[0];
        Assert.Equal(2, selected.Allocations.Select(a => a.Pawn).Distinct().Count());
        Assert.Equal(100, selected.CompletedAt);
    }

    [Fact]
    public void SupportDoesNotDuplicateMultipleOutputs()
    {
        var food = new Resource("TestFood"); var secondary = new Resource("TestSecondary");
        var worker = ForecastCapacity.ForecastPawnCapacityOf(Capacity()).Capacity;
        var helper = ForecastCapacity.ForecastPawnCapacityOf(Capacity(Pawn("helper"))).Capacity;
        var problem = Jobs(worker, helper) with
        {
            Activities = [new("clean", "home", "home", []), new("harvest", "home", "home", ["clean"])
                { Outputs = EquatableArray.From(new[] { new ForecastActivityYield(food, 10.0), new ForecastActivityYield(secondary, 4) }) }],
            Options = [new("clean", worker.Pawn, 10.0, 0.0), new("clean", helper.Pawn, 10.0, 0.0), new("harvest", worker.Pawn, 20.0, 0.0)],
        };
        var comparison = ForecastActivities.CompareActivityPlans(problem);
        // An unread yield has no stock change to give: the plan's stock changes need it read.
        Assert.All(comparison.Alternatives, p =>
        {
            Assert.Equal(30, p.Allocations.Sum(a => a.WorkTicks));
            var changes = ForecastActivities.StockChangesOf(problem, p);
            Assert.Equal(2, changes.Count);
            Assert.Equal(10, Assert.Single(changes, e => e.Resource == food).Units);
            Assert.Equal(4, Assert.Single(changes, e => e.Resource == secondary).Units);
        });
    }

    [Fact]
    public void NeedsAndOngoingObligationsOverlapOnceAndCancellationReleasesTheirTime()
    {
        var input = Capacity() with { Occupancy = new ForecastProtection.Read(EquatableArray.From(new[] { Busy("needs", 20, 50), Busy("work", 40, 70), Busy("work", 40, 70) })) };
        var result = ForecastCapacity.ForecastPawnCapacityOf(input);
        Assert.Equal(50, result.Capacity.Windows.ReadWindows().Sum(w => w.End - w.Start));
        Assert.Equal(new ForecastTimeWindow(0, 20) { StartLocation = "home" }, result.Capacity.Windows.ReadWindows()[0]);
        Assert.Equal(70, result.Capacity.Windows.ReadWindows()[1].Start);
        var cancelled = input with { Occupancy = new ForecastProtection.Read(EquatableArray.From(new[] { Busy("needs", 20, 50, ForecastOccupancyState.Completed), Busy("work", 40, 70, ForecastOccupancyState.Cancelled) })) };
        Assert.Equal(100, ForecastCapacity.ForecastPawnCapacityOf(cancelled).Capacity.Windows.ReadWindows().Sum(w => w.End - w.Start));
        var unknown = input with { Occupancy = new ForecastProtection.Read(EquatableArray.From(new[] { Busy("work", 0, 70) with { State = ForecastOccupancyState.Untimed } })) };
        Assert.IsType<ForecastCapacityWindows.Unread>(ForecastCapacity.ForecastPawnCapacityOf(unknown).Capacity.Windows);
    }

    [Fact]
    public void JobsCannotSpanProtectedTimeAndUnknownLaterLocationDoesNotReuseOldPosition()
    {
        var input = Capacity() with { Occupancy = new ForecastProtection.Read(EquatableArray.From(new[] { Busy("duty", 20, 70) })) };
        var capacity = ForecastCapacity.ForecastPawnCapacityOf(input).Capacity;
        var problem = Jobs(capacity) with
        {
            Activities = [new("cook", "home", "home", [])],
            Options = [new("cook", "cook-builder", 25.0, 0.0)],
        };
        Assert.Equal(70, ForecastActivities.CompareActivityPlans(problem).Alternatives[0].Allocations[0].Depart);
        var unknown = ForecastCapacity.ForecastPawnCapacityOf(input with { LocationAfterProtectedTime = default }).Capacity;
        var blocked = ForecastActivities.CompareActivityPlans(problem with { Pawns = [unknown] });
        Assert.Empty(blocked.Alternatives);
        Assert.Contains("unknown_window_location:cook-builder", blocked.Findings);
        Assert.Empty(ForecastActivities.CompareActivityPlans(problem with { Options = [new("cook", "cook-builder", 40.0, 0.0)] }).Alternatives);
    }

    [Fact]
    public void ZeroUnknownUnavailableAndChangedTimetableAreDifferent()
    {
        Assert.Equal(0, ForecastCapacity.ForecastPawnCapacityOf(Capacity() with { HorizonTicks = 0 }).Capacity.Windows.ReadWindows().Sum(w => w.End - w.Start));
        Assert.IsType<ForecastCapacityWindows.Unread>(ForecastCapacity.ForecastPawnCapacityOf(Capacity() with { Occupancy = new ForecastProtection.Truncated() }).Capacity.Windows);
        Assert.Equal(0, ForecastCapacity.ForecastPawnCapacityOf(Capacity(Pawn() with { State = ForecastPawnCapacityState.Unavailable })).Capacity.Windows.ReadWindows().Sum(w => w.End - w.Start));
        Assert.IsType<ForecastCapacityWindows.Unread>(ForecastCapacity.ForecastPawnCapacityOf(Capacity(Pawn() with { State = ForecastPawnCapacityState.Unread })).Capacity.Windows);
        var sleeping = Pawn() with { Schedule = EquatableArray.From(Enumerable.Repeat(PawnRules.ScheduleSleep, 24)) };
        Assert.Equal(0, ForecastCapacity.ForecastPawnCapacityOf(Capacity(sleeping)).Capacity.Windows.ReadWindows().Sum(w => w.End - w.Start));
        Assert.Equal(100, ForecastCapacity.ForecastPawnCapacityOf(Capacity()).Capacity.Windows.ReadWindows().Sum(w => w.End - w.Start));
        var slots = Enumerable.Repeat(PawnRules.ScheduleAnything, 24).ToArray();
        slots[13] = PawnRules.ScheduleSleep;
        var shifted = ForecastCapacity.ForecastPawnCapacityOf(Capacity(Pawn() with { Schedule = EquatableArray.From(slots) }) with
        { LocalHour = 12.5, HorizonTicks = GameTime.TicksPerHour });
        Assert.Equal(GameTime.TicksPerHour / 2.0, shifted.Capacity.Windows.ReadWindows().Sum(w => w.End - w.Start));
        var worker = PawnFixtures.Worker("cook-builder") with { Observed = (PawnFixtures.Worker("cook-builder")).Observed with { Job = new RimGovernor.Brain.GameFacts.Observations.JobEvidence { DefName = "UsefulWork", TargetA = new() { Target = new RimGovernor.Brain.GameFacts.Common.Unavailable { Reason = RimGovernor.Brain.GameFacts.Common.UnavailableReason.NotApplicable } } } } };
        var busy = Capacity() with { CurrentWork = ForecastCurrentWork.ForecastCurrentWorkOf(worker) };
        Assert.IsType<ForecastCapacityWindows.Unread>(ForecastCapacity.ForecastPawnCapacityOf(busy).Capacity.Windows);
        Assert.Equal(80, ForecastCapacity.ForecastPawnCapacityOf(busy with { CurrentWork = ForecastCurrentWork.ForecastCurrentWorkOf(worker, new ForecastWorkRemaining.Timed(20.0)) }).Capacity.Windows.ReadWindows().Sum(w => w.End - w.Start));
    }

    [Fact]
    public void AdmissionEvidenceCannotTurnAnAbsentPawnOrUntimedJobIntoFreeWork()
    {
        var observed = new WorkPawnAvailability("cook-builder", true, true, false, "")
        { LocalHour = 12d, Schedule = Pawn().Schedule };
        var protection = new ForecastProtection.Read([]);
        var free = ForecastCapacity.InputOf("cook-builder", observed, 100, protection);
        Assert.Equal(ForecastPawnCapacityState.Available, free.Pawn.State);
        Assert.Equal(100, ForecastCapacity.ForecastPawnCapacityOf(free).Capacity.Windows.ReadWindows().Sum(w => w.End - w.Start));
        var midnight = ForecastCapacity.InputOf("cook-builder", observed with { LocalHour = 0 }, 100, protection);
        Assert.Equal(ForecastPawnCapacityState.Available, midnight.Pawn.State);
        Assert.Equal(100, ForecastCapacity.ForecastPawnCapacityOf(midnight).Capacity.Windows.ReadWindows().Sum(w => w.End - w.Start));
        Assert.Throws<DomainException>(() => ForecastCapacity.ForecastPawnCapacityOf(ForecastCapacity.InputOf("cook-builder", observed with { LocalHour = double.NaN }, 100, protection)));
        Assert.Equal(ForecastPawnCapacityState.Unread, ForecastCapacity.InputOf("cook-builder", null, 100, protection).Pawn.State);
        var unavailable = ForecastCapacity.InputOf("cook-builder", observed with { Available = false, LocalHour = default }, 100, protection);
        Assert.Equal(ForecastPawnCapacityState.Unavailable, unavailable.Pawn.State);
        Assert.Empty(ForecastCapacity.ForecastPawnCapacityOf(unavailable).Capacity.Windows.ReadWindows());
        var busy = ForecastCapacity.InputOf("cook-builder", observed with { Occupied = true }, 100, protection);
        Assert.IsType<ForecastWorkRemaining.Untimed>(busy.CurrentWork.Remaining);
        Assert.IsType<ForecastCapacityWindows.Unread>(ForecastCapacity.ForecastPawnCapacityOf(busy).Capacity.Windows);
    }
    private static ReviewFacts Facts()
    {
        var building = new Building("TestBuilding", new Cell(1, 1), Rotation.North, "");
        var site = ConstructionObservationFixtures.Site(building, id: "frame", stage: "frame");
        var claim = new ConstructionClaim { Plan = new PlanId("method"), Action = new ActionId("action"), Building = building };
        return new()
        {
            Tick = new Tick(100), Gear = ColonyModelFixtures.NoGear(), CurrentConstruction = CurrentConstruction.Empty with { Sites = EquatableArray.From(new[] { site }) },
            ConstructionClaims = EquatableArray.From(new[] { claim, claim }),
            ForecastObserved = new() { WorkPawns = new WorkPawnsRead(EquatableArray.From(new[] { PawnFixtures.Worker("cook-builder") with { Observed = PawnFixtures.Row("cook-builder") with { Settings = PawnFixtures.Row("cook-builder").Settings with { Schedule = EquatableArray.From((Pawn().Schedule).Select((assignment, hour) => new RimGovernor.Brain.GameFacts.Observations.TimetableSlot { Hour = (uint)hour, AssignmentDefName = assignment })) } } } })) },
            BillReservations = [],
            ProductionPromises = new ProductionPromiseSet.Assessed([]),
            Census = ColonyFactsCoreTests.Snapshot() with { Resources = default(EquatableArray<RimGovernor.Brain.GameFacts.Observations.Quantity>) },
        };
    }

    [Fact]
    public void LiveReviewDemandCarriesTheObservedRosterAndReservations()
    {
        var facts = Facts();
        var demand = ResourceRules.ResourceDemandOf(facts, Rules.DefaultPlannerSettings(), new());
        Assert.Equal(facts.Tick, demand.Work.Tick);
    }
}
