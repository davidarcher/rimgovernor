using System.Text.Json;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.GameFacts;
using RimGovernor.Brain.GameOrders;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

// #3877: the resource method and the supply request read whole: an invalid request is a defect, not an unknown
// method, and the supply ranker takes its demands, candidates and labor budget plainly.
public sealed class ResourceMethodTests
{
    [Fact]
    public void AnInvalidResourceMethodRequestThrows()
    {
        Assert.Throws<DomainException>(() => ResourceRules.SelectResourceMethod(new ResourceMethodRequest { Resource = new(""), Target = 10 }));
        Assert.Throws<DomainException>(() => ResourceRules.SelectResourceMethod(new ResourceMethodRequest { Resource = new("Steel"), Target = 0 }));
    }

    [Fact]
    public void AResourceNoBenchOrCraftMakesIsBlocked()
    {
        var method = ResourceRules.SelectResourceMethod(new ResourceMethodRequest { Resource = new("Steel"), Target = 10, Benches = [], Crafts = [] });

        Assert.Equal(ResourceMethodKind.Blocked, method.Kind);
    }

    [Fact]
    public void ASupplyPlanNeedsAFiniteLaborBudget()
    {
        Assert.Throws<SupplyPlanFactsException>(() => SupplyPlanRules.PlanSupply(new SupplyPlanRequest { Demands = [], Candidates = [], Labor = double.NaN }));
        Assert.Throws<SupplyPlanFactsException>(() => SupplyPlanRules.PlanSupply(new SupplyPlanRequest { Demands = [], Candidates = [], Labor = -1 }));
        Assert.NotNull(SupplyPlanRules.PlanSupply(new SupplyPlanRequest { Demands = [], Candidates = [], Labor = 0 }));
    }

    // #3877: a placement preview's material row is counted whole; an incomplete scan sends no materials at all.
    [Fact]
    public void APlacementMaterialRowCarriesItsCount()
    {
        var row = new RimGovernor.Brain.GameFacts.Placement.PlacementMaterialStock { DefName = "Steel", Available = 0 };

        Assert.Equal(row, OrdersJson.Deserialize<RimGovernor.Brain.GameFacts.Placement.PlacementMaterialStock>(OrdersJson.Serialize(row)));
        Assert.ThrowsAny<JsonException>(() => OrdersJson.Deserialize<RimGovernor.Brain.GameFacts.Placement.PlacementMaterialStock>("""{"defName":"Steel"}"""));
    }

    // #3877: resource demand reads a whole stock census: an absent key is zero, a present one spends the target once.
    [Fact]
    public void ResourceDemandSpendsAWholeStockCensus()
    {
        var steel = new ResourceKey { Def = new Resource("Steel") };
        var wood = new ResourceKey { Def = new Resource("WoodLog") };
        var input = new ResourceDemandInput
        {
            Targets = [new ResourceDemand { Key = steel, Count = 50 }, new ResourceDemand { Key = wood, Count = 20 }],
            Stock = [new ResourceQuantity { Key = steel, Count = 30 }],
        };

        Assert.Equal([new ResourceDemand { Key = steel, Count = 20 }, new ResourceDemand { Key = wood, Count = 20 }], ResourceRules.BuildResourceDemand(input).ToList());
    }
}