using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using Obs = RimGovernor.Brain.Observation;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The clustering prices the bed merge (#3658): the free-bed pool takes the barracks size whose saved rooms (walls, door
/// and heat over the horizon) beat the sleepers' losses, cheapest first; an unknown heat on a cold map keeps the beds
/// apart with a finding, a pawn near its break keeps its bedroom, the plan's barracks holds within the tie resolution, and
/// a planned barracks credits its sleepers.
/// </summary>
public sealed class BedMergePriceTests
{
    private const string Bed = "Bed";
    private const string Pool = "Bed/sleep";

    // Twelfths' outdoor means (C): a temperate map and an ice sheet.
    internal static readonly double[] Temperate = [6, 8, 11, 14, 17, 20, 22, 21, 18, 14, 10, 7];
    internal static readonly double[] IceSheet = [-40, -38, -34, -30, -26, -24, -23, -25, -28, -32, -36, -39];

    /// <summary>A free bed as the game scores it (a lone one a bedroom, several a barracks) and a climate model in the game's numbers.</summary>
    internal static readonly StationCatalog Catalog = new()
    {
        Stations = new Dictionary<string, Station>
        {
            [Bed] = new()
            {
                Shape = new InteriorPieceDef(Bed, new Cell(1, 2), RoomRole.Bedroom, null),
                EmptyCountsForBarracks = true,
                Capacity = new StationCapacity { Kind = CapacityKind.Sleepers, Slots = 1 },
                Contributions =
                [
                    new() { Role = RoomRole.Bedroom, When = RoleCondition.BedFree, Weight = 100000 },
                    new() { Role = RoomRole.Barracks, When = RoleCondition.BedFree, Weight = 100100, Additive = true },
                    new() { Role = RoomRole.Hospital, When = RoleCondition.BedMedical, Weight = 100000 },
                    new() { Role = RoomRole.PrisonCell, When = RoleCondition.BedPrisoner, Weight = 100000 },
                ],
            },
        },
        Climate = new ClimateModel
        {
            Vent = new Exchange { IntervalTicks = 250, Rate = 14 },
            DoorOpen = new Exchange { IntervalTicks = 34, Rate = 1 },
            DoorClosed = new Exchange { IntervalTicks = 375, Rate = 1 },
            Door = new DoorTiming { OpenTicks = 45, CloseDelayTicks = 110, MoveTicks = 26 },
            Heater = new ClimatePlant { HeatPerSecond = 21, PowerW = 175 },
            Cooler = new ClimatePlant { HeatPerSecond = 21, PowerW = 200 },
            StepTicks = 250,
            TicksPerSecond = 60,
            SwingC = 7,
            Living = new Band(16, 26),
            HeldC = 21,
        },
    };

    // Three colonists and the spare bed. A calm colonist loses 0.004 capability-days a day in a barracks (WellbeingWeight
    // 0.1 times four points), 0.24 over the 60-day horizon.
    private static ClusterInput Input(int beds, BedMergePrice? price) => new()
    {
        Graph = new StationGraph { Nodes = [new StationNode { Id = Pool, Def = Bed, Purpose = StationPurpose.Sleep, Count = beds }] },
        Catalog = Catalog,
        RoofDistance = 6,
        BedMerge = price,
    };

    internal static readonly BedMergeFacts Calm = new() { Losses = [0.004, 0.004, 0.004] };

    // Wood walls at the game's numbers: a wall cell is 135 x 0.7 work and 5 logs, a door 850 x 0.7 work and 25 logs; the
    // work alone is 0.0016 and 0.0099 capability-days.

    /// <summary>Trees in reach: logs at chop labor (about 0.0005 a log) price a wall cell at 0.0042 and a door at 0.023.</summary>
    internal static BedMergeFacts Wood(BedMergeFacts f) => f with { WallCell = Known(0.0042), Door = Known(0.023) };

    /// <summary>No tree in reach (the lab map is bare soil): logs at the market fallback (about 0.0055 a log) price a wall cell at 0.029 and a door at 0.149.</summary>
    internal static BedMergeFacts MarketWood(BedMergeFacts f) => f with { WallCell = Known(0.029), Door = Known(0.149) };

    private static BedMergePrice Price(BedMergeFacts facts, double[]? curve, bool harsh) => new()
    {
        Facts = facts,
        OutdoorCurve = curve is null ? default : Known(EquatableArray.From(curve)),
        Harsh = harsh,
        WorkDayW = 8000, // a colonist's work-day at the game's rice (the key test's facts give the same)
    };

    private static List<int> Rooms(Clustering c) => [.. c.Groups.Where(g => g.Stations.Any(s => s.Node == Pool)).Select(g => g.Stations.Single(s => s.Node == Pool).Count).OrderDescending()];

    [Fact]
    public void ATemperateMapsCheapWoodKeepsThreeColonistsBedsApart()
    {
        var c = LayoutClusterRules.ClusterStations(Input(4, Price(Wood(Calm), Temperate, harsh: false)));

        Assert.Equal([1, 1, 1, 1], Rooms(c));
        Assert.All(c.Groups, g => Assert.Equal(RoomRole.Bedroom, g.Role));
        var d = Assert.Single(c.Decisions, d => d.A == Pool);
        Assert.Equal((LayoutClusterRules.ClusterSplit, LayoutClusterRules.ReasonClusterBedMerge), (d.Verdict, d.Reason));
    }

    [Fact]
    public void ATemperateMapsMarketWoodMergesCalmColonistsIntoOneBarracks()
    {
        var c = LayoutClusterRules.ClusterStations(Input(4, Price(MarketWood(Calm), Temperate, harsh: false)));

        var g = Assert.Single(c.Groups);
        Assert.Equal((RoomRole.Barracks, 4), (g.Role, g.Stations.Single().Count));
    }

    [Fact]
    public void AnIceSheetsPricedHeatMergesEveryBedIntoOneBarracks()
    {
        var c = LayoutClusterRules.ClusterStations(Input(4, Price(Wood(Calm), IceSheet, harsh: true)));

        var g = Assert.Single(c.Groups);
        Assert.Equal((RoomRole.Barracks, PlannedRole.Barracks, 4), (g.Role, g.Planned, g.Stations.Single().Count));
        Assert.Equal((PlannedRole.Barracks, true), LayoutTileRules.PlannedRoleOfGroup(g));
        var d = Assert.Single(c.Decisions, d => d.A == Pool);
        Assert.Equal((LayoutClusterRules.ClusterMerge, LayoutClusterRules.ReasonClusterBedMerge), (d.Verdict, d.Reason));
    }

    [Fact]
    public void AnUnknownHeatOnAColdMapKeepsTheBedsApartAndSaysSo()
    {
        var c = LayoutClusterRules.ClusterStations(Input(4, Price(Wood(Calm), null, harsh: true)));

        Assert.Equal([1, 1, 1, 1], Rooms(c));
        var d = Assert.Single(c.Decisions, d => d.A == Pool);
        Assert.Equal((LayoutClusterRules.ClusterSplit, "cluster_bed_merge_unpriced:thermal"), (d.Verdict, d.Reason));

        // On a mild map the unknown heat is zero: the walls alone decide.
        var mild = LayoutClusterRules.BarracksSizeOf(Input(4, Price(Wood(Calm), null, harsh: false)));
        Assert.Equal(new BarracksChoice(1, ""), mild);
    }

    // a and b are calm; c sits under its break threshold, which a bedroom's +2 lifts it clear of and a barracks' -2 does not.
    private static NeedThoughtStage Stage(string thought, string role, double offset) => new(thought, role, 0, offset, Known((double)GameTime.TicksPerDay), false);

    private static NeedsForecastPawn Pawn(string id, double target) => new()
    {
        Pawn = id, MoodUnclampedTarget = Known(target), BreakThreshold = Known(0.35),
        ThoughtStages = [Stage("SleptInBedroom", "slept_in_bedroom", 2), Stage("SleptInBarracks", "slept_in_barracks", -2)],
    };

    private static BedroomGainFacts Gains(params NeedsForecastPawn[] pawns) => new()
    {
        Pawns = pawns.ToDictionary(p => p.Pawn, StringComparer.Ordinal),
        Breaks = new() { Threshold = 0.35, Tiers = [new(1, 2), new(1, 3), new(1, 5)] },
        WellbeingWeight = 0.1,
    };

    [Fact]
    public void APawnNearItsBreakKeepsItsBedroomWhileTheOthersMerge()
    {
        var facts = Gains(Pawn("a", 0.8), Pawn("b", 0.8), Pawn("c", 0.34)).BedMerge();
        Assert.Equal(0, facts.Unpriced);
        Assert.Equal(0.004, facts.Losses[0], 6);
        Assert.Equal(0.004, facts.Losses[1], 6);
        Assert.True(facts.Losses[2] > 0.05, $"c loses {facts.Losses[2]} a day");

        // Dearer walls on a mild map with the heat unread: two bedrooms saved outweigh a and b's mood, not c's.
        var c = LayoutClusterRules.ClusterStations(Input(4, Price(facts with { WallCell = Known(0.08), Door = Known(0.12) }, null, harsh: false)));

        Assert.Equal([3, 1], Rooms(c));
        Assert.Equal([RoomRole.Barracks, RoomRole.Bedroom], c.Groups.OrderBy(g => g.Stations.Single().Count == 1).Select(g => g.Role));
    }

    [Fact]
    public void ThePlansBarracksHoldsUnlessTheNewSizeBeatsItByMoreThanTheTieResolution()
    {
        // Walls alone: one saved bedroom is worth 1.0. Three beds (the spare, a and b) are worth 2 - 0.48 = 1.52; with c
        // the fourth is worth 3 - 0.48 - 60L.
        BedMergePrice Near(double c, int incumbent) => Price(new BedMergeFacts { Losses = [0.004, 0.004, c], WallCell = Known(0.08), Door = Known(0.12) }, null, harsh: false) with { Incumbent = incumbent };

        // Four beds stand planned and three are better by 0.005: the plan's four hold.
        Assert.Equal(4, LayoutClusterRules.BarracksSizeOf(Input(4, Near(1.005 / 60, 4)))!.Value.Size);
        // With no barracks planned, three win.
        Assert.Equal(3, LayoutClusterRules.BarracksSizeOf(Input(4, Near(1.005 / 60, 0)))!.Value.Size);
        // Better by 0.05, three replace the plan's four.
        Assert.Equal(3, LayoutClusterRules.BarracksSizeOf(Input(4, Near(1.05 / 60, 4)))!.Value.Size);
    }

    [Fact]
    public void AMergeBackPaysWhatWasSpentOnTheBedroomItGivesUp()
    {
        // Walls alone, three calm sleepers: the plan's three-bed barracks is worth 2 - 0.48 = 1.52, four beds 3 - 0.72 =
        // 2.28 less the spend on the one committed room four beds give up (#3675). The barracks stands whole.
        var barracks = new ShellPieces(17, 1);
        int Size(params ShellPieces[] bedroom) => LayoutClusterRules.BarracksSizeOf(Input(4, Price(new BedMergeFacts { Losses = [0.004, 0.004, 0.004], WallCell = Known(0.08), Door = Known(0.12) }, null, harsh: false) with
        {
            Incumbent = 3,
            Committed = EquatableArray.From(bedroom.Prepend(barracks)),
        }))!.Value.Size;

        // A bedroom on paper is not committed: it is free to drop, and the beds merge.
        Assert.Equal(4, Size());
        // A built bedroom charges its shell (eleven walls and the door, 1.0): the merge back is worth 1.28 and the split holds.
        Assert.Equal(3, Size(new ShellPieces(11, 1)));
        // A bedroom under construction charges the walls standing: three (0.24) do not hold the split, ten (0.8) do.
        Assert.Equal(4, Size(new ShellPieces(3, 0)));
        Assert.Equal(3, Size(new ShellPieces(10, 0)));
    }

    // The crop that prices a colonist's work-day: the game's rice, six units of 0.05 nutrition a cell.
    internal static readonly CropChoice Rice = new()
    {
        Name = "Rice", Available = Known(true), Edible = Known(true), GrowDays = Known(6.0), FertilityMin = Known(0.5),
        FertilitySensitivity = Known(1.0), HarvestNutrition = Known(0.3), Demand = Known(1.6), DietAllowed = Known(true),
    };

    [Fact]
    public void ThePlannedBarracksIsTheIncumbentAndTheReplanKeyCarriesTheChosenSize()
    {
        var barracks = new PlannedRoom { Id = 2, Role = PlannedRole.Barracks, Region = RegionRules.RectRegion(new Policy.Rectangle(10, 0, 5, 4)) };
        InteriorPiece Slot(int x) => new() { Slot = "bed", Def = Bed, Size = new Cell(1, 2), Rot = Rotation.South, Rect = new Policy.Rectangle(x, 2, 1, 2) };
        var s = FirstLayoutPlanTests.Survey("cold");
        var f = FirstLayoutPlanTests.Facts(s, 3) with
        {
            Catalog = FirstLayoutPlanTests.Facts(s, 3).Catalog with { Stations = new Dictionary<string, Station>(FirstLayoutPlanTests.Facts(s, 3).Catalog.Stations) { [Bed] = Catalog.Stations[Bed] }, Climate = Catalog.Climate },
            Plan = new LayoutPlan { Rooms = [barracks], NextRoomId = 3, Pieces = new Dictionary<int, IReadOnlyList<InteriorPiece>> { [2] = [Slot(10), Slot(12), Slot(14)] } },
            OutdoorCurve = Known(EquatableArray.From(IceSheet)),
            BedMerge = Wood(Calm),
            Crops = [Rice],
        };
        Assert.Equal(3, f.BedMergePriceOf().Incumbent);
        Assert.True(f.BedMergePriceOf().Harsh);

        string Key(StationFacts facts) => facts.InputsKeyParts().Single(p => p.Key == "barracks").Value;
        Assert.Equal("4;", Key(f));
        Assert.Equal("1;", Key(f with { OutdoorCurve = default }));
    }

    [Fact]
    public void OnlyTheRoomsStandingOrUnderConstructionAreCommittedWithWhatOfThemStands()
    {
        PlannedRoom Room(int id, PlannedRole role, int x) => new() { Id = id, Role = role, Region = RegionRules.RectRegion(new Policy.Rectangle(x, 0, 3, 3)) };
        var plan = new LayoutPlan { Rooms = [Room(2, PlannedRole.Barracks, 10), Room(3, PlannedRole.Bedroom, 20), Room(4, PlannedRole.Bedroom, 30)], NextRoomId = 5 };
        // The barracks stands whole; bedroom 3 is on paper; bedroom 4 is being built (pinned by its journal plan) with two walls up.
        var ground = new GroundCensus();
        var barracksRing = plan.Rooms[0].WallRing().Cells().Except(plan.ShellDoors(plan.Rooms[0])).ToList();
        ground.Walls.UnionWith(barracksRing);
        ground.Walls.UnionWith(plan.Rooms[2].WallRing().Cells().Except(plan.ShellDoors(plan.Rooms[2])).Take(2));
        var f = FirstLayoutPlanTests.Facts(FirstLayoutPlanTests.Survey("cold"), 3) with
        {
            Plan = plan,
            Built = new HashSet<int> { 2 },
            Pinned = new HashSet<int> { 4 },
            ShellStanding = LayoutPlan.ShellStanding(plan, ground),
        };

        Assert.Equal([new ShellPieces(barracksRing.Count, 0), new ShellPieces(2, 0)], f.BedMergePriceOf().Committed);
    }

    [Fact]
    public void APlannedBarracksCreditsEveryOccupantAtTheBarracksThought()
    {
        // A non-ascetic gains slept_in_barracks over the bed thought it has; an ascetic gains nothing and loses its Ascetic thought.
        var stages = EquatableArray.From(new[] { Stage("SleptInBedroom", "slept_in_bedroom", 2), Stage("SleptInBarracks", "slept_in_barracks", -2), new NeedThoughtStage("Ascetic", "ascetic", 0, 3, Known((double)GameTime.TicksPerDay), false) });
        var plain = new NeedsForecastPawn { Pawn = "a", ThoughtStages = stages };
        var ascetic = plain with { Pawn = "b", Ascetic = true };
        Assert.Equal(Known(-2.0), ForecastHousing.BedroomMoodDelta(plain, Known(0), default, barracks: true));
        Assert.Equal(Known(2.0), ForecastHousing.BedroomMoodDelta(plain, Known(0), default));
        var observed = Known(EquatableArray.From(new[] { new MoodThought("Ascetic", 3) }));
        Assert.Equal(Known(-3.0), ForecastHousing.BedroomMoodDelta(ascetic, Known(0), observed, barracks: true));
        Assert.Contains(PlannedRole.Barracks, ForecastPlannedSite.PricedRoomRoles);
    }

    // The game's wall and door: 135 and 850 work, five and 25 stuff; wood builds them at 0.7 work, granite blocks at 2.
    private static Obs.StuffOption Option(Resource stuff, double value, double work, long count) => new()
    {
        Stuff = stuff.Value, Value = value, Common = true, Costs = [new Amount(stuff, count)],
        Stats = new Dictionary<string, double> { [StatDefNames.StatWorkToBuild] = work },
    };

    [Fact]
    public void TheShellIsPricedInTheStuffAPlannedRoomsBaseRingIsReadIn()
    {
        var wood = new Resource("WoodLog");
        var granite = new Resource("BlocksGranite");
        Obs.PlanningDefinition Def(string name, double work, long count) => new()
        {
            Name = name, Available = Known(true),
            StuffOptions = [Option(granite, count * 0.9, work * 2, count), Option(wood, count * 1.2, work * 0.7, count)],
        };
        var colony = new Obs.ColonyProjection
        {
            Definitions = [Def(ContainmentCellRules.ShellWallDefinition, 135, 5), Def(ContainmentCellRules.ShellDoorDefinition, 850, 25)],
            Resources = Known<IReadOnlyDictionary<Resource, long>>(new Dictionary<Resource, long> { [wood] = 600 }),
        };

        // The blocks are cheaper but the colony holds none: the planned ring is read in wood, and the merge prices wood.
        var (wall, door) = Rounder.BedMergeShell(colony);
        Assert.Equal((135 * 0.7, wood, 5L), (wall!.Value.Work, wall.Value.Costs.Single().Resource, wall.Value.Costs.Single().Count));
        Assert.Equal((850 * 0.7, wood, 25L), (door!.Value.Work, door.Value.Costs.Single().Resource, door.Value.Costs.Single().Count));

        // With blocks in stock too, the cheaper blocks are the base ring's stuff.
        var stocked = colony with { Resources = Known<IReadOnlyDictionary<Resource, long>>(new Dictionary<Resource, long> { [wood] = 600, [granite] = 300 }) };
        Assert.Equal(granite, Rounder.BedMergeShell(stocked).Wall!.Value.Costs.Single().Resource);
    }

    [Fact]
    public void AShellPieceCostsItsWorkOverADayAndItsMaterialAtItsValue()
    {
        var wood = new Resource("WoodLog");
        var values = new Dictionary<Resource, ConstructionMaterialValue> { [wood] = new(MaterialValueBasis.Unpriced, 0.001) };
        IReadOnlyList<Amount> costs = [new Amount(wood, 5)];

        Assert.True(LayoutClusterRules.ShellPiecePrice((GameTime.TicksPerDay / 2.0, costs), values).TryGetValue(out var price));
        Assert.Equal(0.5 + 0.005, price, 9);
        Assert.Equal(default, LayoutClusterRules.ShellPiecePrice(null, values));
    }
}
