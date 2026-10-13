using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Telemetry.Otlp;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>explain/every-concern</c> (Go <c>explain/explainable.go</c>): the nightly check that the bot can explain itself
/// (#2702, epic #2690): every refusal or wait a planner reports on a running colony has a plain-language
/// concern_transitions row in the session store.
/// </summary>
internal static class ExplainEveryConcern
{
    // The observed stretch: half a game day on the baseline colony, long enough for several review rounds and the first refusals.
    private const long WindowTicks = 30000;

    internal static AcceptanceCase Case() => new()
    {
        Name = "explain/every-concern",
        Scope = "Epic #2690 (#2702): on a running baseline colony every concern or planner that reports a refusal or wait has at least one row "
            + "in the session store's concern_transitions table whose cause the shared wording table (policy.Wording, the one the launcher renders) names in plain language, and no row carries "
            + "free text. A concern first seen clear writes no row by design and is reported as silent, not failed. Nightly tier. The launcher's timeline model "
            + "lives in package main and cannot be imported, so the case reads the table through the store; a Go snapshot cannot prove that "
            + "the live planners keep emitting.",
        Start = new SaveStart { Name = Baselines.BaselineName },
        Keep = [NeedDef.LiveNeeds.Value],
        Serve = new ServeSpec
        {
            NativeTimeout = TimeSpan.FromSeconds(15),
            StepStall = TimeSpan.FromSeconds(90),
            Prefix = "explain-every-concern",
        },
        Budget = TimeSpan.FromMinutes(20),
        Reason = "half a game day on the baseline colony with every routine family live",
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var report = s.Report;
        var service = await ObservedSession.Of(s).ServeAsync(s.Spec, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<TimelineRecord> rows;
        IReadOnlyList<RimGovernor.Brain.Store.ConcernTransition> transitions;
        var active = new Dictionary<string, Cause>(StringComparer.Ordinal);
        try
        {
            _ = await service.AcquireAsync(cancellationToken).ConfigureAwait(false);
            service.KeepAuthority(cancellationToken);
            using var store = await ServiceJournal.OpenWithRetryAsync(s.Config, cancellationToken).ConfigureAwait(false);

            // The active set: every concern the review held a progress record for at any poll of the window, with the last
            // planner cause filed on it.
            long start = 0;
            try
            {
                await new Wait { Stall = Wait.StallBudget(), Interval = TimeSpan.FromSeconds(2), Terminal = service.Exited }.WaitProgressAsync(
                    async ct =>
                    {
                        var review = await ServiceJournal.TryRoundsAsync(store, ct).ConfigureAwait(false);
                        if (review is null || review.Tick.Value == 0)
                        {
                            return (Wait.Signature("no-review"), false);
                        }

                        var tick = review.Tick.Value;
                        if (start == 0)
                        {
                            start = tick;
                        }

                        foreach (var p in review.Progress)
                        {
                            var id = p.Concern.Value;
                            if (!active.ContainsKey(id) || p.Planner.Value.Length != 0)
                            {
                                active[id] = p.Planner;
                            }
                        }

                        return (Wait.Signature(tick, active.Count), tick - start >= WindowTicks);
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (AcceptanceException e)
            {
                throw new AcceptanceException($"observed {active.Count} active concerns before the window closed: {e.Message}", e);
            }

            _ = await service.StopAsync().ConfigureAwait(false);

            try
            {
                rows = OtlpTimeline.Read(service.LogPath);
            }
            catch (IOException e)
            {
                throw new AcceptanceException($"read log ring: {e.Message}", e);
            }

            transitions = await store.ConcernTransitionsAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ = await service.StopAsync().ConfigureAwait(false);
        }

        report["window_ticks"] = WindowTicks;
        try
        {
            var result = ExplainAudit.Audit(rows, transitions, active);
            report["explainable"] = result.ToJson();
        }
        catch (ExplainAuditException e)
        {
            report["explainable"] = e.Result.ToJson();
            throw;
        }
    }
}
