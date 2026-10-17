using System.Globalization;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>lab/sparring-watch</c> and <c>lab/sparring-watch-no-bout</c> (Go <c>sparring_watch.go</c>): watching a sparring bout
/// (#2732), the watch-sparring joy giver and its driver. test/sparring_watch_prepare stages bystanders north of a ring
/// staged by test/sparring_prepare (bored of every joy kind but Social, so vanilla's joy pick can only choose a Social giver)
/// and a per-tick recorder of every watch job, so the case asserts what it saw: where the spectator stood, that joy rose,
/// that the watch ended with the bout and earned the thought.
/// </summary>
internal static class LabSparringWatch
{
    private const string PrepareTool = "test/sparring_watch_prepare";
    private const string InspectTool = "test/sparring_watch_inspect";

    // A watch this long earns the thought; one this short does not; the edges are left unjudged.
    private const int LongWatch = 350;
    private const int ShortWatch = 250;

    private static readonly FixtureOp<Bystanders, Fx.SparringWatchPrepared> Prepare = new(
        PrepareTool,
        b => ServedOps.Args(("x", b.At.X), ("z", b.At.Z), ("bystanders", 1), ("drafted", b.Drafted), ("working", b.Working)),
        ServedOps.Reply<Fx.SparringWatchPrepared>(PrepareTool));

    private static readonly FixtureOp<NoArgs, Fx.SparringWatchRead> Inspect = ServedOps.Read<Fx.SparringWatchRead>(InspectTool);

    /// <summary>One free bystander at joy time near the ring, optionally a drafted one and one on an all-work schedule.</summary>
    private sealed record Bystanders(Fx.StageCell At, bool Drafted, bool Working);

    internal static IEnumerable<TypedCase> Cases() =>
    [
        new()
        {
            Name = "lab/sparring-watch",
            Scope = "The watch-sparring joy giver (#2732): with a fighting bout and a free colonist at joy time, vanilla's joy pick sends the bystander to a "
                + "spectator cell outside the ring, 3 to 8 cells from a fighter, in sight of the bout; Joy rises while it watches; the watch ends when the bout "
                + "ends and a watch of a few hundred ticks leaves the watched-a-sparring-match thought. A drafted colonist and one on an all-work schedule "
                + "never watch, and no fighter is ever a spectator. A unit test cannot see the joy pick, the walk, the Joy need or the memory.",
            Ticks = 20000,
            Tier = CaseTier.OnDemand,
            Start = new LabStart { Colonists = 8 },
            Stage = LabSparring.Site,
            Ops = [LabSparring.PrepareTool, PrepareTool, InspectTool],
            QuietWorld = true,
            Keep = ["Joy"],
            Run = WatchAsync,
        },
        new()
        {
            Name = "lab/sparring-watch-no-bout",
            Scope = "The watch-sparring joy giver (#2732) with a ring and no bout fighting: a free bystander at joy time, bored of every joy kind but Social, "
                + "never takes a watch job over 2500 ticks. A unit test cannot see the joy pick.",
            Ticks = 2500,
            Tier = CaseTier.OnDemand,
            Start = new LabStart { Colonists = 8 },
            Stage = LabSparring.Site,
            Ops = [LabSparring.PrepareTool, PrepareTool, InspectTool],
            QuietWorld = true,
            Keep = ["Joy"],
            Run = NoBoutAsync,
        },
    ];

