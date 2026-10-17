using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// A contiguous span of samples where the food/supply Need has a committed method but all its Progress rows remain
/// pending: Attempt is zero and Snapshot is unstamped (Go <c>sustainedfood.StallWindow</c>).
/// </summary>
public sealed record StallWindow(DateTimeOffset StartedAt, DateTimeOffset EndedAt, int Samples)
{
    /// <summary>The window in the report's JSON shape; need stays empty, no sample has carried a need state since #3253.</summary>
    public JsonObject ToJson() => new()
    {
        { "started_at", StartedAt },
        { "ended_at", EndedAt },
        { "need", "" },
        { "samples", Samples },
    };
}

/// <summary>
/// Summarizes stock trends, interruptions, blockers and recovery from each variant's collected goal-state samples
/// (Go <c>sustainedfood.Metrics</c>).
/// </summary>
public sealed record SustainedMetrics
{
    public int Samples { get; init; }

    public int DistinctPlans { get; init; }

    public IReadOnlyList<StallWindow> Stalls { get; init; } = [];

    public int StalledSamples { get; init; }

    public double StalledFraction { get; init; }

    public TimeSpan LongestStall { get; init; }

    public bool RecoveredAllStalls { get; init; }

    /// <summary>
    /// The metrics in the report's JSON shape (the Go struct's tags; stalls omitted when empty). need_counts and
    /// status_counts stay empty objects: no sample has carried a need or status state since #3253.
    /// </summary>
    public JsonObject ToJson()
    {
        var row = new JsonObject { { "samples", Samples }, { "need_counts", new JsonObject() }, { "status_counts", new JsonObject() }, { "distinct_plans", DistinctPlans } }
            .AddIfPresent("stalls", Stalls.Count > 0 ? OutputJson.Array(Stalls, stall => stall.ToJson()) : null);
        row.Add("stalled_samples", StalledSamples);
        row.Add("stalled_fraction", StalledFraction);
        row.Add("longest_stall_ns", LongestStall.Ticks * 100);
        row.Add("recovered_all_stalls", RecoveredAllStalls);
        return row;
    }
}

public static class SustainedMetricsDerivation
{
    /// <summary>
    /// Computes <see cref="SustainedMetrics"/> from a Watch's returned timeline. It never errors: an empty timeline
    /// just yields zero-value counts, since this is diagnostic bookkeeping, not an acceptance gate itself.
    /// </summary>
    internal static SustainedMetrics DeriveMetrics(IReadOnlyList<WatchSample> timeline)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        var seenPlans = new HashSet<string>(StringComparer.Ordinal);
        var stalls = new List<StallWindow>();
        var longest = TimeSpan.Zero;
        var samples = 0;
        var stalledSamples = 0;
        DateTimeOffset stallStart = default;
        var inStall = false;
        var stallSamples = 0;

        void FlushStall(DateTimeOffset end)
        {
            if (!inStall)
            {
                return;
            }

            var duration = end - stallStart;
            if (duration > longest)
            {
                longest = duration;
            }

            stalls.Add(new StallWindow(stallStart, end, stallSamples));
            inStall = false;
            stallSamples = 0;
        }

        foreach (var sample in timeline)
        {
            if (sample.Need is not { } need)
            {
                continue;
            }

            samples++;
            var plans = need.Plans ?? [];
            foreach (var plan in plans)
            {
                seenPlans.Add(plan.Plan);
            }

            var stalled = need.MethodCount > 0 && plans.Count > 0 && plans.All(plan => plan.AllPending);
            var at = sample.AtTime();
            if (stalled)
            {
                if (!inStall)
                {
                    inStall = true;
                    stallStart = at;
                }

                stallSamples++;
                stalledSamples++;
            }
            else
            {
                FlushStall(at);
            }
        }

        if (inStall && timeline.Count > 0)
        {
            // Timeline ended still stalled: charge the last sample's timestamp as the (open-ended) stall end so it is
            // still visible in the report, and mark recovery as not confirmed.
            FlushStall(timeline[^1].AtTime());
        }

        var recovered = true;
        if (stalls.Count > 0 && timeline.Count > 0 && stalls[^1].EndedAt >= timeline[^1].AtTime())
        {
            recovered = false;
        }

        return new SustainedMetrics
        {
            Samples = samples,
            DistinctPlans = seenPlans.Count,
            Stalls = stalls,
            StalledSamples = stalledSamples,
            StalledFraction = samples > 0 ? (double)stalledSamples / samples : 0,
            LongestStall = longest,
            RecoveredAllStalls = recovered,
        };
    }
}
