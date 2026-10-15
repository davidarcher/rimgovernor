using System.Globalization;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// A scenario that stopped with retained evidence in the caller's report, not a bug in the acceptance code itself (Go
/// <c>ScenarioInterrupted</c>). Callers that expect an interruption (an authority revocation test, say) catch this
/// instead of treating every failure the same way.
/// </summary>
public sealed class ScenarioInterruptedException : AcceptanceException
{
    public ScenarioInterruptedException(string reason)
        : base("scenario interrupted: " + reason)
    {
        Reason = reason;
    }

    public string Reason { get; }
}

/// <summary>
/// The adapters from the native clock replies (Run/Hold/Heartbeat, Status, the event journal) to the shape
/// <see cref="ScenarioAdvance.AdvanceGameAsync"/> reads. Projected tick and cursor values are JSON numbers that were
/// validated as non-negative integers; read them back with <see cref="Dyn.AsUInt64"/>.
/// </summary>
public static class ScenarioProjection
{
    private const string HoldReasonPrefix = "HOLD_REASON_";

    /// <summary>
    /// A clock stopReason as it belongs in an error message. The native reply omits the field entirely when it has no
    /// reason to report, and an empty quotation reads as a missing message rather than a missing reason.
    /// </summary>
    public static string NamedStopReason(string reason) => reason.Length == 0 ? "an unreported reason" : reason;

    /// <summary>Decodes a JSON number as a non-negative integer.</summary>
    public static ulong ScenarioInteger(JsonNode? value)
    {
        if (value is JsonValue json)
        {
            if (json.TryGetValue<double>(out var number) && number < 0)
            {
                throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"expected a non-negative integer, found {number}"));
            }

