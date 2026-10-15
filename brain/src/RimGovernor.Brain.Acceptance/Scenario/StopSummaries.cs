using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Telemetry.Otlp;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// One clock hold's legs, read from the journal's holdEntered event and the resumed event that ended it. Ticks come
/// from the hold event alone (native's context tick, the tick the hold was raised at and, where the hazard carries one,
/// the tick it arose). ObserveMs is native's own age of the row when the page carrying it was composed (age_at_reply_ms)
/// plus the round trip's transport residual on the controller's side (call_ms less native queue and execute), each on one
/// process's clock. ResumeMs is native's own time from the hold to the Run that resumed it (the two events'
/// observed_at stamps, both on native's clock), so nothing subtracts one process's Unix time from another's; the cursor
/// is the only cross-process key.
/// </summary>
public sealed class HoldLatency
{
    public long Cursor { get; set; }

    public string Reason { get; set; } = "";

    public long HoldTick { get; set; }

    public long? DetectedTick { get; set; }

    public long? OccurrenceTick { get; set; }

    public long? DetectTicks { get; set; }

    public long? HoldTicks { get; set; }

    public double? ObserveMs { get; set; }

    public double? ResumeMs { get; set; }

    /// <summary>The hold as its report row (json tags cursor, reason, hold_tick, then the optional legs).</summary>
    public JsonObject ToJson()
    {
        var row = new JsonObject { ["cursor"] = Cursor, ["reason"] = Reason, ["hold_tick"] = HoldTick };
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

        if (ObserveMs is { } observe)
        {
            row["observe_ms"] = observe;
        }

        if (ResumeMs is { } resume)
        {
            row["resume_ms"] = resume;
        }

        return row;
    }
}

/// <summary>
/// The clock holds one serve process observed, read back from its log-ring timeline: every holdEntered event a
/// clock_read_events reply carried, counted by hold reason, with the latency from the companion's observed_at stamp to
/// the reply reaching the service (observe) and the native time each hold lasted before a Run resumed it (resume).
/// </summary>
public sealed class HoldSummary
{
    public int Holds { get; set; }

    /// <summary>Holds by lowercase reason ("letter_pause", "review", ...).</summary>
    public Dictionary<string, int> Reasons { get; } = new(StringComparer.Ordinal);

    public int ObserveCount { get; set; }

    public double MeanObserveMs { get; set; }

    public double MaxObserveMs { get; set; }

    /// <summary>Holds a later Run resumed, and their native hold-to-resume time.</summary>
    public int ResumeCount { get; set; }

    public double MeanResumeMs { get; set; }

    public double MaxResumeMs { get; set; }

    /// <summary>The per-hold split, in cursor order.</summary>
    public List<HoldLatency> Latencies { get; } = [];

    /// <summary>The summary as its report row (json tags holds, reasons, observe_*, resume_*, ...).</summary>
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

        row["observe_samples"] = ObserveCount;
        row["mean_observe_ms"] = MeanObserveMs;
        row["max_observe_ms"] = MaxObserveMs;
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

/// <summary>Reads the clock holds a serve process's log ring recorded (Go speedmatrix.go's SummarizeStops and its helpers).</summary>
public static class StopSummaries
{
    private const string ClockEventsTool = "rimgovernor/clock_read_events";
    private const string HoldReasonPrefix = "HOLD_REASON_";

    /// <summary>
    /// Reads clock events from direct replies and bundle pages, deduplicating by cursor. Events before
    /// <paramref name="sinceUnixMs"/> are skipped because the journal survives game reloads in the same process and
    /// can contain previous cases' holds. Zero keeps every event.
    /// </summary>
    public static HoldSummary SummarizeHolds(IReadOnlyList<TimelineRecord> rows, long sinceUnixMs)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var summary = new HoldSummary();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        double observeTotal = 0;

        // The journal's own order is the cursor order: holds and resumes pair up by it, whichever reply carried them.
        var ordered = new List<(long Cursor, bool Hold, long ObservedAt, HoldLatency? Latency)>();
        foreach (var row in rows)
        {
            if (row.Kind != "native_call" || NativeReplyFailed(row))
            {
                continue;
            }

            if ((row.Payload.GetValueOrDefault("native_tool") as string ?? "") != ClockEventsTool || RecordedReply(row.Payload) is not { } reply)
            {
                continue;
            }

            foreach (var evt in FindEvents(reply))
            {
                var hasObserved = Int64Value(evt["observedAtUnixMs"], out var observed);
                if (sinceUnixMs > 0 && (!hasObserved || observed < sinceUnixMs))
                {
                    continue;
                }

                var hasCursor = Int64Value(evt["cursor"], out var cursor);
                var key = evt["cursor"] is { } cursorNode
                    ? CursorKey(cursorNode)
                    : string.Create(CultureInfo.InvariantCulture, $"row-{row.Sequence}-{evt["observedAtUnixMs"].ToJson()}");
                if (evt["resumed"] is JsonObject)
                {
                    if (seen.Add(key) && hasCursor && hasObserved)
                    {
                        ordered.Add((cursor, false, observed, null));
                    }

                    continue;
                }

                if (evt["holdEntered"] is not JsonObject held || !seen.Add(key))
                {
                    continue;
                }

                var reason = ReasonName(held["reason"]);
                summary.Holds++;
                summary.Reasons[reason] = summary.Reasons.GetValueOrDefault(reason) + 1;
                var latency = HoldLatencyOf(evt, held, reason, row);
                summary.Latencies.Add(latency);
                ordered.Add((cursor, true, hasObserved ? observed : 0, latency));
                if (hasObserved && row.WallTime > 0)
                {
                    var sinceObserved = (row.WallTime * 1000) - observed;
                    if (sinceObserved >= 0)
                    {
                        summary.ObserveCount++;
                        observeTotal += sinceObserved;
                        if (sinceObserved > summary.MaxObserveMs)
                        {
                            summary.MaxObserveMs = sinceObserved;
                        }
                    }
                }
            }
        }

