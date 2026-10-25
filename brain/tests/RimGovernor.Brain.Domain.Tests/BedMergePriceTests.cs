using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using Obs = RimGovernor.Brain.Observation;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The clustering prices the bed merge (#3658, #3748): the free-bed pool takes the barracks size and room areas whose
/// sleepers' gains from each ring's closure, less the rooms' walls, door, floor and heat, are largest; an unread price fact fails the chain naming it
/// (#3747), the shell is priced in the stuff the ring is placed in, a pawn near its break keeps its bedroom, the plan's barracks holds within the tie resolution, and
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
        PlayBuildings = [],
        Stations = new Dictionary<string, Station>
        {
            [Bed] = new()
            {
                Shape = new InteriorPieceDef(Bed, new Cell(1, 2), RoomRole.Bedroom, new InteractionCell.None()),
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

    // Three colonists and a fourth bed the pool holds past them (as after a death), which loses nothing. A calm colonist
    // loses 0.004 capability-days a day in a barracks (WellbeingWeight 0.1 times four points), 0.24 over the 60-day horizon.
    private static ClusterInput Input(int beds, BedMergePrice? price) => new()
    {
        Graph = new StationGraph { Nodes = [new StationNode { Id = Pool, Def = Bed, Purpose = StationPurpose.Sleep, Count = beds }] },
        Catalog = Catalog,
        RoofDistance = 6,
        BedMerge = price,
    };

    internal static readonly BedMergeFacts.Read Calm = CalmOf(3);

    /// <summary>The losses of <paramref name="colonists"/> calm colonists: the plan's sleep slots count them exactly (#3749).</summary>
    internal static BedMergeFacts.Read CalmOf(int colonists) => Flat(Enumerable.Repeat(0.004, colonists).ToArray());

    /// <summary>The game's Impressiveness stages up to slightly impressive: awful, dull 20, mediocre 30, decent 40, 50.</summary>
    internal static ImpressivenessLevels Levels => new() { Dull = 20, Mediocre = 30, Decent = 40, SlightlyImpressive = 50 };

    /// <summary>
    /// Sleepers who gain nothing in a bedroom and lose their loss a day in a barracks, at every stage, so a room's size buys
    /// no mood; with the game's stages and a wood ring's work (a wall cell 135 x 0.7 ticks, the door 850 x 0.7).
    /// </summary>
    internal static BedMergeFacts.Read Flat(params double[] losses) => Staged(losses.Select(l => (new double[Levels.StageCount], Enumerable.Repeat(-l, Levels.StageCount).ToArray())).ToArray());

    /// <summary>Sleepers with these gains a day per stage, in a bedroom and in a barracks, at the game's stages and a wood ring's work; the ring's material is free until Wood or MarketWood prices it.</summary>
    internal static BedMergeFacts.Read Staged(params (double[] Bedroom, double[] Barracks)[] sleepers) => new()
    {
        Sleepers = EquatableArray.From(sleepers.Select(s => new SleeperGains(EquatableArray.From(s.Bedroom), EquatableArray.From(s.Barracks))).ToArray()),
        Levels = Levels,
        WallCell = 0,
        Door = 0,
        WallWork = 135 * 0.7,
        DoorWork = 850 * 0.7,
    };

    /// <summary>A colonist's work-day priced at <paramref name="w"/> watts (StationWorkDay.Priced: one unit of nutrition a day per unit at the site-type weights).</summary>
    internal static StationWorkDay Day(double w) => new StationWorkDay.Priced(w * SiteRules.DefaultSiteTypeWeights().Power / 1000, 1);

    // Wood walls at the game's numbers: a wall cell is 135 x 0.7 work and 5 logs, a door 850 x 0.7 work and 25 logs; the
    // work alone is 0.0016 and 0.0099 capability-days.

    /// <summary>Trees in reach: logs at chop labor (about 0.0005 a log) price a wall cell at 0.0042 and a door at 0.023.</summary>
    internal static BedMergeFacts.Read Wood(BedMergeFacts.Read f) => f with { WallCell = 0.0042, Door = 0.023 };

    /// <summary>No tree in reach (the lab map is bare soil): logs at the market fallback (about 0.0055 a log) price a wall cell at 0.029 and a door at 0.149.</summary>
    internal static BedMergeFacts.Read MarketWood(BedMergeFacts.Read f) => f with { WallCell = 0.029, Door = 0.149 };

    private static BedMergePrice Price(BedMergeFacts facts, double[]? curve) => new()
    {
        Facts = facts,
        OutdoorCurve = curve is null ? default : Known(EquatableArray.From(curve)),
        WorkDay = Day(8000.0), // a colonist's work-day at the game's rice (the key test's facts give the same)
        Builders = Known(1),
    };

    /// <summary>The walls alone decide: a temperate room's heat priced at a work-day so dear that it costs nothing.</summary>
    private static BedMergePrice WallsAlone(BedMergeFacts facts) => Price(facts, Temperate) with { WorkDay = Day(1e15) };

    private static List<int> Rooms(Clustering c) => [.. c.Groups.Where(g => g.Stations.Any(s => s.Node == Pool)).Select(g => g.Stations.Single(s => s.Node == Pool).Count).OrderDescending()];

    [Fact]
    public void ATemperateMapsCheapWoodKeepsThreeColonistsBedsApart()
    {
        var c = LayoutClusterRules.ClusterStations(Input(4, Price(Wood(Calm), Temperate)));

        Assert.Equal([1, 1, 1, 1], Rooms(c));
        Assert.All(c.Groups, g => Assert.Equal(RoomRole.Bedroom, g.Role));
        var d = Assert.Single(c.Decisions, d => d.A == Pool);
        Assert.Equal((LayoutClusterRules.ClusterSplit, LayoutClusterRules.ReasonClusterBedMerge), (d.Verdict, d.Reason));
    }

    [Fact]
    public void ATemperateMapsMarketWoodMergesCalmColonistsIntoOneBarracks()
    {
        var c = LayoutClusterRules.ClusterStations(Input(4, Price(MarketWood(Calm), Temperate)));

        var g = Assert.Single(c.Groups);
        Assert.Equal((RoomRole.Barracks, 4), (g.Role, g.Stations.Single().Count));
    }

    [Fact]
    public void AnIceSheetsPricedHeatMergesEveryBedIntoOneBarracks()
    {
        var c = LayoutClusterRules.ClusterStations(Input(4, Price(Wood(Calm), IceSheet)));

        var g = Assert.Single(c.Groups);
        Assert.Equal((RoomRole.Barracks, PlannedRole.Barracks, 4), (g.Role, g.Planned, g.Stations.Single().Count));
        Assert.Equal((PlannedRole.Barracks, true), LayoutTileRules.PlannedRoleOfGroup(g));
        var d = Assert.Single(c.Decisions, d => d.A == Pool);
        Assert.Equal((LayoutClusterRules.ClusterMerge, LayoutClusterRules.ReasonClusterBedMerge), (d.Verdict, d.Reason));
    }

    [Fact]
    public void AnUnreadPriceFactFailsTheClusteringNamingIt()
    {
        // The heat, the work-day, the sleepers' moods and the shell are required on every map (#3747): no plan on a guess.
        RequiredFactException Fails(BedMergePrice price) => Assert.Throws<RequiredFactException>(() => LayoutClusterRules.ClusterStations(Input(4, price)));

        Assert.Equal("outdoor_curve", Fails(Price(Wood(Calm), null)).Fact);
        // A work-day with no price, whatever its reason, fails the heat it prices.
        foreach (var day in new StationWorkDay[] { new StationWorkDay.NoColonist(), new StationWorkDay.NoEater(), new StationWorkDay.NoCrop() })
        {
            Assert.Equal("work_day", Fails(Price(Wood(Calm), Temperate) with { WorkDay = day }).Fact);
        }
        Assert.Equal("builders", Fails(Price(Wood(Calm), IceSheet) with { Builders = default }).Fact);
        // Facts the colony could not read fail naming the one unread.
        foreach (var fact in new[] { "sleeper_mood", "impressiveness_levels", "wall_price", "door_price" })
        {
            Assert.Equal(fact, Fails(Price(new BedMergeFacts.Unread(fact), IceSheet)).Fact);
        }
    }

    [Fact]
    public void TheBedMergeFactsReadWholeOrNameTheFirstUnread()
    {
        var log = new Resource("WoodLog");
        var values = new Dictionary<Resource, ConstructionMaterialValue> { [log] = new(MaterialValueBasis.Unpriced, 0.001) };
        (double Work, IReadOnlyList<Amount> Costs) wall = (135 * 0.7, [new Amount(log, 5)]);
        (double Work, IReadOnlyList<Amount> Costs) door = (850 * 0.7, [new Amount(log, 25)]);
        var gains = Calm.Sleepers;

        Assert.Equal(new BedMergeFacts.Unread("sleeper_mood"), LayoutClusterRules.BedMergeFactsOf(null, Levels, wall, door, values));
        Assert.Equal(new BedMergeFacts.Unread("impressiveness_levels"), LayoutClusterRules.BedMergeFactsOf(gains, new ImpressivenessLevels(), wall, door, values));
        Assert.Equal(new BedMergeFacts.Unread("wall_price"), LayoutClusterRules.BedMergeFactsOf(gains, Levels, null, door, values));
        Assert.Equal(new BedMergeFacts.Unread("door_price"), LayoutClusterRules.BedMergeFactsOf(gains, Levels, wall, null, values));
        var read = Assert.IsType<BedMergeFacts.Read>(LayoutClusterRules.BedMergeFactsOf(gains, Levels, wall, door, values));
        Assert.Equal((wall.Work, door.Work), (read.WallWork, read.DoorWork));
        Assert.Equal(LayoutClusterRules.PiecePrice(wall, values), read.WallCell, 12);
        Assert.Equal(LayoutClusterRules.PiecePrice(door, values), read.Door, 12);
        Assert.Equal(Levels, read.Levels);

        // One sleeper's gain unread leaves every gain unread.
        Assert.Null(LayoutClusterRules.SleeperGainsOf(Gains(Pawn("a", 0.8), Pawn("b", 0.8), NeedsFixtures.Pawn() with { Pawn = "c" }), Levels));
        Assert.Equal(2, LayoutClusterRules.SleeperGainsOf(Gains(Pawn("a", 0.8), Pawn("b", 0.8)), Levels)!.Value.Count);
    }

    // a and b are calm; c sits under its break threshold, which a bedroom's +2 lifts it clear of and a barracks' -2 does not.
    private static NeedThoughtStage Stage(string thought, string role, double offset, int stage = 0) => new(thought, role, stage, offset, NeedsFixtures.Lasting((double)GameTime.TicksPerDay, false));

    private static NeedsForecastPawn Pawn(string id, double target) => NeedsFixtures.Pawn() with
    {
        Pawn = id, Mood = NeedsFixtures.Mood with { UnclampedTarget = target, BreakThreshold = 0.35 }, Sleep = SleepFixtures.Ground(),
        ThoughtStages = [.. Enumerable.Range(0, Levels.StageCount).SelectMany(s => new[] { Stage("SleptInBedroom", "slept_in_bedroom", 2, s), Stage("SleptInBarracks", "slept_in_barracks", -2, s) })],
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
        var facts = Calm with { Sleepers = LayoutClusterRules.SleeperGainsOf(Gains(Pawn("a", 0.8), Pawn("b", 0.8), Pawn("c", 0.34)), Levels)!.Value };
        Assert.All(facts.Sleepers, s => Assert.Equal(Levels.StageCount, s.Bedroom.Count));
        var losses = facts.Sleepers.Select(s => s.Bedroom[0] - s.Barracks[0]).ToList();
        Assert.Equal(0.004, losses[0], 6);
        Assert.Equal(0.004, losses[1], 6);
        Assert.True(losses[2] > 0.05, $"c loses {losses[2]} a day");

        // Dearer walls alone: two bedrooms saved outweigh a and b's mood, not c's.
        var c = LayoutClusterRules.ClusterStations(Input(4, WallsAlone(facts with { WallCell = 0.08, Door = 0.12 })));

        Assert.Equal([3, 1], Rooms(c));
        Assert.Equal([RoomRole.Barracks, RoomRole.Bedroom], c.Groups.OrderBy(g => g.Stations.Single().Count == 1).Select(g => g.Role));
    }

    [Fact]
    public void ThreeCalmSleepersAndOneBuilderAtTheLabsWoodPriceShareOneRoomyBarracks()
    {
        // No tree in reach: logs at the market price the ring, and one builder raises the rooms in turn (#3748). Three
        // bedrooms' walls, doors and floor cost more than the barracks loses in mood, so the three share one room,
        // sized past the bare four cells.
        var choice = LayoutClusterRules.BedMergeChoiceOf(Input(3, Price(MarketWood(Calm), Temperate)))!;
        Assert.Equal(3, choice.Size);
        Assert.True(choice.Area > 4, $"the barracks' interior is {choice.Area} cells");

        var g = Assert.Single(LayoutClusterRules.ClusterStations(Input(3, Price(MarketWood(Calm), Temperate))).Groups);
        Assert.Equal((RoomRole.Barracks, 3), (g.Role, g.Stations.Single().Count));
        Assert.Equal(choice.Area, g.Area);
    }

    [Fact]
    public void ThreeBuildersAndAHighLossKeepEverySleeperInABedroom()
    {
        // The same map with three builders, each sleeper losing 0.05 a day in a barracks: the bedrooms pay.
        var c = LayoutClusterRules.ClusterStations(Input(3, Price(MarketWood(Flat(0.05, 0.05, 0.05)), Temperate) with { Builders = Known(3) }));

        Assert.Equal([1, 1, 1], Rooms(c));
        Assert.All(c.Groups, g => Assert.Equal(RoomRole.Bedroom, g.Role));
    }

    [Fact]
    public void ABedroomGrowsWhenItsWallsCheapenAndShrinksWhenTheyDearen()
    {
        // One sleeper gains 0.01 a day more in a dull bedroom than an awful one: the bare room turns dull at 21 cells
        // (RoomSpaceRules), which takes more wall cells. The chooser holds no size: the wall price alone moves the area.
        var gains = Staged(([0, 0.01, 0.01, 0.01, 0.01], [0, 0, 0, 0, 0]));
        BedMergeChoice At(double wall) => LayoutClusterRules.BedMergeChoiceOf(Input(1, WallsAlone(gains with { WallCell = wall, Door = 0.023 })))!;

        var cheap = At(0.02);
        var dear = At(0.1);
        Assert.True(cheap.Area > dear.Area, $"{cheap.Area} cells at cheap walls, {dear.Area} at dear");
        Assert.True(RoomSpaceRules.BareImpressiveness(cheap.Area) >= Levels.Dull);
        Assert.True(RoomSpaceRules.BareImpressiveness(dear.Area) < Levels.Dull);

        // The cluster's room takes the chosen area.
        var g = Assert.Single(LayoutClusterRules.ClusterStations(Input(1, WallsAlone(gains with { WallCell = 0.02, Door = 0.023 }))).Groups);
        Assert.Equal(cheap.Area, g.Area);
    }
    [Fact]
    public void ThePlansBarracksHoldsUnlessTheNewSizeBeatsItByMoreThanTheTieResolution()
    {
        // Walls alone and dear: the spare, a and b share a barracks, and c's loss decides whether it joins them (four beds)
        // or takes a bedroom (three). Four beds' value falls about 60 a day of c's loss against three's.
        int Size(double c, int incumbent) => LayoutClusterRules.BedMergeChoiceOf(Input(4, WallsAlone(Flat(0.004, 0.004, c) with { WallCell = 0.08, Door = 0.12 }) with { Incumbent = incumbent }))!.Size;

        // With four beds planned, the loss at which three replace them: the line where three beat four by the tie resolution.
        var (held, switched) = (0.004, 0.2);
        Assert.Equal((4, 3), (Size(held, 4), Size(switched, 4)));
        for (var i = 0; i < 60; i++)
        {
            var mid = (held + switched) / 2;
            (held, switched) = Size(mid, 4) == 4 ? (mid, switched) : (held, mid);
        }

        // Just short of the line three are the better by nearly the tie resolution, yet the plan's four hold; a plan of
        // three holds its three.
        Assert.Equal(4, Size(held, 4));
        Assert.Equal(3, Size(held, 3));
        // A loss of 0.015 less over the horizon, four are the better by about 0.005: a plan of three holds; 0.025 less,
        // four beat it by more than the tie resolution and replace it.
        Assert.Equal(3, Size(held - (0.015 / 60), 3));
        Assert.Equal(4, Size(held - (0.025 / 60), 3));
        // Better by 0.05 more, three replace the plan's four.
        Assert.Equal(3, Size(switched + (0.05 / 60), 4));
    }

    [Fact]
    public void AMergeBackPaysWhatWasSpentOnTheBedroomItGivesUp()
    {
        // Walls alone, three calm sleepers: the plan's three-bed barracks is worth 2 - 0.48 = 1.52, four beds 3 - 0.72 =
        // 2.28 less the spend on the one committed room four beds give up (#3675). The barracks stands whole.
        var barracks = new ShellPieces(17, 1);
        int Size(params ShellPieces[] bedroom) => LayoutClusterRules.BedMergeChoiceOf(Input(4, WallsAlone(Calm with { WallCell = 0.08, Door = 0.12 }) with
        {
            Incumbent = 3,
            Committed = EquatableArray.From(bedroom.Prepend(barracks)),
        }))!.Size;

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
        // Six colonists, six sleep slots (#3749), and a three-bed barracks planned: the ice sheet's heat grows it to six.
        var f = FirstLayoutPlanTests.Facts(s, 6) with
        {
            Catalog = FirstLayoutPlanTests.Facts(s, 6).Catalog with { Stations = new Dictionary<string, Station>(FirstLayoutPlanTests.Facts(s, 6).Catalog.Stations) { [Bed] = Catalog.Stations[Bed] }, Climate = Catalog.Climate },
            Plan = new LayoutPlan { Rooms = [barracks], NextRoomId = 3, Pieces = new Dictionary<int, IReadOnlyList<InteriorPiece>> { [2] = [Slot(10), Slot(12), Slot(14)] } },
            OutdoorCurve = Known(EquatableArray.From(IceSheet)),
            BedMerge = Wood(CalmOf(6)),
            Crops = [Rice],
        };
        Assert.Equal(3, f.BedMergePriceOf().Incumbent);
        Assert.IsType<StationWorkDay.Priced>(f.BedMergePriceOf().WorkDay);

        string Key(StationFacts facts) => facts.InputsKeyParts().Single(p => p.Key == "barracks").Value;
        // The key carries the size and both areas.
        Assert.StartsWith("6;", Key(f));
        Assert.Equal(3, Key(f).TrimEnd(';').Split(';').Length);
        // The outdoor curve unread on this cold map (#3747): no size is keyed, and the chain fails naming the curve, so
        // the replan is skipped with the required_fact row and the next hourly check tries again.
        var unread = f with { OutdoorCurve = default };
        Assert.NotEqual(Key(f), Key(unread));
        Assert.Equal("outdoor_curve", Assert.Throws<RequiredFactException>(() => LayoutStationPlanRules.AdviseStations(unread)).Fact);
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
        var stages = EquatableArray.From(new[] { Stage("SleptInBedroom", "slept_in_bedroom", 2), Stage("SleptInBarracks", "slept_in_barracks", -2), new NeedThoughtStage("Ascetic", "ascetic", 0, 3, NeedsFixtures.Lasting((double)GameTime.TicksPerDay, false)) });
        var plain = NeedsFixtures.Pawn() with { Pawn = "a", ThoughtStages = stages, Sleep = SleepFixtures.Ground() };
        var ascetic = plain with { Pawn = "b", Ascetic = true };
        var none = new Dictionary<string, EquatableArray<MoodThought>>();
        Assert.Equal(Known(-2.0), ForecastHousing.BedroomMoodDelta(plain, Known(0), none, barracks: true));
        Assert.Equal(Known(2.0), ForecastHousing.BedroomMoodDelta(plain, Known(0), none));
        var observed = new Dictionary<string, EquatableArray<MoodThought>> { ["b"] = EquatableArray.From(new[] { new MoodThought("Ascetic", 3) }) };
        Assert.Equal(Known(-3.0), ForecastHousing.BedroomMoodDelta(ascetic, Known(0), observed, barracks: true));
        Assert.True(PlannedRoomCredits.Shipped.ContainsKey(PlannedRole.Barracks));
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
        var colony = ColonyFactsCoreTests.Projection() with
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
    public void WithNothingStockedTheShellIsPricedInTheMapsWoodElseItsRockBlocks()
    {
        var wood = new Resource("WoodLog");
        var granite = new Resource("BlocksGranite");
        Obs.PlanningDefinition Def(string name, double work, long count) => new()
        {
            Name = name, Available = Known(true),
            StuffOptions = [Option(granite, count * 0.9, work * 2, count), Option(wood, count * 1.2, work * 0.7, count)],
        };
        Obs.ColonyProjection Colony(RingStuffSources map, params Resource[] stocked) => ColonyFactsCoreTests.Projection() with
        {
            Definitions = [Def(ContainmentCellRules.ShellWallDefinition, 135, 5), Def(ContainmentCellRules.ShellDoorDefinition, 850, 25)],
            Resources = Known<IReadOnlyDictionary<Resource, long>>(stocked.ToDictionary(r => r, _ => 300L)),
            RingSources = map,
        };
        Resource WallStuff(Obs.ColonyProjection colony) => Rounder.BedMergeShell(colony).Wall!.Value.Costs.Single().Resource;
        var trees = new RingStuffSources { Wood = EquatableArray.From([wood.Value]), Blocks = EquatableArray.From([granite.Value]) };
        var rock = new RingStuffSources { Blocks = EquatableArray.From([granite.Value]) };

        // Nothing stocked and trees standing: the ring is priced (and placed) in wood, though the blocks are cheaper.
        Assert.Equal(wood, WallStuff(Colony(trees)));
        var (wall, door) = Rounder.BedMergeShell(Colony(trees));
        var values = new Dictionary<Resource, ConstructionMaterialValue> { [wood] = new(MaterialValueBasis.Unpriced, 0.001) };
        Assert.IsType<BedMergeFacts.Read>(LayoutClusterRules.BedMergeFactsOf(Calm.Sleepers, Levels, wall, door, values));
        Assert.Equal(wood.Value, RoundsBuildingPlanner.ShellMaterials(Colony(trees), ContainmentCellRules.ShellWallDefinition, ContainmentCellRules.ShellDoorDefinition, null).WallStuff);
        // No trees, granite rock: the granite blocks.
        Assert.Equal(granite, WallStuff(Colony(rock)));
        // A stocked stuff wins over what the map offers.
        Assert.Equal(wood, WallStuff(Colony(rock, wood)));
        // Neither stocked nor offered: no stuff, so no price and no placement.
        Assert.Equal((null, null), Rounder.BedMergeShell(Colony(new RingStuffSources())));
    }

    [Fact]
    public void AStartedPlannedRoomIsPlacedInTheStuffItsRingIsPricedIn()
    {
        // Only a little wood stocked and no trees in reach (#3677): a started ring the record selected nothing of is placed in wood, not in the blocks the colony lacks.
        var wood = new Resource("WoodLog");
        var granite = new Resource("BlocksGranite");
        Obs.PlanningDefinition Def(string name, double work, long count) => new()
        {
            Name = name, Available = Known(true),
            StuffOptions = [Option(granite, count * 0.9, work * 2, count), Option(wood, count * 1.2, work * 0.7, count)],
        };
        var colony = ColonyFactsCoreTests.Projection() with
        {
            Definitions = [Def(ContainmentCellRules.ShellWallDefinition, 135, 5), Def(ContainmentCellRules.ShellDoorDefinition, 850, 25)],
            Resources = Known<IReadOnlyDictionary<Resource, long>>(new Dictionary<Resource, long> { [wood] = 30 }),
            Acquisition = [],
        };

        var (wall, door, _, ok) = RoundsBuildingPlanner.ShellMaterials(colony, ContainmentCellRules.ShellWallDefinition, ContainmentCellRules.ShellDoorDefinition, null);
        Assert.True(ok);
        Assert.Equal((wood.Value, wood.Value), (wall, door));
    }

    [Fact]
    public void AShellPieceCostsItsWorkOverADayAndItsMaterialAtItsValue()
    {
        var wood = new Resource("WoodLog");
        var values = new Dictionary<Resource, ConstructionMaterialValue> { [wood] = new(MaterialValueBasis.Unpriced, 0.001) };
        IReadOnlyList<Amount> costs = [new Amount(wood, 5)];

        Assert.Equal(0.5 + 0.005, LayoutClusterRules.PiecePrice((GameTime.TicksPerDay / 2.0, costs), values), 9);
    }

    [Fact]
    public void TheStationWorkDayIsPricedFromTheUpkeepAndTheCropOrNamesWhyNot()
    {
        var s = FirstLayoutPlanTests.Survey("mild");
        var f = FirstLayoutPlanTests.Priced(FirstLayoutPlanTests.Facts(s, 3), "mild");

        // Three colonists eating 1.6 a day each: a colonist's upkeep is 1.6 a day against rice's unit on normal soil.
        var day = Assert.IsType<StationWorkDay.Priced>(LayoutLightRules.StationWorkDayOf(f));
        Assert.Equal(1.6, day.Upkeep, 9);
        Assert.Equal(SiteRules.CropRate(Rice, 1), day.Unit, 9);
        Assert.Equal(day.Upkeep / day.Unit / SiteRules.DefaultSiteTypeWeights().Power * 1000, day.W, 6);
        Assert.Equal(day.W, LayoutRegroupRules.StationWorkPrice(f).WorkDayW, 9);
        Assert.Equal(day, f.BedMergePriceOf().WorkDay);

        // Each reason it has no price is named, and the work price stays invalid.
        Assert.IsType<StationWorkDay.NoColonist>(LayoutLightRules.StationWorkDayOf(f with { Colonists = 0 }));
        Assert.IsType<StationWorkDay.NoEater>(LayoutLightRules.StationWorkDayOf(f with { Food = new FoodSupply { Larder = new(), Consumers = [] } }));
        Assert.IsType<StationWorkDay.NoCrop>(LayoutLightRules.StationWorkDayOf(f with { Crops = [] }));
        Assert.False(LayoutRegroupRules.StationWorkPrice(f with { Crops = [] }).Valid());
        Assert.Empty(LayoutComfortChooseRules.AdviseComfort(f with { Crops = [], ExpectedMood = Known(0.5) }, new StationGraph(), 1));
    }
}
