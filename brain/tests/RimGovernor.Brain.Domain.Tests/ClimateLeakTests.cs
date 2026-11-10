using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The climate model's static leakage (#4045, epic #2795): a closed room costs heating on a cold curve through its
/// outdoor-facing walls and thin roof at the game's constants (environment/wall-layers, #4043); a second wall layer
/// halves the wall term and nothing else; a leakier room sizes more heaters; the plan counts a room's outdoor-facing
/// walls as RoomTempTracker samples them; and a model missing either rate is unknown.
/// </summary>
public sealed class ClimateLeakTests
{
    private const double WallFactor = .00017;
    private const double ThinRoofRate = .00005;

    private static readonly ClimateModel Model = new()
    {
        Vent = new Exchange { IntervalTicks = 250, Rate = 14 },
        DoorOpen = new Exchange { IntervalTicks = 34, Rate = 1 },
        DoorClosed = new Exchange { IntervalTicks = 375, Rate = 1 },
        Door = new DoorTiming { OpenTicks = 45, CloseDelayTicks = 110, MoveTicks = 26 },
        Heater = new ClimatePlant { HeatPerSecond = 21, PowerW = 175 },
        Cooler = new ClimatePlant { HeatPerSecond = 21, PowerW = 200 },
        Build = new ClimateBuildValue { Vent = 30, Door = 25, Wall = 5, Heater = 50, Cooler = 90 },
        StepTicks = 250,
        TicksPerSecond = 60,
        SwingC = 7,
        WallFactor = WallFactor,
        ThinRoofRate = ThinRoofRate,
        Living = new Band(16, 26),
        HeldC = 21,
    };

    private static readonly double[] IceSheet = [.. Enumerable.Repeat(-30.0, 12)];
    private static readonly Band Living = new(16, 26);

    private static ClimateRoom Room(int cells, int walls, ShellLayers shell = ShellLayers.Lone) =>
        new() { Cells = cells, Band = Living, OutdoorWalls = walls, Shell = shell };

    [Fact]
    public void AClosedRoomWithNoOpeningsCostsHeatingOnAColdCurveThroughItsWallsAndRoof()
    {
        var sealedRoom = ClimateRules.ClimateLoad(Model, IceSheet, Room(9, 11), 0);
        Assert.True(sealedRoom.HeatingW > 0);
        Assert.Equal(0, sealedRoom.CoolingW);

        // The leak is the conductance times the band's floor over the curve: 11 walls and 9 roof cells against 46 C
        // (16 against -30) in cell-degrees a tick, as heater watts at 21 heat per second for 175 W.
        var conductance = (WallFactor * 11) + (ThinRoofRate * 9);
        var expected = conductance * 46 * 60 / 21 * 175;
        Assert.InRange(sealedRoom.HeatingW, expected * 0.98, expected * 1.02);

        // No walls and no roof: nothing to hold, as before the leakage terms.
        var airless = ClimateRules.ClimateLoad(Model, IceSheet, Room(9, 0), 0);
        Assert.True(airless.HeatingW > 0, "the roof alone still leaks");
        var roofOnly = (ThinRoofRate * 9) * 46 * 60 / 21 * 175;
        Assert.InRange(airless.HeatingW, roofOnly * 0.98, roofOnly * 1.02);
    }

    [Fact]
    public void ADoubleShellHalvesTheWallTermAndNothingElse()
    {
        var single = ClimateRules.ClimateLoad(Model, IceSheet, Room(9, 12), 0);
        var half = ClimateRules.ClimateLoad(Model, IceSheet, Room(9, 6), 0);
        var doubled = ClimateRules.ClimateLoad(Model, IceSheet, Room(9, 12, ShellLayers.Doubled), 0);
        Assert.Equal(half.HeatingW, doubled.HeatingW, 9);
        Assert.Equal(half.HeatingPeakW, doubled.HeatingPeakW, 9);
        Assert.True(doubled.HeatingW < single.HeatingW);

        // The saving is exactly half the wall term: six walls' worth of the leak.
        var saved = single.HeatingW - doubled.HeatingW;
        var sixWalls = WallFactor * 6 * 46 * 60 / 21 * 175;
        Assert.InRange(saved, sixWalls * 0.98, sixWalls * 1.02);
    }

    [Fact]
    public void ALeakierRoomSizesMoreHeaters()
    {
        var weights = SiteRules.DefaultSiteTypeWeights();
        var snug = ClimateRules.ClimateLoad(Model, IceSheet, Room(64, 0), 0);
        var leaky = ClimateRules.ClimateLoad(Model, IceSheet, Room(64, 32), 0); // 229 W against 85: two heaters against one
        Assert.True(leaky.HeatingPeakW > snug.HeatingPeakW);
        var snugPlants = Model.PlantsW(weights, 1.9, snug);
        var leakyPlants = Model.PlantsW(weights, 1.9, leaky);
        Assert.True(leakyPlants > snugPlants, $"snug {snugPlants} leaky {leakyPlants}");
    }

    [Fact]
    public void TheOpeningsStillPriceOnTopOfTheLeak()
    {
        var options = ClimateRules.ClimateOptions(Model, SiteRules.DefaultSiteTypeWeights(), 1.9, IceSheet, Room(9, 11), new ClimateSide { Outdoors = true }, 20, [Opening.Door, Opening.Airlock]);
        var sealedRoom = ClimateRules.ClimateLoad(Model, IceSheet, Room(9, 11), 0);
        Assert.All(options, o => Assert.True(o.Watts.HeatingW > sealedRoom.HeatingW));
        Assert.True(options[1].Watts.HeatingW < options[0].Watts.HeatingW, "the airlock leaks less than the door");
    }

