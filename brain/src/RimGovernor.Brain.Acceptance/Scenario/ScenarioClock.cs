using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Acceptance;

// This file is the typed scenario clock, so the combat and movement acceptance cases can run and hold the real game
// clock through the native Run/Hold/Heartbeat surface. It stands in for the brain's ClockRunDriver: it sends Run, keeps
// the heartbeat that holds the one ceiling above Normal while it waits, and holds the clock itself when a case's tick
// target is reached. It intentionally omits a medical-rest wait mode: only the combat-target mode is exercised.

/// <summary>
/// The typed clock/authority transport adapter AdvanceGame drives. Every native call goes through <see cref="Wire"/>
/// (ordinarily <see cref="Harness.WireFunc"/>), so this type owns only the owner-side bookkeeping (grant, event cursor,
/// heartbeat cadence, the tick the last Run began at) AdvanceGame needs to resume and detect drift (Go <c>ScenarioClock</c>).
/// </summary>
public sealed class ScenarioClock
{
    /// <summary>The longest gap between heartbeats while this clock waits; the native tolerance is five seconds.</summary>
    private static readonly TimeSpan HeartbeatEvery = TimeSpan.FromSeconds(1);

    /// <summary>The fixed watch policy every acceptance Run starts with (Go <c>scenarioPolicy</c>).</summary>
    private static JsonObject ScenarioPolicy() => new()
    {
        ["mode"] = "WATCH_MODE_COLONY",
        ["healthDropFraction"] = 0.1,
        ["minHealthFraction"] = 0.5,
        ["hostileWithin"] = 40,
        ["injuryStopCooldownMs"] = 0,

        // Native refuses a policy missing any Go-authored hazard threshold (#2587).
        ["seriousSingleHitDamage"] = (double)PolicyFunctions.SeriousSingleHitDamage,
        ["seriousSummaryHealthFloor"] = (double)PolicyFunctions.SeriousSummaryHealthFloor,
        ["seriousBleedRateFloor"] = (double)PolicyFunctions.SeriousBleedRateFloor,
        ["seriousVitalPartFloor"] = (double)PolicyFunctions.SeriousVitalPartFloor,
        ["explosiveNearMarginCells"] = (double)PolicyFunctions.ExplosiveNearMarginCells,
        ["meleeReachCells"] = (double)PolicyFunctions.MeleeReachCells,
        ["injurySeverityFloorTicks"] = PolicyFunctions.InjurySeverityFloorTicks,
        ["predatorMarginCells"] = (double)PolicyFunctions.HuntPredatorMarginCells,
    };

    /// <summary>The hold reasons that mean an external actor now owns the clock's fate (Go <c>scenarioHoldReasons</c>).</summary>
    private static readonly HashSet<string> HoldReasons = new(StringComparer.Ordinal)
    {
        "external_pause",
        "external_speed_changed",
        "session_changed",
        "unavailable",
        "watcher_error",
        "run_refused",
        "event_journal_error",
        "hunting_route_unsafe",
    };

    private readonly Stopwatch _sinceBeat = Stopwatch.StartNew();
    private ulong _cursor;

    public ScenarioClock(WireFunc wire, JsonObject identity, Report? report)
    {
        ArgumentNullException.ThrowIfNull(wire);
        ArgumentNullException.ThrowIfNull(identity);
        Wire = wire;
        Identity = identity;
        Report = report;
    }

    /// <summary>The native transport (ordinarily <see cref="Harness.WireFunc"/>).</summary>
    public WireFunc Wire { get; }

    public JsonObject Identity { get; }

    public Report? Report { get; }

    /// <summary>When non-empty, switches every clock_run this clock issues to WATCH_MODE_COMBAT with these acknowledged hostiles.</summary>
    public IReadOnlyList<string> CombatTargets { get; set; } = [];

    /// <summary>When set, edits every clock_run's watch policy after the combat-target injection (a case arming combat stop events).</summary>
    public Action<JsonObject>? PolicyEdit { get; set; }

    /// <summary>
    /// Opts AdvanceGame into Ultrafast. Native serves every Run(Ultrafast) with the 150x boost (9000 ticks/s) under the
    /// one ceiling, with no launch gate; the acceptance launches' -rimgovernor-test-acceleration
    /// (<c>Status.test_acceleration_available</c>) only gates the acceptance-only world patches.
    /// </summary>
    public bool TestAcceleration { get; set; }

    /// <summary>
    /// The current authority grant (SetMode(Auto)'s "granted" body: {context, authority}); its
    /// context.nativeGeneration is the generation every Run below is admitted against. Settable so callers
    /// can seed or recover it.
    /// </summary>
    public JsonObject? Grant { get; set; }

    /// <summary>The last observed external-hold reason; AdvanceGame refuses to proceed once set, mirroring supervisor.hold.</summary>
    public string Hold { get; set; } = "";

    /// <summary>The game tick the latest Run began at (the status context's tick in its reply).</summary>
    public ulong RunStartTick { get; private set; }

