using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class ForecastWorkInputsTests
{
    private static WorkPawn Pawn(string id = "cook-builder") => new()
    {
        Id = new RimGovernor.Brain.Domain.PawnId(id), Available = Known(true), Applies = Known(true),
        Job = Known(new PawnJob()),
        Schedule = Known(EquatableArray.From(Enumerable.Repeat(PolicyConstants.ScheduleAnything, 24))),
    };
    private static ForecastCapacityInput Capacity(WorkPawn? pawn = null) => new()
    {
        Pawn = pawn ?? Pawn(), Location = "home", HorizonTicks = 100, LocalHour = Known(12.0),
        OpportunityCostPerTick = Known(1.0), Occupancy = Known(default(EquatableArray<ForecastOccupancy>)),
        LocationAfterProtectedTime = Known("home"),
    };
    private static ForecastOccupancy Busy(string id, double start, double end, ForecastOccupancyState state = ForecastOccupancyState.Active) =>
        new(id, "cook-builder", Known(start), Known(end), state);
    private static ForecastActivityProblem Jobs(params ForecastPawnCapacity[] pawns) => new()
    {
        Activities = [new("cook", "home", "home", []), new("build", "home", "home", [])],
        Pawns = pawns,
        Options = pawns.SelectMany(p => new[] { new ForecastActivityOption("cook", p.Pawn, Known(100.0), Known(0.0)), new ForecastActivityOption("build", p.Pawn, Known(100.0), Known(0.0)) }).ToArray(),
        Deadline = Known(100.0), AlternativeLaborCost = Known(300.0),
    };

    [Fact]
    public void ObservedCookBuilderHasOneBudgetAcrossWorkTypesWhileDisjointWorkersOverlap()
    {
        var one = ForecastCapacity.ForecastPawnCapacityOf(Capacity());
        Assert.Equal(100, one.FreeTicks.Value);
        Assert.Null(ForecastActivities.CompareActivityPlans(Jobs(one.Capacity)).Selected);
        var other = ForecastCapacity.ForecastPawnCapacityOf(Capacity(Pawn("other")));
        var selected = ForecastActivities.CompareActivityPlans(Jobs(one.Capacity, other.Capacity)).Selected!;
        Assert.Equal(2, selected.Allocations.Select(a => a.Pawn).Distinct().Count());
        Assert.Equal(100, selected.CompletedAt);
    }

    [Fact]
    public void SupportDoesNotDuplicateMultipleOutputsAndUnknownYieldStaysUnknown()
    {
        var food = new Resource("TestFood"); var secondary = new Resource("TestSecondary");
        var worker = ForecastCapacity.ForecastPawnCapacityOf(Capacity()).Capacity;
        var helper = ForecastCapacity.ForecastPawnCapacityOf(Capacity(Pawn("helper"))).Capacity;
        var problem = Jobs(worker, helper) with
        {
            Activities = [new("clean", "home", "home", []), new("harvest", "home", "home", ["clean"])
                { Outputs = EquatableArray.From(new[] { new ForecastActivityYield(food, Known(10.0)), new ForecastActivityYield(secondary, Unknown<double>()) }) }],
            Options = [new("clean", worker.Pawn, Known(10.0), Known(0.0)), new("clean", helper.Pawn, Known(10.0), Known(0.0)), new("harvest", worker.Pawn, Known(20.0), Known(0.0))],
        };
        var comparison = ForecastActivities.CompareActivityPlans(problem);
        Assert.All(comparison.Alternatives, p =>
        {
            Assert.Equal(30, p.Allocations.Sum(a => a.WorkTicks));
            Assert.Equal(2, p.StockChanges.Count);
            Assert.Equal(10, Assert.Single(p.StockChanges, e => e.Resource == food).Units.Value);
        });
        var forecast = ColonyForecastRules.ForecastColony(new()
        {
            Origin = new("world", 0), HorizonDays = 1,
            Facts = new() { Stock = Known(default(EquatableArray<Amount>)) },
            Commitments = EquatableArray.From(new[] { new ForecastCommitment("harvest", comparison.Selected!.StockChanges) }),
        });
        Assert.Equal(10, Assert.Single(forecast.Proposed.Stocks, s => s.Resource == food).Points[^1].BalanceUnits.Value);
        Assert.False(Assert.Single(forecast.Proposed.Stocks, s => s.Resource == secondary).Points[^1].BalanceUnits.IsKnown);
    }

    [Fact]
    public void NeedsAndOngoingObligationsOverlapOnceAndCancellationReleasesTheirTime()
    {
        var input = Capacity() with { Occupancy = Known(EquatableArray.From(new[] { Busy("needs", 20, 50), Busy("work", 40, 70), Busy("work", 40, 70) })) };
        var result = ForecastCapacity.ForecastPawnCapacityOf(input);
        Assert.Equal(50, result.FreeTicks.Value);
        Assert.Equal(new ForecastTimeWindow(0, 20) { StartLocation = Known("home") }, result.Capacity.Windows.Value[0]);
        Assert.Equal(70, result.Capacity.Windows.Value[1].Start);
        var cancelled = input with { Occupancy = Known(EquatableArray.From(new[] { Busy("needs", 20, 50, ForecastOccupancyState.Completed), Busy("work", 40, 70, ForecastOccupancyState.Cancelled) })) };
        Assert.Equal(100, ForecastCapacity.ForecastPawnCapacityOf(cancelled).FreeTicks.Value);
        var unknown = input with { Occupancy = Known(EquatableArray.From(new[] { Busy("work", 0, 70) with { End = Unknown<double>() } })) };
        Assert.False(ForecastCapacity.ForecastPawnCapacityOf(unknown).FreeTicks.IsKnown);
    }

    [Fact]
    public void JobsCannotSpanProtectedTimeAndUnknownLaterLocationDoesNotReuseOldPosition()
    {
        var input = Capacity() with { Occupancy = Known(EquatableArray.From(new[] { Busy("duty", 20, 70) })) };
        var capacity = ForecastCapacity.ForecastPawnCapacityOf(input).Capacity;
        var problem = Jobs(capacity) with
        {
            Activities = [new("cook", "home", "home", [])],
            Options = [new("cook", "cook-builder", Known(25.0), Known(0.0))],
        };
        Assert.Equal(70, ForecastActivities.CompareActivityPlans(problem).Selected!.Allocations[0].Depart);
        var unknown = ForecastCapacity.ForecastPawnCapacityOf(input with { LocationAfterProtectedTime = default }).Capacity;
        var blocked = ForecastActivities.CompareActivityPlans(problem with { Pawns = [unknown] });
        Assert.Null(blocked.Selected);
        Assert.Contains("unknown_window_location:cook-builder", blocked.Findings);
        Assert.Null(ForecastActivities.CompareActivityPlans(problem with { Options = [new("cook", "cook-builder", Known(40.0), Known(0.0))] }).Selected);
    }

    [Fact]
    public void ZeroUnknownUnavailableAndChangedTimetableAreDifferent()
    {
        Assert.Equal(0, ForecastCapacity.ForecastPawnCapacityOf(Capacity() with { HorizonTicks = 0 }).FreeTicks.Value);
        Assert.False(ForecastCapacity.ForecastPawnCapacityOf(Capacity() with { Occupancy = default }).FreeTicks.IsKnown);
        Assert.Equal(0, ForecastCapacity.ForecastPawnCapacityOf(Capacity(Pawn() with { Available = Known(false) })).FreeTicks.Value);
        Assert.False(ForecastCapacity.ForecastPawnCapacityOf(Capacity(Pawn() with { Available = default })).FreeTicks.IsKnown);
        var sleeping = Pawn() with { Schedule = Known(EquatableArray.From(Enumerable.Repeat(PolicyConstants.ScheduleSleep, 24))) };
        Assert.Equal(0, ForecastCapacity.ForecastPawnCapacityOf(Capacity(sleeping)).FreeTicks.Value);
        Assert.Equal(100, ForecastCapacity.ForecastPawnCapacityOf(Capacity()).FreeTicks.Value);
        var slots = Enumerable.Repeat(PolicyConstants.ScheduleAnything, 24).ToArray();
        slots[13] = PolicyConstants.ScheduleSleep;
        var shifted = ForecastCapacity.ForecastPawnCapacityOf(Capacity(Pawn() with { Schedule = Known(EquatableArray.From(slots)) }) with
        { LocalHour = Known(12.5), HorizonTicks = GameTime.TicksPerHour });
        Assert.Equal(GameTime.TicksPerHour / 2.0, shifted.FreeTicks.Value);
        var busy = Capacity(Pawn() with { Job = Known(new PawnJob { Def = "UsefulWork" }) });
        Assert.False(ForecastCapacity.ForecastPawnCapacityOf(busy).FreeTicks.IsKnown);
        Assert.Equal(80, ForecastCapacity.ForecastPawnCapacityOf(busy with { CurrentJobRemainingTicks = Known(20.0) }).FreeTicks.Value);
    }

    private static RoundsFacts Facts()
    {
        var building = new Building("TestBuilding", new Cell(1, 1), Rotation.North, "");
        var site = new ConstructionSite { Id = "frame", Building = building };
        var claim = new ConstructionClaim { Plan = new PlanId("method"), Action = new ActionId("action"), Building = building };
        return new()
        {
            Tick = new Tick(100), CurrentConstruction = Known(new CurrentConstruction { Colony = true, Sites = EquatableArray.From(new[] { site }) }),
            ConstructionClaims = Known(EquatableArray.From(new[] { claim, claim })),
            ForecastObserved = new() { WorkPawns = Known(EquatableArray.From(new[] { Pawn() })) },
            BillReservations = Known(default(EquatableArray<IngredientReservation>)),
            ProductionPromises = Known(default(EquatableArray<ForecastCommitment>)),
            Resources = Known(default(EquatableArray<Amount>)),
        };
    }

    [Fact]
    public void LiveReviewDemandCarriesClaimsAndAssignmentUsesThatSameValue()
    {
        var facts = Facts(); var policy = RoundsRules.DefaultRoundsPolicy();
        var demand = ResourceRules.ResourceDemandOf(facts, policy, new());
        var commitment = Assert.Single(demand.Work.ConstructionCommitments.Value);
        Assert.Equal("construction/action", commitment.Id);
        Assert.False(commitment.AlternativeLaborCost.IsKnown);
        Assert.Equal(facts.CurrentConstruction, demand.Work.Construction);
        Assert.Equal(facts.BillReservations, demand.Work.IngredientReservations);
    }

    [Fact]
    public void UnknownClaimsAreNotEmptyAndRemovedOrCompletedClaimsStopGeneratingWork()
    {
        var facts = Facts();
        Assert.False(ForecastWorkInputs.ForecastWorkInputsOf(facts with { ConstructionClaims = default }).ConstructionCommitments.IsKnown);
        var cancelled = facts with { ConstructionClaims = Known(default(EquatableArray<ConstructionClaim>)) };
        Assert.Empty(ForecastWorkInputs.ForecastWorkInputsOf(cancelled).ConstructionCommitments.Value);
        var gone = facts with { CurrentConstruction = Known(new CurrentConstruction { Colony = true }) };
        Assert.Empty(ForecastWorkInputs.ForecastWorkInputsOf(gone).ConstructionCommitments.Value);
    }

    [Fact]
    public void WorkInputsCarryTheSharedProductionPromisesAndNoPlannedRoomClaim()
    {
        var resource = new Resource("TestMaterial");
        var promise = new ForecastCommitment("production/batch", default)
        { MaterialClaims = [new(resource, Known(10.0), Known(""))] };
        var facts = Facts() with
        {
            ProductionPromises = Known<EquatableArray<ForecastCommitment>>([promise]),
            PlannedConstruction = Known<IReadOnlyDictionary<int, IReadOnlyDictionary<Resource, long>>>(
                new Dictionary<int, IReadOnlyDictionary<Resource, long>> { [1] = new Dictionary<Resource, long> { [resource] = 50 } }),
        };
        var work = ResourceRules.ResourceDemandOf(facts, RoundsRules.DefaultRoundsPolicy(), new()).Work;
        Assert.Contains(promise, work.ProductionPromises.Value);
        // Planned rooms are construction activities in the coupled portfolio (#3276), not a material-only claim.
        Assert.DoesNotContain(work.ConstructionCommitments.Value, c => c.Id.StartsWith("planned/", StringComparison.Ordinal));
        Assert.False(ForecastWorkInputs.ForecastWorkInputsOf(facts with { ProductionPromises = default }).ProductionPromises.IsKnown);
    }
}
