using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Observation;
using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The first layout plan's shelter (#3406, from the deleted shelter/climate-*, shelter/siting and shelter/solo-cold
/// cases): the plan latches the map's climate from the outdoor curve, the shelter template holds the climate's
/// campfire and cooler slots on the shelter's interior, the climate building goes to the room that owns it, and
/// the one shelter is sized for the colony and overlaps no other room.
/// </summary>
public sealed class ShelterLayoutPlanTests
{
    // Twelfths' outdoor means (C): a tundra dips below freezing, a desert peaks past HotEnter, a temperate map does neither.
    private static readonly double[] ColdCurve = [-22, -18, -10, -2, 4, 9, 12, 10, 3, -6, -14, -20];
    private static readonly double[] HotCurve = [24, 27, 31, 35, 39, 42, 43, 41, 37, 33, 28, 25];
    private static readonly double[] MildCurve = [6, 8, 11, 14, 17, 20, 22, 21, 18, 14, 10, 7];

    public static TheoryData<string> Climates => ["cold", "hot", "mild"];

    private static double[] Curve(string climate) => climate switch
    {
        "cold" => ColdCurve,
        "hot" => HotCurve,
        _ => MildCurve,
    };

    /// <summary>An open, firm, walkable map whose climate latches are read off the curve, as the colony review sets them.</summary>
    internal static MapSurvey Survey(string climate) => new()
    {
        Bounds = new Bounds(80, 80),
        Cells = [.. Enumerable.Range(0, 80).SelectMany(x => Enumerable.Range(0, 80).Select(z => new SurveyCell
        {
            Cell = new Cell(x, z), Walkable = true, Footing = Footing.Firm, Fertility = 1,
        }))],
        Cold = LayoutShelterRules.ColdMapCurve(Curve(climate)),
        Hot = LayoutShelterRules.HotMapCurve(Curve(climate)),
    };

    /// <summary>
    /// A first plan's facts (Derive), every station fact read (#3652): the food census, an empty ledger and demand, no
    /// patient, prisoner or baby, and the dining furniture. The catalog holds a bed, a stove cooking one meal, the
    /// dining table and a shelf, and prices the lamp and the power sites the chain needs.
    /// </summary>
    private static readonly Resource Meal = new("TestMeal");
    private static readonly Resource Raw = new("TestRaw");

