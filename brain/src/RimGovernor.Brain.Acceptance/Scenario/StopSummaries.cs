using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Telemetry.Otlp;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// Splits one clock stop's latency along occurrence -> native detection -> stop -> controller observation -> readmit
/// (Go <c>StopLatency</c>). Ticks come from the stop event alone (native's context tick, the tick the stop was raised
/// at and, where the hazard carries one, the tick it arose). ObserveMs is native's own age of the row when the page
/// carrying it was composed (age_at_reply_ms) plus the round trip's transport residual on the controller's side
/// (call_ms less native queue and execute), each on one process's clock; ReadmitMs is the controller's wall time from
/// that reply to the next clock_start receipt. Nothing subtracts one process's Unix time from another's; the cursor is
/// the only cross-process key.
/// </summary>
public sealed class StopLatency
{
    public long Cursor { get; set; }

    public string Reason { get; set; } = "";

    public long StopTick { get; set; }

    public long? DetectedTick { get; set; }

    public long? OccurrenceTick { get; set; }

    public long? DetectTicks { get; set; }

    public long? StopTicks { get; set; }

    public double? ObserveMs { get; set; }

    public double? ReadmitMs { get; set; }

    /// <summary>The latency as its report row (json tags cursor, reason, stop_tick, then the optional legs).</summary>
    public JsonObject ToJson()
    {
        var row = new JsonObject { ["cursor"] = Cursor, ["reason"] = Reason, ["stop_tick"] = StopTick };
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

        if (StopTicks is { } stopTicks)
        {
            row["stop_ticks"] = stopTicks;
        }

        if (ObserveMs is { } observe)
        {
            row["observe_ms"] = observe;
        }

        if (ReadmitMs is { } readmit)
        {
            row["readmit_ms"] = readmit;
        }

        return row;
    }
}

/// <summary>
/// Classifies the clock stops one serve process observed, read back from its log-ring timeline: every stopped event a
/// clock_read_events reply carried, split into budget stops (the window's tick budget ran out) and reactive stops
/// (everything else: a watch latch, a letter, a requested pause), with the latency from the companion's observed_at
/// stamp to the reply reaching the service (Go <c>StopSummary</c>).
/// </summary>
public sealed class StopSummary
{
    public int Stops { get; set; }

    public int BudgetStops { get; set; }

    public int ReactiveStops { get; set; }

    public Dictionary<string, int> Reasons { get; } = new(StringComparer.Ordinal);

    public int LatencyCount { get; set; }

    public double MeanLatencyMs { get; set; }

    public double MaxLatencyMs { get; set; }

    /// <summary>
    /// Counts the SpeedChanged rows the same pages carried: under the blind-tick regulator its throttle and release
    /// transitions, which end no window.
    /// </summary>
    public int SpeedChanges { get; set; }

    /// <summary>The widest blind span a regulator row reported.</summary>
    public long MaxBlindTicks { get; set; }

    /// <summary>The per-stop split, in cursor order.</summary>
    public List<StopLatency> Latencies { get; } = [];

