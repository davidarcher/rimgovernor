using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using Xunit;
using static RimGovernor.Brain.Policy.PolicyFunctions;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class GearZonesTests
{
    private static PlannedRoom Room(int id, PlannedRole role, int x, int z, int width, int height) =>
        new() { Id = id, Role = role, Region = RegionRules.RectRegion(new Rectangle { X = x, Z = z, Width = width, Height = height }), Door = new Cell(x, z - 1), DoorRot = Rotation.South };

    private static LayoutPlan Plan(params PlannedRoom[] rooms) => new() { Rooms = rooms, NextRoomId = rooms.Length + 1 };

    private static StoreView View(LayoutPlan plan)
    {
        var (weapons, apparel) = StoreRules.GearFilters([new Resource("Armor")]);
        return new StoreView { Layout = plan, Gear = new GearStore { Weapons = weapons, Apparel = apparel } };
    }

    private const int Clearance = 6;

    private static IEnumerable<Cell> Cells(Rectangle r) => Enumerable.Range(r.X, r.Width).SelectMany(x => Enumerable.Range(r.Z, r.Height).Select(z => new Cell(x, z)));

    private static bool WithinClearance(Cell c, IEnumerable<Cell> prisons) => prisons.Any(p => Math.Max(Math.Abs(c.X - p.X), Math.Abs(c.Z - p.Z)) <= Clearance);

    [Fact]
    public void TheWeaponsZoneIsNeverSitedWithinAPrisonsWeaponClearance()
    {
        var plan = Plan(Room(1, LayoutSurveyRules.PlannedStorage, 10, 10, 15, 7), Room(2, LayoutSurveyRules.PlannedPrison, 26, 10, 5, 5));
        var patches = StoreRules.GearPatchesOf(plan);
        var weapons = Assert.IsType<Rectangle>(patches.Weapons);
        Assert.NotNull(patches.Apparel);
        Assert.DoesNotContain(Cells(weapons), c => WithinClearance(c, PrisonLayoutRules.PrisonCells(plan)));

        // The warehouse leaves both patches out, and the declared gear zones stand on exactly them.
        var declared = StoreRules.DeclareStores(View(plan));
        var warehouse = Assert.Single(declared.Stores, s => s.Role == StockpileRolesConstants.GeneralRole);
        Assert.Equal(18, warehouse.Site.Avoid.Count);
        var gear = Assert.Single(declared.Stores, s => s.Role == StockpileRolesConstants.WeaponsRole);
        Assert.Equal(weapons, gear.Site.Interior);
        Assert.Contains(declared.Stores, s => s.Role == StockpileRolesConstants.ApparelRole);
    }

    [Fact]
    public void AWarehouseEntirelyInsideAPrisonsClearanceHostsNoWeaponsZone()
    {
        var plan = Plan(Room(1, LayoutSurveyRules.PlannedStorage, 10, 10, 8, 5), Room(2, LayoutSurveyRules.PlannedPrison, 19, 10, 5, 5));
        var patches = StoreRules.GearPatchesOf(plan);
        Assert.Null(patches.Weapons);
        Assert.NotNull(patches.Apparel);
        var declared = StoreRules.DeclareStores(View(plan));
        Assert.DoesNotContain(declared.Stores, s => s.Role == StockpileRolesConstants.WeaponsRole);
        Assert.Contains(declared.Stores, s => s.Role == StockpileRolesConstants.ApparelRole);
    }

    [Fact]
    public void TheWeaponsZoneMovesToAWarehouseClearOfEveryPrison()
    {
        var plan = Plan(Room(1, LayoutSurveyRules.PlannedStorage, 10, 10, 8, 5), Room(2, LayoutSurveyRules.PlannedPrison, 19, 10, 5, 5), Room(3, LayoutSurveyRules.PlannedStorage, 60, 10, 15, 7));
        var weapons = Assert.IsType<Rectangle>(StoreRules.GearPatchesOf(plan).Weapons);
        Assert.True(weapons.X >= 60);
    }
}