    [Fact]
    public void AModelMissingEitherLeakRateIsUnknown()
    {
        Assert.True(Model.Valid());
        Assert.False((Model with { WallFactor = 0 }).Valid());
        Assert.False((Model with { ThinRoofRate = 0 }).Valid());
        Assert.False((Model with { WallFactor = double.NaN }).Valid());
    }

    [Theory]
    [InlineData(9, 12)]
    [InlineData(10, 14)]
    [InlineData(12, 14)]
    [InlineData(16, 16)]
    [InlineData(1, 4)]
    [InlineData(0, 0)]
    public void TheSquarestPerimeterIsTheWallCountOfARoomKnownByArea(int area, int perimeter) =>
        Assert.Equal(perimeter, ClimateRules.SquarestPerimeter(area));

    [Fact]
    public void ThePlanCountsARoomsOutdoorFacingWallsAsTheGameSamplesThem()
    {
        // A 3x3 room at (0..2, 0..2) with its door south of the middle cell: three walls a side less the door, corners
        // never; each counted by the cell two out that the game samples.
        var room = new PlannedRoom { Id = 1, Role = PlannedRole.Bedroom, Region = RegionRules.RectRegion(new Rectangle(0, 0, 3, 3)), Door = new Cell(1, -1), DoorSide = new DoorSide.Doored(Rotation.South) };
        var plan = new LayoutPlan { NextRoomId = 2, Rooms = [room] };
        var walls = plan.OutdoorWallCells(room);
        Assert.Equal(11, walls.Count);
        Assert.DoesNotContain(new Cell(1, -2), walls); // beyond the door
        Assert.DoesNotContain(walls, w => w.X < 0 && w.Z < 0); // no corner is sampled
        Assert.Contains(new Cell(4, 1), walls);
        Assert.Contains(new Cell(0, -2), walls);

        // A neighbour whose floor starts two out shares the east wall: those three walls face a room, not the outdoors.
        var neighbour = new PlannedRoom { Id = 2, Role = PlannedRole.Bedroom, Region = RegionRules.RectRegion(new Rectangle(4, 0, 3, 3)), Door = new Cell(5, -1), DoorSide = new DoorSide.Doored(Rotation.South) };
        var shared = plan with { NextRoomId = 3, Rooms = [room, neighbour] };
        Assert.Equal(8, shared.OutdoorWallCells(room).Count);
        Assert.DoesNotContain(shared.OutdoorWallCells(room), w => w.X == 4);
        Assert.Equal(8, shared.OutdoorWallCells(neighbour).Count);

        // A doorless room has no door to exclude; an unfenced room has no walls of its own.
        var doorless = room with { DoorSide = new DoorSide.Doorless() };
        Assert.Equal(12, (plan with { Rooms = [doorless] }).OutdoorWallCells(doorless).Count);
    }

    /// <summary>
    /// The outage margin (#4062): a held room falls from HeldC toward the coldest outdoor step at its conductance over
    /// its cells; a second wall layer lengthens the margin, an opening to the outdoors shortens it, a held neighbour changes
    /// nothing, and a map never colder than the floor has no end to it.
    /// </summary>
    [Fact]
    public void AHeldRoomsOutageMarginFollowsItsLeakAndTheColdestOutdoorStep()
    {
        var lone = ClimateRules.OutageMarginTicks(Model, IceSheet, Room(9, 11), 0);

        // 9 cells over 11 walls and 9 roof cells, from 21 to 16 against the coldest step (-30 less the 7 C swing).
        var conductance = (WallFactor * 11) + (ThinRoofRate * 9);
        var expected = 9 / conductance * Math.Log((21 - -37.0) / (16 - -37.0));
        Assert.InRange(lone, expected * 0.999, expected * 1.001);

        var doubled = ClimateRules.OutageMarginTicks(Model, IceSheet, Room(9, 11, ShellLayers.Doubled), 0);
        Assert.True(doubled > lone, "a second wall layer lengthens the margin");

        ClimateLink Link(Opening opening, ClimateSide side) => new() { Opening = opening, Side = side };
        var open = Room(9, 11) with { Links = [Link(Opening.DoorOpen, new ClimateSide { Outdoors = true })] };
        Assert.True(ClimateRules.OutageMarginTicks(Model, IceSheet, open, 0) < lone, "an open door to the outdoors shortens the margin");

        var neighbour = Room(9, 11) with { Links = [Link(Opening.DoorClosed, new ClimateSide { HeldC = 21, Cells = 20 })] };
        Assert.Equal(lone, ClimateRules.OutageMarginTicks(Model, IceSheet, neighbour, 0));

        double[] mild = [.. Enumerable.Repeat(25.0, 12)];
        Assert.Equal(double.PositiveInfinity, ClimateRules.OutageMarginTicks(Model, mild, Room(9, 11), 0));
        Assert.Throws<ClimateException>(() => ClimateRules.OutageMarginTicks(Model with { HeldC = double.NaN }, IceSheet, Room(9, 11), 0));
    }
}
