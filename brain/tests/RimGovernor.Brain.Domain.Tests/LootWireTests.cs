using RimGovernor.Brain.GameFacts;
using RimGovernor.Brain.GameOrders;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

// #3935: the loot census is a plain required list of rows whose hauling verdict is a named union and whose storage
// headroom is a plain number; each state round-trips exactly through the wire JSON and none is a missing value.
public sealed class LootWireTests
{
    [Fact]
    public void PolicyRetainsTheNativeRowAndEntityUntilSelectingSupplyWork()
    {
        var observed = Row(new RimGovernor.Brain.GameFacts.Observations.HaulSafety.Safe { PathLength = 12.5 }, 30, "Zone_a", "Shelf_b");
        var entity = new RimGovernor.Brain.GameFacts.Observations.EntityRef
        {
            Id = observed.Item.Id!, DefName = "WoodLog", Label = "wood",
            Place = new RimGovernor.Brain.GameFacts.Observations.EntityPlace.OnMap(7, new(14, 19)),
        };
        var stack = new Policy.LootItem { Observed = observed, Entity = entity, SpawnForbidden = true };

        Assert.Same(observed, stack.Observed);
        Assert.Same(entity, stack.Entity);
        Assert.Same(observed.Safety, stack.Haul);
        Assert.Same(observed.Deterioration, stack.Deterioration);
        Assert.Equal(observed.Count, stack.Count);
        Assert.Equal(observed.StorageHeadroom, stack.StorageHeadroom);
        Assert.Equal(["Zone_a", "Shelf_b"], stack.AcceptedBy);
        Assert.True(stack.SpawnForbidden);
        Assert.Equal(new Policy.StartingSupply { Thing = observed.Item.Id!, Definition = entity.DefName, Cell = new(14, 19) }, stack.Supply);

        var moved = stack with
        {
            Observed = observed with { Forbidden = true, Count = 12, StorageHeadroom = 0, Safety = new RimGovernor.Brain.GameFacts.Observations.HaulSafety.Unreached() },
            Entity = entity with { Place = new RimGovernor.Brain.GameFacts.Observations.EntityPlace.OnMap(7, new(20, 21)) },
        };
        Assert.True(moved.Forbidden);
        Assert.Equal(12, moved.Count);
        Assert.Equal(0, moved.StorageHeadroom);
        Assert.True(moved.IsUnreached);
        Assert.Equal(new RimGovernor.Brain.Domain.Cell(20, 21), moved.Supply.Cell);
        Assert.False(moved.Supply.Forbid);
    }

    private static RimGovernor.Brain.GameFacts.Observations.LootItem Row(RimGovernor.Brain.GameFacts.Observations.HaulSafety haul, long headroom, params string[] accepted) => new()
    {
        Item = new RimGovernor.Brain.GameFacts.Common.Ref { Id = "Thing_Wood1" },
        Forbidden = false,
        Count = 40,
        Safety = haul,
        StorageHeadroom = headroom,
        AcceptedBy = EquatableArray.From(accepted.Select(id => new RimGovernor.Brain.GameFacts.Common.Ref { Id = id })),
        Deterioration = new RimGovernor.Brain.GameFacts.Observations.LootDeterioration { BaseRatePerDay = 2, MaxHitPoints = 100, RatePerDay = 2, Outdoors = true },
    };

    private static void RoundTrips<T>(T payload)
    {
        var json = OrdersJson.Serialize(payload);
        var back = OrdersJson.Deserialize<T>(json);

        Assert.Equal(json, OrdersJson.Serialize(back));
        Assert.Equal(payload, back);
    }

    [Fact]
    public void ASafeStackRoundTripsWithItsRouteLength() => RoundTrips(Row(new RimGovernor.Brain.GameFacts.Observations.HaulSafety.Safe { PathLength = 12.5 }, 0, "Zone_a", "Shelf_b"));

    [Fact]
    public void AnExposedStackRoundTrips() => RoundTrips(Row(new RimGovernor.Brain.GameFacts.Observations.HaulSafety.Unsafe(), 30));

    [Fact]
    public void AnUnreachedStackRoundTrips() => RoundTrips(Row(new RimGovernor.Brain.GameFacts.Observations.HaulSafety.Unreached(), 0));

    [Fact]
    public void TheHaulStatesAreDistinctOnTheWire()
    {
        var safe = OrdersJson.Serialize(Row(new RimGovernor.Brain.GameFacts.Observations.HaulSafety.Safe { PathLength = 1 }, 0));
        var exposed = OrdersJson.Serialize(Row(new RimGovernor.Brain.GameFacts.Observations.HaulSafety.Unsafe(), 0));
        var unreached = OrdersJson.Serialize(Row(new RimGovernor.Brain.GameFacts.Observations.HaulSafety.Unreached(), 0));

        Assert.Equal(3, new[] { safe, exposed, unreached }.Distinct().Count());
        Assert.IsType<RimGovernor.Brain.GameFacts.Observations.HaulSafety.Unsafe>(OrdersJson.Deserialize<RimGovernor.Brain.GameFacts.Observations.LootItem>(exposed).Safety);
        Assert.IsType<RimGovernor.Brain.GameFacts.Observations.HaulSafety.Unreached>(OrdersJson.Deserialize<RimGovernor.Brain.GameFacts.Observations.LootItem>(unreached).Safety);
        Assert.Equal(1, Assert.IsType<RimGovernor.Brain.GameFacts.Observations.HaulSafety.Safe>(OrdersJson.Deserialize<RimGovernor.Brain.GameFacts.Observations.LootItem>(safe).Safety).PathLength);
    }

    [Fact]
    public void ARowWithoutASafetyVerdictOrHeadroomIsRefused()
    {
        var json = OrdersJson.Serialize(Row(new RimGovernor.Brain.GameFacts.Observations.HaulSafety.Safe { PathLength = 1 }, 0));

        Assert.ThrowsAny<Exception>(() => OrdersJson.Deserialize<RimGovernor.Brain.GameFacts.Observations.LootItem>(json.Replace("\"safety\"", "\"safetyX\"")));
        Assert.ThrowsAny<Exception>(() => OrdersJson.Deserialize<RimGovernor.Brain.GameFacts.Observations.LootItem>(json.Replace("\"baseRatePerDay\"", "\"baseRatePerDayX\"")));
        Assert.ThrowsAny<Exception>(() => OrdersJson.Deserialize<RimGovernor.Brain.GameFacts.Observations.LootItem>(json.Replace("\"storageHeadroom\"", "\"storageHeadroomX\"")));
    }
}
