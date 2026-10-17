using RimGovernor.Brain.Bridge;
using Fx = RimGovernor.Fixtures.Contracts;
using Observations = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>dialog/pause</c>: a force-pausing choice dialog the game opens by itself (the shape of a finished research
/// project's completion dialog or a caravan demand) no longer strands the native clock under the served brain. One
/// dialog is open when the service acquires authority and one opens mid-run on a scheduled tick; both offer "Research
/// screen" before "OK". The colony facts dialog section reads the open one exactly; the served brain answers each with
/// the policy-preferred OK, and the clock runs past the scheduled dialog with no force-pausing window left.
/// </summary>
internal static class DialogPause
{
    private const string Tool = "test/open_choice_dialog";

    /// <summary>Far enough for the service to answer the first dialog and start a run before the second opens.</summary>
    private const int ScheduledDelay = 600;

    /// <summary>Game time the clock must run after the scheduled dialog opened: it resumed, not merely reached it.</summary>
    private const int RunAfter = 300;

    private static readonly FixtureOp<(string Text, int Delay), Fx.ChoiceDialogRead> Open = new(
        Tool,
        r => ServedOps.Args(("action", "open"), ("title", "Research finished"), ("text", r.Text), ("options", "Research screen|OK"), ("delayTicks", r.Delay)),
        ServedOps.Succeeded<Fx.ChoiceDialogRead>(Tool, r => r.Success));

    private static readonly FixtureOp<NoArgs, Fx.ChoiceDialogRead> Read = new(
        Tool,
        _ => ServedOps.Args(("action", "read")),
        ServedOps.Succeeded<Fx.ChoiceDialogRead>(Tool, r => r.Success));

    internal static TypedCase Case() => new()
    {
        Name = "dialog/pause",
        Scope = "A force-pausing Dialog_NodeTree the game opens by itself is read as the colony facts dialog section and answered "
            + "with the policy-preferred option by the served brain, and the native clock runs again; both an already-open dialog "
            + "at acquire and one opening mid-run.",
        Ticks = 3000,
        Tier = CaseTier.OnDemand,
        Ops = [Tool],

        // The building families keep the supervised clock running so the scheduled dialog opens mid-run.
        Serve = new ServeSpec
        {
            Families = ["dialog", "supply", "shelter", "sleeping"],
            NativeTimeout = TimeSpan.FromSeconds(15),
            Prefix = "dialog-pause",
        },
        Run = RunAsync,
    };

    private static async Task RunAsync(ITypedSession s, CancellationToken cancellationToken)
    {
        var scheduled = await s.FixtureAsync("schedule-dialog", Open, ("Scheduled choice dialog (#156).", ScheduledDelay), cancellationToken).ConfigureAwait(false);
        if (!scheduled.Scheduled)
        {
            throw new AcceptanceException($"schedule-dialog: the fixture did not schedule: {ServedOps.Json(scheduled)}");
        }

        var immediate = await s.FixtureAsync("open-dialog", Open, ("Immediate choice dialog (#156).", 0), cancellationToken).ConfigureAwait(false);
        if (!immediate.WindowOpen || !immediate.ForcePaused)
        {
            throw new AcceptanceException($"open-dialog: expected an open force-pausing dialog: {ServedOps.Json(immediate)}");
        }

        // The typed census exposes the open dialog exactly as the service's own review reads it.
        var facts = await s.CallAsync("colony-facts-dialog", NativeOps.ReadColonyFacts, new Observations.ColonyFactsRequest { Scope = s.Scope, Planning = false }, cancellationToken).ConfigureAwait(false);
        var dialog = TypedReads.Observed("colony-facts-dialog", NativeOps.ReadColonyFacts, facts, facts.Outcome?.Observed).Dialog;
        s.RecordWire("dialog", dialog);
        if (dialog?.WindowId != immediate.WindowId || dialog.Options.Count != 2 || dialog.Options[1] is not { Label: "OK", Selectable: true })
        {
            throw new AcceptanceException($"colony-facts-dialog: expected window {immediate.WindowId} with a selectable OK second, got {dialog}");
        }

        // Both dialogs answered OK, the scheduled one is closed, nothing force-pauses and the clock ran on past it.
        _ = await s.ServeUntilAsync("dialogs-answered", s.TicksLeft, async ct =>
        {
            var read = await s.FixtureAsync("dialog-read", Read, NoArgs.Value, ct).ConfigureAwait(false);
            s.Record("dialog_read", ServedOps.Json(read));
            if (read.Chosen.Any(label => label != "OK"))
            {
                throw new AcceptanceException($"dialog-read: an option other than the policy-preferred OK ran: {ServedOps.Json(read)}");
            }

            var done = read.Chosen.Count == 2 && read.WindowId != immediate.WindowId && !read.WindowOpen
                && read.ForcePausingWindows.Count == 0 && read.Tick >= scheduled.DueTick + RunAfter;
            return ($"{read.Tick} {read.Chosen.Count} {read.WindowOpen}", done);
        }, cancellationToken).ConfigureAwait(false);
    }
}
