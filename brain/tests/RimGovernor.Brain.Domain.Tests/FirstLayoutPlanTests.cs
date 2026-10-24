using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Observation;
using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The first layout plan (#3406, #3659): the plan latches the map's climate from the outdoor curve and
/// hosts research at the catalog's research bench.
/// </summary>
public sealed class FirstLayoutPlanTests
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
        Cold = LayoutRoofRules.ColdMapCurve(Curve(climate)),
        Hot = LayoutRoofRules.HotMapCurve(Curve(climate)),
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
                ["ResearchBench"] = new Station
                {
                    Shape = new InteriorPieceDef("ResearchBench", new Cell(3, 1), RoomRole.None, null),
                    Contributions = [new RoleScore { Role = RoomRole.Laboratory, Weight = 60, Additive = true }],
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
        Furniture = new RoomFurniture { Beds = [new FurnitureBed { Def = "Bed", Slots = 1 }], Bench = new Dictionary<RoomRole, string> { [RoomRole.Laboratory] = "ResearchBench" } },
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
        Colonists = colonists,
        Patients = Fact.Known(EquatableArray.From<CarePawn>([])),
        Prisoners = Fact.Known(0),
        WorkPawns = Fact.Known(EquatableArray.From<WorkPawn>([])),
        Rest = RestSchedule.Of([]),
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

    /// <summary>
    /// The facts with the bed merge's required inputs read (#3747): the climate's outdoor curve, the climate model's living
    /// band, the rice that prices a work-day, and calm sleepers in a wooded ring.
    /// </summary>
    internal static StationFacts Priced(StationFacts f, string climate) => f with
    {
        Catalog = f.Catalog with { Climate = BedMergePriceTests.Catalog.Climate },
        OutdoorCurve = Fact.Known(EquatableArray.From(Curve(climate))),
        Crops = [BedMergePriceTests.Rice],
        BedMerge = BedMergePriceTests.Wood(BedMergePriceTests.Calm),
    };

    internal static LayoutPlan FirstPlan(string climate, long colonists)
    {
        var s = Survey(climate);
        var (plan, _) = LayoutDeriveRules.DeriveLayoutPlan(s, Priced(Facts(s, colonists), climate), []);
        Assert.True(plan.IsKnown, "no first plan on an open " + climate + " map");
        return plan.Value;
    }

    [Theory]
    [MemberData(nameof(Climates))]
    public void TheFirstPlanLatchesTheMapsClimateFromTheOutdoorCurve(string climate)
    {
        var plan = FirstPlan(climate, 3);
        Assert.Equal(climate == "cold", plan.Cold);
        Assert.Equal(climate == "hot", plan.Hot);
    }

    [Theory]
    [InlineData("cold", 1)]
    [InlineData("cold", 3)]
    [InlineData("hot", 3)]
    [InlineData("mild", 3)]
    public void TheFirstPlanHostsResearchInALaboratory(string climate, int colonists)
    {
        var plan = FirstPlan(climate, colonists);
        // #3659: a first plan's rooms are the station graph's; research is a station, hosted as any other.
        Assert.NotEmpty(plan.RoomsOf(LayoutSurveyRules.PlannedLab));
    }
}
