using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Policy;
using Xunit;
using O = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class EquipNativeOwnershipTests
{
    [Fact]
    public void LooseWeaponsRetainNativeRowsAndSkipItemsWithoutSnapshots()
    {
        var item = new O.StockItem { Id = "weapon", Cell = new(3, 4), Snapshot = new() { EntityId = "weapon", Token = "token" } };
        var gear = new O.GearItem
        {
            Thing = new() { Id = item.Id }, Quality = new O.ItemQuality.Qualityless(), ConditionFraction = 1,
            Forced = false, Locked = false, BiocodedTo = null, Biocoded = true,
        };
        var stock = new O.ResourceStock
        {
            DefName = "weapon-def", Units = 2, Stacks = 2, Spawned = 2, Ours = 2, OursUnforbidden = 2, Forbidden = 0, PlayerFaction = 2,
            Items = [item, new() { Id = "ineligible", Cell = new(5, 6) }], Holders = [], WeaponItems = [gear],
        };
        var read = FacadeNativeClient.SupplyDecodeEquipWeapons(new() { Context = BillWire.Context, Stocks = [stock] });
        var candidate = Assert.Single(read.Targets);
        Assert.Same(stock, candidate.Stock);
        Assert.Same(item, candidate.Item);
        Assert.Same(gear, candidate.Gear);
        var weapon = new EquipCandidateWeapon { Stock = candidate.Stock, Item = candidate.Item, Gear = candidate.Gear };
        Assert.True(weapon.Biocoded);
        Assert.True(weapon.BiocodedTo.IsEmpty);
        Assert.Equal(item.Cell, weapon.Cell);

        var uncoded = Assert.Single(FacadeNativeClient.SupplyDecodeEquipWeapons(new() { Context = BillWire.Context, Stocks = [stock with { WeaponItems = [] }] }).Targets);
        Assert.Null(uncoded.Gear);
        Assert.False((weapon with { Gear = uncoded.Gear }).Biocoded);
    }
}