    internal static StationFacts Facts(MapSurvey s, long colonists) => new()
    {
        Catalog = new StationCatalog
        {
            Stations = new Dictionary<string, Station>
            {
                ["Bed"] = new Station
                {
                    Shape = new InteriorPieceDef("Bed", new Cell(1, 2), RoomRole.None, null),
                    Contributions = [new RoleScore { Role = RoomRole.Bedroom, Weight = 1 }],
                    Capacity = new StationCapacity { Kind = CapacityKind.Sleepers, Slots = 1 },
                    EmptyCountsForBarracks = true,
                },
                ["Stove"] = new Station
                {
                    Shape = new InteriorPieceDef("Stove", new Cell(3, 1), RoomRole.None, null),
                    Contributions = [new RoleScore { Role = RoomRole.Kitchen, Weight = 1 }],
                    Capacity = new StationCapacity { Kind = CapacityKind.Workers, Slots = 1, Recipes = [new RecipeWorkAmount("CookMeal", 300)] },
                },
                ["Table2x2c"] = new Station
                {
                    Shape = new InteriorPieceDef("Table2x2c", new Cell(2, 2), RoomRole.None, null),
                    Contributions = [new RoleScore { Role = RoomRole.DiningRoom, Weight = 1 }],
                },
                ["Shelf"] = new Station
                {
                    Shape = new InteriorPieceDef("Shelf", new Cell(2, 1), RoomRole.None, null),
                    Contributions = [new RoleScore { Role = RoomRole.Storeroom, Weight = 1 }],
                    Capacity = new StationCapacity { Kind = CapacityKind.Items, Slots = 6 },
                },
            },
            Light = new LightModel { Standing = new LampModel(75, 30, 12, 217) },
            Power = new PowerModel
            {
                Wind = new SupplyRow { NominalW = 2300, Build = 300, Profile = new PowerSourceProfile(.5, .5, false) },
                Solar = new SupplyRow { NominalW = 1700, Build = 400, Profile = new PowerSourceProfile(.5, 0, true) },
                Battery = new PowerBattery(600, .5),
                Conduit = 2,
                Wall = 8,
            },
        },
        Furniture = new RoomFurniture { Beds = [new FurnitureBed { Def = "Bed", Slots = 1 }] },
        Items = new ItemFacts
        {
            Market = new Dictionary<Resource, double> { [SiteRules.SiteSteelUnit] = 1.9 },
            Nutrition = new Dictionary<Resource, double> { [Meal] = 0.9 },
        },
        Recipes = new Dictionary<string, RecipeFlow>
        {
            ["CookMeal"] = new RecipeFlow { Food = true, Ingredients = [[new Amount(Raw, 10)]], Products = [new Amount(Meal, 1)] },
        },
        Races = new AnimalRaceCatalog(),
        Dining = new DiningFurniture
        {
            Chair = new InteriorPieceDef("DiningChair", new Cell(1, 1), RoomRole.None, null),
            Table = new InteriorPieceDef("Table2x2c", new Cell(2, 2), RoomRole.None, null),
            Pin = new InteriorPieceDef("DiningPin", new Cell(1, 1), RoomRole.None, null),
        },
        Colonists = Fact.Known(colonists),
        Patients = Fact.Known(EquatableArray.From<CarePawn>([])),
        Prisoners = Fact.Known(0),
        WorkPawns = Fact.Known(EquatableArray.From<WorkPawn>([])),
        Food = Fact.Known(new FoodSupply
        {
            Complete = Fact.Known(true),
            Consumers = [.. Enumerable.Range(0, (int)colonists).Select(i => new FoodConsumer { Id = new PawnId("c" + i), NutritionPerDay = Fact.Known(1.6) })],
        }),
        Orders = Fact.Known(EquatableArray.From<OrderSpec>([])),
        Stock = Fact.Known<IReadOnlyDictionary<Resource, long>>(new Dictionary<Resource, long>()),
        Survey = s,
        Built = new HashSet<int>(),
        RoofDistance = 6,
        Derive = true,
    };

    internal static LayoutPlan FirstPlan(string climate, long colonists)
    {
        var s = Survey(climate);
        var (plan, _) = LayoutDeriveRules.DeriveLayoutPlan(s, Facts(s, colonists), []);
        Assert.True(plan.IsKnown, "no first plan on an open " + climate + " map");
        return plan.Value;
    }

    private static PieceShapes Shapes => new()
    {
        Defs = new Dictionary<string, InteriorPieceDef>
        {
            [LayoutCapabilityRules.ShelterHeatDef] = new(LayoutCapabilityRules.ShelterHeatDef, new Cell(1, 1), RoomRole.None, null),
            [LayoutCapabilityRules.ShelterCoolDef] = new(LayoutCapabilityRules.ShelterCoolDef, new Cell(1, 1), RoomRole.None, null),
        },
    };

    private static bool Inside(Rectangle room, Rectangle piece) =>
        piece.X >= room.X && piece.Z >= room.Z && piece.X + piece.Width <= room.X + room.Width && piece.Z + piece.Height <= room.Z + room.Height;

    [Theory]
    [MemberData(nameof(Climates))]
    public void TheFirstPlanLatchesTheMapsClimateFromTheOutdoorCurve(string climate)
    {
        var plan = FirstPlan(climate, 3);
        Assert.Equal(climate == "cold", plan.Cold);
        Assert.Equal(climate == "hot", plan.Hot);
    }

