using System.Text;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Telemetry;
using RimGovernor.Brain.Telemetry.Otlp;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// One clock hold's legs, read from native's <c>clock_hold</c> row and the <c>clock_resume</c> row that ended it. Ticks
/// come from the hold row alone (its tick, the tick the hold was raised at and, where the hazard carries one, the tick
/// it arose). ResumeMs is native's own time from the hold to the Run that resumed it (the hold's stop stamp to the
/// resume's observed stamp, both on native's clock).
/// </summary>
public sealed class HoldLatency
{
    public string Reason { get; set; } = "";

    public long HoldTick { get; set; }

    public long? DetectedTick { get; set; }

    public long? OccurrenceTick { get; set; }

    public long? DetectTicks { get; set; }

    public long? HoldTicks { get; set; }

    public double? ResumeMs { get; set; }

    /// <summary>The hold as its report row (json tags reason, hold_tick, then the optional legs).</summary>
    public JsonObject ToJson()
    {
        var row = new JsonObject { ["reason"] = Reason, ["hold_tick"] = HoldTick };
        if (DetectedTick is { } detected)
        {
            row["detected_tick"] = detected;
        }

        if (OccurrenceTick is { } occurrence)
        {
            row["occurrence_tick"] = occurrence;
        }

        if (DetectTicks is { } detectTicks)
        {
            row["detect_ticks"] = detectTicks;
        }

        if (HoldTicks is { } holdTicks)
        {
            row["hold_ticks"] = holdTicks;
        }

        if (ResumeMs is { } resume)
        {
            row["resume_ms"] = resume;
        }

        return row;
    }
}

/// <summary>
/// The clock holds one serve process recorded, read back from its log ring's native <c>clock_hold</c> rows, counted by
/// hold reason, with the native time each hold lasted before a Run resumed it (resume).
/// </summary>
public sealed class HoldSummary
{
    public int Holds { get; set; }

    /// <summary>Holds by snake_case reason ("letter_pause", "review", ...).</summary>
    public Dictionary<string, int> Reasons { get; } = new(StringComparer.Ordinal);

    /// <summary>Holds a later Run resumed, and their native hold-to-resume time.</summary>
    public int ResumeCount { get; set; }

    public double MeanResumeMs { get; set; }

    public double MaxResumeMs { get; set; }

    /// <summary>The per-hold split, in emission order.</summary>
    public List<HoldLatency> Latencies { get; } = [];

    /// <summary>The summary as its report row (json tags holds, reasons, resume_*, ...).</summary>
    public JsonObject ToJson()
    {
        var row = new JsonObject { ["holds"] = Holds };
        if (Reasons.Count > 0)
        {
            var reasons = new JsonObject();
            foreach (var (reason, count) in Reasons.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                reasons[reason] = count;
            }

            row["reasons"] = reasons;
        }

        row["resume_samples"] = ResumeCount;
        row["mean_resume_ms"] = MeanResumeMs;
        row["max_resume_ms"] = MaxResumeMs;
        if (Latencies.Count > 0)
        {
            var latencies = new JsonArray();
            foreach (var latency in Latencies)
            {
                latencies.Add(latency.ToJson());
            }

            row["latencies"] = latencies;
        }

        return row;
    }
}

/// <summary>Reads the clock holds a serve process's log ring recorded from native's hold and resume rows.</summary>
public static class StopSummaries
{
    /// <summary>
    /// Summarizes the <c>clock_hold</c> and <c>clock_resume</c> rows in reader order. Rows observed before
    /// <paramref name="sinceUnixMs"/> are skipped because a kept process's ring carries earlier cases' holds. Zero keeps
    /// every row.
    /// </summary>
    public static HoldSummary SummarizeHolds(IReadOnlyList<TimelineRecord> rows, long sinceUnixMs)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var summary = new HoldSummary();

        // A resume ends the hold before it; the first reason stands, so holds raised while one is already standing
        // add no pending entry of their own.
        HoldLatency? pending = null;
        double pendingAt = 0;
        double resumeTotal = 0;
        foreach (var row in rows)
        {
            var hold = row.Kind == TelemetryKinds.ClockHold.Name;
            if (!hold && row.Kind != TelemetryKinds.ClockResume.Name)
            {
                continue;
            }

            var observed = Number(row.Payload, ClockEventTelemetry.ObservedAtKey);
            if (sinceUnixMs > 0 && (observed is not { } at || at < sinceUnixMs))
            {
                continue;
            }

            if (hold)
            {
                var latency = HoldLatencyOf(row.Payload);
                summary.Holds++;
                summary.Reasons[latency.Reason] = summary.Reasons.GetValueOrDefault(latency.Reason) + 1;
                summary.Latencies.Add(latency);
                if (pending is null)
                {
                    pending = latency;
                    pendingAt = Number(row.Payload, ClockEventTelemetry.StopAtMsKey) ?? observed ?? 0;
                }

                continue;
            }

            if (pending is null)
            {
                continue;
            }

            if (pendingAt > 0 && observed is { } resumedAt && resumedAt >= pendingAt)
            {
                var resume = resumedAt - pendingAt;
                pending.ResumeMs = resume;
                summary.ResumeCount++;
                resumeTotal += resume;
                summary.MaxResumeMs = Math.Max(summary.MaxResumeMs, resume);
            }

            pending = null;
        }

        if (summary.ResumeCount > 0)
        {
            summary.MeanResumeMs = resumeTotal / summary.ResumeCount;
        }

        return summary;
    }

    // One hold's tick legs from its row; the resume leg is filled in once the scan has paired it with its resume.
    private static HoldLatency HoldLatencyOf(IReadOnlyDictionary<string, object?> payload)
    {
        var output = new HoldLatency
        {
            Reason = ReasonName(payload.GetValueOrDefault(ClockEventTelemetry.ReasonKey) as string ?? ""),
            HoldTick = (long)(Number(payload, ClockEventTelemetry.TickKey) ?? 0),
        };
        if (Number(payload, ClockEventTelemetry.DetectedTickKey) is { } detected)
        {
            output.DetectedTick = (long)detected;
            output.HoldTicks = output.HoldTick - (long)detected;
            if (Number(payload, ClockEventTelemetry.OccurrenceTickKey) is { } occurrence)
            {
                output.OccurrenceTick = (long)occurrence;
                output.DetectTicks = (long)detected - (long)occurrence;
            }
        }

        return output;
    }

    // "LetterPause" (a HoldReason name) is "letter_pause"; an unnamed reason reads as "unspecified".
    private static string ReasonName(string reason)
    {
        if (reason.Length == 0)
        {
            return "unspecified";
        }

        var text = new StringBuilder();
        foreach (var c in reason)
        {
            if (char.IsUpper(c) && text.Length > 0)
            {
                text.Append('_');
            }

            text.Append(char.ToLowerInvariant(c));
        }

        return text.ToString();
    }

    // A row's numeric field; null when absent (a null attribute is absent from the record).
    private static double? Number(IReadOnlyDictionary<string, object?> payload, string key) =>
        payload.GetValueOrDefault(key) switch
        {
            double value => value,
            long value => value,
            _ => null,
        };
}
