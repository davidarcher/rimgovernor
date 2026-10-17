using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Store;
using Journal = RimGovernor.Brain.Store.Store;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>dialog/pause</c> (Go <c>dialog/pause.go</c>, the former dialogpauseaccept): the dialog lifecycle end to end against
/// a live game under the real player service. A force-pausing choice dialog the game opens by itself (the shape of a
/// finished research project's completion dialog or a caravan demand) no longer strands the native clock. Two dialogs are
/// staged through the DialogFixture before the service attaches: one already open when the service acquires authority
/// (the clock cannot start at all until it is answered), and one scheduled to open on a later game tick while a
/// supervised clock is running (the HOLD_REASON_DIALOG_PAUSE hold, read off the native clock ring). For each, the
/// people/dialog Need is unmet, its step picks the policy-preferred option ("OK" over "Research screen"), the
/// executor activates it through a DialogIntent on Actions/Apply, and the clock runs again.
/// </summary>
internal static class DialogPause
{
    private const string FixtureTool = "test/open_choice_dialog";

    // How many ticks after the case's setup the second dialog opens: far enough that the service has answered the first
    // one and started a run, short enough for a Normal-speed run.
    private const int ScheduledDelay = 600;

    // Bounds each dialog's wait to be answered and the clock to run again; the stall budget ends it earlier when nothing moves.
    private static readonly TimeSpan Ceiling = TimeSpan.FromMinutes(8);

    internal static AcceptanceCase Case() => new()
    {
        Name = "dialog/pause",
        Scope = "Issue #156: a force-pausing Dialog_NodeTree the game opens by itself is read as the colony "
            + "facts dialog section, answered with the policy-preferred option through a DialogIntent on Actions/Apply under the "
            + "people/dialog Need, its HOLD_REASON_DIALOG_PAUSE hold on the native clock ring, and the "
            + "native clock runs again afterwards; both an already-open dialog at acquire and one opening mid-run.",
        Start = new LabStart(),
        RequiredOps = [FixtureTool],

        // The building families keep the supervised clock running so the scheduled dialog opens mid-run.
        Serve = new ServeSpec
        {
            Families = ["dialog", "supply", "shelter", "sleeping"],
            NativeTimeout = TimeSpan.FromSeconds(15),
            Prefix = "dialog-pause",
        },
        Budget = TimeSpan.FromMinutes(12),
        Crew = new Crew { Size = 3 }, // A dialog answer took 68s to show in the trace once.
        Stall = TimeSpan.FromMinutes(2),
        Run = RunAsync,
    };

    /// <summary>
    /// What the fixture opened before the service took the game: the immediate dialog's window ID and the scheduled one's
    /// due tick. Both offer "Research screen" first and "OK" second, so the policy-preferred answer is provably not the
    /// first option.
    /// </summary>
    private sealed class StagedDialogs
    {
        internal int ImmediateWindow { get; private set; }

        internal long DueTick { get; private set; }

