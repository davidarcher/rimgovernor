using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class ForecastThermalPortfolioTests
{
    private const double Horizon = 1000;
    internal static RoundsFacts Review(double ambient, bool demand = true, bool occupiedInside = true, bool restDemand = true)
    {
        var facts = ForecastCoupledTests.ConstructionReview(stock: 100);
        var cells = Enumerable.Range(0, 3).SelectMany(x => Enumerable.Range(0, 3).Select(z => new Cell(x, z))).ToArray();
        var inside = new Cell(1, 1);
        var wall = facts.ForecastObserved.Coupled!.Construction.Options.Single() with { Location = new(0, 1) };
        var bed = occupiedInside ? inside : new Cell(3, 1);
        var thermal = CoupledThermalTests.NoThermal with
        {
            Outdoors = ambient, ThinRoofExchangePerTick = .00005, WallExchangePerTick = .000017,
            Cells = EquatableArray.From(cells.Append(new Cell(3, 1)).Select(c => new ThermalCell(c, ambient, "none",
                c != inside && c != wall.Location && c != new Cell(3, 1), false,
                c == wall.Location ? wall.Site : "", c == wall.Location,
                false, 0, c == inside))),
            Workers = [new("pawn", 5d)], RoofRoutes = [new("pawn", inside, new RoofApproach.Path(1d))],
            Sources = [new("heater", inside, true, .35, 20d, CoupledThermalTests.Powered(60000d))],
        };
        var pawn = facts.ForecastObserved.Needs!.Pawns[0] with
        {
            Awake = true, LocalHour = 6d, SleepThreshold = .75, WakeThreshold = 1d, SleepBlockedUntilTicks = 200d,
            ComfortableMin = 10d, ComfortableMax = 30d,
            Mood = NeedsFixtures.Mood with { UnclampedTarget = .5, RisePerTick = .12 / GameTime.TicksPerHour, FallPerTick = .08 / GameTime.TicksPerHour, FreezesWhileAsleep = true, BreakThreshold = .3, Conditional = new ConditionalMood.Modeled([]) },
            Needs = [NeedsFixtures.Rate("food", 1d, 0d, .3), NeedsFixtures.Rate("rest", restDemand ? .2 : .6, restDemand ? .0004 : 0d, .3)],
            Targets = [NeedsFixtures.Service("bed", "rest", 0d, .0005) with { Location = bed }],
        };
        facts = facts with
        {
            PlannedShelters = demand ? [new() { Id = 1, Region = RegionRules.RectRegion(new(1, 1, 1, 1)) }] : [],
            ConstructionDeficit = demand ? facts.ConstructionDeficit : Known<IReadOnlyDictionary<Resource, long>>(new Dictionary<Resource, long>()),
            ForecastObserved = facts.ForecastObserved with
            {
                Needs = facts.ForecastObserved.Needs with { Pawns = [pawn] },
                Coupled = facts.ForecastObserved.Coupled with
                {
                    Food = facts.ForecastObserved.Coupled.Food with { Options = [] }, FoodUses = [],
                    Thermal = thermal, Construction = facts.ForecastObserved.Coupled.Construction with { Options = demand ? [wall] : [] },
                    Routes = EquatableArray.From(cells.Append(bed).Distinct().SelectMany(a => cells.Append(bed).Distinct().Where(b => a != b)
                        .Select(b => new CoupledRoute("pawn", a, b, Known(1d))))),
                },
            },
        };
        facts = WorkRules.ReviewWorkResources(facts, Known<EquatableArray<ProductionBatch>>([]), new([]));
        return facts with { CoupledAlternatives = ForecastCoupledCandidates.GenerateCoupledActivities(facts, Horizon)?.Priced() };
    }
    internal static ForecastCoupledSelection Select(RoundsFacts facts) => ForecastCoupledSelection.CompareActivityOutcomes(
        ForecastCoupled.CoupledForecastRequestOf(new("world", 100), Horizon / GameTime.TicksPerDay, facts, 
            new() { Work = ForecastWorkInputs.ForecastWorkInputsOf(facts) }, [], new()), facts.CoupledAlternatives!);

    [Fact]
    public void SharedSelectorCombinesRealShellRoofAndInsideOccupancyForConditionalThermalRelief()
    {
        var facts = Review(-10);
        var result = Select(facts);
        Assert.Equal(Known(true), result.Selected.Feasible);
        Assert.True(result.Selected.StationaryThermalRelief.Value > 0);
        Assert.Contains(result.Selected.Plan.Allocations, a => facts.CoupledAlternatives!.Context.NeedTasks.Any(n => n.Task == a.Activity && n.Kind == "rest"));
        Assert.Contains(result.Selected.CompletedDevelopmentSites, id => id.StartsWith("site/", StringComparison.Ordinal));
        Assert.Contains(result.Selected.CompletedDevelopmentSites, id => id.StartsWith("roof/", StringComparison.Ordinal));
        // Idle and travel ticks take the last located temperature, else outdoors (#3673): none is left unassessed.
        Assert.Equal(0, result.Selected.ThermalExposure.Single().UnknownTicks);
        // Without the bed the pawn's rest reaches zero; the bed (with the shelter's relief on top) does not.
        Assert.True(result.SelectedVector.Value.UrgentZero.Excess < result.BaselineVector.Value.UrgentZero.Excess);
        Assert.True(result.Selected.Shelters.Single().CompleteAt.Value < 200);
    }

    [Fact]
    public void WarmOrOutsideOccupancyCannotClaimShelterReliefAndNoDemandRetainsNoChange()
    {
        var warm = Select(Review(20));
        Assert.Equal(Known(0d), warm.Selected.StationaryThermalRelief);
        Assert.Empty(warm.Selected.CompletedDevelopmentSites);
        var outside = Select(Review(-10, occupiedInside: false));
        Assert.Equal(Known(0d), outside.Selected.StationaryThermalRelief);
        Assert.Empty(outside.Selected.CompletedDevelopmentSites);
        var noDemand = Select(Review(20, demand: false, restDemand: false));
        Assert.Empty(noDemand.Selection.Selected);
        Assert.Empty(noDemand.Selected.Plan.Allocations);
        var coldNoDemand = Select(Review(-10, demand: false, restDemand: false));
        Assert.Empty(coldNoDemand.Selection.Selected);
        // Without rest demand or cold no rest is scheduled. (In the cold a rest in the heated room may be: the ticks after
        // it take its warm temperature, #3673.)
        var onlyConstruction = Review(20, restDemand: false);
        var built = Select(onlyConstruction);
        Assert.DoesNotContain(built.Selected.Plan.Allocations, a => onlyConstruction.CoupledAlternatives!.Context.NeedTasks
            .Any(n => n.Task == a.Activity && n.Kind == "rest"));
    }

    /// <summary>A standing room at <paramref name="ambient"/> with an unfinished heater frame inside, a bed beside it and a door
    /// east of the heater; the pawn's rest reaches its threshold at <paramref name="sleepAt"/>.</summary>
    internal static RoundsFacts StandingRoomWithHeaterFrame(double ambient, double sleepAt, bool doorHeldOpen)
    {
        var facts = ForecastCoupledTests.ConstructionReview(stock: 100);
        var cells = Enumerable.Range(0, 4).SelectMany(x => Enumerable.Range(0, 3).Select(z => new Cell(x, z))).ToArray();
        Cell bed = new(1, 1), heater = new(2, 1), door = new(3, 1);
        var interior = new[] { bed, heater };
        var frame = facts.ForecastObserved.Coupled!.Construction.Options.Single() with { Location = heater };
        var thermal = CoupledThermalTests.NoThermal with
        {
            Outdoors = ambient, ThinRoofExchangePerTick = .00005, WallExchangePerTick = .000017,
            Cells = EquatableArray.From(cells.Select(c => new ThermalCell(c, ambient, interior.Contains(c) ? "thin" : "none",
                !interior.Contains(c), c == door && doorHeldOpen, c == heater ? frame.Site : "", false, false,
                c == door ? .00267 : 0, false))),
            Workers = [new("pawn", 5d)], RoofRoutes = [],
            Sources = [new(frame.Site, heater, false, .35, 20d, CoupledThermalTests.Powered(60000d))],
        };
        var pawn = facts.ForecastObserved.Needs!.Pawns[0] with
        {
            Awake = true, LocalHour = 6d, SleepThreshold = .75, WakeThreshold = 1d, SleepBlockedUntilTicks = sleepAt,
            ComfortableMin = 10d, ComfortableMax = 30d,
            Mood = NeedsFixtures.Mood with { UnclampedTarget = .5, RisePerTick = .12 / GameTime.TicksPerHour, FallPerTick = .08 / GameTime.TicksPerHour, FreezesWhileAsleep = true, BreakThreshold = .3, Conditional = new ConditionalMood.Modeled([]) },
            Needs = [NeedsFixtures.Rate("food", 1d, 0d, .3), NeedsFixtures.Rate("rest", .2, .0004, .3)],
            Targets = [NeedsFixtures.Service("bed", "rest", 0d, .0005) with { Location = bed }],
        };
        facts = facts with
        {
            Upkeep = facts.Upkeep with { Rooms = Known(RoomFixtures.Census((RoomFixtures.State("room-1", beds: ["bed"]), interior))) },
            PlannedShelters = [],
            ForecastObserved = facts.ForecastObserved with
            {
                Needs = facts.ForecastObserved.Needs with { Pawns = [pawn] },
                Coupled = facts.ForecastObserved.Coupled with
                {
                    Food = facts.ForecastObserved.Coupled.Food with { Options = [] }, FoodUses = [],
                    Thermal = thermal, Construction = facts.ForecastObserved.Coupled.Construction with { Options = [frame] },
                    Routes = EquatableArray.From(interior.SelectMany(a => interior.Where(b => a != b).Select(b => new CoupledRoute("pawn", a, b, Known(1d))))),
                },
            },
        };
        facts = WorkRules.ReviewWorkResources(facts, Known<EquatableArray<ProductionBatch>>([]), new([]));
        return facts with { CoupledAlternatives = ForecastCoupledCandidates.GenerateCoupledActivities(facts, Horizon)?.Priced() };
    }

    /// <summary>
    /// #3682/#3554: a cold standing room whose heater frame is the only unfinished work. Its door stands open while a pawn passes
    /// but shuts behind it, so finishing the heater before the pawn sleeps warms the room it sleeps in: the heater with the later
    /// sleep is selected over the sleep alone.
    /// </summary>
    [Fact]
    public void AStandingRoomsUnfinishedHeaterIsSelectedAheadOfALaterSleep()
    {
        var facts = StandingRoomWithHeaterFrame(-10, sleepAt: 400, doorHeldOpen: false);
        var result = Select(facts);
        Assert.Contains(result.Selected.Plan.Allocations, a => a.Activity.StartsWith("site-0-finish", StringComparison.Ordinal));
        Assert.Contains(result.Selected.Plan.Allocations, a => facts.CoupledAlternatives!.Context.NeedTasks.Any(n => n.Task == a.Activity && n.Kind == "rest"));
        Assert.True(result.Selected.StationaryThermalRelief.Value > 0);
    }

    /// <summary>A door held open leaves the ring open: the heater has no assessed relief and is not built.</summary>
    [Fact]
    public void AHeldOpenDoorLeavesTheHeaterUnbuilt()
    {
        var result = Select(StandingRoomWithHeaterFrame(-10, sleepAt: 400, doorHeldOpen: true));
        Assert.DoesNotContain(result.Selected.Plan.Allocations, a => a.Activity.StartsWith("site-0-finish", StringComparison.Ordinal));
    }
}