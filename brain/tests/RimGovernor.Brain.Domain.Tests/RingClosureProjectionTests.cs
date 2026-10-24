using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// #3744: the closure of a planned ring one schedule leaves open is projected from the ring work left over the builders
/// (epic #3738 D6): the work the schedule spent on rings first, then the started rings, then the rings it opened, each
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
            EquatableArray.From(ring.Where(s => s.Planned.Stuff.Length == 0).Select(s => s.Planned.Site)), new(room.ToString(System.Globalization.CultureInfo.InvariantCulture), "WoodLog", "Bedroom", Known(30d), Known(2), 1, 0, false));

    private static ForecastCoupledContext Context(int builders, int[] started, Piece[] sites, params ForecastPlannedClosure[] closures) =>
        ForecastValueTests.Context(ForecastValueTests.Pawn()) with
        {
            PlannedSites = EquatableArray.From(sites.Select(s => s.Planned)), Closures = EquatableArray.From(closures),
            RingWork = sites.Where(s => s.Work is not null).ToDictionary(s => s.Planned.Site, s => s.Work!.Value, StringComparer.Ordinal),
            PlannedBuilders = builders, StartedRooms = started.ToHashSet(),
        };

    private static Dictionary<string, double> Finished(params (Piece Site, double At)[] done) =>
        done.ToDictionary(d => d.Site.Planned.Finishes[0], d => d.At, StringComparer.Ordinal);

    private static IReadOnlyList<ForecastClosedRoom> Project(ForecastCoupledContext context, Dictionary<string, double> finished) =>
        ForecastRoofConsequences.PlannedClosedRooms(context, finished, Day);

    /// <summary>Acceptance (a): one builder, ring A started with two days left; finishing B's ring work in the schedule moves A's closure later by B's work.</summary>
    [Fact]
    public void OpeningBDelaysTheOpenRingAByBsWorkOverOneBuilder()
    {
        var a = Site(1, "wall", 2 * Day);
        var b = Site(2, "wall", Day / 3);
        var context = Context(1, [1], [a, b], Closure(1, a));
        var alone = Assert.Single(Project(context, []));
        Assert.Equal((1, 2 * Day, true, double.PositiveInfinity), (alone.Room, alone.ClosedAt, alone.Projected, alone.RoofedAt));
        var delayed = Assert.Single(Project(context, Finished((b, Day / 3))));
        Assert.Equal(2 * Day + (Day / 3), delayed.ClosedAt, 6);
    }

    /// <summary>Acceptance (b): three builders share the work, so the same opening delays A by a third of B's work.</summary>
    [Fact]
    public void ThreeBuildersDelayTheOpenRingByAThirdOfIt()
    {
        var a = Site(1, "wall", 6 * Day);
        var b = Site(2, "wall", Day / 3);
        var context = Context(3, [1], [a, b], Closure(1, a));
        Assert.Equal(2 * Day, Assert.Single(Project(context, [])).ClosedAt);
        Assert.Equal(2 * Day + (Day / 9), Assert.Single(Project(context, Finished((b, Day / 3)))).ClosedAt, 6);
    }

    /// <summary>
    /// A ring the schedule opens (its wall finished) and does not close is projected at its whole ring work, the roof's
    /// included, never inside the horizon; a ring neither started nor opened, or with no builders, is not.
    /// </summary>
    [Fact]
    public void AnOpenedRingIsProjectedAtItsRingWorkAndAnUnopenedOneIsNot()
    {
        var wall = Site(1, "wall", Day / 2);
        var door = Site(1, "door", 2 * Day);
        var roof = Site(1, "roof", Day / 2, stuff: "");
        var context = Context(1, [], [wall, door, roof], Closure(1, wall, door, roof));
        var closed = Assert.Single(Project(context, Finished((wall, Day / 2))));
        Assert.Equal((3 * Day, true), (closed.ClosedAt, closed.Projected));
        Assert.Empty(Project(context, []));
        Assert.Empty(Project(context with { PlannedBuilders = 0 }, Finished((wall, Day / 2))));
        // Without the horizon (the per-tick reads inside the schedule) nothing is projected.
        Assert.Empty(ForecastRoofConsequences.PlannedClosedRooms(context, Finished((wall, Day / 2))));
        // A short ring is never projected inside the horizon, which the schedule decides.
        var quick = Context(1, [], [Site(1, "wall", 60), Site(1, "door", 60)], Closure(1, Site(1, "wall", 60), Site(1, "door", 60)));
        Assert.Equal(Day, Assert.Single(Project(quick, Finished((Site(1, "wall", 60), 60)))).ClosedAt);
    }

    /// <summary>The started rings close first, by their remaining work, then the rings the schedule opens, by theirs.</summary>
    [Fact]
    public void StartedRingsCloseBeforeTheRingsTheScheduleOpens()
    {
        var a = Site(1, "wall", 2 * Day);
        var c = Site(3, "wall", Day);
        var cDoor = Site(3, "door", Day / 2);
        var context = Context(1, [1], [a, c, cDoor], Closure(1, a), Closure(3, c, cDoor));
        var rooms = Project(context, Finished((c, Day))).ToDictionary(r => r.Room, r => r.ClosedAt);
        Assert.Equal(Day + (2 * Day), rooms[1], 6);
        Assert.Equal(Day + (2 * Day) + (Day / 2), rooms[3], 6);
    }

    /// <summary>A ring whose closure names a site without a ring-work row is not projected.</summary>
    [Fact]
    public void ARingWithUnknownWorkIsNotProjected()
    {
        var a = Site(1, "wall", 2 * Day) with { Work = null };
        Assert.Empty(Project(Context(1, [1], [a], Closure(1, a)), []));
    }
}
