using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Policy;
using Xunit;
using O = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class ResourceCensusTests
{
    private static readonly Resource Fuel = new("TestFuel");

    private static FuelConsumer Consumer(bool useDriven = false, bool rain = false, bool empty = false) =>
        PowerBuildingFixtures.Trader("consumer", "FuelConsumer", new(1, 1), 100, 100, true) with
        {
            FuelDefinitions = [Fuel.Value],
            Service = BuildingServiceWire.NoService with
            {
                Fuel = new O.BuildingFuel.Refuelable
                {
                    Fuel = empty ? 0 : 2, TargetFuel = 10, OutOfFuel = empty,
                    Burn = new O.FuelBurn { PerDay = 2, UnitsPerItem = 2, UseDriven = useDriven, InRain = rain, WhenPowered = false, Flickable = false },
                },
            },
        };

    private static FuelRunway Plan(params FuelConsumer[] consumers) => FuelRunwayRules.PlanFuelRunway(new()
    {
        Consumers = EquatableArray.From(consumers), Stock = new StockReader([], 0),
    });

    [Fact]
    public void ContinuousFuelProjectsTheObservedTankAndConversion()
    {
        var runway = Plan(Consumer());
        var projected = Assert.IsType<FuelProjection.Projected>(runway.Projection);
        var need = (long)Math.Ceiling((2 * ColonyDiagnosticsRules.MaintenanceHorizonDays - 2) / 2);
        Assert.Equal(need, runway.Needs[Fuel]);
        Assert.Empty(projected.Gaps);
    }

    [Fact]
    public void ASwitchedOffConsumerHasNoContinuousDemand()
    {
        var consumer = Consumer();
        consumer = consumer with { Service = consumer.Service with { Fuel = consumer.Refuelable with { Burn = consumer.Burn with { Flickable = true } }, SwitchedOn = false } };
        var runway = Plan(consumer);
        Assert.Empty(runway.Needs);
        Assert.Empty(Assert.IsType<FuelProjection.Projected>(runway.Projection).Gaps);
    }

    [Fact]
    public void PerUseFuelNamesItsGapUntilTheBarrelIsEmpty()
    {
        Assert.Equal(["consumer"], Assert.IsType<FuelProjection.Projected>(Plan(Consumer(useDriven: true)).Projection).Gaps);
        var empty = Plan(Consumer(useDriven: true, empty: true));
        Assert.Empty(Assert.IsType<FuelProjection.Projected>(empty.Projection).Gaps);
        Assert.Equal(5, empty.Needs[Fuel]);
    }

    [Fact]
    public void RainDependentFuelNamesTheWeatherStateButAnEmptyTankCanBeRefilled()
    {
        Assert.Equal(["consumer"], Assert.IsType<FuelProjection.RainDependent>(Plan(Consumer(rain: true)).Projection).Consumers);
        var empty = Plan(Consumer(rain: true, empty: true));
        Assert.IsType<FuelProjection.Projected>(empty.Projection);
        Assert.Equal(5, empty.Needs[Fuel]);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(-1)]
    public void BrokenFuelFactsFailInsteadOfDroppingDemand(double fuel)
    {
        var consumer = Consumer();
        Assert.Throws<DomainException>(() => Plan(consumer with { Service = consumer.Service with { Fuel = consumer.Refuelable with { Fuel = fuel } } }));
        Assert.Throws<DomainException>(() => Plan(consumer with { FuelDefinitions = [] }));
        Assert.Throws<DomainException>(() => Plan(consumer with { Service = consumer.Service with { Fuel = consumer.Refuelable with { Burn = consumer.Burn with { UnitsPerItem = 0 } } } }));
    }

    [Fact]
    public void MissingDevelopmentCensusFailsTheCommonDecoder()
    {
        var snapshot = ColonyFactsCoreTests.Snapshot();
        Assert.Throws<BridgeContractException>(() => ColonyPlanningReads.Compose(snapshot with { Development = null! }, new() { Context = snapshot.Context }, default!, null));
    }

    [Fact]
    public void MaterialBudgetCountsStandingSitesAndOtherPawnsReservationsOnce()
    {
        var budget = MaterialBudgetRules.MaterialBudget([new(Fuel, 10)], new Dictionary<Resource, long> { [Fuel] = 3 },
            [BillWire.Reservation("own", "worker", new Amount(Fuel, 4)), BillWire.Reservation("other", "other", new Amount(Fuel, 2))], new("worker"));
        Assert.Equal(5, budget[Fuel]);
    }

    [Fact]
    public void AnUncomposedConstructionProjectionRefusesPlanning()
    {
        Assert.Throws<RequiredFactException>(() => new RoundsFacts().CurrentConstruction);
        Assert.Throws<RequiredFactException>(() => ConstructionOwnershipRules.WorkOpen(new("TestBuilding", new(1, 1), Rotation.North, ""), null));
        Assert.Empty(ConstructionReads.ConstructionFromTables(new(default, default, default, [])).Buildings);
    }
}
