using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// A contiguous span of samples where EnsureFoodSupply has a committed method but all its Progress rows remain
/// pending: Attempt is zero and Snapshot is unstamped (Go <c>sustainedfood.StallWindow</c>).
/// </summary>
public sealed record StallWindow(DateTimeOffset StartedAt, DateTimeOffset EndedAt, string Need, int Samples)
{
    /// <summary>The window in the report's JSON shape.</summary>
    public JsonObject ToJson() => new()
    {
        ["started_at"] = StartedAt,
        ["ended_at"] = EndedAt,
        ["need"] = Need,
        ["samples"] = Samples,
    };
}

/// <summary>
/// Summarizes stock and need trends, interruptions, blockers and recovery from each variant's collected goal-state
/// samples (Go <c>sustainedfood.Metrics</c>).
/// </summary>
public sealed record SustainedMetrics
{
    public int Samples { get; init; }

    public IReadOnlyDictionary<string, int> NeedCounts { get; init; } = new Dictionary<string, int>();

    public IReadOnlyDictionary<string, int> StatusCounts { get; init; } = new Dictionary<string, int>();

    public int DistinctPlans { get; init; }

    public IReadOnlyList<StallWindow> Stalls { get; init; } = [];

    public int StalledSamples { get; init; }

    public double StalledFraction { get; init; }

    public TimeSpan LongestStall { get; init; }

    public bool RecoveredAllStalls { get; init; }

    /// <summary>The metrics in the report's JSON shape (the Go struct's tags; stalls omitted when empty).</summary>
    public JsonObject ToJson()
    {
        var row = new JsonObject
        {
            ["samples"] = Samples,
            ["need_counts"] = Counts(NeedCounts),
            ["status_counts"] = Counts(StatusCounts),
            ["distinct_plans"] = DistinctPlans,
        };
        if (Stalls.Count > 0)
        {
            var stalls = new JsonArray();
            foreach (var stall in Stalls)
            {
                stalls.Add(stall.ToJson());
            }

            row["stalls"] = stalls;
        }

        row["stalled_samples"] = StalledSamples;
        row["stalled_fraction"] = StalledFraction;
        row["longest_stall_ns"] = LongestStall.Ticks * 100;
        row["recovered_all_stalls"] = RecoveredAllStalls;
        return row;
    }

    private static JsonObject Counts(IReadOnlyDictionary<string, int> counts)
    {
        var map = new JsonObject();
        foreach (var (key, value) in counts)
        {
            map[key] = value;
        }

        return map;
    }
}

public static partial class SustainedFood
{
    /// <summary>
    /// Computes <see cref="SustainedMetrics"/> from a Watch's returned timeline. It never errors: a malformed or
    /// empty timeline just yields zero-value counts, since this is diagnostic bookkeeping, not an acceptance gate
    /// itself.
    /// </summary>
    public static SustainedMetrics DeriveMetrics(IReadOnlyList<JsonObject> timeline)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        var needCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var statusCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var seenPlans = new HashSet<string>(StringComparer.Ordinal);
        var stalls = new List<StallWindow>();
        var longest = TimeSpan.Zero;
        var samples = 0;
        var stalledSamples = 0;
        DateTimeOffset stallStart = default;
        var stallNeed = "";
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

            stalls.Add(new StallWindow(stallStart, end, stallNeed, stallSamples));
            inStall = false;
            stallSamples = 0;
        }

        foreach (var sample in timeline)
        {
            if (sample.ContainsKey("error"))
            {
                continue;
            }

            samples++;
            var need = sample["need"].AsString();
            var status = sample["status"].AsString();
            if (need.Length > 0)
            {
                needCounts[need] = needCounts.GetValueOrDefault(need) + 1;
            }

            if (status.Length > 0)
            {
                statusCounts[status] = statusCounts.GetValueOrDefault(status) + 1;
            }

            var methodCount = SampleJson.Count(sample, "method_count");
            var plans = sample["plans"].AsSlice().OfType<JsonObject>().ToList();
            foreach (var plan in plans)
            {
                if (plan["plan"] is JsonValue id && id.TryGetValue<string>(out var planId))
                {
                    seenPlans.Add(planId);
                }
            }

            var stalled = methodCount > 0 && plans.Count > 0 && AllPending(plans);
            var at = SampleJson.ParseStamp(sample["at"]);
            if (stalled)
            {
                if (!inStall)
                {
                    inStall = true;
                    stallStart = at;
                    stallNeed = need;
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
            FlushStall(SampleJson.ParseStamp(timeline[^1]["at"]));
        }

        var recovered = true;
        if (stalls.Count > 0 && timeline.Count > 0)
        {
            var lastAt = SampleJson.ParseStamp(timeline[^1]["at"]);
            if (stalls[^1].EndedAt >= lastAt)
            {
                recovered = false;
            }
        }

        return new SustainedMetrics
        {
            Samples = samples,
            NeedCounts = needCounts,
            StatusCounts = statusCounts,
            DistinctPlans = seenPlans.Count,
            Stalls = stalls,
            StalledSamples = stalledSamples,
            StalledFraction = samples > 0 ? (double)stalledSamples / samples : 0,
            LongestStall = longest,
            RecoveredAllStalls = recovered,
        };
    }

    private static bool AllPending(IReadOnlyList<JsonObject> plans)
    {
        foreach (var plan in plans)
        {
            if (plan["stages"] is not JsonObject stages)
            {
                return false;
            }

            foreach (var (stage, count) in stages)
            {
                if (stage != "pending" && SampleJson.TryFloat(count, out var n) && n > 0)
                {
                    return false;
                }
            }
        }

        return true;
    }
}