            if (value.AsUInt64() is { } integer)
            {
                return integer;
            }
        }

        throw new AcceptanceException($"expected an integer, found {value.ToJson()}");
    }

    /// <summary>Go <c>validateScenarioContext</c>: the context carries a loaded identity and a tick.</summary>
    public static void ValidateContext(JsonObject? value)
    {
        var identity = value?["identity"].AsMap()
            ?? throw new AcceptanceException($"context missing identity: {value.ToJson()}");
        if (identity["colonyId"].AsString().Length == 0 || identity["loadToken"].AsString().Length == 0)
        {
            throw new AcceptanceException($"context identity missing colonyId/loadToken: {identity.ToJson()}");
        }

        if (!IsNumber(identity["mapId"]) || identity["mapId"].AsNumber() < 0)
        {
            throw new AcceptanceException($"context identity missing mapId: {identity.ToJson()}");
        }

        try
        {
            _ = ScenarioInteger(value["tick"]);
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException("context tick: " + e.Message, e);
        }
    }

    /// <summary>The lowercase name of a wire hold reason ("HOLD_REASON_LETTER_PAUSE" is "letter_pause"); a missing or unspecified reason is refused.</summary>
    public static string HoldKind(JsonNode? reason, string what)
    {
        var text = reason.AsString();
        return !text.StartsWith(HoldReasonPrefix, StringComparison.Ordinal) || text == HoldReasonPrefix + "UNSPECIFIED"
            ? throw new AcceptanceException($"invalid {what} hold reason: {reason.ToJson()}")
            : text[HoldReasonPrefix.Length..].ToLowerInvariant();
    }

    /// <summary>
    /// Adapts a decoded clock_read_status/clock_run/clock_hold status into advance_game's protocol: {native, active
    /// (Running), held, lastTick (the status context's tick), newestCursor, paused, nativeTickBoundary} and, for a
    /// held clock, {stopReason (lowercase, without HOLD_REASON_), pauseVerified}.
    /// </summary>
    public static JsonObject ProjectStatus(JsonObject status)
    {
        ArgumentNullException.ThrowIfNull(status);
        var contextValue = status["context"].AsMap() ?? throw new AcceptanceException($"clock status missing context: {status.ToJson()}");
        ValidateContext(contextValue);
        if (status.ContainsKey("unavailable"))
        {
            throw new AcceptanceException($"clock status unavailable: {status.ToJson()}");
        }

        var state = status["state"].AsString();
        if (state is not ("CLOCK_STATE_RUNNING" or "CLOCK_STATE_HELD"))
        {
            throw new AcceptanceException($"unsupported clock state \"{state}\": {status.ToJson()}");
        }

        var paused = status["actualPaused"].AsBool() ?? throw new AcceptanceException($"clock status missing actualPaused: {status.ToJson()}");
        var nativeBoundary = status["nativeTickBoundary"].AsBool() ?? throw new AcceptanceException($"clock status missing nativeTickBoundary: {status.ToJson()}");
        ulong lastTick;
        ulong newestCursor = 0;
        try
        {
            lastTick = ScenarioInteger(contextValue["tick"]);
            if (status["newestCursor"] is not null)
            {
                newestCursor = ScenarioInteger(status["newestCursor"]);
            }
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException("clock status tick fields: " + e.Message, e);
        }

        var running = state == "CLOCK_STATE_RUNNING";
        var result = new JsonObject
        {
            ["native"] = status.DeepClone(),
            ["active"] = running,
            ["held"] = !running,
            ["lastTick"] = lastTick,
            ["newestCursor"] = newestCursor,
            ["paused"] = paused,
            ["nativeTickBoundary"] = nativeBoundary,
        };
        if (!running)
        {
            result["stopReason"] = HoldKind(status["reason"], "clock status");
            result["pauseVerified"] = status["pauseVerified"].AsBool() == true;
        }

        return result;
    }

    /// <summary>
    /// Adapts a decoded clock_read_events page: {native, events: [{native, cursor, kind, event?}], nextCursor, gap:
    /// false}. Any gap, lost event or cursor discontinuity is refused. A hold row's kind is its lowercase hold reason
    /// (letter_pause, combat_event, hostile, ...); every other row's kind is its case name (resumed, notification, ...).
    /// </summary>
    public static JsonObject ProjectEvents(JsonObject page, ulong after)
    {
        ArgumentNullException.ThrowIfNull(page);
        var contextValue = page["context"].AsMap() ?? throw new AcceptanceException($"event page missing context: {page.ToJson()}");
        ValidateContext(contextValue);
        if (page["gap"].AsBool() is not false)
        {
            throw new AcceptanceException($"event page reported a gap: {page.ToJson()}");
        }

        if (!TryInteger(page["lostCount"], out var lostCount) || lostCount != 0)
        {
            throw new AcceptanceException($"event page lostCount: {page.ToJson()}");
        }

        ulong newest;
        ulong nextCursor;
        try
        {
            newest = ScenarioInteger(page["newestCursor"]);
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException("event page newestCursor: " + e.Message, e);
        }

        try
        {
            nextCursor = ScenarioInteger(page["nextCursor"]);
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException("event page nextCursor: " + e.Message, e);
        }

        if (after > newest)
        {
            throw new AcceptanceException($"event page cursor precedes requested afterCursor: {page.ToJson()}");
        }

        if (page.ContainsKey("oldestCursor") && (!TryInteger(page["oldestCursor"], out var oldest) || oldest > newest))
        {
            throw new AcceptanceException($"event page oldestCursor: {page.ToJson()}");
        }

        var rows = new JsonArray();
        var previous = after;
        foreach (var raw in page["events"].AsSlice())
        {
            var evt = raw.AsMap() ?? throw new AcceptanceException($"event row is not an object: {raw.ToJson()}");
            if (!TryInteger(evt["cursor"], out var cursor) || cursor != previous + 1 || cursor > newest)
            {
                throw new AcceptanceException($"event cursor discontinuity: {page.ToJson()}");
            }

            previous = cursor;
            var eventContext = evt["context"].AsMap() ?? throw new AcceptanceException($"event missing context: {evt.ToJson()}");
            ValidateContext(eventContext);
            var row = new JsonObject
            {
                ["native"] = evt.DeepClone(),
                ["cursor"] = cursor,
            };
            if (evt["holdEntered"].AsMap() is { } hold)
            {
                var kind = HoldKind(hold["reason"], "event");
                row["kind"] = kind;
                if (kind == "letter_pause")
                {
                    var letter = hold["pause"].AsMap()?["letter"].AsMap();
                    var letterId = letter?["id"].AsString() ?? "";
                    if (letter is null || letterId.Length == 0)
                    {
                        throw new AcceptanceException($"letter_pause event missing letter id: {evt.ToJson()}");
                    }

                    row["event"] = new JsonObject { ["letterId"] = letterId, ["source"] = "LetterStack.ReceiveLetter" };
                }

                if (kind == "dialog_pause")
                {
                    var dialog = hold["pause"].AsMap()?["dialog"].AsMap();
                    if (dialog is null || dialog["windowId"] is null)
                    {
                        throw new AcceptanceException($"dialog_pause event missing window id: {evt.ToJson()}");
                    }

                    row["event"] = new JsonObject
                    {
                        ["windowId"] = dialog["windowId"]?.DeepClone(),
                        ["windowType"] = dialog["windowType"].AsString(),
                        ["title"] = dialog["title"].AsString(),
                    };
                }
            }
            else
            {
                var cases = new List<string>();
                foreach (var key in new[]
                {
                    "resumed", "notification", "alert", "injuryObserved", "hostilesCleared", "pauseFailed",
                    "forcePauseWaiting", "forcePauseCleared", "operationOutcome", "authorityChanged", "observationInvalidated",
                })
                {
                    if (evt.ContainsKey(key))
                    {
                        cases.Add(key);
                    }
                }

                if (cases.Count != 1)
                {
                    throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"event carries {cases.Count} cases, expected exactly one: {evt.ToJson()}"));
                }

                row["kind"] = cases[0];
            }

            rows.Add(row);
        }

        if (nextCursor != previous)
        {
            throw new AcceptanceException($"event page nextCursor disagrees with the last row's cursor: {page.ToJson()}");
        }

        if (rows.Count == 0 && nextCursor != newest)
        {
            throw new AcceptanceException($"empty event page before the newest retained cursor: {page.ToJson()}");
        }

        return new JsonObject
        {
            ["native"] = page.DeepClone(),
            ["events"] = rows,
            ["nextCursor"] = nextCursor,
            ["gap"] = false,
        };
    }

    private static bool TryInteger(JsonNode? value, out ulong integer)
    {
        try
        {
            integer = ScenarioInteger(value);
            return true;
        }
        catch (AcceptanceException)
        {
            integer = 0;
            return false;
        }
    }

    // A JSON number (not a numeric string): Go's `.(float64)` assertion.
    private static bool IsNumber(JsonNode? value) =>
        value is JsonValue json && (json.TryGetValue<double>(out _) || json.TryGetValue<long>(out _) || json.TryGetValue<int>(out _) || json.TryGetValue<ulong>(out _));
}
