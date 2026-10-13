using System.Globalization;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

// Acceptance authority uses SetMode(Auto|Manual) and Revoke with native-generation continuity. Callers retain the
// granted generation; authority has no acquire/renew lease (Go authority.go).

/// <summary>
/// The authority helpers a native-driven harness uses to hold and release native control (Go
/// <c>authority.go</c>): read the status, grant Auto at the current generation, revoke Manual at the granted one.
/// They speak the <c>rimgovernor/*</c> ProtoJSON wire through a <see cref="WireFunc"/> (ordinarily
/// <see cref="Harness.WireFunc"/>).
/// </summary>
public static class Authority
{
    /// <summary>
    /// Reads authority_read_status for <paramref name="identity"/> and returns the status body plus its native
    /// generation.
    /// </summary>
    public static async Task<(JsonObject Status, ulong Generation)> AuthorityStatusAsync(WireFunc wire, string label, JsonObject identity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(wire);
        var status = await OutcomeAsync(wire, label, "authority_read_status", new JsonObject { ["identity"] = identity.DeepClone() }, "status", cancellationToken).ConfigureAwait(false);
        var generation = status["context"].AsMap()?["nativeGeneration"].AsUInt64() ?? 0;
        if (status.ContainsKey("unavailable") && generation == 0)
        {
            // Authority is initialized by the game's own update polling; a read that lands before that reports it
            // unavailable, and the first generation is always 1.
            generation = 1;
        }

        return (status, generation);
    }

    /// <summary>
    /// Issues SetMode(Auto) at <paramref name="identity"/>'s current native generation and returns the "granted"
    /// body ({context, authority}); callers thread its context.nativeGeneration into every later write
    /// precondition. Re-issuing it while already Auto is admitted and advances the generation again.
    /// </summary>
    public static async Task<JsonObject> GrantAutoAsync(WireFunc wire, string label, JsonObject identity, CancellationToken cancellationToken)
    {
        var (_, generation) = await AuthorityStatusAsync(wire, label + "-status", identity, cancellationToken).ConfigureAwait(false);
        return await GrantAutoAtAsync(wire, label, identity, generation, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Issues SetMode(Auto) at an explicit expected generation.</summary>
    public static async Task<JsonObject> GrantAutoAtAsync(WireFunc wire, string label, JsonObject identity, ulong expected, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(wire);
        var request = new JsonObject
        {
            ["setMode"] = new JsonObject
            {
                ["identity"] = identity.DeepClone(),
                ["expectedGeneration"] = expected.ToString(CultureInfo.InvariantCulture),
                ["mode"] = "MODE_AUTO",
            },
        };
        var granted = await OutcomeAsync(wire, label, "authority_control", request, "granted", cancellationToken).ConfigureAwait(false);
        if (Dig(granted, "authority", "mode").AsString() != "MODE_AUTO")
        {
            throw new AcceptanceException($"{label}: expected MODE_AUTO, got {granted.ToJson()}");
        }

        return granted;
    }

    /// <summary>The native generation a <see cref="GrantAutoAsync"/> body was admitted at.</summary>
    public static ulong GrantGeneration(JsonObject grant) => Dig(grant, "context", "nativeGeneration").AsUInt64() ?? 0;

    /// <summary>Issues Revoke(MANUAL) at <paramref name="grant"/>'s generation and returns the "revoked" body.</summary>
    public static async Task<JsonObject> RevokeManualAsync(WireFunc wire, string label, JsonObject identity, JsonObject grant, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(wire);
        var request = new JsonObject
        {
            ["revoke"] = new JsonObject
            {
                ["identity"] = identity.DeepClone(),
                ["expectedGeneration"] = Dig(grant, "context", "nativeGeneration")?.DeepClone(),
                ["reason"] = "REVOCATION_REASON_MANUAL",
            },
        };
        return await OutcomeAsync(wire, label, "authority_control", request, "revoked", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Asserts an authority_read_status body is Inactive with exactly <paramref name="reason"/> and returns its inactive case.</summary>
    public static JsonObject RequireInactive(JsonObject status, string reason)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (status["inactive"].AsMap() is not { } inactive || inactive["reason"].AsString() != reason)
        {
            throw new AcceptanceException($"expected inactive authority {reason}, got {status.ToJson()}");
        }

        return inactive;
    }

    /// <summary>
    /// Revokes an active authority grant at the generation the game reports now (a keep-alive may have re-acquired
    /// since the first grant). Returns null when nothing was active. A harness that kills its service rather than
    /// shutting it down uses this before a reuse EndCase, which otherwise retires the game over the lingering grant.
    /// </summary>
    public static async Task<JsonObject?> ReleaseAuthorityAsync(Harness harness, JsonObject identity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(harness);
        var request = new JsonObject { ["identity"] = identity.DeepClone() };
        JsonObject status;
        try
        {
            var reply = await harness.WireAsync("release-authority-status", "authority_read_status", request, cancellationToken).ConfigureAwait(false);
            status = Harness.Outcome(reply, "status").Value;
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"authority status: {e.Message}", e);
        }

        if (!status.ContainsKey("active"))
        {
            return null;
        }

        var grant = new JsonObject
        {
            ["context"] = new JsonObject { ["nativeGeneration"] = status["context"].AsMap()?["nativeGeneration"]?.DeepClone() },
        };
        return await RevokeManualAsync(harness.WireFunc(), "release-authority", identity, grant, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Walks <paramref name="path"/> through nested objects (Go <c>dig</c>); null when any step is not an object or lacks the key.</summary>
    internal static JsonNode? Dig(JsonNode? value, params string[] path)
    {
        var current = value;
        foreach (var key in path)
        {
            if (current is not JsonObject map)
            {
                return null;
            }

            current = map.TryGetPropertyValue(key, out var next) ? next : null;
        }

        return current;
    }

    // wire + Outcome with the label on any failure, as Go's fmt.Errorf("%s: %w").
    private static async Task<JsonObject> OutcomeAsync(WireFunc wire, string label, string method, JsonObject request, string outcome, CancellationToken cancellationToken)
    {
        try
        {
            var reply = await wire(label, method, request, cancellationToken).ConfigureAwait(false);
            return Harness.Outcome(reply, outcome).Value;
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"{label}: {e.Message}", e);
        }
    }
}