    private static JsonNode? Dig(JsonNode? root, params string[] path)
    {
        var current = root;
        foreach (var key in path)
        {
            if (current.AsMap() is not { } next)
            {
                return null;
            }

            current = next[key];
        }

        return current;
    }

    /// <summary>Grants Auto at the current native generation and records the granted body as <see cref="Grant"/>. Authority has no lease.</summary>
    public async Task<JsonObject> AcquireAsync(string label, CancellationToken cancellationToken)
    {
        var grant = await Authority.GrantAutoAsync(Wire, label, Identity, cancellationToken).ConfigureAwait(false);
        Grant = grant;
        return grant;
    }

    /// <summary>
    /// Proves <see cref="Grant"/> is still the live authority: Active(Auto) at exactly Grant's generation. Authority
    /// no longer lapses on its own, so "renewing" means confirming generation continuity rather than extending a
    /// lease; it deliberately does not re-issue SetMode, which would advance the generation. Callers that need
    /// authority back after it moved re-acquire.
    /// </summary>
    public async Task RenewAuthorityAsync(CancellationToken cancellationToken)
    {
        if (Grant is null)
        {
            throw new AcceptanceException("renew requires an existing authority grant");
        }

        var (status, generation) = await Authority.AuthorityStatusAsync(Wire, "authority-renew", Identity, cancellationToken).ConfigureAwait(false);
        if (Dig(status, "active", "mode").AsString() != "MODE_AUTO")
        {
            throw new AcceptanceException($"authority is no longer Auto: {status.ToJson()}");
        }

        var granted = Authority.GrantGeneration(Grant);
        if (generation != granted)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"authority generation moved from {granted} to {generation}"));
        }
    }

    /// <summary>
    /// Runs the clock at <paramref name="speed"/> under the scenario watch policy: resumes a held clock, or changes the
    /// speed or policy in place while running (Run is idempotent). There is no tick budget; the clock runs until a hold.
    /// The event cursor is seeded to just before this Run so a case that polls reads this run's own events, never the
    /// retained history before it.
    /// </summary>
    public async Task<JsonObject> RunAsync(string speed, CancellationToken cancellationToken)
    {
        if (speed is not ("Normal" or "Fast" or "Superfast" or "Ultrafast"))
        {
            throw new AcceptanceException($"unsupported clock speed \"{speed}\"");
        }

        if (TestAcceleration && speed != "Ultrafast")
        {
            throw new AcceptanceException("test acceleration requires Ultrafast");
        }

        if (Hold.Length != 0)
        {
            throw new AcceptanceException("external clock hold: " + Hold);
        }

        var before = await Wire("clock-before-run", "clock_read_status", new JsonObject { ["identity"] = Identity.DeepClone() }, cancellationToken).ConfigureAwait(false);
        var (_, beforeStatus) = Harness.Outcome(before, "status");
        if (!Dyn.DeepEqual(Dig(beforeStatus, "context", "identity"), Identity))
        {
            throw new AcceptanceException($"pre-run status identity mismatch: {beforeStatus.ToJson()}");
        }

        var watermark = beforeStatus["newestCursor"] is { } cursorNode ? ScenarioProjection.ScenarioInteger(cursorNode) : 0;
        var request = new JsonObject
        {
            ["identity"] = Identity.DeepClone(),
            ["expectedGeneration"] = Dig(Grant, "context", "nativeGeneration")?.DeepClone(),
            ["speed"] = "SPEED_" + speed.ToUpperInvariant(),
            ["policy"] = Policy(),
        };
        var reply = await Wire("clock-run", "clock_run", request, cancellationToken).ConfigureAwait(false);
        var projected = ProjectReply(reply, "clock run");
        if (projected["active"].AsBool() != true)
        {
            throw new AcceptanceException($"clock run left the clock held ({ScenarioProjection.NamedStopReason(projected["stopReason"].AsString())}): {projected.ToJson()}");
        }

        _sinceBeat.Restart();
        RunStartTick = projected["lastTick"].AsUInt64() ?? 0;
        projected["startTick"] = RunStartTick;
        projected["newestCursor"] = watermark;
        SeekEvents(watermark);
        return projected;
    }

    /// <summary>
    /// Holds the clock for a brain reason ("REVIEW": the harness is the reviewer) and returns its projected status.
    /// Native keeps the first reason when the clock is already held, so check <c>stopReason</c> for who won.
    /// </summary>
    public async Task<JsonObject> HoldAsync(string reason, string detail, CancellationToken cancellationToken)
    {
        var reply = await Wire(
            "clock-hold",
            "clock_hold",
            new JsonObject
            {
                ["identity"] = Identity.DeepClone(),
                ["reason"] = "HOLD_REASON_" + reason.ToUpperInvariant(),
                ["detail"] = detail,
            },
            cancellationToken).ConfigureAwait(false);
        return ProjectReply(reply, "clock hold");
    }

    /// <summary>One heartbeat: the contact that keeps the native ceiling from dropping to Normal.</summary>
    public async Task BeatAsync(CancellationToken cancellationToken)
    {
        var reply = await Wire("clock-heartbeat", "clock_heartbeat", new JsonObject { ["identity"] = Identity.DeepClone() }, cancellationToken).ConfigureAwait(false);
        _ = Harness.Outcome(reply, "ack");
        _sinceBeat.Restart();
    }

    /// <summary>Beats when the last beat is older than the cadence; a wait loop calls this every pass.</summary>
    public Task BeatIfDueAsync(CancellationToken cancellationToken) =>
        _sinceBeat.Elapsed >= HeartbeatEvery ? BeatAsync(cancellationToken) : Task.CompletedTask;

    /// <summary>
    /// Polls the clock status (beating as due) every 100 ms until <paramref name="done"/> accepts it, returning that
    /// status; throws when <paramref name="timeout"/> passes first.
    /// </summary>
    public async Task<JsonObject> WaitAsync(Func<JsonObject, bool> done, TimeSpan timeout, string what, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(done);
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            await BeatIfDueAsync(cancellationToken).ConfigureAwait(false);
            var status = await CallAsync("status", null, cancellationToken).ConfigureAwait(false);
            if (done(status))
            {
                return status;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new AcceptanceException($"timed out after {timeout} waiting for {what}: {status.ToJson()}");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Waits until the clock is held (by native or by a brain reason) and returns its status.</summary>
    public Task<JsonObject> AwaitHeldAsync(TimeSpan timeout, string what, CancellationToken cancellationToken) =>
        WaitAsync(status => status["held"].AsBool() == true, timeout, what, cancellationToken);

    /// <summary>Issues a read-only clock op ("status" or "events"), mirroring TypedScenarioClock.call().</summary>
    public async Task<JsonObject> CallAsync(string op, JsonObject? arguments, CancellationToken cancellationToken)
    {
        switch (op)
        {
            case "events":
            {
                var after = ScenarioProjection.ScenarioInteger(arguments?["afterCursor"]);
                var reply = await Wire(
                    "clock-events",
                    "clock_read_events",
                    new JsonObject
                    {
                        ["identity"] = Identity.DeepClone(),
                        ["afterCursor"] = after,
                        ["limit"] = arguments?["limit"]?.DeepClone(),
                    },
                    cancellationToken).ConfigureAwait(false);
                var (_, page) = Harness.Outcome(reply, "page");
                return ScenarioProjection.ProjectEvents(page, after);
            }

            case "status":
            {
                var reply = await Wire("clock-status", "clock_read_status", new JsonObject { ["identity"] = Identity.DeepClone() }, cancellationToken).ConfigureAwait(false);
                var (_, status) = Harness.Outcome(reply, "status");
                return FinishStatus(status);
            }

            default:
                throw new AcceptanceException($"unsupported scenario clock op \"{op}\"");
        }
    }

    private JsonObject Policy()
    {
        var policy = ScenarioPolicy();
        if (CombatTargets.Count > 0)
        {
            var targets = new JsonArray();
            foreach (var target in CombatTargets)
            {
                targets.Add(target);
            }

            policy["mode"] = "WATCH_MODE_COMBAT";
            policy["acknowledgedHostileIds"] = targets;
        }

        PolicyEdit?.Invoke(policy);
        return policy;
    }

    // A Run or Hold reply is a Status or a Failure; a failure names the refusal.
    private JsonObject ProjectReply(JsonObject reply, string what)
    {
        if (reply["failure"] is JsonObject failure)
        {
            throw new AcceptanceException($"{what} refused: {failure.ToJson()}");
        }

        var (_, status) = Harness.Outcome(reply, "status");
        return FinishStatus(status);
    }

    private JsonObject FinishStatus(JsonObject status)
    {
        if (!Dyn.DeepEqual(Dig(status, "context", "identity"), Identity))
        {
            throw new AcceptanceException($"clock status identity mismatch: {status.ToJson()}");
        }

        var projected = ScenarioProjection.ProjectStatus(status);
        var reason = projected["stopReason"].AsString();
        if (HoldReasons.Contains(reason))
        {
            Hold = reason;
        }

        return projected;
    }

    /// <summary>
    /// Moves the Poll cursor forward to <paramref name="cursor"/> so a case that polls the events of a run it started
    /// itself reads from that run, never from the retained history before it. Never regresses.
    /// </summary>
    public void SeekEvents(ulong cursor)
    {
        if (cursor > _cursor)
        {
            _cursor = cursor;
        }
    }

    /// <summary>Drains events since the last Poll, mirroring TypedScenarioClock.poll().</summary>
    public async Task<IReadOnlyList<JsonNode?>> PollAsync(CancellationToken cancellationToken)
    {
        var batch = await CallAsync("events", new JsonObject { ["afterCursor"] = _cursor, ["limit"] = 128 }, cancellationToken).ConfigureAwait(false);
        _cursor = ScenarioProjection.ScenarioInteger(batch["nextCursor"]);
        return [.. batch["events"].AsSlice().Select(row => row?.DeepClone())];
    }
}
