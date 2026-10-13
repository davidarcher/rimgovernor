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
/// The adapters from the native clock replies to the shape <see cref="ScenarioAdvance.AdvanceGameAsync"/> reads (Go
/// <c>projectStatus</c>, <c>projectEvents</c> and their validation). Projected tick, epoch and cursor values are JSON
/// numbers that were validated as non-negative integers; read them back with <see cref="Dyn.AsUInt64"/>.
/// </summary>
public static class ScenarioProjection
{
    /// <summary>
    /// A clock stopReason as it belongs in an error message. The native reply omits the field entirely when it has no
    /// reason to report, and an empty quotation reads as a missing message rather than a missing reason.
    /// </summary>
    public static string NamedStopReason(string reason) => reason.Length == 0 ? "an unreported reason" : reason;

    /// <summary>Decodes a ProtoJSON integer (number, uint64 or decimal string) as a non-negative integer.</summary>
    public static ulong ScenarioInteger(JsonNode? value)
    {
        if (value is JsonValue json)
        {
            if (json.TryGetValue<string>(out var text))
            {
                return ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : throw new AcceptanceException($"expected an integer string, found \"{text}\"");
            }

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

    /// <summary>
    /// Adapts a decoded clock_read_status/clock_pause/receipt status into advance_game's protocol: {native, owner,
    /// epoch, active, startTick, lastTick, tickDeadline, newestCursor, paused, nativeTickBoundary} and, for a stopped
    /// clock, {stopReason (lowercase, without STOP_REASON_), pauseVerified}.
    /// </summary>
    public static JsonObject ProjectStatus(JsonObject status)
    {
        ArgumentNullException.ThrowIfNull(status);
        var contextValue = status["context"].AsMap() ?? throw new AcceptanceException($"clock status missing context: {status.ToJson()}");
        ValidateContext(contextValue);
        var caseName = "";
        foreach (var key in new[] { "running", "stopping", "stopped", "neverStarted", "unavailable" })
        {
            if (status.ContainsKey(key))
            {
                if (caseName.Length > 0)
                {
                    throw new AcceptanceException($"multiple clock status cases: {status.ToJson()}");
                }

                caseName = key;
            }
        }

        if (caseName is not ("running" or "stopping" or "stopped"))
        {
            throw new AcceptanceException($"unsupported clock status case \"{caseName}\": {status.ToJson()}");
        }

        var body = status[caseName].AsMap() ?? [];
        var epoch = body["epoch"].AsMap() ?? throw new AcceptanceException($"clock status missing epoch: {body.ToJson()}");
        var origin = epoch["origin"].AsMap() ?? throw new AcceptanceException($"clock epoch missing origin: {epoch.ToJson()}");
        ValidateContext(origin);
        var owner = epoch["owner"].AsMap() ?? throw new AcceptanceException($"clock epoch missing owner: {epoch.ToJson()}");
        var ownerSession = owner["controllerSessionId"].AsString();
        if (ownerSession.Length == 0)
        {
            throw new AcceptanceException($"clock epoch owner missing controllerSessionId: {owner.ToJson()}");
        }

        ulong epochId;
        try
        {
            epochId = ScenarioInteger(owner["epoch"]);
        }
        catch (AcceptanceException)
        {
            epochId = 0;
        }

        if (epochId == 0)
        {
            throw new AcceptanceException($"clock epoch owner epoch: {owner.ToJson()}");
        }

        var paused = status["actualPaused"].AsBool() ?? throw new AcceptanceException($"clock status missing actualPaused: {status.ToJson()}");
        var nativeBoundary = status["nativeTickBoundary"].AsBool() ?? throw new AcceptanceException($"clock status missing nativeTickBoundary: {status.ToJson()}");
        ulong startTick;
        ulong lastTick;
        ulong tickDeadline;
        ulong newestCursor;
        try
        {
            startTick = ScenarioInteger(epoch["startTick"]);
            lastTick = ScenarioInteger(epoch["lastTick"]);
            tickDeadline = ScenarioInteger(epoch["tickDeadline"]);
            newestCursor = ScenarioInteger(status["newestCursor"]);
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException("clock epoch tick fields: " + e.Message, e);
        }

        if (!(startTick <= lastTick && lastTick <= tickDeadline))
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"clock epoch tick ordering violated: start={startTick} last={lastTick} deadline={tickDeadline}"));
        }

