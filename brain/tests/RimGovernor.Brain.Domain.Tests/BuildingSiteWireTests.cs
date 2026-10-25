using RimGovernor.Brain.Policy;
using System.Text.Json;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Bridge;
using RimGovernor.Contracts.Wire;
using Xunit;
using C = RimGovernor.Contracts.Wire.Common;
using O = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Domain.Tests;

// #3774: the clearance, excavation and wall-upgrade censuses are plain required records; the native throws instead
// of answering an unknown, so a row missing a member does not decode.
public sealed class BuildingSiteWireTests
{
    private static readonly C.ObservationContext Context = new() { NativeGeneration = new RimGovernor.Contracts.Wire.Common.ObservedGeneration.UnclaimedCase(), Identity = new C.Identity { ColonyId = "colony", LoadToken = "load", MapId = 0 }, Tick = 10 };

    private static O.ClearanceTarget Target(string id, O.ClearanceSalvage salvage) => new()
    {
        EntityId = id, DefName = "AncientConcreteWall", Minimum = new Cell(3, 4), Maximum = new Cell(3, 4),
        Class = O.ClearanceClass.AncientWallDoor, RoofBlocker = null, AncientDanger = false, Designated = false,
        Player = false, EnclosesRoom = false, Salvage = salvage,
    };

    private static readonly O.ClearanceTargetsSnapshot Census = new()
    {
        Context = Context,
        Targets =
        [
            Target("wall-1", new O.ClearanceSalvage.NotRequested()),
            Target("wall-2", new O.ClearanceSalvage.Skipped()),
            Target("wall-3", new O.ClearanceSalvage.Computed(
                new O.SalvageEvidence
                {
                    Safe = true, PathLength = 12.5, Labor = 300,
                    Yields = [new O.SalvageYield { DefName = "Steel", Count = 20, UnitValue = 1.9, StorageHeadroom = 400 }],
                })),
        ],
        Floors = [new O.ClearanceFloor { Cell = new Cell(5, 5), DefName = "WoodPlankFloor", Designated = true }],
    };

    private static readonly O.ExcavationSiteSnapshot Site = new()
    {
        Context = Context,
        Cells =
        [
            new O.ExcavationCell { Cell = new Cell(7, 7), Fogged = false, Definition = "Granite", Walkable = false, MineDesignated = true, Eligible = true },
            new O.ExcavationCell { Cell = new Cell(7, 8), Fogged = false, Definition = null, Walkable = true, MineDesignated = false, Eligible = false },
        ],
        Support = O.ExcavationSupport.Unsupported, SupportBlocker = "Roof collapse is already pending",
        CollapsePending = true, Workers = ["Human1"], AccessReachable = true,
    };

    private static readonly O.WallUpgradeSnapshot Walls = new()
    {
        Context = Context,
        Sites =
        [
            new O.WallUpgradeSite
            {
                Target = new C.Ref { Id = "wall-9" }, Origin = new Cell(10, 10), Normal = new Cell(0, 1),
                BackupCells = [new Cell(9, 11), new Cell(10, 11), new Cell(11, 11)], Blocker = null, Designated = false,
                RemovalId = null, Original = new C.Ref { Id = "wall-9" }, LeftSupport = new C.Ref { Id = "wall-8" },
                RightSupport = new C.Ref { Id = "wall-10" }, CompletedBackups = [], Replacement = null,
                TargetSnapshot = new O.SnapshotRef { Context = Context, EntityId = "wall-9", Token = "t" },
            },
        ],
    };

    [Fact]
    public void TheClearanceCensusRoundTripsExactly()
    {
        var json = WireJson.Serialize(Census);
        Assert.Contains("\"type\":\"skipped\"", json);
        var back = WireJson.Deserialize<O.ClearanceTargetsSnapshot>(json);

        Assert.Equal(json, WireJson.Serialize(back));
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
        var json = WireJson.Serialize(Site);
        var back = WireJson.Deserialize<O.ExcavationSiteSnapshot>(json);

        Assert.Equal(json, WireJson.Serialize(back));
        Assert.Equal(Site, back);
    }

    [Fact]
    public void TheWallUpgradeCensusRoundTripsExactly()
    {
        var json = WireJson.Serialize(Walls);
        var back = WireJson.Deserialize<O.WallUpgradeSnapshot>(json);

        Assert.Equal(json, WireJson.Serialize(back));
        Assert.Equal(Walls, back);
    }

    [Theory]
    [InlineData("salvage")]
    [InlineData("player")]
    [InlineData("enclosesRoom")]
    [InlineData("class")]
    public void AClearanceRowMissingAMemberDoesNotDecode(string member)
    {
        var node = JsonNode.Parse(WireJson.Serialize(Census))!.AsObject();
        Assert.True(node["targets"]![0]!.AsObject().Remove(member));

        Assert.ThrowsAny<JsonException>(() => WireJson.Deserialize<O.ClearanceTargetsSnapshot>(node.ToJsonString()));
    }

    [Theory]
    [InlineData("support")]
    [InlineData("workers")]
    [InlineData("accessReachable")]
    public void AnExcavationSiteMissingAMemberDoesNotDecode(string member)
    {
        var node = JsonNode.Parse(WireJson.Serialize(Site))!.AsObject();
        Assert.True(node.Remove(member));

        Assert.ThrowsAny<JsonException>(() => WireJson.Deserialize<O.ExcavationSiteSnapshot>(node.ToJsonString()));
    }

    [Theory]
    [InlineData("origin")]
    [InlineData("normal")]
    [InlineData("targetSnapshot")]
    public void AWallUpgradeSiteMissingAMemberDoesNotDecode(string member)
    {
        var node = JsonNode.Parse(WireJson.Serialize(Walls))!.AsObject();
        Assert.True(node["sites"]![0]!.AsObject().Remove(member));

        Assert.ThrowsAny<JsonException>(() => WireJson.Deserialize<O.WallUpgradeSnapshot>(node.ToJsonString()));
    }
}
