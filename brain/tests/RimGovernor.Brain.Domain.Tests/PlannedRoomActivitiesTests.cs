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
        IReadOnlyList<(string Id, Cell At, double Available)> stacks, PlannedSiteVerdict verdict = PlannedSiteVerdict.Feasible, int room = 1)
    {
        var at = cells[0];
        var reach = Ticks(Start, Nearest(cells, Start));
        var rows = count == 0
            ? [new ConstructionActivityOption(id, def, at, Pawn, "", "", at, 0, 0, "Finish", default, default, Known(finish)) { SiteApproachTicks = Known(reach) }]
            : stacks.Select(s => new ConstructionActivityOption(id, def, at, Pawn, stuff, s.Id, s.At, count, 75, "Finish", Known(Ticks(Start, s.At)),
                Known(Ticks(s.At, Nearest(cells, s.At))), Known(finish)) { SiteApproachTicks = Known(reach), AvailableInput = Known(s.Available) }).ToArray();
        return new PlannedBuildSite(id, room, op, def, op == OpKind.FloorIn ? "" : stuff, Rotation.North, EquatableArray.From(cells))
        {
            Verdict = verdict, Options = EquatableArray.From(rows),
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
        return PlannedRules.JoinPlannedRoutes(facts, probes, new ConstructionActivityRead
        {
            Known = true,
            Routes = EquatableArray.From(probes.Select(p =>
            {
                var origin = p.FromStart ? Start : p.From;
                return new CoupledRoute(Pawn, origin, p.To, Known(Ticks(origin, p.Reach.Count == 0 ? p.To : Nearest(p.Reach, origin)))) { FromStart = p.FromStart };
            })),
        });
    }

    private static NeedsForecastPawn Idle(string pawn, double level = 1, double fall = 0) => new()
    {
        Pawn = pawn, State = "eligible", Mood = Known(.5), MoodTarget = Known(.5),
        MoodUnclampedTarget = Known(.5), LocalHour = Known(6d), Awake = Known(true),
        MoodRisePerTick = Known(.12 / GameTime.TicksPerHour), MoodFallPerTick = Known(.08 / GameTime.TicksPerHour),
        MoodIntervalTicks = Known(150d), MoodFreezesWhileAsleep = Known(true), BreakThreshold = Known(.3),
        ConditionalThoughts = Known<EquatableArray<NeedConditionalThought>>([]),
        Memories = Known<EquatableArray<NeedMemory>>([]),
        Needs = [new("food", Known(level), Known(fall), Known(.3)),
            new("rest", Known(1d), Known(0d), Known(.3))],
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
                .GroupBy(o => o.Resource).Select(g => new RimGovernor.Brain.Policy.Amount(new Resource(g.Key), (long)g.Sum(o => o.AvailableInput.Value))))),
            PlannedSites = Known(PlannedFacts(planned, routes)),
            ForecastObserved = facts.ForecastObserved with
            {
                Needs = new() { Tick = 100, Pawns = EquatableArray.From(others.Select(p => Idle(p)).Append(Idle(Pawn, level, fall))) },
                Coupled = new()
                {
                    Tick = 100,
                    Food = new() { Options = EquatableArray.From(Plants.Select(source =>
                        new FoodActivityOption("pawn", source, "Rice", new(1, 1), new(1, 1), "Harvest", 2, 10)
                        { ApproachTicks = Known(0d), DeliveryTicks = Known(10d), HarvestAllowed = Known(true),
                            SourceCount = Known(2d) })) },
                    FoodUses = [new("pawn", "Rice", new(1, 1), Known(true), Known(.2), Known(10d), 2, Known(1000d))],
                    Construction = new() { Context = Context, Known = true },
                    // A roof is worked cell by cell at the coupled read's RoofCellWorkTicks.
                    Thermal = roofCellWork > 0 ? new() { Workers = [new(Pawn, Known(roofCellWork))] } : null,
                },
            },
        };
        facts = WorkRules.ReviewWorkResources(facts, Known<EquatableArray<ProductionBatch>>([]), new([]));
        return facts with { CoupledAlternatives = ForecastCoupledCandidates.GenerateCoupledActivities(facts, horizon)?.Priced() };
    }

    private static ForecastCoupledSelection Select(RoundsFacts facts, double horizon = Horizon, ForecastSearchBudget? budget = null)
    {
        var request = ForecastCoupled.CoupledForecastRequestOf(new("world/map/load", 100), horizon / GameTime.TicksPerDay,
            facts,  new() { Work = ForecastWorkInputs.ForecastWorkInputsOf(facts) }, [], new());
        return ForecastCoupledSelection.CompareActivityOutcomes(request, facts.CoupledAlternatives!, budget);
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

    /// <summary>
    /// Twenty colonists (#3341): every free colonist is in the native needs and planned-site reads, so the builder's
    /// planned rows reach the forecast problem though it is the twentieth colonist by id, and it builds the room.
    /// </summary>
    [Fact]
    public void TheTwentiethColonistBuildsThePlannedRoom()
    {
        var facts = Review([Ring(OpKind.WallIn, "WoodLog", 200), Ring(OpKind.DoorIn, "WoodLog", 60)], colonists: 20);
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
        var facts = Review([
            Ring(OpKind.WallIn, "WoodLog", 60, count: 10, available: 4), Ring(OpKind.DoorIn, "WoodLog", 30, count: 5, available: 4),
            Ring(OpKind.WallIn, "BlocksGranite", 90, count: 10, available: 50), Ring(OpKind.DoorIn, "BlocksGranite", 40, count: 5, available: 50)]);
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
        var facts = Review([
            Site("planned/room1/wall/WoodLog", OpKind.WallIn, "Wall", "WoodLog", WallCells, 60, 75, set),
            Site("planned/room1/door/WoodLog", OpKind.DoorIn, "Door", "WoodLog", [DoorCell], 30, 25, [("wood-c", new(10, 4), 30)])]);
        var result = Select(facts);
        Assert.Equal(["planned/room1/WoodLog/Shell"], Assert.Single(result.Selected.PlannedRooms).Complete);
        string[] deliveries = ["planned/room1/wall/WoodLog/delivery/wood-a", "planned/room1/wall/WoodLog/delivery/wood-b"];
        Assert.All(deliveries, delivery => Assert.Contains(result.Selected.Plan.Allocations, a => a.Activity.StartsWith(delivery + "/composition/", StringComparison.Ordinal)));
        Assert.Equal((35, 40), (ConstructionMethodCandidates.StackShares(facts.PlannedSites.Value.Sites.Single(s => s.Op == OpKind.WallIn).Options, _ => 0)!["wood-a"],
            ConstructionMethodCandidates.StackShares(facts.PlannedSites.Value.Sites.Single(s => s.Op == OpKind.WallIn).Options, _ => 0)!["wood-b"]));
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
        var clearing = new PlannedSiteClearing(clearingId, PlannedClearingKind.Deconstruct)
        {
            Options = [new ConstructionActivityOption(clearingId, "Wall", cells[0], Pawn, "Wall", ruin, cells[0], 0, 0, "Deconstruct", default, default, Known(50d))
                { SiteApproachTicks = Known(Ticks(Start, cells[0])) }],
            Cells = EquatableArray.From(cells),
        };
        var facts = Review([Ring(OpKind.WallIn, "WoodLog", 120) with { Clearing = [clearing] }, Ring(OpKind.DoorIn, "WoodLog", 60)]);
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
        var clearing = new PlannedSiteClearing(clearingId, PlannedClearingKind.Deconstruct)
        {
            Options = [new ConstructionActivityOption(clearingId, "Wall", at, Pawn, "Door", ruin, at, 0, 0, "Deconstruct", default, default, Known(50d))
                { SiteApproachTicks = Known(Ticks(Start, at)) }],
        };
        var facts = Review([Ring(OpKind.WallIn, "WoodLog", 120) with { Clearing = [clearing] }, Ring(OpKind.DoorIn, "WoodLog", 60)]);
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
        var unnamed = Review([Ring(OpKind.WallIn, "WoodLog", 120) with { Clearing = [clearing with { Options = [clearing.Options[0] with { Input = "" }] }] },
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
        var clearing = new PlannedSiteClearing(clearingId, PlannedClearingKind.Cut)
        {
            Options = [new ConstructionActivityOption(clearingId, "Wall", at, Pawn, "", "", at, 0, 0, "PlantsCut", default, default, Known(50d))
                { SiteApproachTicks = Known(Ticks(Start, at)) }],
            Targets = EquatableArray.From(trees),
        };
        var facts = Review([Ring(OpKind.WallIn, "WoodLog", 120) with { Clearing = [clearing] }, Ring(OpKind.DoorIn, "WoodLog", 60)]);
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

        var unnamed = Review([Ring(OpKind.WallIn, "WoodLog", 120) with { Clearing = [clearing with { Targets = default }] }, Ring(OpKind.DoorIn, "WoodLog", 60)]);
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
        var clearing = new PlannedSiteClearing(clearingId, PlannedClearingKind.Mine)
        {
            Options = [new ConstructionActivityOption(clearingId, "Wall", at, Pawn, "", "", at, 0, 0, "Mine", default, default, Known(50d))
                { SiteApproachTicks = Known(Ticks(Start, at)) }],
            Targets = EquatableArray.From(rocks),
        };
        var facts = Review([Ring(OpKind.WallIn, "WoodLog", 120) with { Clearing = [clearing] }, Ring(OpKind.DoorIn, "WoodLog", 60)]);
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

        var unnamed = Review([Ring(OpKind.WallIn, "WoodLog", 120) with { Clearing = [clearing with { Targets = default }] }, Ring(OpKind.DoorIn, "WoodLog", 60)]);
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
        var clearing = new PlannedSiteClearing(clearingId, PlannedClearingKind.Haul)
        {
            Options = [new ConstructionActivityOption(clearingId, "Wall", store, Pawn, "Steel", stack, at, count, 75, "HaulGeneral", Known(Ticks(Start, at)),
                Known(Ticks(at, store)), Known(0d)) { SiteApproachTicks = Known(Ticks(Start, at)), AvailableInput = Known((double)count) }],
        };
        var facts = Review([Ring(OpKind.WallIn, "WoodLog", 120) with { Clearing = [clearing] }, Ring(OpKind.DoorIn, "WoodLog", 60)]);
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
        var facts = Review([Ring(OpKind.WallIn, "WoodLog", 270), Ring(OpKind.DoorIn, "WoodLog", 270)], level: hungry ? .2 : 1, fall: hungry ? .003 : 0);
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
        var facts = Review([Ring(OpKind.WallIn, "WoodLog", 200), Ring(OpKind.DoorIn, "WoodLog", 60)]);
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
        var floor = new ConstructionActivityOption("blueprint/floor", FlooringRules.DefaultFlooringPolicy().Floors[0], new(2, 2), Pawn,
            "WoodLog", "stack-WoodLog", StackCell("WoodLog"), 1, 1, "FinishFrames", Known(0d), Known(10d), Known(200d))
            { SiteApproachTicks = Known(0d), AvailableInput = Known(10d) };
        facts = facts with
        {
            Resources = Known<EquatableArray<RimGovernor.Brain.Policy.Amount>>([new(new("WoodLog"), 10)]),
            ForecastObserved = facts.ForecastObserved with { Coupled = facts.ForecastObserved.Coupled! with
                { Construction = facts.ForecastObserved.Coupled.Construction! with { Options = [floor] } } },
        };
        facts = WorkRules.ReviewWorkResources(facts, Known<EquatableArray<ProductionBatch>>([]), new([]));
        facts = facts with { CoupledAlternatives = ForecastCoupledCandidates.GenerateCoupledActivities(facts, Horizon)?.Priced() };
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
    /// The handoff places only what the plan starts (#3278, #3342, #3744): the door's finish falls past the horizon and the
    /// schedule starts none of its activities, so a selected alternative root that names the door records the wall alone;
    /// the reconcile places the door once the room has started.
    /// </summary>
    [Fact]
    public void ASiteTheScheduleDoesNotStartIsNotPlacedByTheSelection()
    {
        var facts = Review([Ring(OpKind.WallIn, "WoodLog", 200), Ring(OpKind.DoorIn, "WoodLog", 400, count: 5, available: 50)]);
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
    /// #3651: a room whose role carries a priced value leaves the development tiebreak. Its completed sites add nothing to
    /// the count, yet the selection still exempts its site roots from the thermal-relief gate and the record still places
    /// its sites when they are selected. #3655: a bedroom is priced in production.
    /// </summary>
    [Fact]
    public void APricedRoomsSitesCountNoDevelopmentYetAreEmittedAndNotRefused()
    {
        var facts = Review([Ring(OpKind.WallIn, "WoodLog", 200), Ring(OpKind.DoorIn, "WoodLog", 60)]);
        var counted = Select(facts);
        Assert.Equal(2, counted.SelectedVector.Development);
        var bedroom = facts with { PlannedSites = Known(facts.PlannedSites.Value with { Roles = [(1, PlannedRole.Bedroom)] }) };
        var generated = ForecastCoupledCandidates.GenerateCoupledActivities(bedroom, Horizon)!.Priced();
        Assert.Equal(PlannedRole.Bedroom, generated.Context.PlannedRoomRoles[1]);
        Assert.True(generated.Context.Priced(1));
        var priced = bedroom with { CoupledAlternatives = generated };
        var result = Select(priced);
        string[] roomSites = ["planned/room1/door/WoodLog", "planned/room1/wall/WoodLog"];
        var planned = result.Rejected.Where(e => e.Commitments.Count > 0 && e.Commitments.All(id => roomSites.Any(s => id.StartsWith(s + "/composition/", StringComparison.Ordinal)))).ToArray();
        Assert.NotEmpty(planned);
        Assert.DoesNotContain(planned, e => e.Reason.StartsWith("construction_without_supported_service_relief", StringComparison.Ordinal));
        Assert.All(planned, e => Assert.Equal(0, e.Consequences!.Development));

        var roots = counted.Selection.Selected.Where(id => roomSites.Any(s => id.StartsWith(s + "/composition/", StringComparison.Ordinal))).ToArray();
        Assert.Equal(2, roots.Length);
        var record = PlannedRules.PlannedSelectionOf(priced.CoupledAlternatives, result with
            { Selection = result.Selection with { Selected = roots }, Selected = counted.Selected }, priced.PlannedSites)!;
        var (gate, sites) = PlannedRules.PlannedRoomGateOf(record, new PlannedRoom { Id = 1 }, started: false);
        Assert.Equal(PlannedRoomGate.Selected, gate);
        Assert.Equal([OpKind.DoorIn, OpKind.WallIn], sites.Select(s => s.Op));
    }

    /// <summary>
    /// #3815: generated the production way, from the round's planned rooms and the layout's doors, a corridor (room 1) that
    /// a planned bedroom (room 2) opens on is that bedroom's connector, priced with it: its sites leave the development
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
        Assert.Contains("planned/room1/wall/WoodLog", PlannedRoomCredits.PricedUnits(generated.Context));
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
        var generated = ForecastCoupledCandidates.GenerateCoupledActivities(bedroom, Horizon)!.Priced();
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
        new("SleptInBedroom", "slept_in_bedroom", 0, -4, Known((double)GameTime.TicksPerDay), false),
        new("SleptInBedroom", "slept_in_bedroom", 2, 2, Known((double)GameTime.TicksPerDay), false),
        new("SleptInBarracks", "slept_in_barracks", 0, -7, Known((double)GameTime.TicksPerDay), false),
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
        var indoors = new NeedCell(Known(true), Known(false), Known(false), Known(false), Known(false));
        var pawns = facts.ForecastObserved.Needs!.Pawns.Select(p => p.Pawn != Pawn ? p : p with
        {
            ThoughtStages = EquatableArray.From(BedroomStages),
            OwnedBed = "bed",
            Targets = [new("bed", "rest", "", 1, Known(0d), default, default, Known(.00005)) { Bed = new(role, Known(stage), false), Cell = indoors, Location = Interior[4] }],
        });
        facts = facts with
        {
            PlannedSites = Known(facts.PlannedSites.Value with { Roles = [(1, PlannedRole.Bedroom)] }),
            PlannedShelters = [new PlannedRoom { Id = 1, Role = PlannedRole.Bedroom, Region = RegionRules.RectRegion(new RimGovernor.Brain.Policy.Rectangle { X = 5, Z = 5, Width = 3, Height = 3 }) }],
            PlannedRoomPredictions = Known(EquatableArray.From(stages.Select(s => new PlannedRoomPrediction("1", s.Key, "Bedroom", Known(30d), Known(s.Value), 9, 0, false)
                { Beds = inside ? ["bed"] : [] }))),
            MoodBreak = new() { Threshold = .3, Tiers = [new(1, 2), new(1, 3), new(1, 5)] },
            ForecastObserved = facts.ForecastObserved with { Needs = facts.ForecastObserved.Needs with { Pawns = EquatableArray.From(pawns) } },
        };
        var generated = ForecastCoupledCandidates.GenerateCoupledActivities(facts, Horizon)!.Priced();
        return facts with
        {
            CoupledAlternatives = generated with
            {
                Context = generated.Context with
                {
                    BedroomOccupants = inside ? new Dictionary<int, EquatableArray<string>>() : new Dictionary<int, EquatableArray<string>> { [1] = [Pawn] },
                    InvestmentHorizonDays = 10d,
                    MaterialValues = material.ToDictionary(m => new Resource(m.Key), m => new ConstructionMaterialValue(MaterialValueBasis.Labor, m.Value)),
                },
            },
        };
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
    /// <paramref name="builders"/> are the projection's builders (unknown when null), <paramref name="open"/> the open rooms
    /// (observed started and not done); tier 2's horizon is 10 days and wellbeing is weighed a capability-day per mood-day, so a day's
    /// delay of a bedroom is worth more than the ledger's tie resolution.
    /// </summary>
    private static RoundsFacts Rings(IEnumerable<PlannedBuildSite> planned, Dictionary<int, PlannedRole> roles,
        Dictionary<int, string> occupants, int? builders, int[] open, int colonists = 1, IReadOnlyDictionary<string, double>? material = null)
    {
        var facts = Review(planned, horizon: Day, colonists: colonists);
        var indoors = new NeedCell(Known(true), Known(false), Known(false), Known(false), Known(false));
        var pawns = facts.ForecastObserved.Needs!.Pawns.Select(p => p with
        {
            ThoughtStages = EquatableArray.From(BedroomStages),
            OwnedBed = "bed-" + p.Pawn,
            Targets = [new("bed-" + p.Pawn, "rest", "", 1, Known(0d), default, default, Known(.00005)) { Bed = new("Barracks", Known(0), false), Cell = indoors, Location = Start }],
        });
        facts = facts with
        {
            PlannedSites = Known(facts.PlannedSites.Value with { Roles = EquatableArray.From(roles.Select(r => (r.Key, r.Value))) }),
            PlannedShelters = [.. occupants.Keys.Select(room => new PlannedRoom { Id = room, Role = roles[room],
                Region = RegionRules.RectRegion(new RimGovernor.Brain.Policy.Rectangle { X = 5 + (10 * (room - 1)), Z = 5, Width = 3, Height = 3 }) })],
            PlannedRoomPredictions = Known(EquatableArray.From(occupants.Keys.Select(room => new PlannedRoomPrediction(
                room.ToString(System.Globalization.CultureInfo.InvariantCulture), "WoodLog", "Bedroom", Known(30d), Known(2), 9, 0, false)))),
            MoodBreak = new() { Threshold = .3, Tiers = [new(1, 2), new(1, 3), new(1, 5)] },
            ForecastObserved = facts.ForecastObserved with { Needs = facts.ForecastObserved.Needs with { Pawns = EquatableArray.From(pawns) } },
        };
        var generated = ForecastCoupledCandidates.GenerateCoupledActivities(facts, Day)!.Priced();
        return facts with
        {
            CoupledAlternatives = generated with
            {
                Context = generated.Context with
                {
                    BedroomOccupants = occupants.ToDictionary(o => o.Key, o => EquatableArray.From([o.Value])),
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
    /// schedule of the day finishes, and a storage ring B (room 2) a third of a day long. A's closure is projected past the
    /// horizon at its remaining work over the builder; opening B would push it later by B's work, which A's credit pays, so
    /// B is not opened though its sites count development. With A closed (its ring owes nothing), B opens.
    /// </summary>
    [Fact]
    public void OneBuilderLeavesAStorageRingClosedWhileABedroomRingIsOpenAndOpensItOnceTheBedroomCloses()
    {
        var roles = new Dictionary<int, PlannedRole> { [1] = PlannedRole.Bedroom, [2] = PlannedRole.Storage };
        var bedroom = new Dictionary<int, string> { [1] = Pawn };
        var b = RoomTwoWall(Day / 3);
        var facts = Rings([Ring(OpKind.WallIn, "WoodLog", 2 * Day), b], roles, bedroom, builders: 1, open: [1]);
        var result = Select(facts, Day);
        Assert.False(PlannedSelected(result, b.Id), Why(facts, result));
        Assert.Equal(2 * Day, ClosureOf(result.Baseline, 1).ServedAt);
        Assert.Contains(BedroomCredits(result), c => c.Key == "bedroom/1/" + Pawn && c.Value < 0);
        // The delay is what holds B: unprojected, A's ring carries no credit for B to delay and B's development opens it.
        var unprojected = Rings([Ring(OpKind.WallIn, "WoodLog", 2 * Day), b], roles, bedroom, builders: null, open: [1]);
        Assert.True(PlannedSelected(Select(unprojected, Day), b.Id));

        var closed = Rings([b], roles, bedroom, builders: 1, open: [1]);
        var opened = Select(closed, Day);
        Assert.True(PlannedSelected(opened, b.Id), Why(closed, opened));
        Assert.Contains("planned/room2/wall/WoodLog", PlannedRules.PlannedSelectionOf(closed.CoupledAlternatives, opened, closed.PlannedSites)!.Sites
            .Select(s => "planned/room" + s.Room + "/wall/" + s.Stuff));
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
}
