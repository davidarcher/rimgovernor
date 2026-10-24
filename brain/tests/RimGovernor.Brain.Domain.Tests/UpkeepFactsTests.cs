using RimGovernor.Brain.Domain;
using RimGovernor.Contracts.Wire;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using Defs = RimGovernor.Contracts.Wire.Defs;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The upkeep census is read whole (#3832): every list is total, an item's rot, a bed's quality, a person's royal
/// standing and an animal's roping are named states, and a missing relation (no owned bed, a stuffless bed, no
/// suitable pen, no pending floor) is null. On the wire each row round-trips through <c>WireJsonContext</c> exactly.
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
            WireJson.Serialize(rows));
        Assert.All(rows, row => Assert.Equal(row, WireJson.Deserialize<UpkeepItem>(WireJson.Serialize(row))));
    }

    [Fact]
    public void BedsRoundTripExactly()
    {
        EquatableArray<UpkeepBed> rows =
        [
            new()
            {
                Id = "Thing_Bed1", Medical = false, Prisoners = false, Owners = [new PawnId("Human1")], Users = [], AccessibleTo = [new PawnId("Human1")],
                Roofed = true, TemperatureC = 21.5, Room = "room-3", Quality = new BedQuality.Graded(Defs.QualityCategory.Good), Stuff = "WoodLog", ForSlaves = false,
            },
            new()
            {
                Id = "Thing_SleepingSpot2", Medical = false, Prisoners = true, Owners = [], Users = [], AccessibleTo = [],
                Roofed = false, TemperatureC = -3, Room = "room-4", Quality = new BedQuality.Ungraded(), Stuff = null, ForSlaves = false,
            },
        ];

        Assert.Equal(
            """[{"id":"Thing_Bed1","medical":false,"prisoners":false,"owners":["Human1"],"users":[],"accessibleTo":["Human1"],"roofed":true,"temperatureC":21.5,"room":"room-3","quality":{"type":"graded","category":"QUALITY_CATEGORY_GOOD"},"stuff":"WoodLog","forSlaves":false},{"id":"Thing_SleepingSpot2","medical":false,"prisoners":true,"owners":[],"users":[],"accessibleTo":[],"roofed":false,"temperatureC":-3,"room":"room-4","quality":{"type":"ungraded"},"stuff":null,"forSlaves":false}]""",
            WireJson.Serialize(rows));
        Assert.All(rows, row => Assert.Equal(row, WireJson.Deserialize<UpkeepBed>(WireJson.Serialize(row))));
    }

    [Fact]
    public void PeopleRoundTripExactly()
    {
        EquatableArray<UpkeepPerson> rows =
        [
            new()
            {
                Pawn = new PawnId("Human1"), OwnedBed = "Thing_Bed1", ComfortableMinC = 16, ComfortableMaxC = 26, Partners = [new PawnId("Human2")],
                BedSharingAllowed = true, Title = new RoyalStanding.Titled("Knight", true, ["Ascetic"]),
            },
            new()
            {
                Pawn = new PawnId("Human3"), OwnedBed = null, ComfortableMinC = 16, ComfortableMaxC = 26, Partners = [],
                BedSharingAllowed = false, Title = new RoyalStanding.Untitled(),
            },
        ];

        Assert.Equal(
            """[{"pawn":"Human1","ownedBed":"Thing_Bed1","comfortableMinC":16,"comfortableMaxC":26,"partners":["Human2"],"bedSharingAllowed":true,"title":{"type":"titled","title":"Knight","ascetic":true,"precepts":["Ascetic"]}},{"pawn":"Human3","ownedBed":null,"comfortableMinC":16,"comfortableMaxC":26,"partners":[],"bedSharingAllowed":false,"title":{"type":"untitled"}}]""",
            WireJson.Serialize(rows));
        Assert.All(rows, row => Assert.Equal(row, WireJson.Deserialize<UpkeepPerson>(WireJson.Serialize(row))));
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
            WireJson.Serialize(rows));
        Assert.All(rows, row => Assert.Equal(row, WireJson.Deserialize<ColonyAnimal>(WireJson.Serialize(row))));
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
            WireJson.Serialize(flooring));
        Assert.Equal(flooring, WireJson.Deserialize<FlooringFacts>(WireJson.Serialize(flooring)));
    }
}
