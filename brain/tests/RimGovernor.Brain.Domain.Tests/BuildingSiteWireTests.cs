using RimGovernor.Brain.Policy;
using System.Text.Json;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.GameFacts;
using RimGovernor.Brain.GameOrders;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

// #3774: the clearance, excavation and wall-upgrade censuses are plain required records; the native throws instead
// of answering an unknown, so a row missing a member does not decode.
public sealed class BuildingSiteWireTests
{
    private static readonly RimGovernor.Brain.GameFacts.Common.ObservationContext Context = new() { NativeGeneration = new RimGovernor.Brain.GameFacts.Common.ObservedGeneration.UnclaimedCase(), Identity = new RimGovernor.Brain.GameFacts.Common.Identity { ColonyId = "colony", LoadToken = "load", MapId = 0 }, Tick = 10 };

    private static RimGovernor.Brain.GameFacts.Observations.ClearanceTarget Target(string id, RimGovernor.Brain.GameFacts.Observations.ClearanceSalvage salvage) => new()
    {
        EntityId = id, DefName = "AncientConcreteWall", Minimum = new Cell(3, 4), Maximum = new Cell(3, 4),
        Class = RimGovernor.Brain.GameFacts.Observations.ClearanceClass.AncientWallDoor, RoofBlocker = null, AncientDanger = false, Designated = false,
        Player = false, EnclosesRoom = false, Salvage = salvage,
    };

    private static readonly RimGovernor.Brain.GameFacts.Observations.ClearanceTargetsSnapshot Census = new()
    {
        Context = Context,
        Targets =
        [
            Target("wall-1", new RimGovernor.Brain.GameFacts.Observations.ClearanceSalvage.NotRequested()),
            Target("wall-2", new RimGovernor.Brain.GameFacts.Observations.ClearanceSalvage.Skipped()),
            Target("wall-3", new RimGovernor.Brain.GameFacts.Observations.ClearanceSalvage.Computed(
                new RimGovernor.Brain.GameFacts.Observations.SalvageEvidence
                {
                    Safe = true, PathLength = 12.5, Labor = 300,
                    Yields = [new RimGovernor.Brain.GameFacts.Observations.SalvageYield { DefName = "Steel", Count = 20, UnitValue = 1.9, StorageHeadroom = 400 }],
                })),
        ],
        Floors = [new RimGovernor.Brain.GameFacts.Observations.ClearanceFloor { Cell = new Cell(5, 5), DefName = "WoodPlankFloor", Designated = true }],
    };

    private static readonly RimGovernor.Brain.GameFacts.Observations.ExcavationSiteSnapshot Site = new()
    {
        Context = Context,
        Cells =
        [
            new RimGovernor.Brain.GameFacts.Observations.ExcavationCell { Cell = new Cell(7, 7), Fogged = false, Definition = "Granite", Walkable = false, MineDesignated = true, Eligible = true },
            new RimGovernor.Brain.GameFacts.Observations.ExcavationCell { Cell = new Cell(7, 8), Fogged = false, Definition = null, Walkable = true, MineDesignated = false, Eligible = false },
        ],
        Support = RimGovernor.Brain.GameFacts.Observations.ExcavationSupport.Unsupported, SupportBlocker = "Roof collapse is already pending",
        CollapsePending = true, Workers = ["Human1"], AccessReachable = true,
    };

    private static readonly RimGovernor.Brain.GameFacts.Observations.WallUpgradeSnapshot Walls = new()
    {
        Context = Context,
        Sites =
        [
            new RimGovernor.Brain.GameFacts.Observations.WallUpgradeSite
            {
                Target = new RimGovernor.Brain.GameFacts.Common.Ref { Id = "wall-9" }, Origin = new Cell(10, 10), Normal = new Cell(0, 1),
                BackupCells = [new Cell(9, 11), new Cell(10, 11), new Cell(11, 11)], Blocker = null, Designated = false,
                RemovalId = null, Original = new RimGovernor.Brain.GameFacts.Common.Ref { Id = "wall-9" }, LeftSupport = new RimGovernor.Brain.GameFacts.Common.Ref { Id = "wall-8" },
                RightSupport = new RimGovernor.Brain.GameFacts.Common.Ref { Id = "wall-10" }, CompletedBackups = [], Replacement = null,
                TargetSnapshot = new RimGovernor.Brain.GameFacts.Observations.SnapshotRef { Context = Context, EntityId = "wall-9", Token = "t" },
            },
        ],
    };