        internal async Task OpenAsync(Harness h, IReadOnlyList<string> names, JsonObject identity, Report report, CancellationToken cancellationToken)
        {
            if (!Dyn.Contains(names, FixtureTool))
            {
                throw new AcceptanceException($"missing {FixtureTool} in discovery; rebuild the native mod with -Fixture DialogFixture");
            }

            var scheduled = await h.CallAsync(
                "schedule-dialog",
                FixtureTool,
                new JsonObject
                {
                    ["action"] = "open",
                    ["title"] = "Research finished",
                    ["text"] = "Scheduled choice dialog (#156).",
                    ["options"] = "Research screen|OK",
                    ["delayTicks"] = ScheduledDelay,
                },
                cancellationToken).ConfigureAwait(false);
            if (scheduled["scheduled"].AsBool() != true)
            {
                throw new AcceptanceException($"schedule-dialog: fixture did not schedule: {scheduled.ToJson()}");
            }

            DueTick = (long)scheduled["dueTick"].AsNumber();
            report["scheduled_dialog"] = scheduled.DeepClone();
            var immediate = await h.CallAsync(
                "open-dialog",
                FixtureTool,
                new JsonObject
                {
                    ["action"] = "open",
                    ["title"] = "Research finished",
                    ["text"] = "Immediate choice dialog (#156).",
                    ["options"] = "Research screen|OK",
                    ["delayTicks"] = 0,
                },
                cancellationToken).ConfigureAwait(false);
            if (immediate["windowOpen"].AsBool() != true)
            {
                throw new AcceptanceException($"open-dialog: fixture dialog is not open: {immediate.ToJson()}");
            }

            if (immediate["forcePaused"].AsBool() != true)
            {
                throw new AcceptanceException($"open-dialog: the dialog does not force-pause the game: {immediate.ToJson()}");
            }

            ImmediateWindow = (int)immediate["windowId"].AsNumber();
            report["immediate_dialog"] = immediate.DeepClone();

            // The typed census must already expose the open dialog exactly as the service's own review will read it.
            var reply = await h.WireAsync(
                "colony-facts-dialog",
                "observations_read_colony_facts",
                new JsonObject { ["scope"] = new JsonObject { ["expectedIdentity"] = identity.DeepClone() } },
                cancellationToken).ConfigureAwait(false);
            var observed = Harness.Outcome(reply, "observed").Value;
            var dialog = observed["dialog"].AsMap();
            if (dialog is null || (int)dialog["windowId"].AsNumber() != ImmediateWindow)
            {
                throw new AcceptanceException($"colony-facts-dialog: typed census lacks the open dialog: {observed["dialog"].ToJson()}");
            }

            var options = dialog["options"].AsSlice();
            if (options.Count != 2)
            {
                throw new AcceptanceException($"colony-facts-dialog: expected two options: {dialog.ToJson()}");
            }

            var second = options[1].AsMap();
            if (second?["label"].AsString() != "OK")
            {
                throw new AcceptanceException($"colony-facts-dialog: unexpected option labels: {dialog.ToJson()}");
            }

            if (second["selectable"].AsBool() != true)
            {
                throw new AcceptanceException($"colony-facts-dialog: OK is not selectable: {dialog.ToJson()}");
            }

            report["colony_facts_dialog"] = dialog.DeepClone();
        }
    }

    /// <summary>What the durable journal proves for one dialog: the people/dialog plan whose action targets the window.</summary>
    private sealed record Answered(string Method, string Plan, string Option, int Index, Stage Stage)
    {
        internal JsonObject ToJson() => new()
        {
            ["method"] = Method,
            ["plan"] = Plan,
            ["option"] = Option,
            ["index"] = Index,
            ["stage"] = Wire.Name(Stage),
        };
    }

    /// <summary>Lists every plan the people/dialog Need bound in the review's world (one Method per window and option), keyed by the targeted window.</summary>
    private static async Task<Dictionary<int, Answered>> DialogAnswersAsync(Journal journal, CancellationToken cancellationToken)
    {
        var review = await Task.Run(() => journal.LoadRounds(cancellationToken), cancellationToken).ConfigureAwait(false);
        var output = new Dictionary<int, Answered>();
        var history = await Task.Run(() => journal.OwnerMethodHistory(journal.LoadNeed(Policy.PeopleFamily.Dialog, review.Snapshot, cancellationToken), cancellationToken), cancellationToken).ConfigureAwait(false);
        foreach (var method in history)
        {
            var plan = await Task.Run(() => journal.LoadPlan(method.Plan, cancellationToken), cancellationToken).ConfigureAwait(false);
            for (var i = 0; i < plan.Spec.Actions.Count; i++)
            {
                if (plan.Spec.Actions[i].DialogAnswer is not { } answer)
                {
                    continue;
                }

                output[answer.WindowId] = new Answered(method.Method.Value, method.Plan.Value, answer.OptionLabel, answer.OptionIndex, plan.Progress[i].View.Stage);
            }
        }

        return output;
    }

    /// <summary>
    /// What the native clock ring showed after the cursor primed before the service started: every hold reason in
    /// order, the dialog-pause holds with their dialog window, and the cursor of the latest run. Each read pages on
    /// from where the last one ended, so the trace accumulates across the waits.
    /// </summary>
    private sealed class ClockTrace
    {
        internal List<string> Stops { get; } = [];

        internal List<int> DialogPauses { get; } = [];

        internal long DialogCursor { get; set; }

        internal long StartCursor { get; set; }

        internal int Starts { get; set; }

        internal long Cursor { get; set; }

        internal JsonObject ToJson() => new()
        {
            ["stops"] = new JsonArray([.. Stops.Select(x => (JsonNode?)x)]),
            ["dialog_pause_windows"] = new JsonArray([.. DialogPauses.Select(x => (JsonNode?)x)]),
            ["last_dialog_pause_cursor"] = DialogCursor,
            ["last_start_cursor"] = StartCursor,
            ["starts"] = Starts,
            ["cursor"] = Cursor,
        };
    }

