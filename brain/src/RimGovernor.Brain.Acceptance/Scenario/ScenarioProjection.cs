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
/// The adapters from the native clock replies (Run/Hold/Heartbeat, Status) to the shape
/// <see cref="ScenarioAdvance.AdvanceGameAsync"/> reads. Projected tick values are JSON numbers that were
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
    private static ulong ScenarioInteger(JsonNode? value)
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
    private static void ValidateContext(JsonObject? value)
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
    private static string HoldKind(JsonNode? reason, string what)
    {
        var text = reason.AsString();
        return !text.StartsWith(HoldReasonPrefix, StringComparison.Ordinal) || text == HoldReasonPrefix + "UNSPECIFIED"
            ? throw new AcceptanceException($"invalid {what} hold reason: {reason.ToJson()}")
            : text[HoldReasonPrefix.Length..].ToLowerInvariant();
    }

    /// <summary>
    /// Adapts a decoded clock_read_status/clock_run/clock_hold status into advance_game's protocol: {native, active
    /// (Running), held, lastTick (the status context's tick), holdSerial, paused, nativeTickBoundary} and, for a
    /// held clock, {stopReason (lowercase, without HOLD_REASON_), pauseVerified, heldAtUnixMs (the stamp its
    /// clock_hold row carries as stop_at_ms, when native reports one)}.
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
        try
        {
            lastTick = ScenarioInteger(contextValue["tick"]);
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
            ["holdSerial"] = status["holdSerial"]?.DeepClone(),
            ["paused"] = paused,
            ["nativeTickBoundary"] = nativeBoundary,
        };
        if (!running)
        {
            result["stopReason"] = HoldKind(status["reason"], "clock status");
            result["pauseVerified"] = status["pauseVerified"].AsBool() == true;
            result["heldAtUnixMs"] = status["heldAtUnixMs"]?.DeepClone();
        }

        return result;
    }

    // A JSON number (not a numeric string): Go's `.(float64)` assertion.
    private static bool IsNumber(JsonNode? value) =>
        value is JsonValue json && (json.TryGetValue<double>(out _) || json.TryGetValue<long>(out _) || json.TryGetValue<int>(out _) || json.TryGetValue<ulong>(out _));
}
