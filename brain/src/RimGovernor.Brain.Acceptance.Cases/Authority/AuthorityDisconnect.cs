using System.Globalization;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>authority/disconnect</c> (Go <c>disconnect.go</c>, the former disconnectaccept M1): a bot that vanishes mid-epoch
/// loses authority. The only native-observable sign of a dropped controller is a typed clock lease lapsing, so the case
/// grants Auto, starts a typed epoch with the shortest admissible lease and never renews it. Native must stop the clock as
/// lease_expired, revoke authority as REVOCATION_REASON_DISCONNECT at the next generation, and then admit a fresh
/// SetMode(Auto) at that generation exactly as a reconnecting controller would issue it.
/// <para>
/// Deviation: the Go case's third phase dropped its own GABP connection and exercised the Go bridge client's loss signal,
/// fail-fast error and Reattach. The in-process C# harness has no connection to drop, so that phase is not ported; the
/// DISCONNECT revocation and regrant it ended with are covered by the first two phases.
/// </para>
/// </summary>
internal static class AuthorityDisconnect
{
    private const string Controller = "disconnectaccept";

    internal static AcceptanceCase Case() => new()
    {
        Name = "authority/disconnect",
        Scope = "Typed clock lease expiry revokes native authority as REVOCATION_REASON_DISCONNECT at generation+1 with the clock "
            + "stopped lease_expired and pause verified; a fresh SetMode(Auto) at that generation is granted.",
        Start = new LabStart(),
        Quiet = QuietMode.Loud,
        Reason = "an interruption case: the typed epochs run the colony watch policy against the game's own storyteller, and the transport drop must reattach to a game that kept running unquieted",
        Budget = TimeSpan.FromMinutes(8),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var report = s.Report;
        var h = s.Harness;
        var identity = s.Identity;
        foreach (var tool in new[] { "rimgovernor/authority_read_status", "rimgovernor/authority_control", "rimgovernor/clock_start", "rimgovernor/clock_read_status" })
        {
            if (!Dyn.Contains(s.Names, tool))
            {
                throw new AcceptanceException($"missing {tool} in discovery");
            }
        }

        // Setup: grant Auto at the fresh game's generation. Authority is initialized by the game's own update polling; a
        // read that lands before that reports it unavailable, and the first generation is always 1.
        var (generation, state) = await ReadAuthorityAsync(h, "status-fresh", identity, cancellationToken).ConfigureAwait(false);
        if (state.ContainsKey("unavailable"))
        {
            generation = 1;
        }

        var granted = await GrantAsync(h, "grant", identity, generation, cancellationToken).ConfigureAwait(false);
        if (granted != generation + 1)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"grant: expected generation {generation + 1}, got {granted}"));
        }

        report["setup"] = new JsonObject { ["generationBefore"] = generation, ["generationGranted"] = granted };

        // Case 1: a typed epoch whose lease is never renewed. 1000ms is the shortest lease clock_start admits.
        var startReply = await h.WireAsync("clock-start", "clock_start", ClockStart(identity, granted, "clock-start"), cancellationToken).ConfigureAwait(false);
        JsonObject receipt;
        try
        {
            receipt = Harness.Outcome(startReply, "receipt").Value;
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"clock-start: expected a receipt: {e.Message}", e);
        }

        var appliedStatus = receipt["applied"].AsMap()?["status"].AsMap();
        if (appliedStatus is null || !appliedStatus.ContainsKey("running"))
        {
            throw new AcceptanceException($"clock-start: expected a running epoch, got {receipt.ToJson()}");
        }

        var stopped = await WaitStoppedAsync(h, identity, "clock-poll", cancellationToken).ConfigureAwait(false);
        if (stopped["reason"].AsString() != "STOP_REASON_LEASE_EXPIRED")
        {
            throw new AcceptanceException($"clock-stopped: expected STOP_REASON_LEASE_EXPIRED, got {stopped.ToJson()}");
        }

        if (stopped["pauseVerified"].AsBool() != true)
        {
            throw new AcceptanceException($"clock-stopped: pause was not verified: {stopped.ToJson()}");
        }

        var (afterGeneration, afterState) = await ReadAuthorityAsync(h, "status-after-expiry", identity, cancellationToken).ConfigureAwait(false);
        if (afterState["inactive"].AsMap() is not { } inactive)
        {
            throw new AcceptanceException($"status-after-expiry: expected inactive authority, got {afterState.ToJson()}");
        }

        if (inactive["reason"].AsString() != "REVOCATION_REASON_DISCONNECT")
        {
            throw new AcceptanceException($"status-after-expiry: expected REVOCATION_REASON_DISCONNECT, got {inactive.ToJson()}");
        }

        if (afterGeneration != granted + 1)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"status-after-expiry: expected generation {granted + 1}, got {afterGeneration}"));
        }

        report["case_lease_expiry_disconnect"] = new JsonObject
        {
            ["stopped"] = stopped.DeepClone(),
            ["inactive"] = inactive.DeepClone(),
            ["generation"] = afterGeneration,
        };

        // Case 2: the reconnecting controller grants Auto again at the observed generation; a grant at the pre-disconnect
        // generation must be stale.
        var staleReply = await h.WireAsync(
            "regrant-stale",
            "authority_control",
            new JsonObject
            {
                ["setMode"] = new JsonObject
                {
                    ["identity"] = identity.DeepClone(),
                    ["expectedGeneration"] = granted,
                    ["mode"] = "MODE_AUTO",
                },
            },
            cancellationToken).ConfigureAwait(false);
        if (staleReply["failure"].AsMap()?["code"].AsString() != "FAILURE_CODE_STALE_GENERATION")
        {
            throw new AcceptanceException($"regrant-stale: expected FAILURE_CODE_STALE_GENERATION, got {staleReply.ToJson()}");
        }

        var regranted = await GrantAsync(h, "regrant", identity, afterGeneration, cancellationToken).ConfigureAwait(false);
        if (regranted != afterGeneration + 1)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"regrant: expected generation {afterGeneration + 1}, got {regranted}"));
        }

        report["case_regrant"] = new JsonObject { ["generation"] = regranted };

        // Case 3 (the Go transport drop) has no in-process equivalent; see the type remarks.
        report["case_transport_drop"] = "not ported: the in-process harness has no game connection to drop";
    }

    private static JsonObject ClockStart(JsonObject identity, ulong generation, string actionId) => new()
    {
        ["authority"] = new JsonObject
        {
            ["identity"] = identity.DeepClone(),
            ["expectedGeneration"] = generation,
            ["attempt"] = new JsonObject { ["controllerSessionId"] = Controller, ["actionId"] = actionId, ["attemptId"] = 1 },
        },
        ["speed"] = "SPEED_NORMAL",
        ["leaseMs"] = 1000,
        ["maxTicks"] = 60000,
        ["policy"] = new JsonObject
        {
            ["mode"] = "WATCH_MODE_COLONY",
            ["healthDropFraction"] = 0.5,
            ["minHealthFraction"] = 0.2,
            ["hostileWithin"] = 40,
            ["injuryStopCooldownMs"] = 0,
        },
    };

    /// <summary>Reads the native generation and the state message (one of unavailable/inactive/active as its single key) for the identity.</summary>
    private static async Task<(ulong Generation, JsonObject State)> ReadAuthorityAsync(Harness h, string label, JsonObject identity, CancellationToken cancellationToken)
    {
        var reply = await h.WireAsync(label, "authority_read_status", new JsonObject { ["identity"] = identity.DeepClone() }, cancellationToken).ConfigureAwait(false);
        JsonObject status;
        try
        {
            status = Harness.Outcome(reply, "status").Value;
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"{label}: expected a status outcome: {e.Message}", e);
        }

        var generation = status["context"].AsMap()?["nativeGeneration"].AsUInt64() ?? 0;
        var state = new JsonObject();
        foreach (var key in new[] { "unavailable", "inactive", "active" })
        {
            if (status.TryGetPropertyValue(key, out var value))
            {
                state[key] = value?.DeepClone();
            }
        }

        if (state.Count != 1)
        {
            throw new AcceptanceException($"{label}: expected exactly one authority state, got {status.ToJson()}");
        }

        if (!state.ContainsKey("unavailable") && generation == 0)
        {
            throw new AcceptanceException($"{label}: missing native generation: {status.ToJson()}");
        }

        return (generation, state);
    }

    /// <summary>Issues SetMode(Auto) at the expected generation and returns the granted generation.</summary>
    private static async Task<ulong> GrantAsync(Harness h, string label, JsonObject identity, ulong expected, CancellationToken cancellationToken)
    {
        var granted = await Authority.GrantAutoAtAsync(h.WireFunc(), label, identity, expected, cancellationToken).ConfigureAwait(false);
        return Authority.GrantGeneration(granted);
    }

    /// <summary>Polls clock_read_status until the typed epoch reports Stopped.</summary>
    private static async Task<JsonObject> WaitStoppedAsync(Harness h, JsonObject identity, string label, CancellationToken cancellationToken)
    {
        JsonObject? stopped = null;
        JsonObject? status = null;
        var attempt = 0;
        try
        {
            await new Wait { Ceiling = TimeSpan.FromSeconds(20), Interval = TimeSpan.FromMilliseconds(250) }.WaitProgressAsync(
                async ct =>
                {
                    var reply = await h.WireAsync(
                        string.Create(CultureInfo.InvariantCulture, $"{label}-{attempt}"),
                        "clock_read_status",
                        new JsonObject { ["identity"] = identity.DeepClone() },
                        ct).ConfigureAwait(false);
                    attempt++;
                    try
                    {
                        status = Harness.Outcome(reply, "status").Value;
                    }
                    catch (AcceptanceException e)
                    {
                        throw new AcceptanceException($"{label}: expected a status outcome: {e.Message}", e);
                    }

                    stopped = status["stopped"].AsMap();
                    return (Wait.Signature(status.ToJson()), stopped is not null);
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (WaitError e)
        {
            throw new AcceptanceException($"{label}: epoch did not stop ({status.ToJson()}): {e.Message}", e);
        }

        return stopped ?? throw new AcceptanceException($"{label}: epoch did not stop ({status.ToJson()})");
    }
}
