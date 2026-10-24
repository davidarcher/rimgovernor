using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using ResourceSource = RimGovernor.Brain.Policy.ResourceSource;

namespace RimGovernor.Brain.Domain.Tests;

// #3653: a priced planned room's deliveries are charged the construction material they consume, at replacement labor
// (chop or mine work per unit plus a haul trip over its load), market value without a source in reach, nothing without either.
public sealed class MaterialChargeTests
{
    private const string Delivery = "planned/room1/wall/WoodLog/delivery/stack";
    private const string Refuel = "refuel/stack";
    private const double ChopWork = 1000, ChopYield = 25, Distance = 10;
    private const long StackLimit = 75;

    private static readonly Resource Wood = new("WoodLog");
    private static readonly Resource Steel = new("Steel");
    private static readonly Resource Plasteel = new("Plasteel");

    private static readonly HaulModel Haul = new()
    {
        Chopped = [new HaulSource { Good = Wood, Units = ChopYield, Work = ChopWork }],
        Mined = [new HaulSource { Good = Steel, Units = 35, Work = 2000 }],
    };

    private static readonly ItemFacts Items = new() { Market = new Dictionary<Resource, double> { [Steel] = 1.9 } };

    private static readonly ResourceStorage Storage = new() { StackLimit = StackLimit };

    private static ResourceSource Tree(double distance) => new() { ThingId = "tree", Yield = 25, Distance = distance };

    /// <summary>A delivery of <paramref name="units"/> of <paramref name="def"/> to room 1's wall, and a refuel drawing the same stack,
    /// both allocated; room 1 a bedroom, priced when <paramref name="priced"/>.</summary>
    private static ForecastValueResult Ledger(Resource def, int units, IReadOnlyDictionary<Resource, ConstructionMaterialValue> values, bool priced = true)
    {
        var context = new ForecastCoupledContext
        {
            Activities = new()
            {
                Activities =
                [
                    new(Delivery, "stack", "planned/room1", []) { Inputs = [new(def, Known((double)units))] },
                    new(Refuel, "stack", "stove", []) { Inputs = [new(def, Known(5d))] },
                ],
            },
            Needs = new(), Resources = ForecastValueTests.Resources(),
            PlannedSites = [new("planned/room1/wall/WoodLog", 1, "WoodLog", [Delivery, "planned/room1/wall/WoodLog/finish"], [])],
            PlannedRoomRoles = new Dictionary<int, PlannedRole> { [1] = PlannedRole.Bedroom },
            RoomCredits = priced ? PlannedRoomCredits.Shipped : new Dictionary<PlannedRole, RoomCredit>(),
            MaterialValues = values,
        };
        var plan = new ForecastActivityPlan(
            [new(Delivery, "builder", 0, 0, 0, 100, 0, 0, 100, 100, 0), new(Refuel, "builder", 100, 100, 100, 200, 0, 0, 100, 200, 0)], 200, 200);
        return ForecastValue.Of(new(plan, Known(true), new Dictionary<string, ForecastNeedsResult>(), []), context, GameTime.TicksPerDay,
            new(new([], 0, 0), Known(true), new Dictionary<string, ForecastNeedsResult>(), []));
    }

    [Fact]
    public void WoodDeliveredToAPricedRoomIsChargedItsChopWorkAndHaulPerUnit()
    {
        var value = ForecastValue.MaterialValueOf(Wood, [Tree(40), Tree(Distance)], Storage, Haul, Items);
        Assert.Equal(MaterialValueBasis.Labor, value.Basis);
        // The nearest tree's trip is carried a load of min(stack limit, yield) logs.
        var perUnit = ((ChopWork / ChopYield) + (Distance * 13 / Math.Min(StackLimit, ChopYield))) / GameTime.TicksPerDay;
        Assert.Equal(perUnit, value.PerUnit, 12);

        var ledger = Ledger(Wood, 20, new Dictionary<Resource, ConstructionMaterialValue> { [Wood] = value });

        var row = Assert.Single(ledger.InvestmentContributions);
        Assert.Equal((Delivery, "builder", "material/WoodLog", 20d), (row.Activity, row.Pawn, row.Key, row.Units));
        Assert.Equal(20 * perUnit, row.Value, 12);
        Assert.Equal(20 * perUnit, ledger.Investment.Value, 12);
        Assert.DoesNotContain(ledger.Findings, f => f.StartsWith("material_value_", StringComparison.Ordinal));
    }

    [Fact]
    public void AnUnpricedRoomsDeliveryStaysFree()
    {
        var value = ForecastValue.MaterialValueOf(Wood, [Tree(Distance)], Storage, Haul, Items);

        var ledger = Ledger(Wood, 20, new Dictionary<Resource, ConstructionMaterialValue> { [Wood] = value }, priced: false);

        Assert.Empty(ledger.InvestmentContributions);
        Assert.Equal(0, ledger.Investment.Value);
    }

    [Fact]
    public void SteelWithNoOreInReachFallsBackToMarketValue()
    {
        var buried = new ResourceSource { ThingId = "ore", Yield = 35, Distance = 5, Method = ResourceSourceMethod.Mine, Buried = true };
        var value = ForecastValue.MaterialValueOf(Steel, [buried], Storage, Haul, Items);
        Assert.Equal(new ConstructionMaterialValue(MaterialValueBasis.Market, 1.9 / SilverGapRules.ValuePerWorkTick / GameTime.TicksPerDay), value);
        // A failed read prices it the same way.
        Assert.Equal(value, ForecastValue.MaterialValueOf(Steel, null, null, Haul, Items));

        var ledger = Ledger(Steel, 10, new Dictionary<Resource, ConstructionMaterialValue> { [Steel] = value });

        var row = Assert.Single(ledger.InvestmentContributions);
        Assert.Equal(("material/Steel", 10d), (row.Key, row.Units));
        Assert.Equal(10 * value.PerUnit, ledger.Investment.Value, 12);
        Assert.Contains("material_value_market:Steel", ledger.Findings);
    }

    [Fact]
    public void AMaterialWithNeitherASourceNorAMarketValueIsChargedNothing()
    {
        var value = ForecastValue.MaterialValueOf(Plasteel, [], Storage, Haul, Items);
        Assert.Equal(MaterialValueBasis.Unpriced, value.Basis);

        var ledger = Ledger(Plasteel, 10, new Dictionary<Resource, ConstructionMaterialValue> { [Plasteel] = value });

        Assert.Empty(ledger.InvestmentContributions);
        Assert.Equal(0, ledger.Investment.Value);
        Assert.Contains("material_value_unpriced:Plasteel", ledger.Findings);
    }
}
