using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Policy.Methods.Generation;
using RimGovernor.Contracts.Wire.Observations;
using RimGovernor.Contracts.Wire.Common;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// Planned rooms as construction activities in the coupled portfolio (#3276), at their own cells (#3313): room 1 is a
/// 3x3 interior at (5,5)-(7,7) inside its ring (4,4)-(8,8), the door south at (6,4). The pawn and its food stand at
/// (1,1), wood at (10,2) and stone at (10,7). Native rows and routes are walked at 2 ticks a cell (Manhattan), to
/// the target cell nearest the origin, as the native read reaches a site's nearest piece.
/// </summary>
public sealed class PlannedRoomActivitiesTests
{
    private const double Horizon = 300;
    private const string Pawn = "pawn";
    private static readonly string[] Plants = ["plant-a", "plant-b"];
    private static readonly ObservationContext Context = new()
        { NativeGeneration = new RimGovernor.Contracts.Wire.Common.ObservedGeneration.UnclaimedCase(), Identity = new() { ColonyId = "colony", LoadToken = "load", MapId = 0 }, Tick = 100 };
    private static readonly Cell Start = new(1, 1);
    private static readonly Cell DoorCell = new(6, 4);
    private static readonly Cell[] Interior = [.. Enumerable.Range(5, 3).SelectMany(x => Enumerable.Range(5, 3).Select(z => new Cell(x, z)))];
    private static readonly Cell[] WallCells = [.. Enumerable.Range(4, 5).SelectMany(x => Enumerable.Range(4, 5).Select(z => new Cell(x, z)))
        .Where(c => (c.X is 4 or 8 || c.Z is 4 or 8) && c != DoorCell).OrderBy(c => c.X).ThenBy(c => c.Z)];

    private static Cell StackCell(string stuff) => stuff == "WoodLog" ? new(10, 2) : new(10, 7);

    private static double Ticks(Cell from, Cell to) => 2.0 * (Math.Abs(from.X - to.X) + Math.Abs(from.Z - to.Z));

    private static Cell Nearest(IEnumerable<Cell> cells, Cell origin) =>
        cells.OrderBy(c => Ticks(origin, c)).ThenBy(c => c.X).ThenBy(c => c.Z).First();

    /// <summary>
    /// One planned site of room 1 in the native answer's shape: rows located at its first position, one per material
    /// stack (count owed from each, which holds its available), approach and delivery walked to the nearest position.
    /// </summary>
    private static PlannedBuildSite Site(string id, OpKind op, string def, string stuff, Cell[] cells, double finish, int count,
        IReadOnlyList<(string Id, Cell At, double Available)> stacks, PlannedSiteVerdict verdict = PlannedSiteVerdict.Feasible, int room = 1)
    {
        var at = cells[0];
        var reach = Ticks(Start, Nearest(cells, Start));
        var rows = count == 0
            ? [new Row(id, def, at, Pawn, "", "", at, 0, 0, "Finish", default, default, Known(finish)) { SiteApproachTicks = Known(reach) }]
            : stacks.Select(s => new Row(id, def, at, Pawn, stuff, s.Id, s.At, count, 75, "Finish", Known(Ticks(Start, s.At)),
                Known(Ticks(s.At, Nearest(cells, s.At))), Known(finish)) { SiteApproachTicks = Known(reach), AvailableInput = Known(s.Available) }).ToArray();
        return new PlannedBuildSite(id, room, op, def, op == OpKind.FloorIn ? "" : stuff, Rotation.North, EquatableArray.From(cells))
        {
            Answer = ConstructionFixtures.Answer(id, verdict) with { Options = [.. rows.Select(r => r.Option)] },
            Material = count > 0 ? [new RimGovernor.Brain.Policy.Amount(new Resource(stuff), count)] : [],
        };
    }

    /// <summary>The wall run or door of room 1 the pawn can finish in finish ticks, delivering count of stuff from its one stack holding available.</summary>
    private static PlannedBuildSite Ring(OpKind op, string stuff, double finish, int count = 0, double available = 0,
        PlannedSiteVerdict verdict = PlannedSiteVerdict.Feasible)
    {
        var wall = op == OpKind.WallIn;
        return Site("planned/room1/" + (wall ? "wall" : "door") + "/" + stuff, op, wall ? "Wall" : "Door", stuff, wall ? WallCells : [DoorCell],
            finish, count, [("stack-" + stuff, StackCell(stuff), available)], verdict);
    }

    /// <summary>Room 1's roof over its interior: no native rows; its work per cell is the coupled read's RoofCellWorkTicks.</summary>
    private static PlannedBuildSite Roof() => new("planned/room1/roof", 1, OpKind.RoofIn, "", "", Rotation.North, EquatableArray.From(Interior));

    /// <summary>
    /// The planned-site facts with native routes between the sites (#3313): the production probes (PlannedRouteProbes),
    /// answered per probe at 2 ticks a cell, joined by JoinPlannedRoutes. Without routes, the facts native gave before #3313.
    /// </summary>
    private static PlannedSiteFacts PlannedFacts(IEnumerable<PlannedBuildSite> planned, bool routes)
    {
        var facts = new PlannedSiteFacts { Sites = EquatableArray.From(planned) };
        if (!routes) return facts;
        var probes = PlannedRules.PlannedRouteProbes(facts.Sites);
        return PlannedRules.JoinPlannedRoutes(facts, probes, ConstructionFixtures.NoConstruction() with
        {
            Routes = EquatableArray.From(probes.Select(p =>
            {
                var origin = p.FromStart ? Start : p.From;
                return new CoupledRoute(Pawn, origin, p.To, new RouteTicks.Reachable(Ticks(origin, p.Reach.Count == 0 ? p.To : Nearest(p.Reach, origin)))) { FromStart = p.FromStart };
            })),
        });
    }

    private static NeedsForecastPawn Idle(string pawn, double level = 1, double fall = 0) => NeedsFixtures.Pawn() with
    {
        Pawn = pawn, State = "eligible", Mood = NeedsFixtures.Mood with { Level = .5, Target = .5, UnclampedTarget = .5, RisePerTick = .12 / GameTime.TicksPerHour, FallPerTick = .08 / GameTime.TicksPerHour, FreezesWhileAsleep = true, BreakThreshold = .3, Conditional = new ConditionalMood.Modeled([]), Memories = [] }, LocalHour = 6d, Awake = true,
        Needs = [NeedsFixtures.Rate("food", level, fall, .3),
            NeedsFixtures.Rate("rest", 1d, 0d, .3)],
    };

