using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// #3744, #3796: the service day of a planned room one schedule leaves owing is projected from the owed work left over the builders
/// (epic #3738 D6, epic #3650 P2): the owed work the schedule spent first, then the open rooms (started and not done), then the rooms it opened, each
/// group by its remaining work. The schedule here is the finish ticks given; its horizon is one day.
/// </summary>
public sealed class RingClosureProjectionTests
{
    private const double Day = GameTime.TicksPerDay;

    /// <summary>A planned ring site and its ring work (none: no ring-work row).</summary>
    private sealed record Piece(ForecastPlannedSite Planned, double? Work);

    private static Piece Site(int room, string kind, double work, string stuff = "WoodLog") =>
        new(new($"planned/room{room}/{kind}" + (stuff.Length > 0 ? "/" + stuff : ""), room, stuff, [], [$"planned/room{room}/{kind}/finish"]), work);

    private static ForecastPlannedClosure Closure(int room, params Piece[] ring) =>
        new(room, "WoodLog", [new(room, room)], EquatableArray.From(ring.Where(s => s.Planned.Stuff.Length > 0).Select(s => s.Planned.Site)),
            EquatableArray.From(ring.Where(s => s.Planned.Stuff.Length == 0).Select(s => s.Planned.Site)), PlannedRoomPredictionTests.Closed(room.ToString(System.Globalization.CultureInfo.InvariantCulture), "WoodLog", "Bedroom", 30d, 2, 1, 0, false));

    private static ForecastCoupledContext Context(int builders, int[] open, Piece[] sites, params ForecastPlannedClosure[] closures) =>
        ForecastValueTests.Context(ForecastValueTests.Pawn()) with
        {
            PlannedSites = EquatableArray.From(sites.Select(s => s.Planned)), Closures = EquatableArray.From(closures),
            OwedWork = sites.Where(s => s.Work is not null).ToDictionary(s => s.Planned.Site, s => s.Work!.Value, StringComparer.Ordinal),
            PlannedBuilders = builders, OpenRooms = open.ToHashSet(),
        };

    private static Dictionary<string, double> Finished(params (Piece Site, double At)[] done) =>
        done.ToDictionary(d => d.Site.Planned.Finishes[0], d => d.At, StringComparer.Ordinal);

    private static EquatableArray<ForecastRoomService> Project(ForecastCoupledContext context, Dictionary<string, double> finished) =>
        PlannedRoomCredits.ServiceDays(context, finished, Day);

    /// <summary>Acceptance (a): one builder, ring A open with two days left; finishing B's ring work in the schedule moves A's closure later by B's work.</summary>
    [Fact]
    public void OpeningBDelaysTheOpenRingAByBsWorkOverOneBuilder()
    {
        var a = Site(1, "wall", 2 * Day);
        var b = Site(2, "wall", Day / 3);
        var context = Context(1, [1], [a, b], Closure(1, a));
        var alone = Assert.Single(Project(context, []));
        Assert.Equal((1, 2 * Day), (alone.Room, alone.ServedAt));
        var delayed = Project(context, Finished((b, Day / 3)));
        Assert.Equal(2 * Day + (Day / 3), Assert.Single(delayed, r => r.Room == 1).ServedAt, 6);
        // B owes nothing more once its wall stands: it serves then, inside the schedule (#3796).
        Assert.Equal(Day / 3, Assert.Single(delayed, r => r.Room == 2).ServedAt);
    }

    /// <summary>Acceptance (b): three builders share the work, so the same opening delays A by a third of B's work.</summary>
    [Fact]
    public void ThreeBuildersDelayTheOpenRingByAThirdOfIt()
    {
        var a = Site(1, "wall", 6 * Day);
        var b = Site(2, "wall", Day / 3);
        var context = Context(3, [1], [a, b], Closure(1, a));
        Assert.Equal(2 * Day, Assert.Single(Project(context, [])).ServedAt);
        Assert.Equal(2 * Day + (Day / 9), Assert.Single(Project(context, Finished((b, Day / 3))), r => r.Room == 1).ServedAt, 6);
    }

    /// <summary>
    /// A room the schedule opens (its wall finished) and leaves owing is projected at its whole owed work, the roof's
    /// included, never inside the horizon; a room neither open nor opened is not, and one with no builders is unassessed.
    /// </summary>
    [Fact]
    public void AnOpenedRingIsProjectedAtItsRingWorkAndAnUnopenedOneIsNot()
    {
        var wall = Site(1, "wall", Day / 2);
        var door = Site(1, "door", 2 * Day);
        var roof = Site(1, "roof", Day / 2, stuff: "");
        var context = Context(1, [], [wall, door, roof], Closure(1, wall, door, roof));
        var closed = Assert.Single(Project(context, Finished((wall, Day / 2))));
        Assert.Equal(3 * Day, closed.ServedAt);
        Assert.Empty(Project(context, []));
        // No free builder: the room is never served (#3835).
        Assert.Empty(Project(context with { PlannedBuilders = 0 }, Finished((wall, Day / 2))));
        // The physical closure the per-tick reads use inside the schedule projects nothing.
        Assert.Empty(ForecastRoofConsequences.PlannedClosedRooms(context, Finished((wall, Day / 2))));
        // A short ring is never projected inside the horizon, which the schedule decides.
        var quick = Context(1, [], [Site(1, "wall", 60), Site(1, "door", 60)], Closure(1, Site(1, "wall", 60), Site(1, "door", 60)));
        Assert.Equal(Day, Assert.Single(Project(quick, Finished((Site(1, "wall", 60), 60)))).ServedAt);
    }

    /// <summary>The open rooms are served first, by their remaining work, then the rooms the schedule opens, by theirs.</summary>
    [Fact]
    public void OpenRingsCloseBeforeTheRingsTheScheduleOpens()
    {
        var a = Site(1, "wall", 2 * Day);
        var c = Site(3, "wall", Day);
        var cDoor = Site(3, "door", Day / 2);
        var context = Context(1, [1], [a, c, cDoor], Closure(1, a), Closure(3, c, cDoor));
        var rooms = Project(context, Finished((c, Day))).ToDictionary(r => r.Room, r => r.ServedAt);
        Assert.Equal(Day + (2 * Day), rooms[1], 6);
        Assert.Equal(Day + (2 * Day) + (Day / 2), rooms[3], 6);
    }

    /// <summary>A room owing a site without an owed-work row (no option can do it) is never served (#3835).</summary>
    [Fact]
    public void ARingWithUnknownWorkIsNotProjected()
    {
        var a = Site(1, "wall", 2 * Day) with { Work = null };
        Assert.Empty(Project(Context(1, [1], [a], Closure(1, a)), Finished()));
    }
}
