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
    private static MapSurvey Survey(string climate) => new()
    {
        Bounds = new Bounds(80, 80),
        Cells = [.. Enumerable.Range(0, 80).SelectMany(x => Enumerable.Range(0, 80).Select(z => new SurveyCell
        {
            Cell = new Cell(x, z), Walkable = true, Footing = Footing.Firm, Fertility = 1,
        }))],
        Cold = LayoutShelterRules.ColdMapCurve(Curve(climate)),
        Hot = LayoutShelterRules.HotMapCurve(Curve(climate)),
    };

    /// <summary>A first plan's facts (Derive, no ledger or census): the catalog prices only the lamp and the power sites the chain needs.</summary>
    private static StationFacts Facts(MapSurvey s, long colonists) => new()
    {
        Catalog = new StationCatalog
        {
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
        Furniture = new RoomFurniture(),
        Items = new ItemFacts { Market = new Dictionary<Resource, double> { [SiteRules.SiteSteelUnit] = 1.9 } },
        Races = new AnimalRaceCatalog(),
        Colonists = Fact.Known(colonists),
        Survey = s,
        Built = new HashSet<int>(),
        RoofDistance = 6,
        Derive = true,
    };

    private static LayoutPlan FirstPlan(string climate, long colonists)
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
