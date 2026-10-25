using RimGovernor.Brain.Domain;
using RimGovernor.Brain.GameFacts;
using RimGovernor.Brain.GameOrders;
using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The structure census retains Home coverage and joins canonical building state. Flammability and repair priority
/// are the catalog's, not the wire's. On the wire a row round-trips through
/// <c>OrdersJsonContext</c> exactly.
/// </summary>
public sealed class UpkeepStructureTests
{
    [Fact]
    public void JsonRoundTripIsExact()
    {
        EquatableArray<UpkeepStructure> rows =
        [
            new() { Id = "Thing_Wall3", Home = true },
            new() { Id = "Thing_Cooler4", Home = false },
        ];

        var json = OrdersJson.Serialize(rows);

        Assert.Equal(
            """[{"id":"Thing_Wall3","home":true},{"id":"Thing_Cooler4","home":false}]""",
            json);
        Assert.All(rows, row => Assert.Equal(row, OrdersJson.Deserialize<UpkeepStructure>(OrdersJson.Serialize(row))));
    }

    [Fact]
    public void RepairUsesCanonicalBuildingHitPointsAndHomeCoverage()
    {
        var native = new UpkeepStructure { Id = "wall", Home = true };
        var hp = new BuildingHitPoints.Tracked { Current = 70, Maximum = 100 };
        var building = PowerBuildingFixtures.Trader("wall", "Wall", new(3, 4), 0, 0, false) with { HitPoints = hp };
        var repair = new RimGovernor.Brain.Policy.RepairStructure(native, building, 1, 0);

        Assert.Same(native, repair.Observed);
        Assert.Same(building, repair.Building);
        Assert.Same(hp, repair.HitPoints);
        var damaged = RimGovernor.Brain.Policy.HomeUpkeepFamily.DamagedStructures([repair]);
        Assert.Equal(["wall"], damaged.Targets);
        Assert.Equal(30, damaged.Missing);
        Assert.Empty(RimGovernor.Brain.Policy.HomeUpkeepFamily.DamagedStructures(
            [repair with { Building = building with { HitPoints = hp with { Current = 100 } } }]).Targets);
        Assert.Empty(RimGovernor.Brain.Policy.HomeUpkeepFamily.DamagedStructures(
            [repair with { Observed = native with { Home = false } }]).Targets);
        Assert.Throws<DomainException>(() => RimGovernor.Brain.Policy.HomeUpkeepFamily.DamagedStructures(
            [repair with { Building = building with { HitPoints = new BuildingHitPoints.Untracked() } }]));
    }
}
