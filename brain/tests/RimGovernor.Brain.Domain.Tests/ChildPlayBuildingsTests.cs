using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The layout plans the buildings the babies' play uses (#3837): the room the babies sleep in carries one of each
/// play building the catalog reads from the BabyPlayDef givers (the toy box), in its stations and its area, and the
/// nursery and playroom furnish it; a load without Biotech plans none.
/// </summary>
public sealed class ChildPlayBuildingsTests
{
    private const string Crib = "Crib";
    private const string ToyBox = "ToyBox";
    private const string Decoration = "BabyDecoration";

    // A crib as the catalog scores it (a humanlike bed too small for a child) and a toy box (baby play power above one: a playroom piece).
    private static StationCatalog Catalog(params string[] play) => new()
    {
        PlayBuildings = play,
        Stations = new Dictionary<string, Station>
        {
            [Crib] = new()
            {
                Shape = new InteriorPieceDef(Crib, new Cell(1, 1), RoomRole.Nursery, null),
                Capacity = new StationCapacity { Kind = CapacityKind.Sleepers, Slots = 1 },
                Contributions =
                [
                    new() { Role = RoomRole.Nursery, When = RoleCondition.BedFree, Weight = 100200, Additive = true },
                    new() { Role = RoomRole.Hospital, When = RoleCondition.BedMedical, Weight = 100000 },
                    new() { Role = RoomRole.PrisonCell, When = RoleCondition.BedPrisoner, Weight = 100000 },
                ],
            },
            [ToyBox] = new()
            {
                Shape = new InteriorPieceDef(ToyBox, new Cell(1, 1), RoomRole.Playroom, null),
                Contributions = [new() { Role = RoomRole.Playroom, When = RoleCondition.Always, Weight = 8, Additive = true }],
            },
        },
    };

    private static ClusterInput Babies(StationCatalog catalog) => new()
    {
        Graph = new StationGraph { Nodes = [new StationNode { Id = "Crib/sleep_baby", Def = Crib, Purpose = StationPurpose.SleepBaby, Count = 2 }] },
        Catalog = catalog,
        RoofDistance = 6,
    };

    [Fact]
    public void ThePlannedNurseryHoldsThePlayBuildingsTheDefsName()
    {
        var bare = Assert.Single(LayoutClusterRules.ClusterStations(Babies(Catalog())).Groups);
        var g = Assert.Single(LayoutClusterRules.ClusterStations(Babies(Catalog(ToyBox))).Groups);

        Assert.Equal(RoomRole.Nursery, g.Role);
        var play = Assert.Single(g.Stations, s => s.Purpose == StationPurpose.Play);
        Assert.Equal(new GroupStation("ToyBox/play", ToyBox, StationPurpose.Play, 1), play);
        // The toy box's cell and its clearance cell join the room's area.
        Assert.Equal(bare.Footprint + 1, g.Footprint);
        Assert.Equal(bare.Area + 2, g.Area);
        // It is no node of the graph.
        Assert.Equal(["Crib/sleep_baby"], LayoutRegroupRules.RoomNodes(g));
    }

    [Fact]
    public void AColonyWithoutBiotechPlansNoPlayBuilding()
    {
        // Without Biotech the catalog reads no BabyPlayDef, so it names no play building.
        var g = Assert.Single(LayoutClusterRules.ClusterStations(Babies(Catalog())).Groups);
        Assert.DoesNotContain(g.Stations, s => s.Purpose == StationPurpose.Play);
        Assert.Empty(ChildRoomsRules.ChildRoomNeeds([], null));
    }

    [Fact]
    public void APlayBuildingTheCatalogHasNoStationForFailsTheClustering()
    {
        var catalog = Catalog() with { PlayBuildings = ["ModdedToy"] };
        Assert.Throws<ClusterException>(() => LayoutClusterRules.ClusterStations(Babies(catalog)));
    }

    [Fact]
    public void TheNurseryAndThePlayroomFurnishTheToyTheCatalogReads()
    {
        // The catalog gives the toy box the Toy role (DefinitionCatalog.GameRoomRoles over PlayBuildings).
        FurnitureDefinition Def(string name, FurnitureRole role) => new() { Name = name, Available = Known(true), Size = Known(new Bounds(1, 1)), Roles = [role.Value] };
        var defs = new List<FurnitureDefinition> { Def(Crib, FurnitureRole.BabyBed), Def(ToyBox, FurnitureRole.Toy), Def(Decoration, FurnitureRole.Decoration) };
        var needs = ChildRoomsRules.GoalRoomNeeds([RoomRole.Nursery, RoomRole.Playroom], []);

        var nursery = Assert.Single(needs, n => n.Module == PlannedRole.Nursery).Shape(defs)!;
        var playroom = Assert.Single(needs, n => n.Module == PlannedRole.Playroom).Shape(defs)!;

        // Two cribs and the toy box, all one cell.
        Assert.Equal(3, Assert.Single(nursery.Pieces).Count);
        Assert.Equal(2, Assert.Single(playroom.Pieces).Count);
        // A nursery is owed whether or not a toy box is to be had: the toy is placed once one is.
        var cribs = Assert.Single(needs, n => n.Module == PlannedRole.Nursery).Shape([defs[0]])!;
        Assert.Equal(2, Assert.Single(cribs.Pieces).Count);
    }
}