    /// <summary>The summary as its report row (json tags stops, budget_stops, reactive_stops, reasons, ...).</summary>
    public JsonObject ToJson()
    {
        var row = new JsonObject
        {
            ["stops"] = Stops,
            ["budget_stops"] = BudgetStops,
            ["reactive_stops"] = ReactiveStops,
        };
        if (Reasons.Count > 0)
        {
            var reasons = new JsonObject();
            foreach (var (reason, count) in Reasons.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                reasons[reason] = count;
            }

            row["reasons"] = reasons;
        }

        row["latency_samples"] = LatencyCount;
        row["mean_latency_ms"] = MeanLatencyMs;
        row["max_latency_ms"] = MaxLatencyMs;
        row["speed_changes"] = SpeedChanges;
        row["max_blind_ticks"] = MaxBlindTicks;
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

/// <summary>Reads the clock stops a serve process's log ring recorded (Go speedmatrix.go's SummarizeStops and its helpers).</summary>
public static class StopSummaries
{
    private const string ClockEventsTool = "rimgovernor/clock_read_events";
    private const string ClockStartTool = "rimgovernor/clock_start";

    /// <summary>
    /// Reads clock events from direct replies and bundle pages, deduplicating by cursor. Events before
    /// <paramref name="sinceUnixMs"/> are skipped because the journal survives game reloads in the same process and
    /// can contain previous cases' stops. Zero keeps every event.
    /// </summary>
    public static StopSummary SummarizeStops(IReadOnlyList<TimelineRecord> rows, long sinceUnixMs)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var summary = new StopSummary();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        double total = 0;

        // carried is the wall time of the reply that carried each stop, for the readmit leg; starts the wall time of
        // each clock_start receipt.
        var carried = new Dictionary<int, double>();
        var starts = new List<double>();
        foreach (var row in rows)
        {
            if (row.Kind != "native_call" || NativeReplyFailed(row))
            {
                continue;
            }

            var tool = row.Payload.GetValueOrDefault("native_tool") as string ?? "";
            if (tool == ClockStartTool)
            {
                if (RecordedReply(row.Payload) is { } startReply && startReply["receipt"] is not null)
                {
                    starts.Add(row.WallTime);
                }

                continue;
            }

            if (tool != ClockEventsTool)
            {
                continue;
            }

            if (RecordedReply(row.Payload) is not { } reply)
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

                var key = evt["cursor"] is { } cursorNode
                    ? CursorKey(cursorNode)
                    : string.Create(CultureInfo.InvariantCulture, $"row-{row.Sequence}-{evt["observedAtUnixMs"].ToJson()}");
                if (evt["speedChanged"] is JsonObject changed)
                {
                    if (!seen.Add(key))
                    {
                        continue;
                    }

                    summary.SpeedChanges++;
                    if (Int64Value(changed["blindTicks"], out var blind) && blind > summary.MaxBlindTicks)
                    {
                        summary.MaxBlindTicks = blind;
                    }

                    continue;
                }

                if (evt["stopped"] is not JsonObject stopped)
                {
                    continue;
                }

                if (!seen.Add(key))
                {
                    continue;
                }

                var reason = stopped["reason"].AsString();
                if (reason.Length == 0)
                {
                    reason = "STOP_REASON_UNSPECIFIED";
                }

                summary.Stops++;
                summary.Reasons[reason] = summary.Reasons.GetValueOrDefault(reason) + 1;
                if (reason == "STOP_REASON_TICK_BUDGET")
                {
                    summary.BudgetStops++;
                }
                else
                {
                    summary.ReactiveStops++;
                }

                carried[summary.Latencies.Count] = row.WallTime;
                summary.Latencies.Add(StopLatencyOf(evt, stopped, reason, row));
                if (hasObserved && row.WallTime > 0)
                {
                    var latency = (row.WallTime * 1000) - observed;
                    if (latency >= 0)
                    {
                        summary.LatencyCount++;
                        total += latency;
                        if (latency > summary.MaxLatencyMs)
                        {
                            summary.MaxLatencyMs = latency;
                        }
                    }
                }
            }
        }

        if (summary.LatencyCount > 0)
        {
            summary.MeanLatencyMs = total / summary.LatencyCount;
        }

        for (var i = 0; i < summary.Latencies.Count; i++)
        {
            var at = carried.GetValueOrDefault(i);
            foreach (var start in starts)
            {
                if (start > at)
                {
                    summary.Latencies[i].ReadmitMs = (start - at) * 1000;
                    break;
                }
            }
        }

        return summary;
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

    // Derives one stop's tick and observation legs from its event and the reply row that carried it; the readmit leg
    // is filled in once the scan has seen the starts that followed.
    private static StopLatency StopLatencyOf(JsonObject evt, JsonObject stopped, string reason, TimelineRecord row)
    {
        var output = new StopLatency { Reason = reason };
        if (Int64Value(evt["cursor"], out var cursor))
        {
            output.Cursor = cursor;
        }

        if (evt["context"] is JsonObject context && Int64Value(context["tick"], out var stopTick))
        {
            output.StopTick = stopTick;
        }

        if (Int64Value(stopped["detectedTick"], out var detected))
        {
            output.DetectedTick = detected;
            output.StopTicks = output.StopTick - detected;
            if (Int64Value(stopped["occurrenceTick"], out var occurrence))
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
