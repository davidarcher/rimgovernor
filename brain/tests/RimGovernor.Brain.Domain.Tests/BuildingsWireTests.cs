using System.Text.Json;
using System.Text.Json.Nodes;
using RimGovernor.Contracts.Wire;
using Xunit;
using C = RimGovernor.Contracts.Wire.Common;
using O = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Domain.Tests;

// #3774: the building census is a plain required record. Hit points are tracked or untracked, a blueprint or frame
// carries its build (or installation) state and a built row none, so a row missing a member does not decode.
public sealed class BuildingsWireTests
{
    private static readonly C.ObservationContext Context = new() { Identity = new C.Identity { ColonyId = "colony", LoadToken = "load", MapId = 0 }, Tick = 10 };

    private static O.BuildingState Row(string id, O.BuildingStatus status, O.BuildingHitPoints hitPoints, O.ConstructionState? construction) => new()
    {
        Building = new O.EntityRef { Id = id, DefName = "Wall", Label = "wall", Place = new O.EntityPlace.OnMap(0, new C.Cell { X = 3, Z = 4 }) },
        Rotation = RimGovernor.Contracts.Wire.Placement.Rotation.North,
        Stuff = "WoodLog",
        Faction = null,
        Status = status,
        HitPoints = hitPoints,
        Construction = construction,
        Service = new O.BuildingServiceState(),
        Settings = new O.BuildingSettings(),
        Burning = false,
        Occupied = new O.Rectangle { Minimum = new C.Cell { X = 3, Z = 4 }, Maximum = new C.Cell { X = 3, Z = 4 } },
        Anomaly = null,
        Door = new O.BuildingDoor { Open = false, HoldOpen = true },
    };

    private static readonly O.BuildingsSnapshot Census = new()
    {
        Context = Context,
        Buildings =
        [
            Row("wall-1", O.BuildingStatus.Built, new O.BuildingHitPoints.Tracked { Current = 150, Maximum = 300 }, null),
            Row("wall-2", O.BuildingStatus.Frame, new O.BuildingHitPoints.Tracked { Current = 10, Maximum = 300 }, new O.ConstructionState.Build
            {
                Definition = "Wall", WorkLeft = 42.5, Resources = [new O.MaterialDeficit { DefName = "WoodLog", StillNeeded = 2 }],
                QualitySensitive = true, NativeFinishingSkill = 4, Minimum = new O.FinishingMinimum { Skill = 6, EligibleFinishers = 0 },
                Gate = new O.ConstructionGate.Tiered { Tier = ConstructionTier.Comfort },
            }),
            Row("wall-3", O.BuildingStatus.Blueprint, new O.BuildingHitPoints.Untracked(), new O.ConstructionState.Build
            {
                Definition = "Wall", WorkLeft = 100, Resources = [], QualitySensitive = false, NativeFinishingSkill = 0,
                Minimum = null, Gate = new O.ConstructionGate.Ungated(),
            }),
            Row("bed-1", O.BuildingStatus.Blueprint, new O.BuildingHitPoints.Untracked(), new O.ConstructionState.Installation()),
        ],
    };

    [Fact]
    public void TheBuildingCensusRoundTripsExactly()
    {
        var json = WireJson.Serialize(Census);
        Assert.Contains("\"type\":\"tiered\"", json);
        Assert.Contains("\"type\":\"installation\"", json);
        Assert.Contains("\"status\":\"frame\"", json);
        var back = WireJson.Deserialize<O.BuildingsSnapshot>(json);

        Assert.Equal(json, WireJson.Serialize(back));
        Assert.IsType<O.ConstructionState.Installation>(back.Buildings[3].Construction);
    }

    [Theory]
    [InlineData("hitPoints")]
    [InlineData("construction")]
    [InlineData("burning")]
    [InlineData("status")]
    public void ABuildingRowMissingAMemberDoesNotDecode(string member)
    {
        var node = JsonNode.Parse(WireJson.Serialize(Census))!.AsObject();
        Assert.True(node["buildings"]![1]!.AsObject().Remove(member));

        Assert.ThrowsAny<JsonException>(() => WireJson.Deserialize<O.BuildingsSnapshot>(node.ToJsonString()));
    }

    [Theory]
    [InlineData("gate")]
    [InlineData("minimum")]
    [InlineData("workLeft")]
    public void ABuildStateMissingAMemberDoesNotDecode(string member)
    {
        var node = JsonNode.Parse(WireJson.Serialize(Census))!.AsObject();
        Assert.True(node["buildings"]![1]!["construction"]!.AsObject().Remove(member));

        Assert.ThrowsAny<JsonException>(() => WireJson.Deserialize<O.BuildingsSnapshot>(node.ToJsonString()));
    }
}
