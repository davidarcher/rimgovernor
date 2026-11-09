using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store.Codec;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The second wall layer in the tiled plan (#4046, epic #2795): on a cold curve with a cheap wall the layer lands on
/// every outdoor-facing wall of a roofed room and nowhere else (never a shared wall, a corner or the cell beyond the
/// door); with today's site weights and a wooden wall it is not paid and the decision says so; a temperate map has no
/// shell question; the plan rejects a layer on a room, an airlock or a site; and the plan's JSON carries the field,
/// a blob from before it reading empty.
/// </summary>
public sealed class TileShellTests
{
    private const string Bed = "Bed";
    private static readonly RoomFurniture Furniture = new() { Beds = [new FurnitureBed { Def = Bed, Slots = 1 }] };

    private static ClimateModel Climate(double wall) => new()
    {
        Vent = new Exchange { IntervalTicks = 250, Rate = 14 },
        DoorOpen = new Exchange { IntervalTicks = 34, Rate = 1 },
        DoorClosed = new Exchange { IntervalTicks = 375, Rate = 1 },
        Door = new DoorTiming { OpenTicks = 45, CloseDelayTicks = 110, MoveTicks = 26 },
        Heater = new ClimatePlant { HeatPerSecond = 21, PowerW = 175 },
        Cooler = new ClimatePlant { HeatPerSecond = 21, PowerW = 200 },
        Build = new ClimateBuildValue { Vent = 30, Door = 25, Wall = wall, Heater = 50, Cooler = 90 },
        StepTicks = 250,
        TicksPerSecond = 60,
        SwingC = 7,
        WallFactor = .00017,
        ThinRoofRate = .00005,
        Living = new Band(16, 26),
        HeldC = 21,
    };

    private static StationCatalog Catalog(double wall) => new()
    {
        PlayBuildings = [],
        Stations = new Dictionary<string, Station>
        {
            [Bed] = new()
            {
                Shape = new InteriorPieceDef(Bed, new Cell(1, 2), RoomRole.Bedroom, new InteractionCell.None()),
                Contributions = [new() { Role = RoomRole.Bedroom, When = RoleCondition.BedFree, Weight = 100000 }],
            },
        },
        Climate = Climate(wall),
    };

    private static MapSurvey OpenMap() => new()
    {
        Bounds = new Bounds(40, 30),
        Cells = [.. Enumerable.Range(0, 40).SelectMany(x => Enumerable.Range(0, 30).Select(z => new SurveyCell { Cell = new Cell(x, z), Walkable = true, Footing = Footing.Firm }))],
    };

    private static RoomGroup Bedroom(int id) => new()
    {
        Id = id,
        Role = RoomRole.Bedroom,
        Planned = PlannedRole.Bedroom,
        Stations = [new GroupStation("bed", Bed, StationPurpose.Sleep, 1)],
    };

    /// <summary>A wooden wall: five logs at the log's market value.</summary>
    private const double WoodenWall = 6;

    private static TileInput Input(IReadOnlyList<Rectangle> rects, double[] curve, double wall)
    {
        var survey = OpenMap();
        return new TileInput
        {
            OutdoorCurve = EquatableArray.From(curve),
            Groups = [.. rects.Select((_, i) => Bedroom(i))],
            Embedding = new Embedding(),
            Blocks = [.. rects.Select((r, i) => new EmbeddedBlock { Group = i, Interior = r })],
            Catalog = Catalog(wall),
            Zones = LayoutZoningRules.Zone(survey),
            Survey = survey,
            Light = new LightPrice(),
            Graph = new StationGraph(),
            Furniture = Furniture,
            Dining = new DiningFurniture(),
            SteelValue = 1.9,
            NextRoomId = 1,
        };
    }

    /// <summary>The tiled room, in the middle of the map's buildable core so its whole surround is open ground.</summary>
    private static readonly Rectangle Room = new(18, 13, 3, 3);

    /// <summary>A hand-built room for the plan tests.</summary>
    private static readonly Rectangle Hut = new(10, 10, 3, 3);

    /// <summary>Whether c is one cell out from the ring of r, off its corners: the cells the game samples.</summary>
    private static bool OnLayer(Rectangle r, Cell c)
    {
        var outer = LayoutPerimeterRules.Pad(r, 2);
        var onEdge = c.X == outer.X || c.X == outer.X + outer.Width - 1 || c.Z == outer.Z || c.Z == outer.Z + outer.Height - 1;
        var corner = (c.X == outer.X || c.X == outer.X + outer.Width - 1) && (c.Z == outer.Z || c.Z == outer.Z + outer.Height - 1);
        return onEdge && !corner && PlannedRules.RectCells(outer).Contains(c);
    }

    [Fact]
    public void ACheapLayerOnAnIceSheetLandsOnEveryOutdoorFacingWallAndNowhereElse()
    {
        var tiled = LayoutTileRules.TileEmbedding(Input([Room], BedMergePriceTests.IceSheet, wall: 0.01));

        var room = Assert.Single(tiled.Plan.Rooms);
        Assert.Equal(0, Assert.Single(tiled.Decisions, d => d.Reason == LayoutTileRules.ReasonTileShell).Group);
        var layer = tiled.Plan.OuterWalls.Cells().ToList();
        // Three a side less the cell beyond the door; a corner never.
        Assert.True(11 == layer.Count, $"layer [{string.Join(' ', layer)}] door {room.Door}");
        Assert.All(layer, c => Assert.True(OnLayer(Room, c), $"{c}"));
        var rot = Assert.IsType<DoorSide.Doored>(room.DoorSide).Side;
        Assert.DoesNotContain(LayoutAirlockRules.Outside(room.Door, rot), layer);
        Assert.True(tiled.Plan.Valid());
        Assert.True(LayoutPlan.CheckRoutes(tiled.Plan).Ok);
    }

    [Fact]
    public void TheLayerNeverStandsOnASharedWall()
    {
        // Two bedrooms wall to wall: the ring column at x 21 is shared, and each room's floor is the other's cell two out.
        Rectangle[] rects = [Room, new(22, 13, 3, 3)];
        var tiled = LayoutTileRules.TileEmbedding(Input(rects, BedMergePriceTests.IceSheet, wall: 0.01));

        Assert.Equal(2, tiled.Decisions.Count(d => d.Reason == LayoutTileRules.ReasonTileShell));
        var layer = tiled.Plan.OuterWalls.Cells().ToList();
        Assert.DoesNotContain(layer, c => c.X == 21);
        Assert.All(layer, c => Assert.True(OnLayer(rects[0], c) || OnLayer(rects[1], c), $"{c}"));
        Assert.True(tiled.Plan.Valid());
    }

    [Fact]
    public void AWoodenLayerAtTodaysSiteRateIsNotPaid()
    {
        // The layer halves eleven walls' 0.00017 a tick a degree against the curve, about 20 W of heating a year, while
        // eleven wooden wall cells at the layout's rate (BuildW: 20.5 W a cell at 1.9 steel) cost 226 W: the room alone
        // totals 250 W and doubled 456 W, so the decision says not paid until #4044 reprices power.
        var tiled = LayoutTileRules.TileEmbedding(Input([Room], BedMergePriceTests.IceSheet, WoodenWall));

        var m = Climate(WoodenWall);
        var room = new ClimateRoom { Cells = 9, Band = LayoutRoofRules.ComfortBand(), OutdoorWalls = 11, Links = EquatableArray.From(new[] { new ClimateLink { Opening = Opening.Door, Side = new ClimateSide { Outdoors = true } } }) };
        var weights = SiteRules.DefaultSiteTypeWeights();
        double Total(ClimateRoom r)
        {
            var w = ClimateRules.ClimateLoad(m, BedMergePriceTests.IceSheet, r, 0);
            return w.MeanW + m.PlantsW(weights, 1.9, w);
        }
        var lone = Total(room);
        var doubled = Total(room with { Shell = ShellLayers.Doubled }) + (11 * ClimateRules.BuildW(weights, WoodenWall, 1.9));
        Assert.InRange(lone, 240, 260);
        Assert.InRange(doubled, 440, 470);
        Assert.Equal(0, Assert.Single(tiled.Decisions, d => d.Reason == LayoutTileRules.ReasonTileShellNotPaid).Group);
        Assert.True(tiled.Plan.OuterWalls.Empty(), $"lone {lone} doubled {doubled}");
    }

    [Fact]
    public void ATemperateMapHasNoShellQuestion()
    {
        var tiled = LayoutTileRules.TileEmbedding(Input([Room], [.. Enumerable.Repeat(18.0, 12)], wall: 0.01));

        Assert.Equal(-1, Assert.Single(tiled.Decisions, d => d.Reason == LayoutTileRules.ReasonTileShellTemperate).Group);
        Assert.True(tiled.Plan.OuterWalls.Empty());
    }

    private static LayoutPlan Plan(params Cell[] layer)
    {
        var room = new PlannedRoom { Id = 1, Role = PlannedRole.Bedroom, Region = RegionRules.RectRegion(Hut), Door = new Cell(11, 9), DoorSide = new DoorSide.Doored(Rotation.South) };
        var airlock = PlannedRoom.AttachAirlock(room.Door, Rotation.South) with { Id = 2, Parent = 1 };
        var vent = LayoutPerimeterSitesRules.NewSite("vent_13_11", LayoutTileVentRules.NodeVent, RegionRules.RectRegion(new Rectangle(13, 11, 1, 1)), LayoutTileVentRules.ReasonTileVent) with { Rotation = new Heading.Toward(Rotation.East) };
        return new LayoutPlan
        {
            NextRoomId = 3,
            Rooms = [room, airlock],
            Sites = [vent],
            OuterWalls = RegionRules.RegionOf(layer),
        };
    }

    [Fact]
    public void ThePlanRejectsALayerOnARoomAnAirlockOrASite()
    {
        Assert.True(Plan().Valid());
        Assert.True(Plan(new Cell(14, 10), new Cell(10, 14)).Valid()); // one out from the ring: where the layer stands

        Assert.False(Plan(new Cell(11, 11)).Valid(), "a floor cell");
        Assert.False(Plan(new Cell(13, 10)).Valid(), "a ring cell");
        Assert.False(Plan(new Cell(10, 8)).Valid(), "an airlock wall");
        Assert.False(Plan(new Cell(11, 7)).Valid(), "the airlock's outer door");
        var siteOnLayer = Plan() with { Sites = [Plan().Sites[0] with { Footprint = RegionRules.RectRegion(new Rectangle(14, 10, 1, 1)) }] };
        Assert.False((siteOnLayer with { OuterWalls = RegionRules.RegionOf([new Cell(14, 10)]) }).Valid(), "a site");
    }

    [Fact]
    public void ThePlanRoundTripsItsLayerAndABlobFromBeforeItReadsEmpty()
    {
        var plan = Plan(new Cell(14, 10), new Cell(14, 11), new Cell(10, 14));
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            Codecs.Default.Write(writer, plan);
        }
        var json = Encoding.UTF8.GetString(stream.ToArray());
        Assert.Contains("\"OuterWalls\"", json, StringComparison.Ordinal);

        using var doc = JsonDocument.Parse(json);
        var back = Codecs.Default.Read<LayoutPlan>(doc.RootElement);
        Assert.Equal(plan.OuterWalls.Cells(), back.OuterWalls.Cells());

        var older = JsonNode.Parse(json)!.AsObject();
        Assert.True(older.Remove("OuterWalls"));
        using var olderDoc = JsonDocument.Parse(older.ToJsonString());
        Assert.True(Codecs.Default.Read<LayoutPlan>(olderDoc.RootElement).OuterWalls.Empty());
    }
}
