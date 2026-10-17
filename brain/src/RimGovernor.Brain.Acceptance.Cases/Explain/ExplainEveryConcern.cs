using System.Globalization;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Telemetry;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>explain/every-concern</c>: the nightly check that the bot can explain itself
/// (#2702, epic #2690): every refusal or wait a planner reports on a running colony has a plain-language
/// need_transitions row in the session store.
/// </summary>
internal static class ExplainEveryConcern
{
    // The observed stretch: half a game day on the baseline colony, long enough for several review rounds and the first refusals.
    private const int WindowTicks = 30000;

    internal static TypedCase Case() => new()
    {
        Name = "explain/every-concern",
        Scope = "Epic #2690 (#2702): on a running baseline colony every concern or planner that reports a refusal or wait has at least one row "
            + "in the session store's need_transitions table whose cause the shared wording table (policy.Wording, the one the launcher renders) names in plain language, and no row carries "
            + "free text. A concern first seen clear writes no row by design and is reported as silent, not failed. The window is half a game day "
            + "from the first persisted review with every routine family live; a unit test cannot prove that the live planners keep emitting.",

        // The window plus the stretch before the brain persists its first review.
        Ticks = WindowTicks + 6000,
        Tier = CaseTier.Nightly,
        Start = new SaveStart { Name = Baselines.BaselineName },
        Keep = [NeedDef.LiveNeeds.Value],
        Serve = new ServeSpec
        {
            NativeTimeout = TimeSpan.FromSeconds(15),
            StepStall = TimeSpan.FromSeconds(90),
            Prefix = "explain-every-concern",
        },
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        // The active set: every concern the review held a progress record for at any poll of the window, with the last
        // planner cause filed on it.
        var active = new Dictionary<string, Cause>(StringComparer.Ordinal);
        long start = 0;
        _ = await s.ServeUntilAsync(
            "explain",
            s.TicksLeft,
            async ct =>
            {
                var review = await s.ReviewAsync(ct).ConfigureAwait(false);
                if (review is null || review.Tick.Value == 0)
                {
                    return ("no-review", false);
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

                return (string.Create(CultureInfo.InvariantCulture, $"{tick}/{active.Count}"), tick - start >= WindowTicks);
            },
            cancellationToken).ConfigureAwait(false);

        var steps = s.Telemetry(TelemetryKinds.PlannerStep);
        var transitions = await s.NeedTransitionsAsync(cancellationToken).ConfigureAwait(false);
        s.Record("window_ticks", WindowTicks);
        try
        {
            s.Record("explainable", ExplainAudit.Audit(steps, transitions, active).ToJson());
        }
        catch (ExplainAuditException e)
        {
            s.Record("explainable", e.Result.ToJson());
            throw;
        }
    }
}
