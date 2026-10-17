using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using RimGovernor.Contracts.Wire.Common;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using static RimGovernor.Brain.Policy.PolicyFunctions;

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
        { Identity = new() { ColonyId = "colony", LoadToken = "load", MapId = 0 }, Tick = 100 };
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
        IReadOnlyList<(string Id, Cell At, double Available)> stacks, PlannedSiteVerdict verdict = PlannedSiteVerdict.Feasible)
    {
        var at = cells[0];
        var reach = Ticks(Start, Nearest(cells, Start));
        var rows = count == 0
            ? [new ConstructionActivityOption(id, def, at, Pawn, "", "", at, 0, 0, "Finish", default, default, Known(finish)) { SiteApproachTicks = Known(reach) }]
            : stacks.Select(s => new ConstructionActivityOption(id, def, at, Pawn, stuff, s.Id, s.At, count, 75, "Finish", Known(Ticks(Start, s.At)),
                Known(Ticks(s.At, Nearest(cells, s.At))), Known(finish)) { SiteApproachTicks = Known(reach), AvailableInput = Known(s.Available) }).ToArray();
        return new PlannedBuildSite(id, 1, op, def, op == OpKind.FloorIn ? "" : stuff, Rotation.North, EquatableArray.From(cells))
        {
            Verdict = verdict, CanPlace = Known(true), Options = EquatableArray.From(rows),
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
    /// The planned-site facts with native routes between the sites (#3313): the production probes (PlannedRouteBatches),
    /// answered per probe at 2 ticks a cell, joined by JoinPlannedRoutes. Without routes, the facts native gave before #3313.
    /// </summary>
    private static PlannedSiteFacts PlannedFacts(IEnumerable<PlannedBuildSite> planned, bool routes)
    {
        var facts = new PlannedSiteFacts { Sites = EquatableArray.From(planned) };
        if (!routes) return facts;
        var (batches, findings) = PlannedRouteBatches(facts.Sites);
        return JoinPlannedRoutes(facts, findings, batches.Select(probes => (probes, (ConstructionActivityRead?)new ConstructionActivityRead
        {
            Known = true,
            Routes = EquatableArray.From(probes.Select(p =>
            {
                var origin = p.FromStart ? Start : p.From;
                return new CoupledRoute(Pawn, origin, p.To, Known(Ticks(origin, p.Reach.Count == 0 ? p.To : Nearest(p.Reach, origin)))) { FromStart = p.FromStart };
            })),
        })));
    }

    private static RoundsFacts Review(IEnumerable<PlannedBuildSite> planned, double level = 1, double fall = 0, double horizon = Horizon,
        bool routes = true, double roofCellWork = 0)
    {
        var facts = WorkResourceTestFacts.Create(Pawn);
        facts = facts with
        {
            // The colony holds what the stacks hold.
            Resources = Known(EquatableArray.From(planned.SelectMany(s => s.Options).Where(o => o.Input.Length > 0).DistinctBy(o => o.Input)
                .GroupBy(o => o.Resource).Select(g => new RimGovernor.Brain.Policy.Amount(new Resource(g.Key), (long)g.Sum(o => o.AvailableInput.Value))))),
            PlannedSites = Known(PlannedFacts(planned, routes)),
            ForecastObserved = facts.ForecastObserved with
            {
                Needs = new() { Tick = 100, Pawns = [new()
                {
                    Pawn = "pawn", State = "eligible", Mood = Known(.5), MoodTarget = Known(.5),
                    MoodUnclampedTarget = Known(.5), LocalHour = Known(6d), Awake = Known(true),
                    MoodRisePerTick = Known(.12 / GameTime.TicksPerHour), MoodFallPerTick = Known(.08 / GameTime.TicksPerHour),
                    MoodIntervalTicks = Known(150d), MoodFreezesWhileAsleep = Known(true), BreakThreshold = Known(.3),
                    ConditionalThoughts = Known<EquatableArray<NeedConditionalThought>>([]),
                    Memories = Known<EquatableArray<NeedMemory>>([]),
                    Needs = [new("food", Known(level), Known(fall), Known(.3), Known(1d)),
                        new("rest", Known(1d), Known(0d), Known(.3), Known(1d))],
                }] },
                Coupled = new()
                {
                    Tick = 100,
                    Food = new() { Context = Context, Known = true, Options = EquatableArray.From(Plants.Select(source =>
                        new FoodActivityOption("pawn", source, "Rice", new(1, 1), new(1, 1), "Harvest", 2, 10, 10)
                        { ApproachTicks = Known(0d), DeliveryTicks = Known(10d), HarvestAllowed = Known(true),
                            SourceCount = Known(2d), NutritionPerUnit = Known(.2) })) },
                    FoodUses = [new("pawn", "Rice", new(1, 1), Known(true), Known(.2), Known(10d), 2, Known(1000d))],
                    Construction = new() { Context = Context, Known = true },
                    // A roof is worked cell by cell at the coupled read's RoofCellWorkTicks.
                    Thermal = roofCellWork > 0 ? new() { Workers = [new(Pawn, Known(roofCellWork))] } : null,
                },
            },
        };
        facts = ReviewWorkResources(facts, Known<EquatableArray<ProductionBatch>>([]), new([]));
        return facts with { CoupledAlternatives = GenerateCoupledActivities(facts, horizon) };
    }

    private static ForecastCoupledSelection Select(RoundsFacts facts, double horizon = Horizon)
    {
        var request = CoupledForecastRequestOf(new("world/map/load", 100), horizon / GameTime.TicksPerDay,
            facts, new(), new() { Work = ForecastWorkInputsOf(facts) }, [], new());
        return CompareActivityOutcomes(request, facts.CoupledAlternatives!, 16384);
    }

    private static string Why(RoundsFacts facts, ForecastCoupledSelection result) =>
        string.Join(";", facts.CoupledAlternatives!.Findings.Concat(result.Selection.Findings));

    private static bool PlannedSelected(ForecastCoupledSelection result, string site) =>
        result.Selection.Selected.Any(id => id.StartsWith(site + "/composition/", StringComparison.Ordinal));

    [Fact]
    public void AnIdleBuilderBuildsTheRoomAndTheRoomCompletesInTheHorizon()
    {
        var facts = Review([Ring(OpKind.WallIn, "WoodLog", 200), Ring(OpKind.DoorIn, "WoodLog", 60)]);
        var candidates = facts.CoupledAlternatives!;
        Assert.Equal(["planned/room1/door/WoodLog", "planned/room1/wall/WoodLog"], candidates.Context.PlannedSites.Select(s => s.Site).Order(StringComparer.Ordinal));
        Assert.Equal(["planned/room1/WoodLog/Shell"], candidates.Context.PlannedRooms.Select(r => r.Id));

        var result = Select(facts);
        Assert.True(PlannedSelected(result, "planned/room1/wall/WoodLog"), Why(facts, result));
        Assert.True(PlannedSelected(result, "planned/room1/door/WoodLog"), Why(facts, result));
        var room = Assert.Single(result.Selected.PlannedRooms);
        Assert.Equal(["planned/room1/WoodLog/Shell"], room.Complete);
        Assert.True(result.SelectedVector.Development > result.BaselineVector.Development);

        // Nothing binds a planned site before placement (#3278): the selection starts no order.
        Assert.Null(StartCoupledActivity(result, candidates, 1, "planned-test", 100));
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
        var facts = Review(room, horizon: day, roofCellWork: 65);
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

        var unrouted = Review(room, horizon: day, routes: false, roofCellWork: 65);
        Assert.DoesNotContain(Select(unrouted, day).Selected.PlannedRooms, r => r.Complete.Count > 0);
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
        var facts = Review([
            Ring(OpKind.WallIn, "WoodLog", 60, count: 10, available: 4), Ring(OpKind.DoorIn, "WoodLog", 30, count: 5, available: 4),
            Ring(OpKind.WallIn, "BlocksGranite", 90, count: 10, available: 50), Ring(OpKind.DoorIn, "BlocksGranite", 40, count: 5, available: 50)]);
        var result = Select(facts);
        Assert.True(PlannedSelected(result, "planned/room1/wall/BlocksGranite"), Why(facts, result));
        Assert.False(PlannedSelected(result, "planned/room1/wall/WoodLog"));
        Assert.Contains(result.Selected.PlannedRooms.SelectMany(r => r.Complete), id => id.Contains("BlocksGranite", StringComparison.Ordinal));
    }

    [Fact]
    public void EqualDevelopmentForLessLaborPicksTheCheaperStuff()
    {
        var facts = Review([
            Ring(OpKind.WallIn, "WoodLog", 60, count: 10, available: 50), Ring(OpKind.DoorIn, "WoodLog", 30, count: 5, available: 50),
            Ring(OpKind.WallIn, "BlocksGranite", 120, count: 10, available: 50), Ring(OpKind.DoorIn, "BlocksGranite", 60, count: 5, available: 50)]);
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

    /// <summary>
    /// The handoff (#3278): the selection record names the selected sites with their stuff and the assessed room, so the
    /// reconcile places the requested room without hunger and holds it while the one cook-builder must eat first.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UrgentHungerHoldsTheRequestedRoomThatRoundAndWithoutHungerItIsStarted(bool hungry)
    {
        var facts = Review([Ring(OpKind.WallIn, "WoodLog", 270), Ring(OpKind.DoorIn, "WoodLog", 270)], level: hungry ? .2 : 1, fall: hungry ? .003 : 0);
        var result = Select(facts);
        var record = PlannedSelectionOf(facts.CoupledAlternatives, result, facts.PlannedSites, 100);
        Assert.NotNull(record);
        Assert.Equal([1], record.Assessed);
        var (gate, sites) = PlannedRoomGateOf(record, 1, started: false);
        if (hungry)
        {
            Assert.Equal(PlannedRoomGate.Hold, gate);
            Assert.Empty(sites);
            return;
        }
        Assert.Equal(PlannedRoomGate.Selected, gate);
        Assert.NotEmpty(sites);
        Assert.All(sites, s => Assert.Equal((1, "WoodLog"), (s.Room, s.Stuff)));
        Assert.All(sites, s => Assert.True(PlannedSelected(result, s.Id), Why(facts, result)));
        // A started room is placed as before, selected or not.
        Assert.Equal(PlannedRoomGate.Place, PlannedRoomGateOf(record, 1, started: true).Gate);
    }

    /// <summary>
    /// The record holds the sites the selected plan completes (#3342): the room alternative root, or the site roots its
    /// schedule equals, give one record of its scope's sites.
    /// </summary>
    [Fact]
    public void ASelectedAlternativeRootSelectsAllItsSitesAndAnUnassessedRoomPlacesAsBefore()
    {
        var facts = Review([Ring(OpKind.WallIn, "WoodLog", 200), Ring(OpKind.DoorIn, "WoodLog", 60)]);
        var candidates = facts.CoupledAlternatives!;
        var root = Assert.Single(candidates.Alternatives, c => c.Id.StartsWith("planned/room1/WoodLog/Shell/composition/", StringComparison.Ordinal));
        var result = Select(facts);
        Assert.Equal(["planned/room1/door/WoodLog", "planned/room1/wall/WoodLog"], result.Selected.RequestedDevelopment);
        PlannedSelection Record(params string[] roots) =>
            PlannedSelectionOf(candidates, result with { Selection = result.Selection with { Selected = roots } }, facts.PlannedSites, 100)!;
        var record = Record(root.Id);
        Assert.Equal(["planned/room1/door/WoodLog", "planned/room1/wall/WoodLog"], record.Sites.Select(s => s.Id));
        Assert.Equal([OpKind.DoorIn, OpKind.WallIn], record.Sites.Select(s => s.Op));
        Assert.Equal(record, Record("planned/room1/door/WoodLog", "planned/room1/wall/WoodLog"));
        Assert.Equal(PlannedRoomGate.Place, PlannedRoomGateOf(record, 2, started: false).Gate);
        // A review that compared nothing leaves no record of its own.
        Assert.Null(PlannedSelectionOf(candidates, null, facts.PlannedSites, 100));
    }

    [Fact]
    public void ARoomLeftPartialInTheHorizonIsNamedUnassessed()
    {
        // The wall alone fits the horizon; the door does not, so the room's whole-room effect is unassessed, not zero.
        var facts = Review([Ring(OpKind.WallIn, "WoodLog", 200), Ring(OpKind.DoorIn, "WoodLog", 400)]);
        var result = Select(facts);
        Assert.True(PlannedSelected(result, "planned/room1/wall/WoodLog"), Why(facts, result));
        var room = Assert.Single(result.Selected.PlannedRooms);
        Assert.Equal(["planned/room1/wall/WoodLog"], room.CompletedSites);
        Assert.Empty(room.Complete);
        Assert.Contains(result.Selected.Findings, f => f.Reason == "planned_room_effect_unassessed" && f.Commitment == "planned/room1");
    }

    /// <summary>
    /// The handoff places only what the plan completes (#3278, #3342): the door's finish falls past the horizon (its
    /// delivery may be scheduled, its finish is not completed), so a selected alternative root that names the door
    /// records the wall alone; the reconcile places the door once the room has started.
    /// </summary>
    [Fact]
    public void ASiteNotFinishedInTheHorizonIsNotPlacedByTheSelection()
    {
        var facts = Review([Ring(OpKind.WallIn, "WoodLog", 200), Ring(OpKind.DoorIn, "WoodLog", 400, count: 5, available: 50)]);
        var result = Select(facts);
        var candidates = facts.CoupledAlternatives!;
        var door = candidates.Context.PlannedSites.Single(s => s.Site == "planned/room1/door/WoodLog");
        Assert.DoesNotContain(result.Selected.Plan.Allocations, a => door.Finishes.Contains(a.Activity) && a.End <= Horizon);
        Assert.DoesNotContain(door.Site, result.Selected.RequestedDevelopment);
        var root = Assert.Single(candidates.Alternatives, c => c.Id.StartsWith("planned/room1/WoodLog/Shell/composition/", StringComparison.Ordinal));
        var record = PlannedSelectionOf(candidates, result with { Selection = result.Selection with { Selected = [root.Id] } }, facts.PlannedSites, 100)!;
        var (gate, sites) = PlannedRoomGateOf(record, 1, started: false);
        Assert.Equal(PlannedRoomGate.Selected, gate);
        Assert.Equal(["planned/room1/wall/WoodLog"], sites.Select(s => s.Id));
    }
}