    // The ring's newest cursor before the service starts: the trace reads only what the service's run produced.
    private static async Task<long> RingCursorAsync(Harness h, JsonObject identity, CancellationToken cancellationToken)
    {
        var reply = await h.WireAsync("clock-prime", "clock_read_events", new JsonObject { ["identity"] = identity.DeepClone(), ["afterCursor"] = 0, ["limit"] = 1 }, cancellationToken).ConfigureAwait(false);
        var (_, page) = Harness.Outcome(reply, "page");
        return (long)ScenarioProjection.ScenarioInteger(page["newestCursor"]);
    }

    // Pages the native ring on from the trace's cursor to its newest event, folding each event into the trace.
    private static async Task TraceClockAsync(Harness h, JsonObject identity, ClockTrace trace, CancellationToken cancellationToken)
    {
        while (true)
        {
            var reply = await h.WireAsync("clock-trace", "clock_read_events", new JsonObject { ["identity"] = identity.DeepClone(), ["afterCursor"] = trace.Cursor, ["limit"] = 128 }, cancellationToken).ConfigureAwait(false);
            var (_, page) = Harness.Outcome(reply, "page");
            if (page["gap"].AsBool() == true)
            {
                throw new AcceptanceException($"clock-trace: the native ring lost events after cursor {trace.Cursor}: {page.ToJson()}");
            }

            var events = page["events"].AsSlice();
            foreach (var raw in events)
            {
                var evt = raw.AsMap() ?? [];
                var cursor = (long)ScenarioProjection.ScenarioInteger(evt["cursor"]);
                if (evt["resumed"] is not null)
                {
                    trace.Starts++;
                    trace.StartCursor = cursor;
                }

                if (evt["holdEntered"].AsMap() is { } stop)
                {
                    var kind = ScenarioProjection.HoldKind(stop["reason"], "clock-trace");
                    trace.Stops.Add(kind);
                    if (kind == "dialog_pause")
                    {
                        trace.DialogPauses.Add((int)(stop["pause"].AsMap()?["dialog"].AsMap()?["windowId"].AsNumber() ?? 0));
                        trace.DialogCursor = cursor;
                    }
                }
            }

            var next = (long)ScenarioProjection.ScenarioInteger(page["nextCursor"]);
            var more = next != trace.Cursor && events.Count == 128;
            trace.Cursor = next;
            if (!more)
            {
                return;
            }
        }
    }

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var report = s.Report;
        var staged = new StagedDialogs();
        try
        {
            await staged.OpenAsync(s.Harness, s.Names, s.Identity, report, cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"before service: {e.Message}", e);
        }