        var result = new JsonObject
        {
            ["native"] = status.DeepClone(),
            ["owner"] = ownerSession,
            ["epoch"] = epochId,
            ["active"] = caseName != "stopped",
            ["startTick"] = startTick,
            ["lastTick"] = lastTick,
            ["tickDeadline"] = tickDeadline,
            ["newestCursor"] = newestCursor,
            ["paused"] = paused,
            ["nativeTickBoundary"] = nativeBoundary,
        };
        if (caseName == "stopped")
        {
            var reason = body["reason"].AsString();
            if (!reason.StartsWith("STOP_REASON_", StringComparison.Ordinal) || reason == "STOP_REASON_UNSPECIFIED")
            {
                throw new AcceptanceException($"invalid stopped reason: {body.ToJson()}");
            }

            var pauseVerified = body["pauseVerified"].AsBool() ?? throw new AcceptanceException($"stopped clock missing pauseVerified: {body.ToJson()}");
            if (body["actualPaused"].AsBool() is not { } actualPaused || actualPaused != paused)
            {
                throw new AcceptanceException($"stopped clock actualPaused mismatch: {body.ToJson()}");
            }

            result["stopReason"] = reason["STOP_REASON_".Length..].ToLowerInvariant();
            result["pauseVerified"] = pauseVerified;
        }

        return result;
    }

    /// <summary>
    /// Adapts a decoded clock_read_events page: {native, events: [{native, cursor, owner, epoch, kind, event?}],
    /// nextCursor, gap: false}. Any gap, lost event, cursor discontinuity or ownerless event is refused.
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
            var owner = evt["owner"].AsMap() ?? throw new AcceptanceException($"event missing owner: {evt.ToJson()}");
            var ownerSession = owner["controllerSessionId"].AsString();
            if (ownerSession.Length == 0)
            {
                throw new AcceptanceException($"event owner missing controllerSessionId: {owner.ToJson()}");
            }

            if (!TryInteger(owner["epoch"], out var epochId) || epochId == 0)
            {
                throw new AcceptanceException($"event owner epoch: {owner.ToJson()}");
            }

            var row = new JsonObject
            {
                ["native"] = evt.DeepClone(),
                ["cursor"] = cursor,
                ["owner"] = ownerSession,
                ["epoch"] = epochId,
            };
            if (evt["stopped"].AsMap() is { } stopped)
            {
                var reason = stopped["reason"].AsString();
                if (!reason.StartsWith("STOP_REASON_", StringComparison.Ordinal) || reason == "STOP_REASON_UNSPECIFIED")
                {
                    throw new AcceptanceException($"invalid event stop reason: {evt.ToJson()}");
                }

                var kind = reason["STOP_REASON_".Length..].ToLowerInvariant();
                row["kind"] = kind;
                if (kind == "letter_pause")
                {
                    var letter = stopped["pause"].AsMap()?["letter"].AsMap();
                    var letterId = letter?["id"].AsString() ?? "";
                    if (letter is null || letterId.Length == 0)
                    {
                        throw new AcceptanceException($"letter_pause event missing letter id: {evt.ToJson()}");
                    }

                    row["event"] = new JsonObject { ["letterId"] = letterId, ["source"] = "LetterStack.ReceiveLetter" };
                }

                if (kind == "dialog_pause")
                {
                    var dialog = stopped["pause"].AsMap()?["dialog"].AsMap();
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
                    "started", "speedChanged", "notification", "alert", "injuryObserved",
                    "observationInvalidated", "hostilesCleared", "pauseFailed", "forcePauseWaiting", "forcePauseCleared",
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
