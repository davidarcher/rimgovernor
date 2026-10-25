using System.Text.Json;
using System.Text.Json.Nodes;
using RimGovernor.Brain.GameFacts;
using RimGovernor.Brain.GameOrders;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

// #3774: the building census is a plain required record. Hit points are tracked or untracked, a blueprint or frame
// carries its build (or installation) state and a built row none, so a row missing a member does not decode.
public sealed class BuildingsWireTests
{
    private static readonly RimGovernor.Brain.GameFacts.Common.ObservationContext Context = new() { NativeGeneration = new RimGovernor.Brain.GameFacts.Common.ObservedGeneration.UnclaimedCase(), Identity = new RimGovernor.Brain.GameFacts.Common.Identity { ColonyId = "colony", LoadToken = "load", MapId = 0 }, Tick = 10 };

    private static RimGovernor.Brain.GameFacts.Observations.BuildingState Row(string id, RimGovernor.Brain.GameFacts.Observations.BuildingStatus status, RimGovernor.Brain.GameFacts.Observations.BuildingHitPoints hitPoints, RimGovernor.Brain.GameFacts.Observations.ConstructionState? construction) => new()
    {
        Roofed = false, RainVulnerable = false, FuelDefinitions = [], Turret = null, Conduit = RimGovernor.Brain.GameFacts.Observations.ConduitKind.None,
        Building = new RimGovernor.Brain.GameFacts.Observations.EntityRef { Id = id, DefName = "Wall", Label = "wall", Place = new RimGovernor.Brain.GameFacts.Observations.EntityPlace.OnMap(0, new RimGovernor.Brain.Domain.Cell { X = 3, Z = 4 }) },
        Rotation = RimGovernor.Brain.GameFacts.Placement.Rotation.North,
        Stuff = "WoodLog",
        Faction = null,
        Status = status,
        HitPoints = hitPoints,
        Construction = construction,
        Service = BuildingServiceWire.NoService,
        Settings = new RimGovernor.Brain.GameFacts.Observations.BuildingSettings { Forbidden = false, Control = new RimGovernor.Brain.GameFacts.Observations.BuildingControl.None() },
        Burning = false,
        Occupied = new RimGovernor.Brain.GameFacts.Observations.Rectangle { Minimum = new RimGovernor.Brain.Domain.Cell { X = 3, Z = 4 }, Maximum = new RimGovernor.Brain.Domain.Cell { X = 3, Z = 4 } },
        Anomaly = null,
        Door = new RimGovernor.Brain.GameFacts.Observations.BuildingDoor { Open = false, HoldOpen = true },
    };

    private static readonly RimGovernor.Brain.GameFacts.Observations.BuildingsSnapshot Census = new()
    {
        Context = Context,
        Buildings =
        [
            Row("wall-1", RimGovernor.Brain.GameFacts.Observations.BuildingStatus.Built, new RimGovernor.Brain.GameFacts.Observations.BuildingHitPoints.Tracked { Current = 150, Maximum = 300 }, null),
            Row("wall-2", RimGovernor.Brain.GameFacts.Observations.BuildingStatus.Frame, new RimGovernor.Brain.GameFacts.Observations.BuildingHitPoints.Tracked { Current = 10, Maximum = 300 }, new RimGovernor.Brain.GameFacts.Observations.ConstructionState.Build
            {
                Definition = "Wall", Resources = [new RimGovernor.Brain.GameFacts.Observations.MaterialDeficit { DefName = "WoodLog", StillNeeded = 2 }],
                QualitySensitive = true, NativeFinishingSkill = 4, Minimum = new RimGovernor.Brain.GameFacts.Observations.FinishingMinimum { Skill = 6, EligibleFinishers = 0 },
                Gate = new RimGovernor.Brain.GameFacts.Observations.ConstructionGate.Tiered { Tier = ConstructionTier.Comfort },
            }),
            Row("wall-3", RimGovernor.Brain.GameFacts.Observations.BuildingStatus.Blueprint, new RimGovernor.Brain.GameFacts.Observations.BuildingHitPoints.Untracked(), new RimGovernor.Brain.GameFacts.Observations.ConstructionState.Build
            {
                Definition = "Wall", Resources = [], QualitySensitive = false, NativeFinishingSkill = 0,
                Minimum = null, Gate = new RimGovernor.Brain.GameFacts.Observations.ConstructionGate.Ungated(),
            }),
            Row("bed-1", RimGovernor.Brain.GameFacts.Observations.BuildingStatus.Blueprint, new RimGovernor.Brain.GameFacts.Observations.BuildingHitPoints.Untracked(), new RimGovernor.Brain.GameFacts.Observations.ConstructionState.Installation()),
        ],
    };

    [Fact]
    public void TheBuildingCensusRoundTripsExactly()
    {
        var json = OrdersJson.Serialize(Census);
        Assert.Contains("\"type\":\"tiered\"", json);
        Assert.Contains("\"type\":\"installation\"", json);
        Assert.Contains("\"status\":\"frame\"", json);
        var back = OrdersJson.Deserialize<RimGovernor.Brain.GameFacts.Observations.BuildingsSnapshot>(json);

        Assert.Equal(json, OrdersJson.Serialize(back));
        Assert.IsType<RimGovernor.Brain.GameFacts.Observations.ConstructionState.Installation>(back.Buildings[3].Construction);
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
        var node = JsonNode.Parse(OrdersJson.Serialize(Census))!.AsObject();
        Assert.True(node["buildings"]![1]!.AsObject().Remove(member));

        Assert.ThrowsAny<JsonException>(() => OrdersJson.Deserialize<RimGovernor.Brain.GameFacts.Observations.BuildingsSnapshot>(node.ToJsonString()));
    }