    private static async Task WatchAsync(TypedSession s, CancellationToken cancellationToken)
    {
        // A fighting bout (painless, so it runs to the exchange cap), a free bystander, a drafted one and an all-work one.
        var ring = LabSparring.RingCell(s);
        var prepared = await s.FixtureAsync("sparring-prepare", LabSparring.Prepare, new LabSparring.Ring(ring, 2, "5,4", Scenario: "cap"), cancellationToken).ConfigureAwait(false);
        var watch = await s.FixtureAsync("sparring-watch-prepare", Prepare, new Bystanders(ring, Drafted: true, Working: true), cancellationToken).ConfigureAwait(false);
        var free = watch.Free.Count > 0 ? watch.Free[0] : throw new AcceptanceException("sparring_watch_prepare staged no free bystander");
        Fx.SparringWatchRead? last = null;
        try
        {
            _ = await s.RunUntilAsync("sparring-watch", s.TicksLeft, async ct =>
            {
                var got = await s.FixtureAsync("sparring-watch-inspect", Inspect, NoArgs.Value, ct).ConfigureAwait(false);
                last = got;

                // A long watch ended, with time to read its thought.
                var done = got.Watchers.Any(w => w.Id == free && w.Ended >= 0 && w.AtCellTicks >= LongWatch && got.Tick > w.Ended + 5);
                return (Wait.Signature(got.Watchers.Count), done);
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            s.Record("watch", last is null ? null : ServedOps.Json(last));
        }

        Check(prepared, watch, free, last!);
    }

    private static async Task NoBoutAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var ring = LabSparring.RingCell(s);
        _ = await s.FixtureAsync("sparring-prepare", LabSparring.Prepare, new LabSparring.Ring(ring, 0, "5"), cancellationToken).ConfigureAwait(false);
        _ = await s.FixtureAsync("sparring-watch-prepare", Prepare, new Bystanders(ring, Drafted: false, Working: false), cancellationToken).ConfigureAwait(false);
        await s.RunTicksAsync("sparring-watch-none", s.TicksLeft, cancellationToken).ConfigureAwait(false);
        var idle = await s.FixtureAsync("sparring-watch-inspect", Inspect, NoArgs.Value, cancellationToken).ConfigureAwait(false);
        s.Record("no_bout", ServedOps.Json(idle));
        if (idle.WatchJobs != 0)
        {
            throw new AcceptanceException($"a colonist watched for {idle.WatchJobs} ticks with no bout fighting");
        }
    }

    private static void Check(Fx.SparringPrepared prepared, Fx.SparringWatchPrepared watch, string free, Fx.SparringWatchRead got)
    {
        if (got.FighterWatchTicks != 0)
        {
            throw new AcceptanceException($"a pawn in a bout was on a watch job for {got.FighterWatchTicks} ticks");
        }

        var longWatches = 0;
        foreach (var w in got.Watchers)
        {
            if (w.Id == watch.Drafted || w.Id == watch.Working)
            {
                throw new AcceptanceException($"{w.Id} (drafted or on an all-work schedule) watched");
            }

            if (prepared.Eligible.Contains(w.Id) || w.Fighter)
            {
                throw new AcceptanceException($"fighter {w.Id} watched");
            }

            if (w.Id != free)
            {
                continue;
            }

            if (!w.CellOk)
            {
                throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"spectator cell is not valid ({w.CellNote}, {w.Dist:F1} cells from a fighter)"));
            }

            if (w.Ended < 0)
            {
                continue;
            }

            if (w.FightingAtEnd)
            {
                throw new AcceptanceException($"the watch ended at tick {w.Ended} while a bout was still fighting");
            }

            if (w.AtCellTicks >= LongWatch && !w.Thought)
            {
                throw new AcceptanceException($"no watched-a-sparring-match thought after {w.AtCellTicks} watching ticks");
            }

            if (w.AtCellTicks <= ShortWatch && w.Thought)
            {
                throw new AcceptanceException($"watched-a-sparring-match thought after only {w.AtCellTicks} watching ticks");
            }

            if (w.AtCellTicks >= LongWatch && w.JoyMax <= w.JoyStart)
            {
                throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"joy did not rise while watching ({w.JoyStart:F3} to {w.JoyMax:F3})"));
            }

            longWatches += w.AtCellTicks >= LongWatch ? 1 : 0;
        }

        if (longWatches == 0)
        {
            throw new AcceptanceException("the free bystander never completed a long watch");
        }
    }
}
