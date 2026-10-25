using RimGovernor.Contracts.Wire;
using Xunit;
using C = RimGovernor.Contracts.Wire.Common;
using O = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Domain.Tests;

// #3935: the loot census is a plain required list of rows whose hauling verdict is a named union and whose storage
// headroom is a plain number; each state round-trips exactly through the wire JSON and none is a missing value.
public sealed class LootWireTests
{
    [Fact]
    public void PolicyRetainsTheNativeRowAndEntityUntilSelectingSupplyWork()
    {
        var observed = Row(new O.HaulSafety.Safe { PathLength = 12.5 }, 30, "Zone_a", "Shelf_b");
        var entity = new O.EntityRef
        {
            Id = observed.Item.Id!, DefName = "WoodLog", Label = "wood",
            Place = new O.EntityPlace.OnMap(7, new(14, 19)),
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
            Observed = observed with { Forbidden = true, Count = 12, StorageHeadroom = 0, Safety = new O.HaulSafety.Unreached() },
            Entity = entity with { Place = new O.EntityPlace.OnMap(7, new(20, 21)) },
        };
        Assert.True(moved.Forbidden);
        Assert.Equal(12, moved.Count);
        Assert.Equal(0, moved.StorageHeadroom);
        Assert.True(moved.IsUnreached);
        Assert.Equal(new RimGovernor.Brain.Domain.Cell(20, 21), moved.Supply.Cell);
        Assert.False(moved.Supply.Forbid);
    }

    private static O.LootItem Row(O.HaulSafety haul, long headroom, params string[] accepted) => new()
    {
        Item = new C.Ref { Id = "Thing_Wood1" },
        Forbidden = false,
        Count = 40,
        Safety = haul,
        StorageHeadroom = headroom,
        AcceptedBy = EquatableArray.From(accepted.Select(id => new C.Ref { Id = id })),
        Deterioration = new O.LootDeterioration { BaseRatePerDay = 2, MaxHitPoints = 100, RatePerDay = 2, Outdoors = true },
    };

    private static void RoundTrips<T>(T payload)
    {
        var json = WireJson.Serialize(payload);
        var back = WireJson.Deserialize<T>(json);

        Assert.Equal(json, WireJson.Serialize(back));
        Assert.Equal(payload, back);
    }

    [Fact]
    public void ASafeStackRoundTripsWithItsRouteLength() => RoundTrips(Row(new O.HaulSafety.Safe { PathLength = 12.5 }, 0, "Zone_a", "Shelf_b"));

    [Fact]
    public void AnExposedStackRoundTrips() => RoundTrips(Row(new O.HaulSafety.Unsafe(), 30));

    [Fact]
    public void AnUnreachedStackRoundTrips() => RoundTrips(Row(new O.HaulSafety.Unreached(), 0));

    [Fact]
    public void TheHaulStatesAreDistinctOnTheWire()
    {
        var safe = WireJson.Serialize(Row(new O.HaulSafety.Safe { PathLength = 1 }, 0));
        var exposed = WireJson.Serialize(Row(new O.HaulSafety.Unsafe(), 0));
        var unreached = WireJson.Serialize(Row(new O.HaulSafety.Unreached(), 0));

        Assert.Equal(3, new[] { safe, exposed, unreached }.Distinct().Count());
        Assert.IsType<O.HaulSafety.Unsafe>(WireJson.Deserialize<O.LootItem>(exposed).Safety);
        Assert.IsType<O.HaulSafety.Unreached>(WireJson.Deserialize<O.LootItem>(unreached).Safety);
        Assert.Equal(1, Assert.IsType<O.HaulSafety.Safe>(WireJson.Deserialize<O.LootItem>(safe).Safety).PathLength);
    }

    [Fact]
    public void ARowWithoutASafetyVerdictOrHeadroomIsRefused()
    {
        var json = WireJson.Serialize(Row(new O.HaulSafety.Safe { PathLength = 1 }, 0));

        Assert.ThrowsAny<Exception>(() => WireJson.Deserialize<O.LootItem>(json.Replace("\"safety\"", "\"safetyX\"")));
        Assert.ThrowsAny<Exception>(() => WireJson.Deserialize<O.LootItem>(json.Replace("\"baseRatePerDay\"", "\"baseRatePerDayX\"")));
        Assert.ThrowsAny<Exception>(() => WireJson.Deserialize<O.LootItem>(json.Replace("\"storageHeadroom\"", "\"storageHeadroomX\"")));
    }
}