    // #3877: a building's service is read whole: power is a trader, a battery or none; fuel is refuelable or not; the
    // wind path is a turbine's or none. Every named state round-trips and a row missing one does not decode.
    [Fact]
    public void EveryServiceStateRoundTripsExactly()
    {
        RimGovernor.Brain.GameFacts.Observations.BuildingServiceState[] services =
        [
            BuildingServiceWire.NoService,
            BuildingServiceWire.NoService with
            {
                Power = new RimGovernor.Brain.GameFacts.Observations.BuildingPower.Trader { RatedW = 1000, PowerOn = true, OutputW = 1000, Network = "net-1" },
                Fuel = new RimGovernor.Brain.GameFacts.Observations.BuildingFuel.Refuelable { Burn = BuildingServiceWire.IdleBurn, Fuel = 0, TargetFuel = 30, OutOfFuel = true },
            },
            BuildingServiceWire.NoService with { Power = new RimGovernor.Brain.GameFacts.Observations.BuildingPower.Trader { RatedW = -100, PowerOn = false, OutputW = 0, Network = null } },
            BuildingServiceWire.NoService with { Power = new RimGovernor.Brain.GameFacts.Observations.BuildingPower.Battery { StorageWD = 600, Network = "net-2" }, BrokenDown = true },
            BuildingServiceWire.NoService with { Wind = new RimGovernor.Brain.GameFacts.Observations.BuildingWind.WindTurbine { BlockedCells = 4 }, SwitchedOn = false },
        ];

        var json = services.Select(s => OrdersJson.Serialize(s)).ToList();

        Assert.Equal(services, json.Select(j => OrdersJson.Deserialize<RimGovernor.Brain.GameFacts.Observations.BuildingServiceState>(j)).ToList());
    }

    [Theory]
    [InlineData("power")]
    [InlineData("fuel")]
    [InlineData("wind")]
    public void AServiceRowMissingAStateDoesNotDecode(string member)
    {
        var node = JsonNode.Parse(OrdersJson.Serialize(BuildingServiceWire.NoService))!.AsObject();
        Assert.True(node.Remove(member));

        Assert.ThrowsAny<JsonException>(() => OrdersJson.Deserialize<RimGovernor.Brain.GameFacts.Observations.BuildingServiceState>(node.ToJsonString()));
    }

    // #3770: a building's settings are its forbidden flag and one control family the brain reads; only temperature carries its CAS snapshot.
    [Fact]
    public void EveryBuildingControlRoundTripsExactly()
    {
        var snapshot = new RimGovernor.Brain.GameFacts.Observations.SnapshotRef { Context = Context, EntityId = "b-1", Token = "tok" };
        RimGovernor.Brain.GameFacts.Observations.BuildingControl[] controls =
        [
            new RimGovernor.Brain.GameFacts.Observations.BuildingControl.Temperature(snapshot, -5.5),
            new RimGovernor.Brain.GameFacts.Observations.BuildingControl.Bed(true, false),
            new RimGovernor.Brain.GameFacts.Observations.BuildingControl.Grower(null),
            new RimGovernor.Brain.GameFacts.Observations.BuildingControl.None(),
        ];
        var settings = controls.Select(c => new RimGovernor.Brain.GameFacts.Observations.BuildingSettings { Forbidden = true, Control = c }).ToList();

        var json = string.Join("\n", settings.Select(x => OrdersJson.Serialize(x)));

        const string Snap = """{"context":{"identity":{"colonyId":"colony","loadToken":"load","mapId":0},"tick":10,"nativeGeneration":{"type":"unclaimed"}},"entityId":"b-1","token":"tok"}""";
        Assert.Equal(
            string.Join("\n",
                $$$"""{"forbidden":true,"control":{"type":"temperature","snapshot":{{{Snap}}},"targetC":-5.5}}""",
                $$$"""{"forbidden":true,"control":{"type":"bed","medical":true,"forPrisoners":false}}""",
                """{"forbidden":true,"control":{"type":"grower","cropDefName":null}}""",
                """{"forbidden":true,"control":{"type":"none"}}"""),
            json);
        Assert.Equal(settings, settings.Select(x => OrdersJson.Deserialize<RimGovernor.Brain.GameFacts.Observations.BuildingSettings>(OrdersJson.Serialize(x))).ToList());
    }

    [Theory]
    [InlineData("gate")]
    [InlineData("minimum")]
    public void ABuildStateMissingAMemberDoesNotDecode(string member)
    {
        var node = JsonNode.Parse(OrdersJson.Serialize(Census))!.AsObject();
        Assert.True(node["buildings"]![1]!["construction"]!.AsObject().Remove(member));

        Assert.ThrowsAny<JsonException>(() => OrdersJson.Deserialize<RimGovernor.Brain.GameFacts.Observations.BuildingsSnapshot>(node.ToJsonString()));
    }
}
