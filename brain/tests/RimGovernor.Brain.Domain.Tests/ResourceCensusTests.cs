using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.Observation;
using RimGovernor.Brain.Policy;
using Xunit;
using O = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class ResourceCensusTests
{
    private static readonly Resource Fuel = new("TestFuel");

    private static FuelConsumer Consumer(bool useDriven = false, bool rain = false, bool empty = false) => new()
    {
        Id = "consumer", Fuel = empty ? 0 : 2, Target = 10, OutOfFuel = empty, Fuels = [Fuel],
        Burn = new() { PerDay = 2, UnitsPerItem = 2, UseDriven = useDriven, InRain = rain },
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
        var consumer = Consumer() with { Burn = Consumer().Burn with { Flickable = true }, SwitchedOn = false };
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
        Assert.Throws<DomainException>(() => Plan(Consumer() with { Fuel = fuel }));
        Assert.Throws<DomainException>(() => Plan(Consumer() with { Fuels = [] }));
        Assert.Throws<DomainException>(() => Plan(Consumer() with { Burn = Consumer().Burn with { UnitsPerItem = 0 } }));
    }

    [Fact]
    public void MissingOrUnresolvedDevelopmentCensusFailsTheCommonDecoder()
    {
        var snapshot = ColonyFactsCoreTests.Snapshot();
        Assert.Throws<BridgeContractException>(() => ColonyDecodeReads.DecodeColony(snapshot with { Development = null! }, new(), default!, null));
        var unresolved = snapshot with { Development = snapshot.Development with { Outcome = snapshot.Development.Outcome with { Power = [new O.DevelopmentPower { Building = new() { Id = "missing" }, Turret = new O.PowerTurret.NoGun() }] } } };
        Assert.Throws<BridgeContractException>(() => ColonyDecodeReads.DecodeColony(unresolved, new(), new(default, default, default, []), null));
    }

    [Fact]
    public void MaterialBudgetCountsStandingSitesAndOtherPawnsReservationsOnce()
    {
        var budget = MaterialBudgetRules.MaterialBudget([new(Fuel, 10)], new Dictionary<Resource, long> { [Fuel] = 3 },
            [new() { Bill = "own", Pawn = new("worker"), Items = [new(Fuel, 4)] }, new() { Bill = "other", Pawn = new("other"), Items = [new(Fuel, 2)] }], new("worker"));
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