        var trace = new ClockTrace { Cursor = await RingCursorAsync(s.Harness, s.Identity, cancellationToken).ConfigureAwait(false) };
        var service = await ObservedSession.Of(s).ServeAsync(s.Spec, cancellationToken).ConfigureAwait(false);
        var pausedWindow = 0;
        try
        {
            _ = await service.AcquireAsync(cancellationToken).ConfigureAwait(false);
            service.KeepAuthority(cancellationToken);
            using var store = await ServiceJournal.OpenWithRetryAsync(s.Config, cancellationToken).ConfigureAwait(false);

            async Task<ClockTrace> WaitForAsync(string label, Func<Dictionary<int, Answered>, ClockTrace, bool> ready)
            {
                Dictionary<int, Answered>? answers = null;
                try
                {
                    await new Wait { Ceiling = Ceiling, Stall = Wait.StallBudget(), Terminal = service.Exited }.WaitProgressAsync(
                        async ct =>
                        {
                            var a = await DialogAnswersAsync(store, ct).ConfigureAwait(false);
                            await TraceClockAsync(s.Harness, s.Identity, trace, ct).ConfigureAwait(false);
                            answers = a;
                            var done = ready(a, trace);
                            var shown = string.Join(",", a.OrderBy(kv => kv.Key).Select(kv => string.Create(CultureInfo.InvariantCulture, $"{kv.Key}:{kv.Value.Option}/{kv.Value.Index}/{Wire.Name(kv.Value.Stage)}")));
                            return (Wait.Signature(a.Count, shown, trace.Stops.Count, trace.Starts), done);
                        },
                        cancellationToken).ConfigureAwait(false);
                }
                catch (AcceptanceException e)
                {
                    Record(label, answers);
                    throw new AcceptanceException($"{label}: {e.Message}", e);
                }

                Record(label, answers);
                return trace;
            }

            void Record(string label, Dictionary<int, Answered>? answers)
            {
                var rows = new JsonObject();
                foreach (var (window, answer) in (answers ?? []).OrderBy(kv => kv.Key))
                {
                    rows[window.ToString(CultureInfo.InvariantCulture)] = answer.ToJson();
                }

                report[label + "_answers"] = rows;
                report[label + "_clock"] = trace.ToJson();
            }

            // Phase 1: the dialog open at acquire is answered with "OK". The clock could not start while it was up (an
            // Unavailable start, retried), so no run ever reports that dialog as its pause.
            _ = await WaitForAsync("phase1", (a, _) =>
            {
                if (!a.TryGetValue(staged.ImmediateWindow, out var first))
                {
                    return false;
                }

                if (first.Option != "OK" || first.Index != 1)
                {
                    throw new AcceptanceException($"planner chose \"{first.Option}\" (index {first.Index}), not the policy-preferred OK");
                }

                if (first.Stage is Stage.Unsuccessful or Stage.Cancelled)
                {
                    throw new AcceptanceException($"first dialog answer ended {Wire.Name(first.Stage)}");
                }

                return first.Stage == Stage.Completed;
            }).ConfigureAwait(false);

            // Phase 2: the scheduled dialog opens while the clock runs, holds it with HOLD_REASON_DIALOG_PAUSE naming
            // its dialog, is answered under a new occurrence, and a later run starts again.
            var trace2 = await WaitForAsync("phase2", (a, t) =>
            {
                if (t.DialogPauses.Count == 0)
                {
                    return false;
                }

                var window = t.DialogPauses[^1];
                if (!a.TryGetValue(window, out var second))
                {
                    return false;
                }

                if (second.Option != "OK" || second.Index != 1)
                {
                    throw new AcceptanceException($"planner chose \"{second.Option}\" (index {second.Index}) for the paused dialog, not OK");
                }

                if (second.Stage is Stage.Unsuccessful or Stage.Cancelled)
                {
                    throw new AcceptanceException($"second dialog answer ended {Wire.Name(second.Stage)}");
                }

                return second.Stage == Stage.Completed && t.StartCursor > t.DialogCursor;
            }).ConfigureAwait(false);
            pausedWindow = trace2.DialogPauses[^1];
            foreach (var window in trace2.DialogPauses)
            {
                if (window == staged.ImmediateWindow)
                {
                    throw new AcceptanceException($"phase2: a running clock reported the dialog open at acquire (window {window}) as its pause: {trace2.ToJson().ToJsonString()}");
                }
            }
        }
        finally
        {
            report["run_keepalive"] = (await service.StopAsync().ConfigureAwait(false))?.DeepClone();
        }

        // Independent native read after the service releases the game slot: the fixture's last dialog is closed, OK was
        // the option that ran, no force-pausing window remains and the game ticked past the due tick.
        var h = s.Reattach();
        _ = await h.CallAsync("pause-after", "rimgovernor/set_time_speed", new JsonObject { ["speed"] = "Paused", ["ultraSpeedBoost"] = false }, cancellationToken).ConfigureAwait(false);
        var after = await h.CallAsync("dialog-after", FixtureTool, new JsonObject { ["action"] = "read" }, cancellationToken).ConfigureAwait(false);
        report["dialog_after"] = after.DeepClone();
        if (after["windowOpen"].AsBool() == true)
        {
            throw new AcceptanceException($"dialog-after: the scheduled dialog is still open: {after.ToJson()}");
        }

        if (after["chosen"].AsString() != "OK")
        {
            throw new AcceptanceException($"dialog-after: native recorded \"{after["chosen"].AsString()}\" as the activated option, not OK: {after.ToJson()}");
        }

        if ((int)after["windowId"].AsNumber() != pausedWindow)
        {
            throw new AcceptanceException($"dialog-after: fixture window {after["windowId"].ToJson()} is not the paused window {pausedWindow}");
        }

        if (after["forcePausingWindows"].AsSlice().Count != 0)
        {
            throw new AcceptanceException($"dialog-after: force-pausing windows remain: {after["forcePausingWindows"].ToJson()}");
        }

        var tick = (long)after["tick"].AsNumber();
        if (tick <= staged.DueTick)
        {
            throw new AcceptanceException($"dialog-after: game tick {tick} never passed the scheduled dialog's due tick {staged.DueTick}");
        }
    }
}
