using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Domain;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// #2827: with the clock advancing and no evidence, every optional planner and family entry is re-run within its
/// own review cadence. The bound is derived from each entry's schedule (<see cref="PlannerEntry.ReviewEvery"/>)
/// plus the step spacing, not a chosen number of hours.
/// </summary>
public sealed class PlannerQueueStarvationTests
{
    private const long StepTicks = GameTime.TicksPerHour / 5;
    private const long Horizon = 15L * 24 * GameTime.TicksPerHour;

    private static PlannerEntry Entry(string name, long every = 0) => new()
    {
        Name = name,
        Class = PlannerClass.Optional,
        Every = new Tick(every),
        Configured = _ => true,
        Run = (_, _, _, _, _) => Task.FromResult(default(Verdict)),
    };

    private static readonly PlannerEntry[] Catalog =
    [
        Entry("hourly"),
        Entry("six_hour", 6 * GameTime.TicksPerHour),
        Entry("daily", 24 * GameTime.TicksPerHour),
    ];

    // Drives timer steps over the horizon, the first one full; `finishes` decides which selected planners return
    // from a wave. Returns the longest stretch each entry went unrun, counted from the start.
    private static Dictionary<string, long> LongestGaps(
        Func<string, long, bool> finishes,
        Func<string, Verdict?>? reasonOf = null,
        Func<IReadOnlyList<ActionKind>, IReadOnlyList<ActionId>>? openWork = null,
        Func<ActionId, bool>? stillOpen = null)
    {
        var queue = new PlannerQueue { Catalog = () => Catalog };
        var lastRun = Catalog.ToDictionary(e => e.Name, _ => 0L);
        var worst = Catalog.ToDictionary(e => e.Name, _ => 0L);
        for (long tick = 0; tick <= Horizon; tick += StepTicks)
        {
            var sel = queue.Selection(tick, full: tick == 0, stillOpen);
            if (!sel.Planners)
            {
                continue;
            }
            var ran = Catalog.Where(e => sel.Pick?.Invoke(e) ?? true).Select(e => e.Name).ToList();
            var returned = ran.Where(name => finishes(name, tick)).ToList();
            queue.Ran(sel, returned, reasonOf ?? (_ => null), tick, openWork);
            foreach (var name in returned)
            {
                worst[name] = Math.Max(worst[name], tick - lastRun[name]);
                lastRun[name] = tick;
            }
        }
        foreach (var name in worst.Keys.ToList())
        {
            worst[name] = Math.Max(worst[name], Horizon - lastRun[name]);
        }
        return worst;
    }

    private static void AssertWithinCadence(Dictionary<string, long> gaps)
    {
        foreach (var entry in Catalog)
        {
            Assert.True(gaps[entry.Name] <= entry.ReviewEvery().Value + StepTicks,
                $"{entry.Name} went {gaps[entry.Name]} ticks unrun; its cadence is {entry.ReviewEvery().Value}");
        }
    }

    [Fact]
    public void EveryOptionalEntryRerunsWithinItsCadenceWhileTheClockAdvances() =>
        AssertWithinCadence(LongestGaps((_, _) => true));

    [Fact]
    public void AnEntryThatMissedTheFullWaveCutoffIsStillDueOnTheClock() =>
        AssertWithinCadence(LongestGaps((name, tick) => !(tick == 0 && name == "six_hour")));

    [Fact]
    public void TheReviewTickMarksNothingWhileAnyOtherHoldMarksEveryPlanner()
    {
        // #3556: a quiet review runs only what is dirty or due; a hazard, a game pause or the brain's own hold is an
        // early review of everything.
        var queue = new PlannerQueue { Catalog = () => Catalog };
        var seq = queue.Seq;
        queue.Wake(new StepReason { Cause = StepCause.Review, Stopped = true });
        Assert.Equal(seq, queue.Seq);

        queue.Wake(new StepReason { Cause = StepCause.Wake, Stopped = true });
        Assert.True(queue.Seq > seq);
        var sel = queue.Selection(StepTicks, full: false, null);
        Assert.True(sel.Planners);
        Assert.All(Catalog, e => Assert.Contains("dirty", sel.Why![e.Name]));
    }

    [Fact]
    public void AnEntryWaitingOnOpenWorkIsReleasedAtItsDeadline()
    {
        var open = new ActionId("a1");
        AssertWithinCadence(LongestGaps(
            (_, _) => true,
            name => name == "six_hour" ? Verdict.BuildingReasonExistingWork : null,
            _ => [open],
            id => id == open));
    }
}
