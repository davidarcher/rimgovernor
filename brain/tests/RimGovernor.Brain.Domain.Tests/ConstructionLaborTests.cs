using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using Xunit;
using static RimGovernor.Brain.Policy.PolicyFunctions;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class ConstructionLaborTests
{
    private static readonly RoundsLatches Shelter = new() { Housing = Phase.HousingShelter };
    private static readonly RoundsLatches Standing = new();
    private static bool Held(RoundsLatches latches, ConcernId need, params ActionKind[] kinds) =>
        ConstructionLaborHeld(latches, need, kinds);

    [Fact]
    public void SuppliesAreNeverHeldBehindTheShelterThatAsksForThem()
    {
        // The shelter's wood demand is served by gathering and growing: holding those deadlocks the colony (#3147).
        Assert.False(Held(Shelter, EconomyFamily.Resource.FilingKey, ActionKind.Acquisition, ActionKind.Gathering));
        Assert.False(Held(Shelter, FoodSupplyNeed.FilingKey, ActionKind.Acquisition, ActionKind.ZoneCreate, ActionKind.GrowerCrop));
        Assert.False(Held(Shelter, MaintainStockpiles, ActionKind.ZoneCellEdit, ActionKind.StockpilePatch));
    }

    [Fact]
    public void BuildersAreHeldForTheShelterOnlyWhileItIsOwed()
    {
        Assert.True(Held(Shelter, MaintainStockpiles, ActionKind.ZoneCellEdit, ActionKind.Building));
        Assert.True(Held(Shelter, EnsureDefensiveLayout, ActionKind.CoverClearance));
        // A Need owner reports no Concern of its own (the home-upkeep clearance Need, #3253).
        Assert.True(Held(Shelter, default, ActionKind.Deconstruction));
        Assert.False(Held(Standing, MaintainStockpiles, ActionKind.ZoneCellEdit, ActionKind.Building));
    }

    [Fact]
    public void TheShelterItselfKeepsItsBuilders()
    {
        Assert.False(Held(Shelter, MaintainHousing, ActionKind.Building));
    }

    [Fact]
    public void OnlyConstructionWorkDrawsOnTheBuilders()
    {
        Assert.All([ActionKind.Building, ActionKind.BuildRoof, ActionKind.RemoveRoof, ActionKind.Deconstruction, ActionKind.FloorRemoval,
            ActionKind.FoundationRemoval, ActionKind.WallRemoval, ActionKind.MoveBuilding, ActionKind.UninstallBuilding, ActionKind.CoverClearance],
            k => Assert.True(DrawsConstructionLabor(k)));
        // Digging is Mining work, hauling and growing their own work, bills the crafters'.
        Assert.All([ActionKind.Excavation, ActionKind.MineAcquisition, ActionKind.Haul, ActionKind.GrowerCrop, ActionKind.ProductionBill, ActionKind.Repair],
            k => Assert.False(DrawsConstructionLabor(k)));
    }
}
