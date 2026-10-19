using System.Diagnostics;
using System.Text.Json.Nodes;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>smoke/clock-held</c> (#3350): the runner's rate-based stall guard on a held clock. The served brain runs the clock;
/// the case injects a force-pausing dialog whose one option is disabled, which nothing can answer, so the clock stays
/// held. The served wait fails within one stall budget with the clock-rate failure naming the hold, not at the tick
/// budget or the hang guard. The dialog stays open, so the runner does not keep the game.
/// </summary>
internal static class SmokeClockHeld
{
    private const string Tool = "test/open_choice_dialog";

    // Far enough for the served brain to start its first Run before the dialog opens.
    private const int Delay = 300;

    private static readonly FixtureOp<NoArgs, Fx.ChoiceDialogRead> Open = new(
        Tool,
        _ => ServedOps.Args(("action", "open"), ("title", "Stuck"), ("text", "A dialog nothing can answer (#3350)."), ("options", "!Stuck"), ("delayTicks", Delay)),
        ServedOps.Succeeded<Fx.ChoiceDialogRead>(Tool, r => r.Success));

    private static readonly FixtureOp<NoArgs, Fx.ChoiceDialogRead> Read = new(
        Tool,
        _ => ServedOps.Args(("action", "read")),
        ServedOps.Succeeded<Fx.ChoiceDialogRead>(Tool, r => r.Success));

    internal static TypedCase Case() => new()
    {
        Name = "smoke/clock-held",
        Scope = "Runner smoke: a served clock held by a force-pausing dialog with no selectable option fails the served wait "
            + "within one stall budget with the clock-rate failure naming the clock's hold, not at the tick budget or the "
            + "hang guard. The guard is the runner's own path, which only a served game exercises.",
        Ticks = 12_000,
        Tier = CaseTier.OnDemand,
        Ops = [Tool],
        NoKeep = true,
        Serve = new ServeSpec { Families = ["work"], Prefix = "clock-held" },
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var scheduled = await s.FixtureAsync("schedule-dialog", Open, NoArgs.Value, cancellationToken).ConfigureAwait(false);
        if (!scheduled.Scheduled)
        {
            throw new AcceptanceException($"schedule-dialog: the fixture did not schedule: {ServedOps.Json(scheduled)}");
        }

        var clock = Stopwatch.StartNew();
        var opened = false;
        AcceptanceException failure;
        try
        {
            _ = await s.ServeUntilAsync("held", s.TicksLeft, async ct =>
            {
                var read = await s.FixtureAsync("dialog-read", Read, NoArgs.Value, ct).ConfigureAwait(false);
                if (!opened && read.WindowOpen)
                {
                    opened = true;
                    clock.Restart();
                }

                return ($"{read.Tick} {read.WindowOpen}", false);
            }, cancellationToken).ConfigureAwait(false);
            throw new AcceptanceException("the held clock ended its served wait without the rate guard");
        }
        catch (AcceptanceException e) when (opened && e.Message.Contains("clock too slow", StringComparison.Ordinal))
        {
            failure = e;
        }

        var took = clock.Elapsed;
        s.Record("guard", new JsonObject { { "error", failure.Message }, { "after_ms", (long)took.TotalMilliseconds } });

        // One window from the dialog, plus the probe and status read that end it.
        if (took > Wait.StallBudget() * 1.5)
        {
            throw new AcceptanceException($"the rate guard fired {took.TotalSeconds:0}s after the dialog opened, past one stall budget: {failure.Message}");
        }

        if (!failure.Message.Contains("clock held (", StringComparison.Ordinal))
        {
            throw new AcceptanceException($"the rate failure does not name the clock's hold: {failure.Message}");
        }
    }
}
