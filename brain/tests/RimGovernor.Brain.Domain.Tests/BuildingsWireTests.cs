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
    private static readonly C.ObservationContext Context = new() { NativeGeneration = new RimGovernor.Contracts.Wire.Common.ObservedGeneration.UnclaimedCase(), Identity = new C.Identity { ColonyId = "colony", LoadToken = "load", MapId = 0 }, Tick = 10 };

    private static O.BuildingState Row(string id, O.BuildingStatus status, O.BuildingHitPoints hitPoints, O.ConstructionState? construction) => new()
    {
        Roofed = false, RainVulnerable = false, FuelDefinitions = [], Turret = null, Conduit = O.ConduitKind.None,
        Building = new O.EntityRef { Id = id, DefName = "Wall", Label = "wall", Place = new O.EntityPlace.OnMap(0, new RimGovernor.Brain.Domain.Cell { X = 3, Z = 4 }) },
        Rotation = RimGovernor.Contracts.Wire.Placement.Rotation.North,
        Stuff = "WoodLog",
        Faction = null,
        Status = status,
        HitPoints = hitPoints,
        Construction = construction,
        Service = BuildingServiceWire.NoService,
        Settings = new O.BuildingSettings { Forbidden = false, Control = new O.BuildingControl.None() },
        Burning = false,
        Occupied = new O.Rectangle { Minimum = new RimGovernor.Brain.Domain.Cell { X = 3, Z = 4 }, Maximum = new RimGovernor.Brain.Domain.Cell { X = 3, Z = 4 } },
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
                Definition = "Wall", Resources = [new O.MaterialDeficit { DefName = "WoodLog", StillNeeded = 2 }],
                QualitySensitive = true, NativeFinishingSkill = 4, Minimum = new O.FinishingMinimum { Skill = 6, EligibleFinishers = 0 },
                Gate = new O.ConstructionGate.Tiered { Tier = ConstructionTier.Comfort },
            }),
            Row("wall-3", O.BuildingStatus.Blueprint, new O.BuildingHitPoints.Untracked(), new O.ConstructionState.Build
            {
                Definition = "Wall", Resources = [], QualitySensitive = false, NativeFinishingSkill = 0,
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
    [InlineData("roofed")]
    [InlineData("rainVulnerable")]
    [InlineData("fuelDefinitions")]
    [InlineData("turret")]
    [InlineData("conduit")]
    public void ABuildingRowMissingAMemberDoesNotDecode(string member)
    {
        var node = JsonNode.Parse(WireJson.Serialize(Census))!.AsObject();
        Assert.True(node["buildings"]![1]!.AsObject().Remove(member));

        Assert.ThrowsAny<JsonException>(() => WireJson.Deserialize<O.BuildingsSnapshot>(node.ToJsonString()));
    }

    // #3877: a building's service is read whole: power is a trader, a battery or none; fuel is refuelable or not; the
    // wind path is a turbine's or none. Every named state round-trips and a row missing one does not decode.
    [Fact]
    public void EveryServiceStateRoundTripsExactly()
    {
        O.BuildingServiceState[] services =
        [
            BuildingServiceWire.NoService,
            BuildingServiceWire.NoService with
            {
                Power = new O.BuildingPower.Trader { RatedW = 1000, PowerOn = true, OutputW = 1000, Network = "net-1" },
                Fuel = new O.BuildingFuel.Refuelable { Burn = BuildingServiceWire.IdleBurn, Fuel = 0, TargetFuel = 30, OutOfFuel = true },
            },
            BuildingServiceWire.NoService with { Power = new O.BuildingPower.Trader { RatedW = -100, PowerOn = false, OutputW = 0, Network = null } },
            BuildingServiceWire.NoService with { Power = new O.BuildingPower.Battery { StorageWD = 600, Network = "net-2" }, BrokenDown = true },
            BuildingServiceWire.NoService with { Wind = new O.BuildingWind.WindTurbine { BlockedCells = 4 }, SwitchedOn = false },
        ];

        var json = services.Select(s => WireJson.Serialize(s)).ToList();

        Assert.Equal(services, json.Select(j => WireJson.Deserialize<O.BuildingServiceState>(j)).ToList());
    }

    [Theory]
    [InlineData("power")]
    [InlineData("fuel")]
    [InlineData("wind")]
    public void AServiceRowMissingAStateDoesNotDecode(string member)
    {
        var node = JsonNode.Parse(WireJson.Serialize(BuildingServiceWire.NoService))!.AsObject();
        Assert.True(node.Remove(member));

        Assert.ThrowsAny<JsonException>(() => WireJson.Deserialize<O.BuildingServiceState>(node.ToJsonString()));
    }

    // #3770: a building's settings are its forbidden flag and one control family the brain reads; only temperature carries its CAS snapshot.
    [Fact]
    public void EveryBuildingControlRoundTripsExactly()
    {
        var snapshot = new O.SnapshotRef { Context = Context, EntityId = "b-1", Token = "tok" };
        O.BuildingControl[] controls =
        [
            new O.BuildingControl.Temperature(snapshot, -5.5),
            new O.BuildingControl.Bed(true, false),
            new O.BuildingControl.Grower(null),
            new O.BuildingControl.None(),
        ];
        var settings = controls.Select(c => new O.BuildingSettings { Forbidden = true, Control = c }).ToList();

        var json = string.Join("\n", settings.Select(x => WireJson.Serialize(x)));

        const string Snap = """{"context":{"identity":{"colonyId":"colony","loadToken":"load","mapId":0},"tick":10,"nativeGeneration":{"type":"unclaimed"}},"entityId":"b-1","token":"tok"}""";
        Assert.Equal(
            string.Join("\n",
                $$$"""{"forbidden":true,"control":{"type":"temperature","snapshot":{{{Snap}}},"targetC":-5.5}}""",
                $$$"""{"forbidden":true,"control":{"type":"bed","medical":true,"forPrisoners":false}}""",
                """{"forbidden":true,"control":{"type":"grower","cropDefName":null}}""",
                """{"forbidden":true,"control":{"type":"none"}}"""),
            json);
        Assert.Equal(settings, settings.Select(x => WireJson.Deserialize<O.BuildingSettings>(WireJson.Serialize(x))).ToList());
    }

    [Theory]
    [InlineData("gate")]
    [InlineData("minimum")]
    public void ABuildStateMissingAMemberDoesNotDecode(string member)
    {
        var node = JsonNode.Parse(WireJson.Serialize(Census))!.AsObject();
        Assert.True(node["buildings"]![1]!["construction"]!.AsObject().Remove(member));

        Assert.ThrowsAny<JsonException>(() => WireJson.Deserialize<O.BuildingsSnapshot>(node.ToJsonString()));
    }
}