    /// <summary>
    /// The round's facts for the builder and, with colonists over one, idle colonists colonist-01.. whose ids sort before
    /// the builder's, so the builder is the last colonist by id. Only the builder has planned rows.
    /// </summary>
    private static RoundsFacts Review(IEnumerable<PlannedBuildSite> planned, double level = 1, double fall = 0, double horizon = Horizon,
        bool routes = true, double roofCellWork = 0, int colonists = 1)
    {
        var others = Enumerable.Range(1, colonists - 1).Select(i => "colonist-" + i.ToString("D2", System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        var facts = WorkResourceTestFacts.Create([.. others, Pawn]);
        facts = facts with
        {
            // The colony holds what the stacks hold.
            Resources = Known(EquatableArray.From(planned.SelectMany(s => s.Options).Where(o => o.Input.Length > 0).DistinctBy(o => o.Input)
                .GroupBy(o => o.Resource).Select(g => new RimGovernor.Brain.Policy.Amount(new Resource(g.Key), (long)g.Sum(o => o.AvailableInput))))),
            PlannedSites = Known(PlannedFacts(planned, routes)),
            ForecastObserved = facts.ForecastObserved with
            {
                Needs = NeedsFixtures.Empty with { Tick = new(100), Pawns = EquatableArray.From(others.Select(p => Idle(p)).Append(Idle(Pawn, level, fall))) },
                Coupled = CoupledFixtures.Empty with {
                    Research = ActivityContinuationFixtures.NoResearch, Hunt = HuntFixtures.NoHunt, Clothing = ClothingFixtures.NoClothing,
                    Tick = 100,
                    Food = new() { Options = EquatableArray.From(Plants.Select(source =>
                        new FoodActivityOption("pawn", source, "Rice", new(1, 1), new(1, 1), "Harvest", 2, 10)
                        { ApproachTicks = 0d, DeliveryTicks = 10d, HarvestAllowed = true,
                            SourceCount = 2, SkillWork = null })) },
                    FoodUses = [new("pawn", "Rice", new(1, 1), true, .2, 10d, 2, new FoodFreshness.Rots(1000d)) { Ingredient = "", Thoughts = [] }],
                    Construction = ConstructionFixtures.NoConstruction() with { Context = Context },
                    // A roof is worked cell by cell at the coupled read's RoofCellWorkTicks.
                    Thermal = roofCellWork > 0 ? CoupledThermalTests.NoThermal with { Workers = [new(Pawn, roofCellWork)] } : CoupledThermalTests.NoThermal,
                },
            },
        };
        facts = WorkRules.ReviewWorkResources(facts, Known<EquatableArray<ProductionBatch>>([]), new([]));
        return facts with { CoupledAlternatives = ForecastCoupledCandidates.GenerateCoupledActivities(facts, horizon)?.Priced().BuildersRead() };
    }

    /// <summary>The test-only workshop credit: one capability-day a served day, booked to no pawn.</summary>
    private static readonly IReadOnlyDictionary<PlannedRole, RoomCredit> WorkshopCredit = new Dictionary<PlannedRole, RoomCredit>
    {
        [PlannedRole.Workshop] = (_, _, _, _, _) => [new("bench", "", 1)],
    };

    /// <summary>
    /// Room 1 as a workshop priced by <see cref="WorkshopCredit"/>, one builder and tier 2's horizon 10 days. No planned room's
    /// sites count as development (#3828), so a room the schedule builds is selected only on its credit: the cases that read
    /// what a selected room's activities and orders are price it this way.
    /// </summary>
    private static RoundsFacts Credited(RoundsFacts facts) => facts with { CoupledAlternatives = facts.CoupledAlternatives! with
    {
        Context = facts.CoupledAlternatives.Context with
        {
            PlannedRoomRoles = new Dictionary<int, PlannedRole> { [1] = PlannedRole.Workshop },
            RoomCredits = WorkshopCredit, PlannedBuilders = 1, InvestmentHorizonDays = 10d,
        },
    } };

    private static ForecastCoupledSelection Select(RoundsFacts facts, double horizon = Horizon, ForecastSearchBudget? budget = null)
    {
        var request = ForecastCoupled.CoupledForecastRequestOf(new(100), horizon / GameTime.TicksPerDay,
            facts,  new() { Work = ForecastWorkInputs.ForecastWorkInputsOf(facts) }, []);
        return ForecastCoupledSelection.CompareActivityOutcomes(request, facts.CoupledAlternatives!, budget);
    }

    private static string Why(RoundsFacts facts, ForecastCoupledSelection result) =>
        "selected=" + string.Join(",", result.Selection.Selected) + " " + string.Join(";", facts.CoupledAlternatives!.Findings.Concat(result.Selection.Findings));

    /// <summary>Whether the selected plan builds the planned site: its own root, or a room alternative naming it (#3823), whichever completes it.</summary>
    private static bool PlannedSelected(ForecastCoupledSelection result, string site) =>
        result.Selection.Selected.Any(id => id.StartsWith("planned/", StringComparison.Ordinal)) && result.Selected.RequestedDevelopment.Contains(site);

    [Fact]
    public void AnIdleBuilderBuildsTheRoomAndTheRoomCompletesInTheHorizon()
    {
        var facts = Credited(Review([Ring(OpKind.WallIn, "WoodLog", 200), Ring(OpKind.DoorIn, "WoodLog", 60)]));
        var candidates = facts.CoupledAlternatives!;
        Assert.Equal(["planned/room1/door/WoodLog", "planned/room1/wall/WoodLog"], candidates.Context.PlannedSites.Select(s => s.Site).Order(StringComparer.Ordinal));
        Assert.Equal(["planned/room1/WoodLog/Shell"], candidates.Context.PlannedRooms.Select(r => r.Id));

        var result = Select(facts);
        Assert.True(PlannedSelected(result, "planned/room1/wall/WoodLog"), Why(facts, result));
        Assert.True(PlannedSelected(result, "planned/room1/door/WoodLog"), Why(facts, result));
        var room = Assert.Single(result.Selected.PlannedRooms);
        Assert.Equal(["planned/room1/WoodLog/Shell"], room.Complete);
        Assert.Contains(result.SelectedVector.Value.Ledger.InvestmentContributions, c => c.Key == "workshop/1/bench" && c.Value < 0);

        // Nothing binds a planned site before placement (#3278): the selection starts no order.
        Assert.Null(ForecastCoupledSelection.StartCoupledActivity(result, candidates, 1, "planned-test", 100));
    }

    /// <summary>
    /// The 3x3 wood room (#3313): 15 walls, a door, 9 plank floors and the roof, each site at its own cells, worked by
    /// one builder within a day, site after site along the routes between them. Without those routes the room is
    /// never complete: every leg from one site to the next is unknown.
    /// </summary>
    [Fact]
    public void OneBuilderCompletesASmallWoodRoomSiteAfterSite()
    {
        (string, Cell, double)[] wood = [("wood-a", new(10, 2), 75), ("wood-b", new(10, 7), 75)];
        PlannedBuildSite[] room =
        [
            Site("planned/room1/wall/WoodLog", OpKind.WallIn, "Wall", "WoodLog", WallCells, 15 * 135, 75, wood),
            Site("planned/room1/door/WoodLog", OpKind.DoorIn, "Door", "WoodLog", [DoorCell], 850, 25, wood),
            Site("planned/room1/floor/WoodPlankFloor", OpKind.FloorIn, "WoodPlankFloor", "WoodLog", Interior, 9 * 85, 27, wood),
            Roof(),
        ];
        const double day = GameTime.TicksPerDay;
        var facts = Credited(Review(room, horizon: day, roofCellWork: 65));
        var result = Select(facts, day);
        var outcome = Assert.Single(result.Selected.PlannedRooms);
        Assert.Equal(room.Select(s => s.Id).Order(StringComparer.Ordinal), outcome.CompletedSites.Order(StringComparer.Ordinal));
        Assert.Contains("planned/room1/WoodLog/ShellFloor", outcome.Complete);

        // One builder: every planned allocation is the pawn's, one after another, each leg a known route.
        var planned = result.Selected.Plan.Allocations.Where(a => a.Activity.StartsWith("planned/room1/", StringComparison.Ordinal)).OrderBy(a => a.Depart).ToArray();
        Assert.All(planned, a => Assert.Equal(Pawn, a.Pawn));
        Assert.All(room, s => Assert.Contains(planned, a => a.Activity.StartsWith(s.Id + "/", StringComparison.Ordinal)));
        Assert.All(planned.Zip(planned.Skip(1)), p => Assert.True(p.Second.Depart >= p.First.End, p.First.Activity + " overlaps " + p.Second.Activity));
        Assert.True(planned[^1].End <= day, Why(facts, result));

        var unrouted = Credited(Review(room, horizon: day, routes: false, roofCellWork: 65));
        Assert.DoesNotContain(Select(unrouted, day).Selected.PlannedRooms, r => r.Complete.Count > 0);
    }

    /// <summary>
    /// Twenty colonists (#3341): every free colonist is in the native needs and planned-site reads, so the builder's
    /// planned rows reach the forecast problem though it is the twentieth colonist by id, and it builds the room.
    /// </summary>
    [Fact]
    public void TheTwentiethColonistBuildsThePlannedRoom()
    {
        var facts = Credited(Review([Ring(OpKind.WallIn, "WoodLog", 200), Ring(OpKind.DoorIn, "WoodLog", 60)], colonists: 20));
        var pawns = facts.ForecastObserved.Needs!.Pawns.Select(p => p.Pawn).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(20, pawns.Length);
        Assert.Equal(Pawn, pawns[^1]);
        var result = Select(facts);
        Assert.True(PlannedSelected(result, "planned/room1/wall/WoodLog"), Why(facts, result));
        Assert.Equal(["planned/room1/WoodLog/Shell"], Assert.Single(result.Selected.PlannedRooms).Complete);
        Assert.All(result.Selected.Plan.Allocations.Where(a => a.Activity.StartsWith("planned/room1/", StringComparison.Ordinal)),
            a => Assert.Equal(Pawn, a.Pawn));
    }

    [Fact]
    public void UrgentHungerOutranksTheRoomForTheCookBuilder()
    {
        // The room takes nearly the whole horizon: a hungry pawn eats first and the room is left partial.
        var facts = Review([Ring(OpKind.WallIn, "WoodLog", 240), Ring(OpKind.DoorIn, "WoodLog", 50)], level: .2, fall: .003);
        var result = Select(facts);
        var needs = facts.CoupledAlternatives!.Context.NeedTasks;
        Assert.Contains(result.Selected.Plan.Allocations, a => needs.Any(n => n.Task == a.Activity && n.Kind == "food"));
        Assert.True(result.SelectedVector.Urgent[0].Value < result.BaselineVector.Urgent[0].Value, Why(facts, result));
        Assert.DoesNotContain(result.Selected.PlannedRooms, r => r.Complete.Count > 0);
    }

    [Fact]
    public void ScarceWoodLeavesTheRoomToPlentifulStone()
    {
        var facts = Credited(Review([
            Ring(OpKind.WallIn, "WoodLog", 60, count: 10, available: 4), Ring(OpKind.DoorIn, "WoodLog", 30, count: 5, available: 4),
            Ring(OpKind.WallIn, "BlocksGranite", 90, count: 10, available: 50), Ring(OpKind.DoorIn, "BlocksGranite", 40, count: 5, available: 50)]));
        var result = Select(facts);
        Assert.True(PlannedSelected(result, "planned/room1/wall/BlocksGranite"), Why(facts, result));
        Assert.False(PlannedSelected(result, "planned/room1/wall/WoodLog"));
        Assert.Contains(result.Selected.PlannedRooms.SelectMany(r => r.Complete), id => id.Contains("BlocksGranite", StringComparison.Ordinal));
    }

    /// <summary>
    /// #3673: no one stack holds the wall's 75 logs, so native lists its two stacks as one set (each under the count owed)
    /// and the wall is delivered from both: the smaller whole (40), the larger the remaining 35. The room completes.
    /// </summary>
    [Fact]
    public void AWallNoOneStackCoversIsDeliveredFromBothStacks()
    {
        (string, Cell, double)[] set = [("wood-a", new(10, 2), 50), ("wood-b", new(10, 7), 40)];
        var facts = Credited(Review([
            Site("planned/room1/wall/WoodLog", OpKind.WallIn, "Wall", "WoodLog", WallCells, 60, 75, set),
            Site("planned/room1/door/WoodLog", OpKind.DoorIn, "Door", "WoodLog", [DoorCell], 30, 25, [("wood-c", new(10, 4), 30)])]));
        var result = Select(facts);
        Assert.Equal(["planned/room1/WoodLog/Shell"], Assert.Single(result.Selected.PlannedRooms).Complete);
        string[] deliveries = ["planned/room1/wall/WoodLog/delivery/wood-a", "planned/room1/wall/WoodLog/delivery/wood-b"];
        Assert.All(deliveries, delivery => Assert.Contains(result.Selected.Plan.Allocations, a => a.Activity.StartsWith(delivery + "/composition/", StringComparison.Ordinal)));
        Assert.Equal((35, 40), (ConstructionMethodCandidates.StackShares(facts.PlannedSites.Value.Sites.Single(s => s.Op == OpKind.WallIn).Options, _ => 0)!["wood-a"],
            ConstructionMethodCandidates.StackShares(facts.PlannedSites.Value.Sites.Single(s => s.Op == OpKind.WallIn).Options, _ => 0)!["wood-b"]));
    }

    [Fact]
    public void TheStuffThatServesTheRoomSoonerIsPicked()
    {
        var facts = Credited(Review([
            Ring(OpKind.WallIn, "WoodLog", 60, count: 10, available: 50), Ring(OpKind.DoorIn, "WoodLog", 30, count: 5, available: 50),
            Ring(OpKind.WallIn, "BlocksGranite", 120, count: 10, available: 50), Ring(OpKind.DoorIn, "BlocksGranite", 60, count: 5, available: 50)]));
        var result = Select(facts);
        Assert.True(PlannedSelected(result, "planned/room1/wall/WoodLog"), Why(facts, result));
        Assert.False(PlannedSelected(result, "planned/room1/wall/BlocksGranite"));
        Assert.False(PlannedSelected(result, "planned/room1/door/BlocksGranite"));
    }

    [Fact]
    public void AnUnestimableBlockerLeavesTheAlternativeUnvaluedAndEmitsNoSite()
    {
        var facts = Review([Ring(OpKind.WallIn, "WoodLog", 60, verdict: PlannedSiteVerdict.Unestimable), Ring(OpKind.DoorIn, "WoodLog", 30)]);
        var candidates = facts.CoupledAlternatives!;
        Assert.Contains("planned_room_alternative_unvalued:planned/room1/WoodLog/Shell:planned/room1/wall/WoodLog:unestimable", candidates.Findings);
        Assert.Empty(candidates.Context.PlannedSites);
        Assert.DoesNotContain(candidates.Alternatives, c => c.Id.StartsWith("planned/", StringComparison.Ordinal));

        var illegal = Review([Ring(OpKind.WallIn, "WoodLog", 60, verdict: PlannedSiteVerdict.Illegal), Ring(OpKind.DoorIn, "WoodLog", 30)]);
        Assert.Contains("planned_room_alternative_infeasible:planned/room1/WoodLog/Shell:planned/room1/wall/WoodLog", illegal.CoupledAlternatives!.Findings);
    }

    private static readonly string[] BarracksBeds = ["planned/room1/build/Bed/5/5", "planned/room1/build/Bed/6/5", "planned/room1/build/Bed/7/5"];
    private const string BarracksStove = "planned/room1/build/FueledStove/6/7";
    private const string BarracksFull = "planned/room1/WoodLog/Full";

    /// <summary>
    /// A barracks (#3838) on the colony <paramref name="colony"/> describes: room 1's wooden ring, three beds the builder can
    /// finish and a FueledStove (80 Steel) with no finish row, as on a map with no Steel stack; the round marks its blocked
    /// pieces (Rounder.BlockedPiecesOf).
    /// </summary>
    private static RoundsFacts Barracks(RimGovernor.Brain.Observation.ColonyProjection colony) => Review(RimGovernor.Brain.BuildingRuntime.Rounder.BlockedPiecesOf(colony,
    [
        Ring(OpKind.WallIn, "WoodLog", 60), Ring(OpKind.DoorIn, "WoodLog", 30),
        .. BarracksBeds.Select((id, i) => Site(id, OpKind.Build, "Bed", "WoodLog", [new Cell(5 + i, 5)], 40, 0, [])),
        new PlannedBuildSite(BarracksStove, 1, OpKind.Build, "FueledStove", "", Rotation.North, [new Cell(6, 7)])
            { Answer = ConstructionFixtures.Answer(BarracksStove), Material = [new RimGovernor.Brain.Policy.Amount(new Resource("Steel"), 80)] },
    ]).ToList());

    /// <summary>
    /// #3838: a barracks' stove with no Steel anywhere (none stocked, none lying, mined or salvaged) is blocked, so its Full
    /// alternative leaves it out and is valued with the beds: the bed sites are generated, the stove's is not.
    /// </summary>
    [Fact]
    public void ABlockedStoveIsLeftOutOfTheBarracksAndItsBedsAreValued()
    {
        var candidates = Barracks(PlannedRoomGateTests.StoveFacts()).CoupledAlternatives!;
        Assert.Contains("planned_room_site_blocked:" + BarracksFull + ":" + BarracksStove, candidates.Findings);
        Assert.DoesNotContain(candidates.Findings, f => f.StartsWith("planned_room_alternative_unvalued:" + BarracksFull + ":", StringComparison.Ordinal));
        var full = Assert.Single(candidates.Context.PlannedRooms, a => a.Id == BarracksFull);
        Assert.Equal([.. BarracksBeds, "planned/room1/door/WoodLog", "planned/room1/wall/WoodLog"], full.Sites.Order(StringComparer.Ordinal));
        var generated = candidates.Context.PlannedSites.Select(s => s.Site).ToHashSet();
        Assert.All(BarracksBeds, bed => Assert.Contains(bed, generated));
        Assert.DoesNotContain(BarracksStove, generated);
    }

    /// <summary>
    /// #3838, #3839: with Steel stocked, a stack lying on the map, compacted steel in the window or an unowned building that
    /// yields it, the stove is kept in the Full alternative, which its missing finish row leaves unvalued as before.
    /// </summary>
    [Theory]
    [InlineData("stocked")]
    [InlineData("lying")]
    [InlineData("ore")]
    [InlineData("salvage")]
    public void AStoveWhoseSteelTheColonyCanObtainIsKept(string source)
    {
        var steel = PlannedRoomGateTests.SteelOffered;
        var colony = source switch
        {
            "stocked" => PlannedRoomGateTests.StoveFacts(steel: 80),
            "lying" => PlannedRoomGateTests.StoveFacts(sources: new RingStuffSources { Items = steel }),
            "ore" => PlannedRoomGateTests.StoveFacts(sources: new RingStuffSources { Mined = steel }),
            _ => PlannedRoomGateTests.StoveFacts(sources: new RingStuffSources { Salvage = steel }),
        };
        Assert.Equal(RimGovernor.Brain.BuildingRuntime.DefinitionReadiness.Available, RimGovernor.Brain.BuildingRuntime.RoundsBuildingPlanner.PieceReadinessOf(colony)("FueledStove"));
        var candidates = Barracks(colony).CoupledAlternatives!;
        Assert.DoesNotContain(candidates.Findings, f => f.StartsWith("planned_room_site_blocked:", StringComparison.Ordinal));
        Assert.Contains("planned_room_alternative_unvalued:" + BarracksFull + ":" + BarracksStove + ":no_finish_option", candidates.Findings);
        Assert.DoesNotContain(candidates.Context.PlannedRooms, a => a.Id == BarracksFull);
    }

    /// <summary>#3838, #3839: with any source of Steel unread the stove is Unread, not blocked: the Full alternative keeps it and stays unvalued.</summary>
    [Theory]
    [InlineData("stock")]
    [InlineData("sources")]
    [InlineData("salvage")]
    public void AStoveWhoseSteelIsUnreadKeepsTheBarracksUnvalued(string unread)
    {
        var colony = unread switch
        {
            "stock" => PlannedRoomGateTests.StoveFacts(stockRead: false),
            "sources" => PlannedRoomGateTests.StoveFacts(sourcesRead: false),
            _ => PlannedRoomGateTests.StoveFacts(sources: new RingStuffSources { Unsalvaged = PlannedRoomGateTests.MachineUnsalvaged }),
        };
        Assert.Equal(RimGovernor.Brain.BuildingRuntime.DefinitionReadiness.Unread, RimGovernor.Brain.BuildingRuntime.RoundsBuildingPlanner.PieceReadinessOf(colony)("FueledStove"));
        var candidates = Barracks(colony).CoupledAlternatives!;
        Assert.DoesNotContain(candidates.Findings, f => f.StartsWith("planned_room_site_blocked:", StringComparison.Ordinal));
        Assert.Contains("planned_room_alternative_unvalued:" + BarracksFull + ":" + BarracksStove + ":no_finish_option", candidates.Findings);
        Assert.DoesNotContain(candidates.Context.PlannedRooms, a => a.Id == BarracksFull);
    }

    /// <summary>
    /// #3617: a roof-holder under the footprint is a valued Deconstruct clearing carrying the cells it leaves unsupported.
    /// The alternative is valued (not unknown), and the clearing binds as one task whose order removes the roof over those
    /// cells (Work/RemoveRoof cell set step) before the Work/Deconstruct.
    /// </summary>
    [Fact]
    public void ARoofHolderUnderTheRingIsValuedAndBindsRoofOffThenDeconstruct()
    {
        const string ruin = "Thing_Wall1";
        const string site = "planned/room1/wall/WoodLog";
        const string clearingId = site + "/deconstruct/" + ruin;
        var at = WallCells[0];
        Cell[] cells = [new(at.X + 1, at.Z), new(at.X + 1, at.Z + 1)];
        var clearing = ConstructionFixtures.Clearing(clearingId, PlannedClearingKind.Deconstruct) with
        {
            Options = [new Row(clearingId, "Wall", cells[0], Pawn, "Wall", ruin, cells[0], 0, 0, "Deconstruct", default, default, Known(50d))
                { SiteApproachTicks = Known(Ticks(Start, cells[0])) }],
            Cells = EquatableArray.From(cells),
        };
        var facts = Credited(Review([Ring(OpKind.WallIn, "WoodLog", 120).Cleared(clearing), Ring(OpKind.DoorIn, "WoodLog", 60)]));
        var candidates = facts.CoupledAlternatives!;
        Assert.DoesNotContain(candidates.Findings, f => f.StartsWith("planned_room_alternative_", StringComparison.Ordinal));
        var binding = Assert.Single(candidates.Context.Bindings.Values, b => b.Job == "Deconstruct");
        Assert.Equal((Pawn, PawnActivityKind.Work, ruin, "Deconstruct", "Wall"), (binding.Pawn, binding.Kind, binding.Target, binding.WorkGiver, binding.SiteDefinition));
        Assert.Equal(cells, binding.RoofCells);

        var result = Select(facts);
        Assert.True(PlannedSelected(result, site), Why(facts, result));
        var batch = ForecastCoupledSelection.StartCoupledActivity(result, candidates, 1, "planned-test", 100);
        Assert.NotNull(batch);
        var task = Assert.Single(batch.Tasks, t => t.Pawn == Pawn);
        var prior = Assert.Single(task.PriorSteps);
        Assert.Equal((PawnActivityKind.Work, ruin, "RemoveRoofs", "RemoveRoof", 2), (prior.Kind, prior.Target, prior.WorkGiver, prior.Job, prior.Count));
        Assert.Equal(cells, prior.Cells);
        Assert.Equal((PawnActivityKind.Work, ruin, "Deconstruct"), (task.Step.Kind, task.Step.Target, task.Step.Job));
        Assert.Contains(batch.Orders, o => o.Pawn == Pawn && !o.Release && o.Task == task.Id);
    }

    /// <summary>
    /// #3557: a planned ring over ruins. Native estimates a deconstructible building under the footprint as a Deconstruct
    /// clearing with per-pawn rows, so the alternative is valued, and that clearing is the one planned activity bound
    /// before placement (its building stands, the job targets it): the selection orders the pawn's Deconstruct work while
    /// the site itself waits for placement.
    /// </summary>
    [Fact]
    public void ARuinUnderTheRingIsValuedAsADeconstructClearingAndItsRemovalIsOrdered()
    {
        const string ruin = "Thing_Door1";
        const string site = "planned/room1/wall/WoodLog";
        const string clearingId = site + "/deconstruct/" + ruin;
        var at = WallCells[0];
        var clearing = ConstructionFixtures.Clearing(clearingId, PlannedClearingKind.Deconstruct) with
        {
            Options = [new Row(clearingId, "Wall", at, Pawn, "Door", ruin, at, 0, 0, "Deconstruct", default, default, Known(50d))
                { SiteApproachTicks = Known(Ticks(Start, at)) }],
        };
        var facts = Credited(Review([Ring(OpKind.WallIn, "WoodLog", 120).Cleared(clearing), Ring(OpKind.DoorIn, "WoodLog", 60)]));
        var candidates = facts.CoupledAlternatives!;
        Assert.DoesNotContain(candidates.Findings, f => f.StartsWith("planned_room_alternative_", StringComparison.Ordinal));
        var binding = Assert.Single(candidates.Context.Bindings.Values, b => b.Job == "Deconstruct");
        Assert.Equal((Pawn, PawnActivityKind.Work, ruin, "Deconstruct", "Door"), (binding.Pawn, binding.Kind, binding.Target, binding.WorkGiver, binding.SiteDefinition));
        Assert.StartsWith(clearingId + "/composition/", binding.Id, StringComparison.Ordinal);
        // The bound clearing is startable work, not an unplaced planned activity.
        var planned = Assert.Single(candidates.Context.PlannedSites, s => s.Site == site);
        Assert.DoesNotContain(binding.Id, planned.Activities);

        var result = Select(facts);
        Assert.True(PlannedSelected(result, site), Why(facts, result));
        var batch = ForecastCoupledSelection.StartCoupledActivity(result, candidates, 1, "planned-test", 100);
        Assert.NotNull(batch);
        var task = Assert.Single(batch.Tasks, t => t.Pawn == Pawn);
        Assert.Equal((binding.Id, PawnActivityKind.Work, ruin, "Deconstruct"), (task.Id, task.Step.Kind, task.Step.Target, task.Step.Job));
        Assert.Contains(batch.Orders, o => o.Pawn == Pawn && !o.Release && o.Task == task.Id);

        // A deconstruct row naming no building binds nothing and leaves the alternative unvalued.
        var unnamed = Review([Ring(OpKind.WallIn, "WoodLog", 120).Cleared(clearing with { Options = [clearing.Options[0] with { Input = "" }] }),
            Ring(OpKind.DoorIn, "WoodLog", 60)]);
        Assert.Contains("planned_room_alternative_unvalued:planned/room1/WoodLog/Shell:" + site + ":clearing_unrouted", unnamed.CoupledAlternatives!.Findings);
    }

    /// <summary>
    /// #3613: a planned ring over trees. Native estimates the plants under the footprint as one Cut clearing naming them
    /// (Targets), and that clearing binds before placement as the pawn's Work/CutPlant set step over them, one task per
    /// op; the selection orders it. A Cut clearing naming no plants is valued but binds nothing.
    /// </summary>
    [Fact]
    public void ThePlantsUnderTheRingAreOneCutSetOrderBoundBeforePlacement()
    {
        const string site = "planned/room1/wall/WoodLog";
        const string clearingId = site + "/cut";
        string[] trees = ["Thing_Plant_TreeOak1", "Thing_Plant_TreeOak2"];
        var at = WallCells[0];
        var clearing = ConstructionFixtures.Clearing(clearingId, PlannedClearingKind.Cut) with
        {
            Options = [new Row(clearingId, "Wall", at, Pawn, "", "", at, 0, 0, "PlantsCut", default, default, Known(50d))
                { SiteApproachTicks = Known(Ticks(Start, at)) }],
            Targets = EquatableArray.From(trees),
        };
        var facts = Credited(Review([Ring(OpKind.WallIn, "WoodLog", 120).Cleared(clearing), Ring(OpKind.DoorIn, "WoodLog", 60)]));
        var candidates = facts.CoupledAlternatives!;
        var binding = Assert.Single(candidates.Context.Bindings.Values, b => b.Job == "CutPlant");
        Assert.Equal((Pawn, PawnActivityKind.Work, clearingId, "PlantsCut", 2, "Wall"), (binding.Pawn, binding.Kind, binding.Target, binding.WorkGiver, binding.Count, binding.SiteDefinition));
        Assert.Equal(trees, binding.Targets);
        Assert.StartsWith(clearingId + "/composition/", binding.Id, StringComparison.Ordinal);
        var planned = Assert.Single(candidates.Context.PlannedSites, s => s.Site == site);
        Assert.DoesNotContain(binding.Id, planned.Activities);

        var result = Select(facts);
        Assert.True(PlannedSelected(result, site), Why(facts, result));
        var batch = ForecastCoupledSelection.StartCoupledActivity(result, candidates, 1, "planned-test", 100);
        Assert.NotNull(batch);
        var task = Assert.Single(batch.Tasks, t => t.Pawn == Pawn);
        Assert.Equal((binding.Id, PawnActivityKind.Work, "CutPlant", 2), (task.Id, task.Step.Kind, task.Step.Job, task.Step.Count));
        Assert.Equal(trees, task.Step.Targets);
        Assert.Contains(batch.Orders, o => o.Pawn == Pawn && !o.Release && o.Task == task.Id);

        var unnamed = Review([Ring(OpKind.WallIn, "WoodLog", 120).Cleared(clearing with { Targets = [] }), Ring(OpKind.DoorIn, "WoodLog", 60)]);
        Assert.DoesNotContain(unnamed.CoupledAlternatives!.Context.Bindings.Values, b => b.Job == "CutPlant");
        Assert.DoesNotContain(unnamed.CoupledAlternatives.Findings, f => f.StartsWith("planned_room_alternative_unvalued", StringComparison.Ordinal));
    }

    /// <summary>
    /// #3616: a planned ring over natural rock. Native estimates the rock under the footprint as one Mine clearing naming
    /// it (Targets), and that clearing binds before placement as the pawn's Work/Mine set step over the rocks (Count the
    /// members), beside the Deconstruct, Cut and Haul bindings; the selection orders it. A Mine clearing naming no rock
    /// is valued but binds nothing.
    /// </summary>
    [Fact]
    public void TheRockUnderTheRingIsOneMineSetOrderBoundBeforePlacement()
    {
        const string site = "planned/room1/wall/WoodLog";
        const string clearingId = site + "/mine";
        string[] rocks = ["Thing_Sandstone1", "Thing_Sandstone2"];
        var at = WallCells[0];
        var clearing = ConstructionFixtures.Clearing(clearingId, PlannedClearingKind.Mine) with
        {
            Options = [new Row(clearingId, "Wall", at, Pawn, "", "", at, 0, 0, "Mine", default, default, Known(50d))
                { SiteApproachTicks = Known(Ticks(Start, at)) }],
            Targets = EquatableArray.From(rocks),
        };
        var facts = Credited(Review([Ring(OpKind.WallIn, "WoodLog", 120).Cleared(clearing), Ring(OpKind.DoorIn, "WoodLog", 60)]));
        var candidates = facts.CoupledAlternatives!;
        var binding = Assert.Single(candidates.Context.Bindings.Values, b => b.Job == "Mine");
        Assert.Equal((Pawn, PawnActivityKind.Work, clearingId, "Mine", 2, "Wall"), (binding.Pawn, binding.Kind, binding.Target, binding.WorkGiver, binding.Count, binding.SiteDefinition));
        Assert.Equal(rocks, binding.Targets);
        Assert.StartsWith(clearingId + "/composition/", binding.Id, StringComparison.Ordinal);

        var result = Select(facts);
        Assert.True(PlannedSelected(result, site), Why(facts, result));
        var batch = ForecastCoupledSelection.StartCoupledActivity(result, candidates, 1, "planned-test", 100);
        Assert.NotNull(batch);
        var task = Assert.Single(batch.Tasks, t => t.Pawn == Pawn);
        Assert.Equal((binding.Id, PawnActivityKind.Work, "Mine", 2), (task.Id, task.Step.Kind, task.Step.Job, task.Step.Count));
        Assert.Equal(rocks, task.Step.Targets);
        Assert.Contains(batch.Orders, o => o.Pawn == Pawn && !o.Release && o.Task == task.Id);

        var unnamed = Review([Ring(OpKind.WallIn, "WoodLog", 120).Cleared(clearing with { Targets = [] }), Ring(OpKind.DoorIn, "WoodLog", 60)]);
        Assert.DoesNotContain(unnamed.CoupledAlternatives!.Context.Bindings.Values, b => b.Job == "Mine");
        Assert.DoesNotContain(unnamed.CoupledAlternatives.Findings, f => f.StartsWith("planned_room_alternative_unvalued", StringComparison.Ordinal));
    }

    /// <summary>
    /// #3614: a stack under the ring is a Haul clearing whose rows carry it to storage; it binds before placement as the
    /// pawn's Deliver of the stack (Count the stack) to the row's destination, and the op keeps its /cleared output. A
    /// stack larger than one carry binds the same way, its order one carry at a time (#3625).
    /// </summary>
    [Theory]
    [InlineData(40)]
    [InlineData(120)]
    public void AStackUnderTheRingIsAHaulClearingBoundAsADeliverToStorage(int count)
    {
        const string stack = "Thing_Steel1";
        const string site = "planned/room1/wall/WoodLog";
        const string clearingId = site + "/haul/" + stack;
        var at = WallCells[0];
        var store = new Cell(2, 2);
        var clearing = ConstructionFixtures.Clearing(clearingId, PlannedClearingKind.Haul) with
        {
            Options = [new Row(clearingId, "Wall", store, Pawn, "Steel", stack, at, count, 75, "HaulGeneral", Known(Ticks(Start, at)),
                Known(Ticks(at, store)), Known(0d)) { SiteApproachTicks = Known(Ticks(Start, at)), AvailableInput = Known((double)count) }],
        };
        var facts = Credited(Review([Ring(OpKind.WallIn, "WoodLog", 120).Cleared(clearing), Ring(OpKind.DoorIn, "WoodLog", 60)]));
        var candidates = facts.CoupledAlternatives!;
        Assert.DoesNotContain(candidates.Findings, f => f.StartsWith("planned_room_alternative_", StringComparison.Ordinal));
        var binding = Assert.Single(candidates.Context.Bindings.Values, b => b.Target == stack);
        Assert.Equal((Pawn, PawnActivityKind.Deliver, count, "Steel", 75, ""), (binding.Pawn, binding.Kind, binding.Count, binding.Resource, binding.Capacity, binding.SupportDemand));
        Assert.Equal(store, binding.Destination);
        Assert.Equal(at, binding.Source);
        Assert.StartsWith(clearingId + "/composition/", binding.Id, StringComparison.Ordinal);
        var planned = Assert.Single(candidates.Context.PlannedSites, s => s.Site == site);
        Assert.DoesNotContain(binding.Id, planned.Activities);

        var result = Select(facts);
        Assert.True(PlannedSelected(result, site), Why(facts, result));
        var batch = ForecastCoupledSelection.StartCoupledActivity(result, candidates, 1, "planned-test", 100);
        Assert.NotNull(batch);
        var task = Assert.Single(batch.Tasks, t => t.Pawn == Pawn);
        Assert.Equal((binding.Id, PawnActivityKind.Deliver, stack, count), (task.Id, task.Step.Kind, task.Step.Target, task.Step.Count));
        Assert.Equal(store, task.Step.Destination);
        var order = Assert.Single(batch.Orders, o => o.Pawn == Pawn && !o.Release && o.Task == task.Id);
        Assert.Equal(Math.Min(count, 75), order.Steps[^1].Count);
    }

    /// <summary>
    /// The handoff (#3278): the selection record names the selected sites with their stuff and the assessed room, so the
    /// reconcile places the requested room without hunger and holds it while the one cook-builder must eat first.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UrgentHungerHoldsTheRequestedRoomThatRoundAndWithoutHungerItIsStarted(bool hungry)
    {
        var facts = Credited(Review([Ring(OpKind.WallIn, "WoodLog", 270), Ring(OpKind.DoorIn, "WoodLog", 270)], level: hungry ? .2 : 1, fall: hungry ? .003 : 0));
        var result = Select(facts);
        var record = PlannedRules.PlannedSelectionOf(facts.CoupledAlternatives, result, facts.PlannedSites);
        Assert.NotNull(record);
        Assert.Equal([1], record.Assessed);
        var (gate, sites) = PlannedRules.PlannedRoomGateOf(record, new PlannedRoom { Id = 1 }, started: false);
        if (hungry)
        {
            Assert.Equal(PlannedRoomGate.Hold, gate);
            Assert.Empty(sites);
            return;
        }
        Assert.Equal(PlannedRoomGate.Selected, gate);
        Assert.NotEmpty(sites);
        Assert.All(sites, s => Assert.Equal((1, "WoodLog"), (s.Room, s.Stuff)));
        Assert.Equal([OpKind.WallIn], sites.Select(s => s.Op));
        Assert.True(PlannedSelected(result, "planned/room1/wall/WoodLog"), Why(facts, result));
        // A started room is placed as before, selected or not.
        Assert.Equal(PlannedRoomGate.Place, PlannedRules.PlannedRoomGateOf(record, new PlannedRoom { Id = 1 }, started: true).Gate);
    }

    /// <summary>
    /// The record holds the sites the selected plan completes (#3342): the room alternative root, or the site roots its
    /// schedule equals, give one record of its scope's sites.
    /// </summary>
    [Fact]
    public void ASelectedAlternativeRootSelectsAllItsSitesAndAnUnassessedRoomIsHeld()
    {
        var facts = Credited(Review([Ring(OpKind.WallIn, "WoodLog", 200), Ring(OpKind.DoorIn, "WoodLog", 60)]));
        var candidates = facts.CoupledAlternatives!;
        var root = Assert.Single(candidates.Alternatives, c => c.Id.StartsWith("planned/room1/WoodLog/Shell/composition/", StringComparison.Ordinal));
        var result = Select(facts);
        Assert.Equal(["planned/room1/door/WoodLog", "planned/room1/wall/WoodLog"], result.Selected.RequestedDevelopment);
        PlannedSelection Record(params string[] roots) =>
            PlannedRules.PlannedSelectionOf(candidates, result with { Selection = result.Selection with { Selected = roots } }, facts.PlannedSites)!;
        var record = Record(root.Id);
        Assert.Equal([OpKind.DoorIn, OpKind.WallIn], record.Sites.Select(s => s.Op));
        Assert.Equal(record, Record("planned/room1/door/WoodLog", "planned/room1/wall/WoodLog"));
        Assert.Equal(PlannedRoomGate.Hold, PlannedRules.PlannedRoomGateOf(record, new PlannedRoom { Id = 2 }, started: false).Gate);
        // A review that compared nothing leaves no record of its own.
        Assert.Null(PlannedRules.PlannedSelectionOf(candidates, null, facts.PlannedSites));
    }

    /// <summary>
    /// #3557: a chosen portfolio of unplaced planned sites alone started no order, so every colonist stood on standby
    /// review after review while the sites waited for placement. The comparison without the planned roots now adds
    /// the review's orders; the planned record keeps the planned comparison's sites.
    /// </summary>
    [Fact]
    public void AnUnplacedPlannedSelectionStillOrdersTheStartableWork()
    {
        // #3557: the room's shell outscored every startable alternative, so the selection was planned sites only; nothing
        // bound before placement, no order went out and the builder stood on standby beside an open blueprint it could work.
        // The floor blueprint and the shell do not both fit the builder's horizon; equal development, so the cheaper shell wins.
        var facts = Review([Ring(OpKind.WallIn, "WoodLog", 100), Ring(OpKind.DoorIn, "WoodLog", 40)]);
        var floor = new Row("blueprint/floor", FlooringRules.DefaultFlooringPolicy().Floors[0], new(2, 2), Pawn,
            "WoodLog", "stack-WoodLog", StackCell("WoodLog"), 1, 1, "FinishFrames", Known(0d), Known(10d), Known(200d))
            { SiteApproachTicks = Known(0d), AvailableInput = Known(10d) };
        facts = facts with
        {
            Resources = Known<EquatableArray<RimGovernor.Brain.Policy.Amount>>([new(new("WoodLog"), 10)]),
            ForecastObserved = facts.ForecastObserved with { Coupled = facts.ForecastObserved.Coupled! with
                { Construction = facts.ForecastObserved.Coupled.Construction! with { Options = [floor] } } },
        };
        facts = WorkRules.ReviewWorkResources(facts, Known<EquatableArray<ProductionBatch>>([]), new([]));
        facts = Credited(facts with { CoupledAlternatives = ForecastCoupledCandidates.GenerateCoupledActivities(facts, Horizon)?.Priced().BuildersRead() });
        var candidates = facts.CoupledAlternatives!;
        var result = Select(facts);
        var why = Why(facts, result) + " selected=" + string.Join(",", result.Selection.Selected);
        Assert.True(PlannedSelected(result, "planned/room1/wall/WoodLog"), why);
        Assert.True(result.Selection.Findings.Contains(ForecastCoupledSelection.PlannedUnplacedReselected), why);
        // #3722: both comparisons spent one allowance. The second's phases are reported under "reselect/", each run's baseline is
        // mandatory, and the phases sum to the totals.
        var reselect = ForecastCoupledSelection.ReselectPhasePrefix;
        Assert.Equal("baseline", result.SearchPhases[0].Phase);
        Assert.Contains(result.SearchPhases, p => p.Phase == reselect + "baseline");
        Assert.Equal(result.OutcomeEvaluations, result.SearchPhases.Sum(p => p.OutcomeEvaluations));
        Assert.Equal(result.Selection.Expansions, result.SearchPhases.Sum(p => p.Expansions));
        Assert.Equal(result.BaselineEvaluations, result.SearchPhases.Where(p => p.Phase is "baseline" || p.Phase == reselect + "baseline").Sum(p => p.OutcomeEvaluations));
        Assert.Equal(result.BaselineExpansions, result.SearchPhases.Where(p => p.Phase is "baseline" || p.Phase == reselect + "baseline").Sum(p => p.Expansions));
        Assert.True(result.OutcomeEvaluations - result.BaselineEvaluations <= result.Budget.Evaluations);
        Assert.DoesNotContain(ForecastCoupledSelection.OutcomeEvaluationsExhausted, result.Selection.Findings);
        // An allowance of three evaluations: the first comparison screens two planned roots, extends from the review-local cache to
        // their pair and so selects planned work only; the comparison without the planned roots gets the one unit left, not a fresh
        // allowance, spends it on its first screened root, and the search ends there, reporting the spent allowance.
        var starved = Select(facts, Horizon, new(3));
        var starvedWhy = "selected=" + string.Join(",", starved.Selection.Selected) + " phases=" + string.Join(",", starved.SearchPhases.Select(p => p.Phase + ":" + p.OutcomeEvaluations))
            + " findings=" + string.Join(";", starved.Selection.Findings);
        Assert.True(starved.OutcomeEvaluations - starved.BaselineEvaluations == 3, starvedWhy);
        Assert.True(starved.Selection.Findings.Contains(ForecastCoupledSelection.OutcomeEvaluationsExhausted), starvedWhy);
        Assert.Equal(starved.OutcomeEvaluations, starved.SearchPhases.Sum(p => p.OutcomeEvaluations));
        Assert.True(starved.Selection.Findings.Contains(ForecastCoupledSelection.PlannedUnplacedReselected), starvedWhy);
        var firstRun = starved.SearchPhases.TakeWhile(p => !p.Phase.StartsWith(reselect, StringComparison.Ordinal)).ToArray();
        var secondRun = starved.SearchPhases.Skip(firstRun.Length).ToArray();
        Assert.True(firstRun.Sum(p => p.OutcomeEvaluations) == 3 && firstRun[0].Phase == "baseline", starvedWhy);
        Assert.True(secondRun.Sum(p => p.OutcomeEvaluations) == 2 && secondRun[0].Phase == reselect + "baseline" && secondRun[1].Phase == reselect + "seed", starvedWhy);
        Assert.True(starved.Selection.Selected.Length != 0 && starved.Selection.Selected.All(id => id.StartsWith("planned/", StringComparison.Ordinal)), starvedWhy);
        Assert.Equal(["planned/room1/WoodLog/Shell"], Assert.Single(result.Selected.PlannedRooms).Complete);
        var record = PlannedRules.PlannedSelectionOf(candidates, result, facts.PlannedSites)!;
        Assert.Equal(PlannedRoomGate.Selected, PlannedRules.PlannedRoomGateOf(record, new PlannedRoom { Id = 1 }, started: false).Gate);
        var unplaced = candidates.Context.PlannedSites.SelectMany(s => s.Activities).ToHashSet(StringComparer.Ordinal);
        Assert.True(result.Selection.Selected.Any(id => !id.StartsWith("planned/", StringComparison.Ordinal)), why);
        var batch = ForecastCoupledSelection.StartCoupledActivity(result, candidates, 1, "planned-test", 100);
        Assert.NotNull(batch);
        var order = Assert.Single(batch.Orders, o => !o.Release && o.Task.Length != 0);
        Assert.Equal(Pawn, order.Pawn);
        Assert.DoesNotContain(unplaced, id => id == order.Task);
    }

    /// <summary>
    /// The wall alone fits the horizon; the door does not. With a builder the priced room's service day is projected past the
    /// horizon, which assesses its whole-room effect (#3796), and its credit selects the wall. With no builder nothing projects
    /// it: the room is never served and earns nothing (#3835), and with no development (#3828) the wall is not selected.
    /// </summary>
    [Fact]
    public void ARoomLeftPartialInTheHorizonIsProjectedOrNamedUnassessed()
    {
        var facts = Credited(Review([Ring(OpKind.WallIn, "WoodLog", 200), Ring(OpKind.DoorIn, "WoodLog", 400)]));
        var result = Select(facts);
        Assert.True(PlannedSelected(result, "planned/room1/wall/WoodLog"), Why(facts, result));
        var room = Assert.Single(result.Selected.PlannedRooms);
        Assert.Equal(["planned/room1/wall/WoodLog"], room.CompletedSites);
        Assert.Empty(room.Complete);
        Assert.True(ClosureOf(result.Selected, 1).ServedAt >= Horizon);
        Assert.DoesNotContain(result.Selected.Findings, f => f.Reason == "planned_room_effect_unassessed");

        var unbuilt = facts with { CoupledAlternatives = facts.CoupledAlternatives! with
            { Context = facts.CoupledAlternatives.Context with { PlannedBuilders = 0 } } };
        var held = Select(unbuilt);
        Assert.False(PlannedSelected(held, "planned/room1/wall/WoodLog"), Why(unbuilt, held));
        Assert.DoesNotContain(held.Rejected, e => e.Consequences?.Value.Ledger.InvestmentContributions.Any(c => c.Key.StartsWith("workshop/", StringComparison.Ordinal)) == true);
    }

    /// <summary>
    /// The handoff places only what the plan starts (#3278, #3342, #3744): the door's finish falls past the horizon and the
    /// schedule starts none of its activities, so a selected alternative root that names the door records the wall alone;
    /// the reconcile places the door once the room has started.
    /// </summary>
    [Fact]
    public void ASiteTheScheduleDoesNotStartIsNotPlacedByTheSelection()
    {
        var facts = Credited(Review([Ring(OpKind.WallIn, "WoodLog", 200), Ring(OpKind.DoorIn, "WoodLog", 400, count: 5, available: 50)]));
        var result = Select(facts);
        var candidates = facts.CoupledAlternatives!;
        var door = candidates.Context.PlannedSites.Single(s => s.Site == "planned/room1/door/WoodLog");
        Assert.DoesNotContain(result.Selected.Plan.Allocations, a => door.Activities.Concat(door.Finishes).Contains(a.Activity));
        Assert.DoesNotContain(door.Site, result.Selected.RequestedDevelopment);
        var root = Assert.Single(candidates.Alternatives, c => c.Id.StartsWith("planned/room1/WoodLog/Shell/composition/", StringComparison.Ordinal));
        var record = PlannedRules.PlannedSelectionOf(candidates, result with { Selection = result.Selection with { Selected = [root.Id] } }, facts.PlannedSites)!;
        var (gate, sites) = PlannedRules.PlannedRoomGateOf(record, new PlannedRoom { Id = 1 }, started: false);
        Assert.Equal(PlannedRoomGate.Selected, gate);
        Assert.Equal([OpKind.WallIn], sites.Select(s => s.Op));
    }

    /// <summary>
    /// #3798: room 1 a planned storage room, its facts filled the production way. The needs read carries the map's rain,
    /// area and environmental part as native reads them; the stock is StoreRules.PlannedStoreStock over a stockpile request
    /// whose stores are the departments' declarations and whose loot census holds 50 steel and 50 cloth stranded in open
    /// air; candidate generation carries it into the context. Before the stockpile review has run (a fresh colony) the
    /// room's credit is unassessed, never thrown.
    /// </summary>
    [Fact]
    public void AStorageRoomsStockReachesItsCreditTheProductionWay()
    {
        var region = RegionRules.RectRegion(new RimGovernor.Brain.Policy.Rectangle { X = 5, Z = 5, Width = 3, Height = 3 });
        var room = new PlannedRoom { Id = 1, Role = PlannedRole.Storage, Region = region };
        var stores = StoreRules.DeclareStores(new StoreView { Layout = new LayoutPlan { Rooms = [room] } }).Stores;
        var zone = new StockpileZone { Id = "Zone_w", Role = StockpileRolesConstants.GeneralRole, Cells = [.. region.Cells()], Filter = StockpileFilter.GeneralFilter() };
        static RimGovernor.Brain.Policy.LootItem Loose(string thing, string def, LootDeterioration? deterioration) => new()
        {
            Supply = new StartingSupply { Thing = thing, Definition = def, Cell = new(20, 20) }, Count = 50, SafeToHaul = true, SafetyKnown = true,
            StorageHeadroom = Known(0L), AcceptedBy = ["Zone_w"], Deterioration = deterioration,
        };
        var cloth = new LootDeterioration { MaxHitPoints = 100, RatePerDay = 2, Environmental = true, Outdoors = true };
        var request = new StockpileRequest
        {
            Stores = stores, Zones = [zone],
            Loot = Known(EquatableArray.From(new[] { Loose("Thing_Steel1", "Steel", null), Loose("Thing_Cloth1", "Cloth", cloth) })),
        };
        var facts = Review([Ring(OpKind.WallIn, "WoodLog", 200)]);
        facts = facts with
        {
            PlannedSites = Known(facts.PlannedSites.Value with { Roles = [(1, PlannedRole.Storage)] }),
            PlannedShelters = [room],
            ForecastObserved = facts.ForecastObserved with
            {
                Needs = facts.ForecastObserved.Needs! with { RainRate = 0d, MapArea = 62500, Deterioration = new(.5, .5, 0) },
            },
        };
        var served = new ForecastRoomService(1, "WoodLog", Horizon);

        var fresh = ForecastCoupledCandidates.GenerateCoupledActivities(facts, Horizon)!.Context;
        Assert.True(fresh.Priced(1));
        Assert.Null(fresh.StoreStock);
        // No stockpile request (#3835): the no-stock state, worth nothing.
        Assert.Empty(ForecastStorage.StorageCredit(served, null!, [], fresh, Horizon));

        var context = ForecastCoupledCandidates.GenerateCoupledActivities(facts with { PlannedStoreStock = StoreRules.PlannedStoreStock(request, [room]) }, Horizon)!.Context;
        var stock = context.StoreStock![1];
        Assert.Equal(9, stock.Cells);
        Assert.Equal(["Thing_Cloth1", "Thing_Steel1"], stock.Rows.Select(r => r.Supply.Thing).Order(StringComparer.Ordinal));
        Assert.Contains(new Resource("Cloth"), ForecastValue.ChargedMaterials(context));
        // Unread until the round prices the stock with the charged materials (it throws); then the cloth alone is credited.
        Assert.Throws<RequiredFactException>(() => ForecastStorage.StorageCredit(served, null!, [], context, Horizon));
        var priced = context with { MaterialValues = new Dictionary<Resource, ConstructionMaterialValue> { [new("Cloth")] = new(MaterialValueBasis.Market, .01) } };
        Assert.Equal("Thing_Cloth1", Assert.Single(ForecastStorage.StorageCredit(served, null!, [], priced, Horizon)).Subject);
    }

    /// <summary>
    /// #3651: a room whose role carries a priced value leaves the development tiebreak. Its completed sites add nothing to
    /// the count, yet the selection still exempts its site roots from the thermal-relief gate and the record still places
    /// its sites when they are selected. #3655: a bedroom is priced in production. #3828: an unpriced room's sites count
    /// nothing either, so with no credit the room ties the baseline and is not selected.
    /// </summary>
    [Fact]
    public void APlannedRoomsSitesCountNoDevelopmentYetAPricedRoomsAreEmittedAndNotRefused()
    {
        var facts = Review([Ring(OpKind.WallIn, "WoodLog", 200), Ring(OpKind.DoorIn, "WoodLog", 60)]);
        var unpriced = Select(facts);
        Assert.Equal(0, unpriced.SelectedVector.Development);
        Assert.DoesNotContain(unpriced.Selection.Selected, id => id.StartsWith("planned/", StringComparison.Ordinal));
        var counted = Select(Credited(facts));
        var bedroom = facts with { PlannedSites = Known(facts.PlannedSites.Value with { Roles = [(1, PlannedRole.Bedroom)] }) };
        var generated = ForecastCoupledCandidates.GenerateCoupledActivities(bedroom, Horizon)!.Priced().BuildersRead()!;
        Assert.Equal(PlannedRole.Bedroom, generated.Context.PlannedRoomRoles[1]);
        Assert.True(generated.Context.Priced(1));
        var priced = bedroom with { CoupledAlternatives = generated };
        var result = Select(priced);
        string[] roomSites = ["planned/room1/door/WoodLog", "planned/room1/wall/WoodLog"];
        var planned = result.Rejected.Where(e => e.Commitments.Count > 0 && e.Commitments.All(id => roomSites.Any(s => id.StartsWith(s + "/composition/", StringComparison.Ordinal)))).ToArray();
        Assert.NotEmpty(planned);
        Assert.DoesNotContain(planned, e => e.Reason.StartsWith("construction_without_supported_service_relief", StringComparison.Ordinal));
        Assert.All(planned, e => Assert.Equal(0, e.Consequences!.Development));

        // The credited room's selected roots (its site roots or its alternative root, whichever the search kept).
        var roots = counted.Selection.Selected.Where(id => id.StartsWith("planned/room1/", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(roots);
        var record = PlannedRules.PlannedSelectionOf(priced.CoupledAlternatives, result with
            { Selection = result.Selection with { Selected = roots }, Selected = counted.Selected }, priced.PlannedSites)!;
        var (gate, sites) = PlannedRules.PlannedRoomGateOf(record, new PlannedRoom { Id = 1 }, started: false);
        Assert.Equal(PlannedRoomGate.Selected, gate);
        Assert.Equal([OpKind.DoorIn, OpKind.WallIn], sites.Select(s => s.Op));
    }

    /// <summary>
    /// #3815: generated the production way, from the round's planned rooms and the layout's doors, a corridor (room 1) that
    /// a planned bedroom (room 2) opens on is that bedroom's connector, priced with it: its floors and removals leave the development
    /// count. With no layout read (a fresh colony) there are no connectors and nothing is required.
    /// </summary>
    [Fact]
    public void ACorridorABedroomOpensOnIsPricedWithTheBedroom()
    {
        var facts = Review([Ring(OpKind.WallIn, "WoodLog", 200), Ring(OpKind.DoorIn, "WoodLog", 60)]);
        var corridor = facts with { PlannedSites = Known(facts.PlannedSites.Value with { Roles = [(1, PlannedRole.Circulation)] }) };
        Assert.Empty(ForecastCoupledCandidates.GenerateCoupledActivities(corridor, Horizon)!.Context.Connectors);
        var joined = corridor with
        {
            PlannedShelters = [new PlannedRoom { Id = 1, Role = PlannedRole.Circulation }, new PlannedRoom { Id = 2, Role = PlannedRole.Bedroom }],
            PlannedLinks = [new RoomLink(1, 2, DoorCell)],
        };
        var generated = ForecastCoupledCandidates.GenerateCoupledActivities(joined, Horizon)!.Priced();
        Assert.Equal([2], generated.Context.Connectors[1]);
        Assert.True(generated.Context.Priced(1));
    }

    /// <summary>
    /// #3653: the composed delivery of a priced room's wall consumes its logs, and every evaluated outcome that schedules
    /// it is charged them under material/WoodLog. The same room with no priced role is charged nothing.
    /// </summary>
    [Fact]
    public void APricedRoomsComposedDeliveryIsChargedItsMaterial()
    {
        var facts = Review([Ring(OpKind.WallIn, "WoodLog", 200, count: 20, available: 20)]);
        var bedroom = facts with { PlannedSites = Known(facts.PlannedSites.Value with { Roles = [(1, PlannedRole.Bedroom)] }) };
        var generated = ForecastCoupledCandidates.GenerateCoupledActivities(bedroom, Horizon)!.Priced().BuildersRead()!;
        const double perUnit = 0.001;
        IEnumerable<ForecastConsequenceVector> Vectors(ForecastCoupledSelection result) =>
            result.Rejected.Where(e => e.Consequences is not null).Select(e => e.Consequences!).Append(result.SelectedVector);
        var values = new Dictionary<Resource, ConstructionMaterialValue> { [new("WoodLog")] = new(MaterialValueBasis.Labor, perUnit) };

        var unpriced = Select(bedroom with { CoupledAlternatives = generated with { Context = generated.Context with { RoomCredits = new Dictionary<PlannedRole, RoomCredit>(), MaterialValues = values } } });
        Assert.All(Vectors(unpriced), v => Assert.DoesNotContain(v.Value.Ledger.InvestmentContributions, c => c.Key.StartsWith("material/", StringComparison.Ordinal)));

        var context = generated.Context with { MaterialValues = values };
        Assert.Equal([new Resource("WoodLog")], ForecastValue.ChargedMaterials(context));
        var priced = Select(bedroom with { CoupledAlternatives = generated with { Context = context } });
        var rows = Vectors(priced).SelectMany(v => v.Value.Ledger.InvestmentContributions).Where(c => c.Key == "material/WoodLog").ToArray();
        Assert.NotEmpty(rows);
        // The delivery's composed realization, as the planned site carries it.
        Assert.All(rows, c => Assert.Equal((true, 20d, 20 * perUnit),
            (c.Activity.StartsWith("planned/room1/wall/WoodLog/delivery/stack-WoodLog/composition/", StringComparison.Ordinal), c.Units, c.Value)));
    }

    private static readonly NeedThoughtStage[] BedroomStages =
    [
        new("SleptInBedroom", "slept_in_bedroom", 0, -4, NeedsFixtures.Lasting((double)GameTime.TicksPerDay, false)),
        new("SleptInBedroom", "slept_in_bedroom", 2, 2, NeedsFixtures.Lasting((double)GameTime.TicksPerDay, false)),
        new("SleptInBarracks", "slept_in_barracks", 0, -7, NeedsFixtures.Lasting((double)GameTime.TicksPerDay, false)),
    ];

    /// <summary>
    /// #3657: room 1 a priced bedroom, native's closed-room prediction per ring stuff at <paramref name="stages"/>, the
    /// builder owning bed "bed" in a room of <paramref name="role"/> at <paramref name="stage"/>, its bed thoughts read,
    /// tier 2's horizon 10 days and each stuff charged its per-unit <paramref name="material"/> value. With
    /// <paramref name="inside"/> the room stands and the bed is in it, so the builder is its occupant as the bed's owner;
    /// without, the builder is the bedroom step's occupant.
    /// </summary>
    private static RoundsFacts Bedroom(IEnumerable<PlannedBuildSite> planned, IReadOnlyDictionary<string, int> stages,
        IReadOnlyDictionary<string, double> material, string role, int stage, bool inside)
    {
        var facts = Review(planned);
        var indoors = new NeedCell(true, false, false, false, false);
        var pawns = facts.ForecastObserved.Needs!.Pawns.Select(p => p.Pawn != Pawn ? p : SleepFixtures.Sleeping(p, SleepFixtures.InBed("bed")) with
        {
            ThoughtStages = EquatableArray.From(BedroomStages),
            OwnedBed = "bed",
            Targets = [NeedsFixtures.Service("bed", "rest", 0d, .00005) with { Bed = NeedsFixtures.BedRoom(role, stage, false), Cell = indoors, Location = Interior[4] }],
        });
        facts = facts with
        {
            PlannedSites = Known(facts.PlannedSites.Value with { Roles = [(1, PlannedRole.Bedroom)] }),
            PlannedShelters = [new PlannedRoom { Id = 1, Role = PlannedRole.Bedroom, Region = RegionRules.RectRegion(new RimGovernor.Brain.Policy.Rectangle { X = 5, Z = 5, Width = 3, Height = 3 }) }],
            PlannedRoomPredictions = EquatableArray.From(stages.Select(s => PlannedRoomPredictionTests.Closed("1", s.Key, "Bedroom", 30d, s.Value, 9, 0, false)
                with { Beds = inside ? ["bed"] : [] })),
            MoodBreak = new() { Threshold = .3, Tiers = [new(1, 2), new(1, 3), new(1, 5)] },
            ForecastObserved = facts.ForecastObserved with { Needs = facts.ForecastObserved.Needs with { Pawns = EquatableArray.From(pawns) } },
        };
        var generated = ForecastCoupledCandidates.GenerateCoupledActivities(facts, Horizon)!.Priced().BuildersRead()!;
        return facts with
        {
            CoupledAlternatives = generated with
            {
                Context = generated.Context with
                {
                    BedroomOccupants = inside ? new Dictionary<int, EquatableArray<string>>() : new Dictionary<int, EquatableArray<string>> { [1] = [Pawn] },
                    PlannedBeds = SleepFixtures.Beds(1, 1), Thermal = generated.Context.Thermal ?? SleepFixtures.Outdoors(),
                    InvestmentHorizonDays = 10d,
                    MaterialValues = material.ToDictionary(m => new Resource(m.Key), m => new ConstructionMaterialValue(MaterialValueBasis.Labor, m.Value)),
                },
            },
        };
    }

    /// <summary>
    /// #3802: the kitchen credit's facts as the round fills them. The layout's kitchen (its meals), native's room prediction
    /// and the catalog's filth model reach the context through GenerateCoupledActivities; a kitchen served there is credited.
    /// Its own planned cooking is priced (#3835), so a fresh colony with no bench cooking now is credited alike.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AKitchensCreditReadsItsFactsAsTheRoundFillsThem(bool cooking)
    {
        var facts = Review([Ring(OpKind.WallIn, "WoodLog", 60, count: 10, available: 50)]);
        var pawns = facts.ForecastObserved.Needs!.Pawns.Select(p => p with
        {
            Consciousness = 1d, Moving = 1d, Manipulation = 1d, Sight = 1d, Talking = 1d, FoodPoisoningSeverity = 0d,
        });
        var coupled = facts.ForecastObserved.Coupled! with
        {
            Support = CoupledFixtures.NoSupport with { Demands = [CoupledFixtures.Demand("campfire", "bench", "campfire", new(1, 1), .02, 0d)] },
            FoodConversions = cooking ? [CoupledFixtures.Conversion("rice", Pawn, "campfire", "CookMeal", "Rice", 4, "Meal", 1,
                new(1, 1), new(1, 1), new(1, 1), 0d, 1d, 10d, 1d, 4, 4)
                with { RequiresHarvest = false, Bill = "bill", WorkGiver = "Cook" }] : [],
        };
        facts = facts with
        {
            PlannedSites = Known(facts.PlannedSites.Value with { Roles = [(1, PlannedRole.Kitchen)] }),
            PlannedShelters = [new PlannedRoom { Id = 1, Role = PlannedRole.Kitchen, Meals = 6,
                Region = RegionRules.RectRegion(new RimGovernor.Brain.Policy.Rectangle { X = 5, Z = 5, Width = 3, Height = 3 }) }],
            PlannedRoomPredictions = EquatableArray.From([PlannedRoomPredictionTests.Closed("1", "WoodLog", "Kitchen", 30d, 2, 9, 0, false) with { Cleanliness = -1 }]),
            // The catalog's roomless score: a campfire cooking outdoors.
            Filth = BedStationPriceTests.Filth with { FoodPoisonRoomless = .02 },
            CapacityPenalties = ForecastValueTests.Tables(),
            ForecastObserved = facts.ForecastObserved with { Needs = facts.ForecastObserved.Needs with { Pawns = EquatableArray.From(pawns) }, Coupled = coupled },
        };
        var context = ForecastCoupledCandidates.GenerateCoupledActivities(facts, Horizon)!.Priced().Context with { InvestmentHorizonDays = 10 };
        Assert.Equal(6, context.LayoutRooms[1].Meals);
        Assert.Equal(facts.Filth, context.Filth);
        Assert.Contains(context.Closures, c => c.Room == 1 && c.Prediction.Role == "Kitchen");

        var outcome = new ForecastCoupledOutcome(new([], 0, 0), Known(true), new Dictionary<string, ForecastNeedsResult>
        {
            [Pawn] = new([], default, default, new(new(Pawn, "", new ForecastCapacityWindows.Unread(), 0), []), []),
        }, []) { ServedRooms = [new(1, "WoodLog", 0)] };
        var ledger = ForecastValue.Of(outcome, context, GameTime.TicksPerDay);
        var rows = ledger.InvestmentContributions.Where(r => r.Key == PlannedRoomCredits.Key(PlannedRole.Kitchen, 1, KitchenCredits.Poisoning)).ToArray();
        Assert.True(Assert.Single(rows).Value < 0, string.Join(";", ledger.Findings));
    }

    private static IEnumerable<ForecastInvestmentContribution> BedroomCredits(ForecastCoupledSelection result) =>
        result.SelectedVector.Value.Ledger.InvestmentContributions.Where(c => c.Key.StartsWith("bedroom/", StringComparison.Ordinal));

    /// <summary>
    /// #3657: wood and stone rings both priced, wood cheaper and enough for the room's stage (both predict stage 2): wood is
    /// selected and built in wood, and nothing swaps its walls afterwards, the style no longer naming a wall stuff. The
    /// ring owes walls alone, so one site root closes the room in each stuff.
    /// </summary>
    [Fact]
    public void ACheaperStuffThatReachesTheRoomsStageIsSelectedAndNoWallUpgradeFollows()
    {
        var facts = Bedroom([
            Ring(OpKind.WallIn, "WoodLog", 60, count: 10, available: 50),
            Ring(OpKind.WallIn, "BlocksGranite", 120, count: 10, available: 50)],
            new Dictionary<string, int> { ["WoodLog"] = 2, ["BlocksGranite"] = 2 }, new Dictionary<string, double> { ["WoodLog"] = .001, ["BlocksGranite"] = .003 }, "Barracks", 0, inside: false);
        var result = Select(facts);
        Assert.True(PlannedSelected(result, "planned/room1/wall/WoodLog"), Why(facts, result));
        Assert.False(PlannedSelected(result, "planned/room1/wall/BlocksGranite"));
        Assert.Contains(BedroomCredits(result), c => c.Key == "bedroom/1/" + Pawn && c.Value < 0);

        var record = PlannedRules.PlannedSelectionOf(facts.CoupledAlternatives, result, facts.PlannedSites)!;
        Assert.Equal(("WoodLog", "WoodLog"), PlannedRules.SelectedRingStuff(PlannedRules.PlannedRoomGateOf(record, new PlannedRoom { Id = 1 }, started: false).Sites));
        // Once it stands, the room was offered no swap and the record names none: its walls stay wood.
        Assert.Null(PlannedRules.SelectedWallUpgrade(PlannedRules.PlannedSelectionMemory(new(), record, _ => true), 1));
    }

    /// <summary>
    /// #3647: a new bedroom whose ring owes a wall run and a door, neither closing the room alone, so each site root alone
    /// carries its labor and material and no credit. With the credit (barracks -7 to the bedroom's +2 to tier 2's horizon)
    /// above the cost, both sites are selected together and the room closes; with the wood priced past the credit,
    /// neither is.
    /// </summary>
    [Theory]
    [InlineData(.001, true)]
    [InlineData(1000, false)]
    public void ANewBedroomsWallAndDoorAreSelectedTogetherWhenItsCreditBeatsTheirCost(double wood, bool selected)
    {
        var facts = Bedroom([
            Ring(OpKind.WallIn, "WoodLog", 60, count: 10, available: 50),
            Ring(OpKind.DoorIn, "WoodLog", 30, count: 2, available: 50)],
            new Dictionary<string, int> { ["WoodLog"] = 2 }, new Dictionary<string, double> { ["WoodLog"] = wood }, "Barracks", 0, inside: false);
        var result = Select(facts);
        Assert.True((selected, selected) == (PlannedSelected(result, "planned/room1/wall/WoodLog"), PlannedSelected(result, "planned/room1/door/WoodLog")), Why(facts, result));
        Assert.Equal(selected, BedroomCredits(result).Any(c => c.Key == "bedroom/1/" + Pawn && c.Value < 0));
    }

    /// <summary>
    /// #3657: a standing wood bedroom at stage 0, its owner the builder. A swap to granite predicting stage 2 earns the
    /// owner's mood (+2 against -4) to the horizon, beats the granite and the labor, and is selected; the record keeps it
    /// for the started room and the reconcile swaps to granite. The same swap predicting the stage the room has gains
    /// nothing and is left.
    /// </summary>
    [Theory]
    [InlineData(2, true)]
    [InlineData(0, false)]
    public void AStandingBedroomsWallUpgradeIsSelectedWhenItsStageBeatsItsCost(int predicted, bool selected)
    {
        var swap = Site("planned/room1/wallup/BlocksGranite", OpKind.WallUp, "Wall", "BlocksGranite", WallCells, 120, 10,
            [("stack-BlocksGranite", StackCell("BlocksGranite"), 50)]);
        var facts = Bedroom([swap], new Dictionary<string, int> { ["BlocksGranite"] = predicted }, new Dictionary<string, double> { ["BlocksGranite"] = .003 }, "Bedroom", 0, inside: true);
        Assert.Equal(["planned/room1/BlocksGranite/Shell"], facts.CoupledAlternatives!.Context.PlannedRooms.Select(r => r.Id));
        var result = Select(facts);
        Assert.Equal(selected, PlannedSelected(result, swap.Id));
        if (!selected)
        {
            return;
        }
        Assert.Contains(BedroomCredits(result), c => c.Key == "bedroom/1/" + Pawn && c.Value < 0);
        var record = PlannedRules.PlannedSelectionMemory(new(), PlannedRules.PlannedSelectionOf(facts.CoupledAlternatives, result, facts.PlannedSites), _ => true);
        Assert.Equal("BlocksGranite", PlannedRules.SelectedWallUpgrade(record, 1));
    }

    /// <summary>
    /// #3651: emission reads the selected portfolio, not the development count. A portfolio with no root of the room
    /// completes none of its sites, so the record emits none and the assessed room is held, as before.
    /// </summary>
    [Fact]
    public void APortfolioThatSelectsNoRootOfTheRoomEmitsNoneOfItsSites()
    {
        var facts = Review([Ring(OpKind.WallIn, "WoodLog", 200), Ring(OpKind.DoorIn, "WoodLog", 60)]);
        var candidates = facts.CoupledAlternatives!;
        var without = facts with { CoupledAlternatives = candidates with
            { Alternatives = EquatableArray.From(candidates.Alternatives.Where(c => !c.Id.StartsWith("planned/", StringComparison.Ordinal))) } };
        var result = Select(without);
        Assert.DoesNotContain(result.Selection.Selected, id => id.StartsWith("planned/", StringComparison.Ordinal));
        var record = PlannedRules.PlannedSelectionOf(candidates, result, facts.PlannedSites)!;
        Assert.Equal([1], record.Assessed);
        Assert.Empty(record.Sites);
        Assert.Equal(PlannedRoomGate.Hold, PlannedRules.PlannedRoomGateOf(record, new PlannedRoom { Id = 1 }, started: false).Gate);
    }

    private const double Day = GameTime.TicksPerDay;

    /// <summary>A wall run of room 2, east of room 1, the builder finishes in finish ticks with nothing to deliver.</summary>
    private static PlannedBuildSite RoomTwoWall(double finish) =>
        Site("planned/room2/wall/WoodLog", OpKind.WallIn, "Wall", "WoodLog", [new(14, 4), new(14, 5), new(14, 6)], finish, 0, [], room: 2);

    /// <summary>
    /// #3744: planned rings over a one-day horizon. Every colonist sleeps in a barracks at stage 0 now (bed thoughts read);
    /// each room of <paramref name="occupants"/> is a bedroom native predicts at stage 2 in WoodLog, its occupant moving in.
    /// <paramref name="builders"/> are the projection's builders (none when null), <paramref name="open"/> the open rooms
    /// (observed started and not done); tier 2's horizon is 10 days and wellbeing is weighed a capability-day per mood-day, so a day's
    /// delay of a bedroom is worth more than the ledger's tie resolution.
    /// </summary>
    private static RoundsFacts Rings(IEnumerable<PlannedBuildSite> planned, Dictionary<int, PlannedRole> roles,
        Dictionary<int, string> occupants, int? builders, int[] open, int colonists = 1, IReadOnlyDictionary<string, double>? material = null)
    {
        var facts = Review(planned, horizon: Day, colonists: colonists);
        var indoors = new NeedCell(true, false, false, false, false);
        var pawns = facts.ForecastObserved.Needs!.Pawns.Select(p => SleepFixtures.Sleeping(p, SleepFixtures.InBed("bed-" + p.Pawn)) with
        {
            ThoughtStages = EquatableArray.From(BedroomStages),
            OwnedBed = "bed-" + p.Pawn,
            Targets = [NeedsFixtures.Service("bed-" + p.Pawn, "rest", 0d, .00005) with { Bed = NeedsFixtures.BedRoom("Barracks", 0, false), Cell = indoors, Location = Start }],
        });
        facts = facts with
        {
            PlannedSites = Known(facts.PlannedSites.Value with { Roles = EquatableArray.From(roles.Select(r => (r.Key, r.Value))) }),
            PlannedShelters = [.. occupants.Keys.Select(room => new PlannedRoom { Id = room, Role = roles[room],
                Region = RegionRules.RectRegion(new RimGovernor.Brain.Policy.Rectangle { X = 5 + (10 * (room - 1)), Z = 5, Width = 3, Height = 3 }) })],
            PlannedRoomPredictions = EquatableArray.From(occupants.Keys.Select(room => PlannedRoomPredictionTests.Closed(
                room.ToString(System.Globalization.CultureInfo.InvariantCulture), "WoodLog", "Bedroom", 30d, 2, 9, 0, false))),
            MoodBreak = new() { Threshold = .3, Tiers = [new(1, 2), new(1, 3), new(1, 5)] },
            ForecastObserved = facts.ForecastObserved with { Needs = facts.ForecastObserved.Needs with { Pawns = EquatableArray.From(pawns) } },
        };
        var generated = ForecastCoupledCandidates.GenerateCoupledActivities(facts, Day)!.Priced().BuildersRead()!;
        return facts with
        {
            CoupledAlternatives = generated with
            {
                Context = generated.Context with
                {
                    BedroomOccupants = occupants.ToDictionary(o => o.Key, o => EquatableArray.From([o.Value])),
                    PlannedBeds = SleepFixtures.Beds(1, [.. occupants.Keys]), Thermal = generated.Context.Thermal ?? SleepFixtures.Outdoors(),
                    InvestmentHorizonDays = 10d,
                    PlannedBuilders = builders ?? 0,
                    OpenRooms = open.ToHashSet(),
                    Value = generated.Context.Value with { WellbeingWeight = 1 },
                    MaterialValues = (material ?? new Dictionary<string, double>()).ToDictionary(m => new Resource(m.Key), m => new ConstructionMaterialValue(MaterialValueBasis.Labor, m.Value)),
                },
            },
        };
    }

    private static ForecastRoomService ClosureOf(ForecastCoupledOutcome outcome, int room) => Assert.Single(outcome.ServedRooms, c => c.Room == room);

    /// <summary>
    /// #3744 acceptance (a): one builder, bedroom ring A (room 1) started with two days of ring work left, which no
    /// schedule of the day finishes, and a workshop ring B (room 2, an unpriced role) a third of a day long. A's closure is
    /// projected past the horizon at its remaining work over the builder; opening B would push it later by B's work, which
    /// A's credit pays, so B is not opened. #3828: B's sites count no development, so unprojected (A's ring carrying no
    /// credit for B to delay) and with A closed (its ring owes nothing) B, with no credit of its own, stays closed too.
    /// </summary>
    [Fact]
    public void OneBuilderLeavesAnUnpricedRingClosedWhileABedroomRingIsOpenAndAfterTheBedroomCloses()
    {
        var roles = new Dictionary<int, PlannedRole> { [1] = PlannedRole.Bedroom, [2] = PlannedRole.Workshop };
        var bedroom = new Dictionary<int, string> { [1] = Pawn };
        var b = RoomTwoWall(Day / 3);
        var facts = Rings([Ring(OpKind.WallIn, "WoodLog", 2 * Day), b], roles, bedroom, builders: 1, open: [1]);
        var result = Select(facts, Day);
        Assert.False(PlannedSelected(result, b.Id), Why(facts, result));
        Assert.Equal(2 * Day, ClosureOf(result.Baseline, 1).ServedAt);
        Assert.Contains(BedroomCredits(result), c => c.Key == "bedroom/1/" + Pawn && c.Value < 0);
        var unprojected = Rings([Ring(OpKind.WallIn, "WoodLog", 2 * Day), b], roles, bedroom, builders: null, open: [1]);
        Assert.False(PlannedSelected(Select(unprojected, Day), b.Id));

        var closed = Rings([b], roles, bedroom, builders: 1, open: [1]);
        var held = Select(closed, Day);
        Assert.False(PlannedSelected(held, b.Id), Why(closed, held));
        Assert.Equal(held.BaselineVector.Development, held.SelectedVector.Development);
    }

    /// <summary>
    /// #3744 acceptance (b): three builders, bedroom ring A (room 1) started with six days left and a second bedroom ring B
    /// (room 2) a third of a day long, colonist-01 moving in. Opening B delays A's projected closure by a third of B's work,
    /// and B's own credit, from its closure inside the day to tier 2's horizon, outweighs that delay: B opens beside A.
    /// </summary>
    [Fact]
    public void ThreeBuildersOpenASecondBedroomBesideAnOpenOneWhenItsCreditOutweighsAThirdOfItsWork()
    {
        var roles = new Dictionary<int, PlannedRole> { [1] = PlannedRole.Bedroom, [2] = PlannedRole.Bedroom };
        var b = RoomTwoWall(Day / 3);
        var facts = Rings([Ring(OpKind.WallIn, "WoodLog", 6 * Day), b], roles,
            new Dictionary<int, string> { [1] = Pawn, [2] = "colonist-01" }, builders: 3, open: [1], colonists: 2);
        var result = Select(facts, Day);
        Assert.True(PlannedSelected(result, b.Id), Why(facts, result));
        var work = facts.CoupledAlternatives!.Context.OwedWork[b.Id];
        Assert.Equal(2 * Day, ClosureOf(result.Baseline, 1).ServedAt);
        Assert.Equal(2 * Day + (work / 3), ClosureOf(result.Selected, 1).ServedAt, 6);
        Assert.True(ClosureOf(result.Selected, 2).ServedAt < Day);
        Assert.Contains(BedroomCredits(result), c => c.Key == "bedroom/2/colonist-01" && c.Value < 0);
    }

    /// <summary>
    /// #3744 acceptance (c): one builder, a new bedroom whose ring takes three builder-days: its wall half a day (the logs
    /// hauled from their stack), its door two and a half. No schedule of the day closes it, yet the wall the day builds
    /// opens the ring, whose closure is projected at its whole ring work over the builder, and the credit from then to the
    /// horizon beats the logs: the room is assessed and its wall, the site the schedule starts, is placed. Without
    /// builders nothing is projected and the wall alone, closing nothing, is never selected.
    /// </summary>
    [Fact]
    public void ABedroomWhoseRingTakesThreeBuilderDaysIsAssessedAndItsFirstSitesPlaced()
    {
        var roles = new Dictionary<int, PlannedRole> { [1] = PlannedRole.Bedroom };
        var bedroom = new Dictionary<int, string> { [1] = Pawn };
        PlannedBuildSite[] ring = [Ring(OpKind.WallIn, "WoodLog", Day / 2, count: 10, available: 50), Ring(OpKind.DoorIn, "WoodLog", 2.5 * Day)];
        var wood = new Dictionary<string, double> { ["WoodLog"] = .001 };
        var facts = Rings(ring, roles, bedroom, builders: 1, open: [], material: wood);
        var context = facts.CoupledAlternatives!.Context;
        Assert.Equal([1], context.PlannedRooms.Select(a => a.Room).Distinct());
        var result = Select(facts, Day);
        Assert.True(PlannedSelected(result, ring[0].Id), Why(facts, result));
        var work = context.PlannedSites.Sum(s => context.OwedWork[s.Site]);
        Assert.True(work > 3 * Day);
        Assert.Equal(work, ClosureOf(result.Selected, 1).ServedAt, 6);
        Assert.DoesNotContain(result.Selected.Findings, f => f.Reason == "planned_room_effect_unassessed");
        Assert.Contains(BedroomCredits(result), c => c.Key == "bedroom/1/" + Pawn && c.Value < 0);
        var record = PlannedRules.PlannedSelectionOf(facts.CoupledAlternatives, result, facts.PlannedSites)!;
        Assert.Equal([1], record.Assessed);
        Assert.Equal([OpKind.WallIn], PlannedRules.PlannedRoomGateOf(record, new PlannedRoom { Id = 1 }, started: false).Sites.Select(s => s.Op));

        var unknown = Rings(ring, roles, bedroom, builders: null, open: [], material: wood);
        Assert.False(PlannedSelected(Select(unknown, Day), ring[0].Id));
    }

    /// <summary>A wall run of unpriced room <paramref name="room"/> (2 to 4), east of room 1, finished in finish ticks with nothing to deliver.</summary>
    private static PlannedBuildSite RoomWall(int room, double finish) =>
        Site(FormattableString.Invariant($"planned/room{room}/wall/WoodLog"), OpKind.WallIn, "Wall", "WoodLog",
            [.. Enumerable.Range(4, 3).Select(z => new Cell(4 + (10 * (room - 1)), z))], finish, 0, [], room: room);

    /// <summary>
    /// #3828 (option A): the first rounds opened three unpriced rings together over the barracks, each tying on the ledger
    /// and winning on its sites as development. No planned room's unit is development now. One builder, a new bedroom
    /// (room 1) whose short ring its occupant's credit pays, and three unpriced workshop rings (rooms 2 to 4) the day could
    /// also build: the bedroom is selected and none of the three rings. Alone, the three rings do not beat the baseline.
    /// </summary>
    [Fact]
    public void ThreeUnpricedRingsDoNotBeatAPricedBedroomAndAloneDoNotBeatTheBaseline()
    {
        var roles = new Dictionary<int, PlannedRole> { [1] = PlannedRole.Bedroom, [2] = PlannedRole.Workshop, [3] = PlannedRole.Workshop, [4] = PlannedRole.Workshop };
        var bedroom = new Dictionary<int, string> { [1] = Pawn };
        PlannedBuildSite[] rings = [RoomWall(2, Day / 16), RoomWall(3, Day / 16), RoomWall(4, Day / 16)];
        PlannedBuildSite[] ring = [Ring(OpKind.WallIn, "WoodLog", Day / 8), Ring(OpKind.DoorIn, "WoodLog", Day / 16)];
        var facts = Rings([.. ring, .. rings], roles, bedroom, builders: 1, open: []);
        Assert.True(facts.CoupledAlternatives!.Context.Priced(1));
        Assert.All(rings, s => Assert.False(facts.CoupledAlternatives.Context.Priced(s.Room)));
        var result = Select(facts, Day);
        Assert.True(PlannedSelected(result, ring[0].Id), Why(facts, result));
        Assert.Contains(BedroomCredits(result), c => c.Key == "bedroom/1/" + Pawn && c.Value < 0);
        Assert.All(rings, s => Assert.False(PlannedSelected(result, s.Id), Why(facts, result)));
        Assert.Equal(result.BaselineVector.Development, result.SelectedVector.Development);

        var unpriced = Rings(rings, roles, bedroom, builders: 1, open: []);
        var alone = Select(unpriced, Day);
        Assert.DoesNotContain(alone.Selection.Selected, id => id.StartsWith("planned/", StringComparison.Ordinal));
        Assert.Equal(alone.BaselineVector.Development, alone.SelectedVector.Development);
    }
}