        if (summary.ObserveCount > 0)
        {
            summary.MeanObserveMs = observeTotal / summary.ObserveCount;
        }

        // A resume ends the hold before it in the journal; the first reason stands, so holds raised while one is
        // already standing add no pending entry of their own.
        HoldLatency? pending = null;
        long pendingAt = 0;
        double resumeTotal = 0;
        foreach (var (_, hold, observed, latency) in ordered.OrderBy(entry => entry.Cursor))
        {
            if (hold)
            {
                if (pending is null)
                {
                    pending = latency;
                    pendingAt = observed;
                }

                continue;
            }

            if (pending is null)
            {
                continue;
            }

            if (pendingAt > 0 && observed >= pendingAt)
            {
                var resume = (double)(observed - pendingAt);
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

    // "HOLD_REASON_LETTER_PAUSE" is "letter_pause"; an unnamed reason reads as "unspecified".
    private static string ReasonName(JsonNode? reason)
    {
        var text = reason.AsString();
        if (text.StartsWith(HoldReasonPrefix, StringComparison.Ordinal))
        {
            text = text[HoldReasonPrefix.Length..];
        }

        return text.Length == 0 ? "unspecified" : text.ToLowerInvariant();
    }

    // fmt.Sprint of a decoded cursor: a ProtoJSON uint64 string reads as itself, a number as its integer.
    private static string CursorKey(JsonNode cursor) =>
        cursor is JsonValue value && value.TryGetValue<string>(out var text) ? text : Int64Value(cursor, out var number) ? number.ToString(CultureInfo.InvariantCulture) : cursor.ToJson();

    // Whether a native_call row records a failure: ok false or an error text.
    private static bool NativeReplyFailed(TimelineRecord row) =>
        (row.Payload.GetValueOrDefault("ok") is false) || (row.Payload.GetValueOrDefault("error") as string ?? "").Length > 0;

    // A recorded native_call row's reply as a JSON object: the JSON text its result.payload holds (the Go rows also
    // carried a packed protobuf reply; the C# rows are plain JSON).
    private static JsonObject? RecordedReply(IReadOnlyDictionary<string, object?> payload)
    {
        if (payload.GetValueOrDefault("result") is not IReadOnlyDictionary<string, object?> result || result.GetValueOrDefault("payload") is not string text)
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(text) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Derives one hold's tick and observation legs from its event and the reply row that carried it; the resume leg is
    // filled in once the scan has paired the hold with its resume.
    private static HoldLatency HoldLatencyOf(JsonObject evt, JsonObject held, string reason, TimelineRecord row)
    {
        var output = new HoldLatency { Reason = reason };
        if (Int64Value(evt["cursor"], out var cursor))
        {
            output.Cursor = cursor;
        }

        if (evt["context"] is JsonObject context && Int64Value(context["tick"], out var holdTick))
        {
            output.HoldTick = holdTick;
        }

        if (Int64Value(held["detectedTick"], out var detected))
        {
            output.DetectedTick = detected;
            output.HoldTicks = output.HoldTick - detected;
            if (Int64Value(held["occurrenceTick"], out var occurrence))
            {
                output.OccurrenceTick = occurrence;
                output.DetectTicks = detected - occurrence;
            }
        }

        if (Int64Value(evt["ageAtReplyMs"], out var age) && age >= 0)
        {
            double observe = age;
            if (row.Payload.GetValueOrDefault("timing") is IReadOnlyDictionary<string, object?> timing)
            {
                // Only the legs the row carries: a missing key reads as 0.
                var residual = TimingMs(timing, "call_ms") - TimingMs(timing, "native_queue_ms") - TimingMs(timing, "native_execute_ms");
                if (residual > 0)
                {
                    observe += residual;
                }
            }

            output.ObserveMs = observe;
        }

        return output;
    }

    // One leg of a native_call timing map, zero when absent.
    private static double TimingMs(IReadOnlyDictionary<string, object?> timing, string key) =>
        timing.ContainsKey(key) ? RowValue.Number(timing[key]) : 0;

    // Every object in an "events" array anywhere in the decoded ProtoJSON reply, in document order.
    private static List<JsonObject> FindEvents(JsonNode? node)
    {
        var output = new List<JsonObject>();
        switch (node)
        {
            case JsonObject map:
                foreach (var key in map.Select(pair => pair.Key).Order(StringComparer.Ordinal))
                {
                    if (key == "events" && map[key] is JsonArray list)
                    {
                        output.AddRange(list.OfType<JsonObject>());
                        continue;
                    }

                    output.AddRange(FindEvents(map[key]));
                }

                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    output.AddRange(FindEvents(item));
                }

                break;
        }

        return output;
    }

    // Accepts ProtoJSON int64 encodings: a JSON string or a number.
    private static bool Int64Value(JsonNode? value, out long result)
    {
        result = 0;
        if (value is not JsonValue json)
        {
            return false;
        }

        if (json.TryGetValue<string>(out var text))
        {
            return long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out result);
        }

        if (json.TryGetValue<long>(out result))
        {
            return true;
        }

        if (json.TryGetValue<double>(out var number))
        {
            result = (long)number;
            return true;
        }

        return false;
    }
}
