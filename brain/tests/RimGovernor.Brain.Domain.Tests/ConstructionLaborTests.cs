using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using Xunit;
using static RimGovernor.Brain.Policy.PolicyFunctions;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class ConstructionLaborTests
{
    private static readonly RoundsLatches Shelter = new() { Housing = Phase.HousingShelter };
    private static readonly RoundsLatches Standing = new();
    private const int Foothold = 2;
    private const int Critical = 1;
    private const int Maintenance = 3;

    private static bool Held(RoundsLatches latches, ConcernId need, int priority, params ActionKind[] kinds) =>
        ConstructionLaborHeld(latches, default, need, priority, kinds);

    [Fact]
    public void SuppliesAreNeverHeldBehindTheShelterThatAsksForThem()
    {
        // The shelter's wood demand is served by gathering and growing: holding those deadlocks the colony (#3147).
        Assert.False(Held(Shelter, MaintainResource, Foothold, ActionKind.Acquisition, ActionKind.Gathering));
        Assert.False(Held(Shelter, EnsureFoodSupply, Foothold, ActionKind.Acquisition, ActionKind.ZoneCreate, ActionKind.GrowerCrop));
        Assert.False(Held(Shelter, MaintainStockpiles, Foothold, ActionKind.ZoneCellEdit, ActionKind.StockpilePatch));
    }

    [Fact]
    public void BuildersAreHeldForTheShelterOnlyWhileItIsOwed()
    {
        Assert.True(Held(Shelter, MaintainStockpiles, Foothold, ActionKind.ZoneCellEdit, ActionKind.Building));
        Assert.True(Held(Shelter, EnsureDefensiveLayout, Maintenance, ActionKind.CoverClearance));
        Assert.True(Held(Shelter, ClearHomeObstructions, Maintenance, ActionKind.Deconstruction));
        Assert.False(Held(Standing, MaintainStockpiles, Foothold, ActionKind.ZoneCellEdit, ActionKind.Building));
    }

    [Fact]
    public void TheShelterItselfEmergenciesAndCriticalWorkKeepTheirBuilders()
    {
        Assert.False(Held(Shelter, MaintainHousing, Foothold, ActionKind.Building));
        Assert.False(Held(Shelter, MaintainMedicalReserves, Critical, ActionKind.Building));
        Assert.False(ConstructionLaborHeld(Shelter, EquatableArray.From([EnsureFoodSupply]), EnsureFoodSupply, Foothold, [ActionKind.Building]));
        Assert.True(ConstructionLaborHeld(Shelter, EquatableArray.From([MaintainHousing]), EnsureFoodSupply, Foothold, [ActionKind.Building]));
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