    [Theory]
    [MemberData(nameof(Climates))]
    public void TheShelterTemplateHoldsTheClimatesSlotsOnTheShelterInterior(string climate)
    {
        var plan = FirstPlan(climate, 3);
        var shelter = Assert.Single(plan.RoomsOf(LayoutSurveyRules.PlannedShelter));
        var bounds = shelter.Region.Bounds();
        var (room, ok) = ShelterRules.ShelterRoom(shelter, [], Shapes, 3, LayoutShelterRules.ShelterCampfires(plan.Cold), LayoutShelterRules.ShelterCoolers(plan.Hot), []);
        Assert.True(ok);
        var (pieces, planned) = InteriorRegionRules.PlanRoomInterior(room, new InteriorPieceDef());
        Assert.True(planned);
        var campfires = pieces.Pieces.Where(p => p.Def == LayoutCapabilityRules.ShelterHeatDef).ToList();
        var coolers = pieces.Pieces.Where(p => p.Def == LayoutCapabilityRules.ShelterCoolDef).ToList();
        Assert.Equal(climate == "cold" ? 2 : 0, campfires.Count);
        Assert.Equal(climate == "hot" ? 1 : 0, coolers.Count);
        Assert.All(campfires.Concat(coolers), p => Assert.True(Inside(bounds, p.Rect), $"{p.Def} at {p.Rect} outside the shelter {bounds}"));
    }

    [Theory]
    [MemberData(nameof(Climates))]
    public void TheClimateBuildingGoesToTheRoomThatOwnsIt(string climate)
    {
        var facts = new ColonyProjection { LayoutPlan = Fact.Known(FirstPlan(climate, 3)) };
        var cooking = new RoundsBuildingPlanner { Concern = FoodRules.CookingNeed.FilingKey, Definition = LayoutCapabilityRules.ShelterHeatDef };
        // A cold map's cooking campfire takes the shelter's slot; elsewhere it stands in the planned kitchen.
        Assert.Equal(climate == "cold", cooking.TakesShelterSlot(facts));
        Assert.Equal(climate != "cold", cooking.HostsInPlannedRoom(PlannedRole.Kitchen, facts));
        var cooler = new RoundsBuildingPlanner
        {
            Concern = ShelterFamily.Temperature.FilingKey,
            Definition = LayoutCapabilityRules.ShelterCoolDef,
            Temperature = new TemperatureProposal { Method = TemperatureMethod.Cool },
        };
        Assert.Equal(climate == "hot", cooler.TakesShelterSlot(facts));
    }

    [Theory]
    [InlineData("cold", 1)]
    [InlineData("cold", 3)]
    [InlineData("hot", 3)]
    [InlineData("mild", 3)]
    public void TheFirstPlanHoldsOneShelterSizedForTheColonyAndClearOfEveryOtherRoom(string climate, int colonists)
    {
        var plan = FirstPlan(climate, colonists);
        var shelter = Assert.Single(plan.RoomsOf(LayoutSurveyRules.PlannedShelter));
        var inner = shelter.Region.Bounds();
        Assert.True(inner.Width * inner.Height >= LayoutShelterRules.ShelterInteriorArea(colonists, LayoutShelterRules.ShelterCampfires(plan.Cold), LayoutShelterRules.ShelterCoolers(plan.Hot)),
            $"shelter interior {inner.Width}x{inner.Height} for {colonists} colonists on a {climate} map");
        var floor = LayoutUtilityRules.RectangleCells(inner).ToHashSet();
        foreach (var other in plan.Rooms.Where(r => r.Role != LayoutSurveyRules.PlannedShelter))
        {
            Assert.DoesNotContain(LayoutUtilityRules.RectangleCells(other.Region.Bounds()), floor.Contains);
        }
        // Research is the shelter's capability, so the first plan holds no laboratory, a colony of one's included.
        Assert.Empty(plan.RoomsOf(LayoutSurveyRules.PlannedLab));
    }
}
