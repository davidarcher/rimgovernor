using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using RimGovernor.Contracts.Wire.Common;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using static RimGovernor.Brain.Policy.PolicyFunctions;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>Planned rooms as construction activities in the coupled portfolio (#3276).</summary>
public sealed class PlannedRoomActivitiesTests
{
    private const double Horizon = 300;
    private static readonly string[] Plants = ["plant-a", "plant-b"];
    private static readonly ObservationContext Context = new()
        { Identity = new() { ColonyId = "colony", LoadToken = "load", MapId = 0 }, Tick = 100 };

    /// <summary>One ring site of room 1 the pawn can finish in finish ticks, delivering count of stuff from a stack holding available.</summary>
    private static PlannedBuildSite Ring(OpKind op, string stuff, double finish, int count = 0, double available = 0,
        PlannedSiteVerdict verdict = PlannedSiteVerdict.Feasible)
    {
        var kind = op == OpKind.WallIn ? "wall" : "door";
        var id = "planned/room1/" + kind + "/" + stuff;
        var cell = op == OpKind.WallIn ? new Cell(3, 3) : new Cell(3, 4);
        // Every row stands where the pawn starts, the food and the stacks: only start routes are known until #3313.
        var row = new ConstructionActivityOption(id, kind == "wall" ? "Wall" : "Door", new Cell(1, 1), "pawn", count > 0 ? stuff : "", count > 0 ? "stack-" + stuff : "",
            new Cell(1, 1), count, count > 0 ? 75 : 0, "Finish", Known(0.0), count > 0 ? Known(10.0) : default, Known(finish))
            { SiteApproachTicks = Known(0.0), AvailableInput = count > 0 ? Known(available) : default };
        return new PlannedBuildSite(id, 1, op, kind == "wall" ? "Wall" : "Door", stuff, Rotation.North, [cell])
        {
            Verdict = verdict, CanPlace = Known(true), Options = [row],
            Material = count > 0 ? [new RimGovernor.Brain.Policy.Amount(new Resource(stuff), count)] : [],
        };
    }

    private static RoundsFacts Review(IEnumerable<PlannedBuildSite> planned, double level = 1, double fall = 0)
    {
        var facts = WorkResourceTestFacts.Create("pawn");
        facts = facts with
        {
            // The colony holds what the stacks hold.
            Resources = Known(EquatableArray.From(planned.SelectMany(s => s.Options).Where(o => o.Input.Length > 0).DistinctBy(o => o.Input)
                .GroupBy(o => o.Resource).Select(g => new RimGovernor.Brain.Policy.Amount(new Resource(g.Key), (long)g.Sum(o => o.AvailableInput.Value))))),
            PlannedSites = Known(new PlannedSiteFacts { Sites = EquatableArray.From(planned) }),
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
                },
            },
        };
        facts = ReviewWorkResources(facts, Known<EquatableArray<ProductionBatch>>([]), new([]));
        return facts with { CoupledAlternatives = GenerateCoupledActivities(facts, Horizon) };
    }

    private static ForecastCoupledSelection Select(RoundsFacts facts)
    {
        var request = CoupledForecastRequestOf(new("world/map/load", 100), Horizon / GameTime.TicksPerDay,
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

    [Fact]
    public void ASelectedAlternativeRootSelectsAllItsSitesAndAnUnassessedRoomPlacesAsBefore()
    {
        var facts = Review([Ring(OpKind.WallIn, "WoodLog", 200), Ring(OpKind.DoorIn, "WoodLog", 60)]);
        var candidates = facts.CoupledAlternatives!;
        var root = Assert.Single(candidates.Alternatives, c => c.Id.StartsWith("planned/room1/WoodLog/Shell/composition/", StringComparison.Ordinal));
        var result = Select(facts);
        var only = result with { Selection = result.Selection with { Selected = [root.Id] } };
        var record = PlannedSelectionOf(candidates, only, facts.PlannedSites, 100)!;
        Assert.Equal(["planned/room1/door/WoodLog", "planned/room1/wall/WoodLog"], record.Sites.Select(s => s.Id));
        Assert.Equal([OpKind.DoorIn, OpKind.WallIn], record.Sites.Select(s => s.Op));
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
}
