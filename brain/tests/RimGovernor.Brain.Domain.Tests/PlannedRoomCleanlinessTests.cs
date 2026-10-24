using System.Text;
using System.Text.Json;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store.Codec;
using PlannedRoomPrediction = RimGovernor.Contracts.Wire.Observations.PlannedRoomPrediction;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The predicted cleanliness of a planned room (#3797): its planned floor goes to the native room prediction as pieces,
/// the station group's filth runs are kept on the room by the tiler, and the steady state adds the filth those runs leave
/// standing by the layout's filth model (Residual, FilthModel.Cleanliness) to the native value.
/// </summary>
public sealed class PlannedRoomCleanlinessTests
{
    // A 3x3 kitchen at (10..12, 10..12): RoomStatWorker_Cleanliness sums each cell's terrain stat and divides by the 9 cells.
    private static readonly PlannedRoom Kitchen = new()
    {
        Id = 1, Role = PlannedRole.Kitchen, Region = RegionRules.RectRegion(new Rectangle(10, 10, 3, 3)),
        FilthRuns = new Dictionary<string, FilthRuns> { [BedStationPriceTests.Butcher] = new() { Runs = 2, Ticks = 1500 } },
    };

    private static PlannedRoomPrediction Predicted(double cleanliness, string role = "Kitchen") =>
        new("1", "WoodLog", role, Known(10d), Known(2), 9, 0, false) { Cleanliness = cleanliness };

    private static double Dirty(PlannedRoomPrediction prediction, PlannedRoom room, FilthModel filth) =>
        PlannedRoomCleanlinessRules.Steady(prediction, room.FilthRuns.Values, filth).Dirty;

    [Fact]
    public void ThePlannedFloorSitesAreAskedAsPiecesInEveryRingStuff()
    {
        var cells = EquatableArray.From(Kitchen.Region.Cells());
        PlannedBuildSite[] sites =
        [
            new("planned/room1/wall/WoodLog", 1, OpKind.WallIn, "Wall", "WoodLog", Rotation.North, [new Cell(9, 9)]),
            new("planned/room1/wall/Steel", 1, OpKind.WallIn, "Wall", "Steel", Rotation.North, [new Cell(9, 9)]),
            new("planned/room1/floor/SterileTile", 1, OpKind.FloorIn, "SterileTile", "", Rotation.North, cells),
        ];

        var shapes = PlannedRules.PlannedRoomPredictionRequestOf([Kitchen], sites, []);

        Assert.Equal(["WoodLog", "Steel"], shapes.Select(s => s.Stuff));
        Assert.All(shapes, s =>
        {
            var floor = s.Pieces.Where(p => p.Definition == "SterileTile").ToList();
            Assert.Equal(cells.OrderBy(c => c.X).ThenBy(c => c.Z), floor.Select(p => p.Cell));
            Assert.All(floor, p => Assert.Equal("", p.Stuff));
            Assert.Single(s.Pieces, p => p.Definition == "Wall");
        });
        // A room that owes only its floor is not asked: the read predicts a room its ring would close. A kitchen is, its
        // cleanliness priced by the kitchen and butchery credits (#3802).
        var workshop = Kitchen with { Role = PlannedRole.Workshop };
        Assert.Empty(PlannedRules.PlannedRoomPredictionRequestOf([workshop], sites.Skip(2), []));
        var floorOnly = Assert.Single(PlannedRules.PlannedRoomPredictionRequestOf([Kitchen], sites.Skip(2), []));
        Assert.Equal(("", 9), (floorOnly.Stuff, floorOnly.Pieces.Count));
    }

    [Fact]
    public void TheFloorStatPerCellCarriesIntoTheSteadyStateAgainstAHandComputedValue()
    {
        // The kitchen on bare soil (Cleanliness -1 a cell) and on sterile tile (0.6 a cell), as RoomStatWorker_Cleanliness
        // reads the 9 cells with nothing else in them: -9 / 9 and 5.4 / 9. The butchery's 2 runs a day of 1500 ticks leave
        // one unit of Filth_Blood (-10) standing at once (600 stand ticks, under one run apart): -10 / 9 more.
        var soil = Dirty(Predicted(-9 / 9d), Kitchen, BedStationPriceTests.Filth);
        var tile = Dirty(Predicted(5.4 / 9), Kitchen, BedStationPriceTests.Filth);

        Assert.Equal((-9 - 10) / 9d, soil, 9);
        Assert.Equal((5.4 - 10) / 9, tile, 9);
        Assert.Equal(1.6, tile - soil, 9);
    }

    [Fact]
    public void AButcherysFilthRunsLowerIt()
    {
        var clean = Kitchen with { FilthRuns = new Dictionary<string, FilthRuns>() };
        Assert.Equal(-1, Dirty(Predicted(-1), clean, BedStationPriceTests.Filth), 9);

        // Two runs 1500 ticks apart leave one unit standing at once; runs closer than the stand leave more: 4 runs of 100
        // ticks keep all 4 standing.
        Assert.Equal(-1 - (10 / 9d), Dirty(Predicted(-1), Kitchen, BedStationPriceTests.Filth), 9);
        var busy = Kitchen with { FilthRuns = new Dictionary<string, FilthRuns> { [BedStationPriceTests.Butcher] = new() { Runs = 4, Ticks = 100 } } };
        Assert.Equal(-1 - (40 / 9d), Dirty(Predicted(-1), busy, BedStationPriceTests.Filth), 9);

        // A room that is no proper room (None) has the stat's roomless score whatever stands in it.
        Assert.Equal(0, Dirty(Predicted(0, "None"), Kitchen, BedStationPriceTests.Filth), 9);
    }

    [Fact]
    public void ARoomsPoisonChanceIsTheCurveAtItsCleanlinessWeightedByTheShareTheFilthStands()
    {
        // 2 runs of 1500 ticks a day, one batch: the filth stands from the first run to the second's stand, 2100 ticks of the day (Residual's exposure 0.035). Clean at
        // -1 the curve gives 0.012, dirty at -1 - 10/9 it gives 0.01422.
        var filth = BedStationPriceTests.Filth with { FoodPoisonRoomless = .02 };
        var clean = LayoutFilthRules.EvaluateCurve(filth.FoodPoison, -1);
        var dirty = LayoutFilthRules.EvaluateCurve(filth.FoodPoison, -1 - (10 / 9d));
        const double exposure = 2100d / GameTime.TicksPerDay;
        Assert.Equal(((1 - exposure) * clean) + (exposure * dirty),
            PlannedRoomCleanlinessRules.FoodPoisonChance(Predicted(-1), Kitchen.FilthRuns.Values, filth), 9);
        Assert.Equal(clean, PlannedRoomCleanlinessRules.FoodPoisonChance(Predicted(-1), [], filth), 9);
        // A None room's chance is the stat's roomless score.
        Assert.Equal(.02, PlannedRoomCleanlinessRules.FoodPoisonChance(Predicted(0, "None"), Kitchen.FilthRuns.Values, filth), 9);
        Assert.Equal("filth_model", Assert.Throws<RequiredFactException>(() =>
            PlannedRoomCleanlinessRules.FoodPoisonChance(Predicted(-1), [], filth with { FoodPoison = [] })).Fact);
    }

    [Fact]
    public void AnUnreadFloorDefOrFilthModelFailsNamingIt()
    {
        // Native leaves the row unread (Role "") when a piece names no ThingDef nor TerrainDef.
        var unread = Assert.Throws<RequiredFactException>(() => Dirty(Predicted(0, ""), Kitchen, BedStationPriceTests.Filth));
        Assert.Equal("planned_room_prediction:1", unread.Fact);

        var model = Assert.Throws<RequiredFactException>(() => Dirty(Predicted(-1), Kitchen, new FilthModel()));
        Assert.Equal("filth_model", model.Fact);
        // No filth runs read no filth model.
        Assert.Equal(-1, Dirty(Predicted(-1), Kitchen with { FilthRuns = new Dictionary<string, FilthRuns>() }, new FilthModel()), 9);
    }

    [Fact]
    public void TheClusteringAndTheTilerKeepTheGroupsFilthRunsOnTheRoom()
    {
        var clustering = LayoutClusterRules.ClusterStations(BedStationPriceTests.Cluster(
            BedStationPriceTests.Butchery(2), 4, BedStationPriceTests.Colony(nightCook: false), BedStationPriceTests.Mood));
        var butchery = Assert.Single(clustering.Groups, g => LayoutRegroupRules.HoldsNode(g, BedStationPriceTests.Butcher));
        Assert.Equal(new FilthRuns { Runs = 2, Ticks = 1500 }, butchery.FilthRuns[BedStationPriceTests.Butcher]);

        var runs = new Dictionary<string, FilthRuns> { [BedStationPriceTests.Butcher] = new() { Runs = 2, Ticks = 1500 } };
        var survey = PlannedBarracksTests.OpenMap();
        var tiled = LayoutTileRules.TileEmbedding(new TileInput
        {
            Groups = [PlannedBarracksTests.BarracksGroup with { FilthRuns = runs, Meals = 6 }],
            Embedding = new Embedding(),
            Blocks = [new EmbeddedBlock { Group = 0, Interior = new Rectangle(15, 12, 5, 4) }],
            Catalog = PlannedBarracksTests.Catalog,
            Zones = LayoutZoningRules.Zone(survey),
            Survey = survey,
            Light = new LightPrice(),
            Graph = new StationGraph(),
            Furniture = PlannedBarracksTests.Furniture,
            Dining = new DiningFurniture(),
            NextRoomId = 1,
        });
        var room = Assert.Single(tiled.Plan.Rooms);
        Assert.Equal(runs.ToList(), room.FilthRuns.ToList());
        Assert.Equal(6, room.Meals);

        // The runs save and load with the plan.
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            Codecs.Default.Write(writer, tiled.Plan);
        }
        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(stream.ToArray()));
        var back = Assert.Single(Codecs.Default.Read<LayoutPlan>(doc.RootElement).Rooms);
        Assert.Equal(runs.ToList(), back.FilthRuns.ToList());
        Assert.Equal(6d, back.Meals);
    }
}