    [Fact]
    public void TheClearanceCensusRoundTripsExactly()
    {
        var json = OrdersJson.Serialize(Census);
        Assert.Contains("\"type\":\"skipped\"", json);
        var back = OrdersJson.Deserialize<RimGovernor.Brain.GameFacts.Observations.ClearanceTargetsSnapshot>(json);

        Assert.Equal(json, OrdersJson.Serialize(back));
        Assert.Equal(Census, back);
    }

    [Fact]
    public void ClearanceSalvageReadsAsTheWorkItemSalvage()
    {
        var targets = ClearanceReads.Targets(Census);

        Assert.Equal("ancient_wall_door", targets[0].Class);
        Assert.Null(targets[0].Salvage);
        Assert.False(targets[0].SalvageSkipped);
        Assert.True(targets[1].SalvageSkipped);
        Assert.NotNull(targets[2].Salvage);
    }

    [Fact]
    public void TheExcavationSiteRoundTripsExactly()
    {
        var json = OrdersJson.Serialize(Site);
        var back = OrdersJson.Deserialize<RimGovernor.Brain.GameFacts.Observations.ExcavationSiteSnapshot>(json);

        Assert.Equal(json, OrdersJson.Serialize(back));
        Assert.Equal(Site, back);
    }

    [Fact]
    public void TheWallUpgradeCensusRoundTripsExactly()
    {
        var json = OrdersJson.Serialize(Walls);
        var back = OrdersJson.Deserialize<RimGovernor.Brain.GameFacts.Observations.WallUpgradeSnapshot>(json);

        Assert.Equal(json, OrdersJson.Serialize(back));
        Assert.Equal(Walls, back);
    }

    [Theory]
    [InlineData("salvage")]
    [InlineData("player")]
    [InlineData("enclosesRoom")]
    [InlineData("class")]
    public void AClearanceRowMissingAMemberDoesNotDecode(string member)
    {
        var node = JsonNode.Parse(OrdersJson.Serialize(Census))!.AsObject();
        Assert.True(node["targets"]![0]!.AsObject().Remove(member));

        Assert.ThrowsAny<JsonException>(() => OrdersJson.Deserialize<RimGovernor.Brain.GameFacts.Observations.ClearanceTargetsSnapshot>(node.ToJsonString()));
    }

    [Theory]
    [InlineData("support")]
    [InlineData("workers")]
    [InlineData("accessReachable")]
    public void AnExcavationSiteMissingAMemberDoesNotDecode(string member)
    {
        var node = JsonNode.Parse(OrdersJson.Serialize(Site))!.AsObject();
        Assert.True(node.Remove(member));

        Assert.ThrowsAny<JsonException>(() => OrdersJson.Deserialize<RimGovernor.Brain.GameFacts.Observations.ExcavationSiteSnapshot>(node.ToJsonString()));
    }

    [Theory]
    [InlineData("origin")]
    [InlineData("normal")]
    [InlineData("targetSnapshot")]
    public void AWallUpgradeSiteMissingAMemberDoesNotDecode(string member)
    {
        var node = JsonNode.Parse(OrdersJson.Serialize(Walls))!.AsObject();
        Assert.True(node["sites"]![0]!.AsObject().Remove(member));

        Assert.ThrowsAny<JsonException>(() => OrdersJson.Deserialize<RimGovernor.Brain.GameFacts.Observations.WallUpgradeSnapshot>(node.ToJsonString()));
    }
}
