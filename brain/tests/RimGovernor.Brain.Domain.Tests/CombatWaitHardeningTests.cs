using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class CombatWaitHardeningTests
{
    private static readonly Resource Material = new("tough-metal");
    private static readonly Resource Component = new("component");
    private static readonly CombatMemory Waiting = new() { Wait = true, WaitRooms = [new(new(1, 1), new(1, 2)), new(new(3, 1), new(3, 2))] };
    private static readonly IReadOnlyDictionary<Cell, WaitDoorCell> Census = Waiting.WaitRooms.ToDictionary(room => room.Door,
        _ => new WaitDoorCell { Edifice = "supplied-door", Stuff = "old-stuff" });

    private static PlanningDefinition Door(params Amount[] costs) => new()
    {
        Name = "supplied-door", Available = true, Stuffed = true,
        StuffOptions = [new StuffOption { Stuff = Material.Value, Costs = costs }],
    };

    [Theory]
    [InlineData(25, 0)]
    [InlineData(40, 1)]
    [InlineData(79, 1)]
    [InlineData(80, 2)]
    public void NativeDoorCostDeterminesHowManyUpgradesTheSharedStockCovers(long units, int expected)
    {
        var stock = new Dictionary<Resource, long> { [Material] = units };
        var buildings = CombatWaitRules.WaitHardening(Waiting, Census, stock, Door(new Amount(Material, 40)), Material.Value, "wall", "wall-stuff");
        Assert.Equal(expected, buildings.Count);
        Assert.Equal(units, stock[Material]);
        foreach (var building in buildings)
        {
            Assert.Equal("supplied-door", building.Definition);
            Assert.Equal(Material.Value, building.Stuff);
            var action = DomainAction.NewBuildingAction(new("harden-0"), building, ConstructionTier.Sustain);
            var intent = Assert.IsType<RimGovernor.Brain.GameOrders.Operations.ActionIntent.BuildingCase>(IntentActionRules.IntentAction("harden", action).Intent).Value;
            Assert.Equal(building.Definition, intent.Placement!.DefName);
        }
    }

    [Fact]
    public void EveryResourceAndRepeatedCostEntryCountsBeforeAnotherDoorIsPlanned()
    {
        var door = Door(new Amount(Material, 20), new(Material, 20), new(Component, 3));
        var stock = new Dictionary<Resource, long> { [Material] = 80, [Component] = 3 };
        Assert.Single(CombatWaitRules.WaitHardening(Waiting, Census, stock, door, Material.Value, "wall", "wall-stuff"));
        Assert.Empty(CombatWaitRules.WaitHardening(Waiting, Census, stock, door, "not-an-allowed-stuff", "wall", "wall-stuff"));
        Assert.Empty(CombatWaitRules.WaitHardening(Waiting, Census, stock, door with { Available = false }, Material.Value, "wall", "wall-stuff"));
        stock[Material] = 25;
        Assert.Empty(CombatWaitRules.WaitHardening(Waiting, Census, stock, door, Material.Value, "wall", "wall-stuff"));
        Assert.Throws<RequiredFactException>(() => CombatWaitRules.WaitHardening(Waiting, Census, stock, Door(new Amount(Material, -1)), Material.Value, "wall", "wall-stuff"));
    }

    [Fact]
    public void BrokenDoorProtectionAndAlreadyHardenedDoorsKeepTheirExistingBehavior()
    {
        var room = Waiting.WaitRooms[0];
        var census = new Dictionary<Cell, WaitDoorCell>
        {
            [room.Door] = new(), [room.Inside] = new() { Walkable = true },
            [Waiting.WaitRooms[1].Door] = new() { Edifice = "supplied-door", Stuff = Material.Value },
        };
        var building = Assert.Single(CombatWaitRules.WaitHardening(Waiting, census, new Dictionary<Resource, long>(), Door(new Amount(Material, 40)), Material.Value, "wall", "wall-stuff"));
        Assert.Equal(new Building("wall", room.Inside, Rotation.North, "wall-stuff"), building);
        Assert.Empty(CombatWaitRules.WaitHardening(Waiting with { Wait = false }, census, new Dictionary<Resource, long>(), Door(), Material.Value, "wall", "wall-stuff"));
    }
}
