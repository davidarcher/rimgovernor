using RimGovernor.Brain.Domain;
using RimGovernor.Brain.GameFacts;
using RimGovernor.Brain.GameOrders;
using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The upkeep census is read whole (#3832): every list is total, an item's rot, a bed's quality, a person's royal
/// standing and an animal's roping are named states, and a missing relation (no owned bed, a stuffless bed, no
/// suitable pen, no pending floor) is null. On the wire each row round-trips through <c>OrdersJsonContext</c> exactly.
/// </summary>
public sealed class UpkeepFactsTests
{
    /// <summary>The empty upkeep census: the base every test colony snapshot starts from.</summary>
    internal static readonly UpkeepFacts NoUpkeep = new()
    {
        Comfort = ComfortTests.NoComfort,
        Items = [],
        Beds = [],
        Structures = [],
        People = [],
        Animals = [],
        HomeCoverage = HomeCoverageTests.NoHomeCoverage,
        Lighting = LightingTests.NoLighting,
        WildAnimals = [],
        Flooring = new() { Rooms = [] },
        Routes = new() { Facilities = [], PawnIds = [], Traffic = [], TrafficSamples = 0 },
        Slaves = [],
        Guests = [],
    };

    [Fact]
    public void ItemsRoundTripExactly()
    {
        EquatableArray<UpkeepItem> rows =
        [
            new() { Id = "Thing_Meat1", Definition = "Meat_Cow", Count = 75, Rot = new ItemRot.Rotting(60000), Forbidden = false },
            new() { Id = "Thing_Steel2", Definition = "Steel", Count = 10, Rot = new ItemRot.Never(), Forbidden = true },
        ];

        Assert.Equal(
            """[{"id":"Thing_Meat1","definition":"Meat_Cow","count":75,"rot":{"type":"rotting","ticks":60000},"forbidden":false},{"id":"Thing_Steel2","definition":"Steel","count":10,"rot":{"type":"never"},"forbidden":true}]""",
            OrdersJson.Serialize(rows));
        Assert.All(rows, row => Assert.Equal(row, OrdersJson.Deserialize<UpkeepItem>(OrdersJson.Serialize(row))));
    }

    [Fact]
    public void BedsRoundTripExactly()
    {
        EquatableArray<UpkeepBed> rows =
        [
            new()
            {
                Id = "Thing_Bed1", Slots = 2, Medical = false, Prisoners = false, Owners = [new PawnId("Human1")], Users = [], AccessibleTo = [new PawnId("Human1")],
                TemperatureC = 21.5, Room = "room-3", Quality = new BedQuality.Graded(RimGovernor.Brain.GameFacts.Defs.QualityCategory.Good), ForSlaves = false,
            },
            new()
            {
                Id = "Thing_SleepingSpot2", Slots = 1, Medical = false, Prisoners = true, Owners = [], Users = [], AccessibleTo = [],
                TemperatureC = -3, Room = "room-4", Quality = new BedQuality.Ungraded(), ForSlaves = false,
            },
        ];

        Assert.Equal(
            """[{"id":"Thing_Bed1","slots":2,"medical":false,"prisoners":false,"owners":["Human1"],"users":[],"accessibleTo":["Human1"],"temperatureC":21.5,"room":"room-3","quality":{"type":"graded","category":"QUALITY_CATEGORY_GOOD"},"forSlaves":false},{"id":"Thing_SleepingSpot2","slots":1,"medical":false,"prisoners":true,"owners":[],"users":[],"accessibleTo":[],"temperatureC":-3,"room":"room-4","quality":{"type":"ungraded"},"forSlaves":false}]""",
            OrdersJson.Serialize(rows));
        Assert.All(rows, row => Assert.Equal(row, OrdersJson.Deserialize<UpkeepBed>(OrdersJson.Serialize(row))));
        Assert.Throws<System.Text.Json.JsonException>(() => OrdersJson.Deserialize<UpkeepBed>(OrdersJson.Serialize(rows[0]).Replace("\"slots\":2,", "", StringComparison.Ordinal)));
    }

    [Fact]
    public void PeopleRoundTripExactly()
    {
        EquatableArray<UpkeepPerson> rows =
        [
            new()
            {
                Pawn = new PawnId("Human1"), Partners = [new PawnId("Human2")],
                BedSharingAllowed = true, Title = new RoyalStanding.Titled("Knight", true, ["Ascetic"]),
            },
            new()
            {
                Pawn = new PawnId("Human3"), Partners = [],
                BedSharingAllowed = false, Title = new RoyalStanding.Untitled(),
            },
        ];

        Assert.Equal(
            """[{"pawn":"Human1","partners":["Human2"],"bedSharingAllowed":true,"title":{"type":"titled","title":"Knight","ascetic":true,"precepts":["Ascetic"]}},{"pawn":"Human3","partners":[],"bedSharingAllowed":false,"title":{"type":"untitled"}}]""",
            OrdersJson.Serialize(rows));
        Assert.All(rows, row => Assert.Equal(row, OrdersJson.Deserialize<UpkeepPerson>(OrdersJson.Serialize(row))));
    }

    [Fact]
    public void AnimalsRoundTripExactly()
    {
        var roam = new AnimalRoam { Race = "Cow", MtbDays = 4, MarketValue = 200, ReachesMapEdge = true };
        EquatableArray<ColonyAnimal> rows =
        [
            new() { Pawn = new PawnId("Cow1"), Roping = new AnimalRoping.Roped(roam, "pen-2") },
            new() { Pawn = new PawnId("Cow2"), Roping = new AnimalRoping.Roped(roam, null) },
            new() { Pawn = new PawnId("Husky3"), Roping = new AnimalRoping.Unroped() },
        ];

        Assert.Equal(
            """[{"pawn":"Cow1","roping":{"type":"roped","roam":{"race":"Cow","mtbDays":4,"marketValue":200,"reachesMapEdge":true},"suitablePen":"pen-2"}},{"pawn":"Cow2","roping":{"type":"roped","roam":{"race":"Cow","mtbDays":4,"marketValue":200,"reachesMapEdge":true},"suitablePen":null}},{"pawn":"Husky3","roping":{"type":"unroped"}}]""",
            OrdersJson.Serialize(rows));
        Assert.All(rows, row => Assert.Equal(row, OrdersJson.Deserialize<ColonyAnimal>(OrdersJson.Serialize(row))));
    }

    [Fact]
    public void FlooringRoundTripsExactly()
    {
        var flooring = new FlooringFacts
        {
            Rooms =
            [
                new()
                {
                    Room = "room-3", Role = RoomRole.Kitchen,
                    Cells = [new() { Cell = new(3, 4), Terrain = "Soil", Pending = "WoodPlankFloor" }, new() { Cell = new(4, 4), Terrain = "Soil", Pending = null }],
                },
            ],
        };

        Assert.Equal(
            """{"rooms":[{"room":"room-3","role":"Kitchen","cells":[{"cell":{"x":3,"z":4},"terrain":"Soil","pending":"WoodPlankFloor"},{"cell":{"x":4,"z":4},"terrain":"Soil","pending":null}]}]}""",
            OrdersJson.Serialize(flooring));
        Assert.Equal(flooring, OrdersJson.Deserialize<FlooringFacts>(OrdersJson.Serialize(flooring)));
    }

    /// <summary>Every routes row reads whole (#3903): a pawn's travel is a named state and only a pending door and a facility's room are absent relations.</summary>
    [Fact]
    public void RoutesRoundTripExactly()
    {
        var routes = new RoutesFacts
        {
            PawnIds = ["Colonist1", "Colonist2", "Colonist3", "Colonist4"],
            Facilities =
            [
                new()
                {
                    Id = "Thing_Bed1", Kind = RouteFacilityKind.Bed, Cell = new(3, 4), Room = "room-2",
                    Travel = [new RouteTravel.Measured { Pawn = "Colonist1", Cost = 40, Cells = 12 }, new RouteTravel.Unreachable { Pawn = "Colonist2" }, new RouteTravel.Skipped { Pawn = "Colonist3" }, new RouteTravel.Unpathed { Pawn = "Colonist4" }],
                    Breaches = [new() { Cell = new(5, 4), Edifice = "Thing_Wall1", Pending = "Thing_Door2", Distance = 2 }, new() { Cell = new(3, 6), Edifice = "Thing_Wall3", Pending = null, Distance = 3 }],
                },
                new() { Id = "zone-7", Kind = RouteFacilityKind.Stockpile, Cell = new(9, 9), Room = null, Travel = [], Breaches = [] },
            ],
            Traffic = [new() { Cell = new(1, 2), Layer = TrafficLayer.Colonist, Samples = 9, Terrain = "Soil", Home = true, Pending = "WoodPlankFloor" }, new() { Cell = new(1, 3), Layer = TrafficLayer.Hostile, Samples = 1, Terrain = "Soil", Home = false, Pending = null }],
            TrafficSamples = 30,
        };

        Assert.Equal(
            """{"facilities":[{"id":"Thing_Bed1","kind":"bed","cell":{"x":3,"z":4},"room":"room-2","travel":[{"type":"measured","cost":40,"cells":12,"pawn":"Colonist1"},{"type":"unreachable","pawn":"Colonist2"},{"type":"skipped","pawn":"Colonist3"},{"type":"unpathed","pawn":"Colonist4"}],"breaches":[{"cell":{"x":5,"z":4},"edifice":"Thing_Wall1","pending":"Thing_Door2","distance":2},{"cell":{"x":3,"z":6},"edifice":"Thing_Wall3","pending":null,"distance":3}]},{"id":"zone-7","kind":"stockpile","cell":{"x":9,"z":9},"room":null,"travel":[],"breaches":[]}],"pawnIds":["Colonist1","Colonist2","Colonist3","Colonist4"],"traffic":[{"cell":{"x":1,"z":2},"layer":"colonist","samples":9,"terrain":"Soil","home":true,"pending":"WoodPlankFloor"},{"cell":{"x":1,"z":3},"layer":"hostile","samples":1,"terrain":"Soil","home":false,"pending":null}],"trafficSamples":30}""",
            OrdersJson.Serialize(routes));
        Assert.Equal(routes, OrdersJson.Deserialize<RoutesFacts>(OrdersJson.Serialize(routes)));
    }

    /// <summary>The routes census is a plain required member (#3782): no section wrapper, and an upkeep read without it is
    /// a contract error, never an unknown census.</summary>
    [Fact]
    public void TheRoutesCensusIsPlainAndRequired()
    {
        var upkeep = NoUpkeep with { Routes = new() { Facilities = [], PawnIds = ["Colonist1"], Traffic = [], TrafficSamples = 12 } };
        var json = OrdersJson.Serialize(upkeep);
        Assert.Contains("\"routes\":{\"facilities\":[],\"pawnIds\":[\"Colonist1\"],\"traffic\":[],\"trafficSamples\":12}", json, StringComparison.Ordinal);
        Assert.Equal(upkeep.Routes, OrdersJson.Deserialize<UpkeepFacts>(json).Routes);
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        node.Remove("routes");
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => OrdersJson.Deserialize<UpkeepFacts>(node.ToJsonString()));
    }
}
