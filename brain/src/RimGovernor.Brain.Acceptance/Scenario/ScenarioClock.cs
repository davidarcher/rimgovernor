using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Acceptance;

// This file is the typed scenario clock, so the combat and movement acceptance cases can advance real game ticks in a
// bounded, letter-acknowledging way (Go scenario.go). It intentionally omits a medical-rest wait mode: only the
// combat-target mode is exercised through the typed clock adapter.

/// <summary>One owned clock control call, so AdvanceGame's caller can replay it after a stop or revocation (Go <c>ScenarioControl</c>).</summary>
public sealed class ScenarioControl
{
    public ScenarioControl(string method, JsonObject request, JsonObject receipt)
    {
        Method = method;
        Request = request;
        Receipt = receipt;
    }

    public string Method { get; }

    public JsonObject Request { get; }

    public JsonObject Receipt { get; }
}

/// <summary>
/// The typed clock/authority transport adapter AdvanceGame drives. Every native call goes through <see cref="Wire"/>
/// (ordinarily <see cref="Harness.WireFunc"/>), so this type owns only the owner-side bookkeeping (grant, attempt
/// counter, event cursor, replay log) AdvanceGame needs to safely resume, replay and detect drift (Go <c>ScenarioClock</c>).
/// </summary>
public sealed class ScenarioClock
{
    /// <summary>The fixed watch policy every acceptance clock window starts with (Go <c>scenarioPolicy</c>).</summary>
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

    /// <summary>The stop reasons that mean an external actor now owns the clock's fate (Go <c>scenarioHoldReasons</c>).</summary>
    private static readonly HashSet<string> HoldReasons = new(StringComparer.Ordinal)
    {
        "external_pause",
        "external_speed_changed",
        "lease_expired",
        "session_changed",
        "unavailable",
        "watcher_error",
        "start_refused",
        "force_paused",
        "event_journal_error",
        "hunting_route_unsafe",
    };

    private readonly List<ScenarioControl> _controls = [];
    private int _counter;
    private ulong _cursor;

    public ScenarioClock(WireFunc wire, JsonObject identity, string owner, Report? report)
    {
        ArgumentNullException.ThrowIfNull(wire);
        ArgumentNullException.ThrowIfNull(identity);
        Wire = wire;
        Identity = identity;
        Owner = owner;
        Report = report;
    }

    /// <summary>The native transport (ordinarily <see cref="Harness.WireFunc"/>).</summary>
    public WireFunc Wire { get; }

    public JsonObject Identity { get; }

    public string Owner { get; }

    public Report? Report { get; }

    /// <summary>When non-empty, switches every clock_start this clock issues to WATCH_MODE_COMBAT with these acknowledged hostiles.</summary>
    public IReadOnlyList<string> CombatTargets { get; set; } = [];

    /// <summary>When set, edits every clock_start's watch policy after the combat-target injection (a case arming combat stop events).</summary>
    public Action<JsonObject>? PolicyEdit { get; set; }

    /// <summary>
    /// Opts AdvanceGame into Ultrafast with the native test tick boost. Requires a launch with
    /// -rimgovernor-test-acceleration; false preserves Superfast and all native stop boundaries.
    /// </summary>
    public bool TestAcceleration { get; set; }

    /// <summary>The lease every clock_start requests; zero means 30000.</summary>
    public uint LeaseMs { get; set; }

    /// <summary>
    /// The current authority grant (SetMode(Auto)'s "granted" body: {context, authority}); its
    /// context.nativeGeneration is the generation every owned write below is admitted against. Settable so callers
    /// can seed or recover it.
    /// </summary>
    public JsonObject? Grant { get; set; }

    /// <summary>Runs after Change admits a fresh clock_start receipt but before AdvanceGame observes it, e.g. to revoke authority mid-window.</summary>
    public Func<JsonObject, CancellationToken, Task>? OnStarted { get; set; }

    /// <summary>The last observed external-hold stop reason; AdvanceGame refuses to proceed once set, mirroring supervisor.hold.</summary>
    public string Hold { get; set; } = "";

    /// <summary>The replay log of every owned control call issued so far.</summary>
    public IReadOnlyList<ScenarioControl> Controls => _controls;

    /// <summary>The epoch of the latest admitted control.</summary>
    public ulong Epoch { get; private set; }

    private uint EffectiveLeaseMs => LeaseMs == 0 ? 30000 : LeaseMs;

    private JsonObject Precondition()
    {
        _counter++;
        return new JsonObject
        {
            ["identity"] = Identity.DeepClone(),
            ["expectedGeneration"] = Dig(Grant, "context", "nativeGeneration")?.DeepClone(),
            ["attempt"] = new JsonObject
            {
                ["controllerSessionId"] = Owner,
                ["actionId"] = "typed-clock-" + _counter.ToString(CultureInfo.InvariantCulture),
                ["attemptId"] = "1",
            },
        };
    }

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
    /// lease; it deliberately does not re-issue SetMode, which would advance the generation and interrupt every
    /// attempt admitted under the current one. Callers that need authority back after it moved re-acquire.
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

    /// <summary>Issues one owned clock control call (start/renew/change_speed) and projects its admitted status, mirroring TypedScenarioClock.control().</summary>
    public async Task<JsonObject> ControlAsync(string method, JsonObject request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (method == "start" && CombatTargets.Count > 0)
        {
            request = (JsonObject)request.DeepClone();
            var watch = request["policy"].AsMap() ?? throw new AcceptanceException($"combat start request missing policy: {request.ToJson()}");
            var targets = new JsonArray();
            foreach (var target in CombatTargets)
            {
                targets.Add(target);
            }

            watch["mode"] = "WATCH_MODE_COMBAT";
            watch["acknowledgedHostileIds"] = targets;
        }

        if (method == "start" && PolicyEdit is not null)
        {
            request = (JsonObject)request.DeepClone();
            var watch = request["policy"].AsMap() ?? throw new AcceptanceException($"start request missing policy: {request.ToJson()}");
            PolicyEdit(watch);
        }

        var reply = await Wire(method, "clock_" + method, request, cancellationToken).ConfigureAwait(false);
        var (_, receipt) = Harness.Outcome(reply, "receipt");
        if (!Dyn.DeepEqual(receipt["attempt"], Dig(request, "authority", "attempt")))
        {
            throw new AcceptanceException($"clock receipt attempt mismatch: {receipt.ToJson()}");
        }

        if (!Dyn.DeepEqual(Dig(receipt, "admittedContext", "identity"), Identity))
        {
            throw new AcceptanceException($"clock receipt identity mismatch: {receipt.ToJson()}");
        }

        JsonObject appliedCase;
        if (receipt["applied"] is { } appliedNode)
        {
            appliedCase = new JsonObject { ["applied"] = appliedNode.DeepClone() };
        }
        else if (receipt["uncertain"] is { } uncertainNode)
        {
            appliedCase = new JsonObject { ["uncertain"] = uncertainNode.DeepClone() };
        }
        else
        {
            throw new AcceptanceException($"clock receipt carries neither applied nor uncertain: {receipt.ToJson()}");
        }

        var (_, applied) = Harness.Outcome(appliedCase, "applied");
        var statusValue = applied["status"].AsMap() ?? throw new AcceptanceException($"applied clock control missing status: {applied.ToJson()}");
        var projected = ScenarioProjection.ProjectStatus(statusValue);
        if (projected["owner"].AsString() != Owner)
        {
            throw new AcceptanceException($"clock control admitted a different owner: {projected.ToJson()}");
        }

        if (!Dyn.DeepEqual(Dig(statusValue, "context", "identity"), Identity))
        {
            throw new AcceptanceException($"clock control status identity mismatch: {statusValue.ToJson()}");
        }

        _controls.Add(new ScenarioControl(method, (JsonObject)request.DeepClone(), (JsonObject)receipt.DeepClone()));
        if (Report is not null)
        {
            var log = new JsonArray();
            foreach (var control in _controls)
            {
                log.Add(new JsonObject
                {
                    ["Method"] = control.Method,
                    ["Request"] = control.Request.DeepClone(),
                    ["Receipt"] = control.Receipt.DeepClone(),
                });
            }

            Report["controls"] = log;
        }

        Epoch = ScenarioProjection.ScenarioInteger(projected["epoch"]);
        return projected;
    }

    /// <summary>Starts a bounded native clock window. TestAcceleration requires Ultrafast; native admission enforces the test-only launch gate.</summary>
    public async Task<JsonObject> ChangeAsync(string speed, ulong maxTicks, CancellationToken cancellationToken)
    {
        if (speed is not ("Normal" or "Fast" or "Superfast" or "Ultrafast"))
        {
            throw new AcceptanceException($"unsupported clock speed \"{speed}\"");
        }

        if (TestAcceleration && speed != "Ultrafast")
        {
            throw new AcceptanceException("test acceleration requires Ultrafast");
        }

        if (maxTicks == 0)
        {
            throw new AcceptanceException("maxTicks must be positive");
        }

        if (Hold.Length != 0)
        {
            throw new AcceptanceException("external clock hold: " + Hold);
        }

        var before = await Wire("clock-before-start", "clock_read_status", new JsonObject { ["identity"] = Identity.DeepClone() }, cancellationToken).ConfigureAwait(false);
        var (_, beforeStatus) = Harness.Outcome(before, "status");
        if (!Dyn.DeepEqual(Dig(beforeStatus, "context", "identity"), Identity))
        {
            throw new AcceptanceException($"pre-start status identity mismatch: {beforeStatus.ToJson()}");
        }

        ulong watermark = 0;
        var freshEpoch = false;
        if (beforeStatus["newestCursor"] is { } cursorNode)
        {
            watermark = ScenarioProjection.ScenarioInteger(cursorNode);
        }
        else
        {
            if (!beforeStatus.ContainsKey("neverStarted") || _controls.Count != 0)
            {
                throw new AcceptanceException($"expected a fresh profile with no owned epoch: {beforeStatus.ToJson()}");
            }

            freshEpoch = true;
        }

        var request = new JsonObject
        {
            ["authority"] = Precondition(),
            ["speed"] = "SPEED_" + speed.ToUpperInvariant(),
            ["policy"] = ScenarioPolicy(),
            ["leaseMs"] = EffectiveLeaseMs,
            ["maxTicks"] = maxTicks,
        };
        if (TestAcceleration)
        {
            request["testAcceleration"] = true;
        }

        var status = await ControlAsync("start", request, cancellationToken).ConfigureAwait(false);
        var boundary = status["nativeTickBoundary"].AsBool() == true;
        var tickDeadline = status["tickDeadline"].AsUInt64() ?? 0;
        var startTick = status["startTick"].AsUInt64() ?? 0;
        if (!boundary || tickDeadline - startTick != maxTicks)
        {
            throw new AcceptanceException($"clock start did not admit the requested tick budget: {status.ToJson()}");
        }

        if (OnStarted is not null)
        {
            await OnStarted(status, cancellationToken).ConfigureAwait(false);
        }

        var newest = status["newestCursor"].AsUInt64() ?? 0;
        if (freshEpoch && newest > 0)
        {
            // A fresh profile can still hold retained events: a journal restored from a checkpoint bundle replays the
            // epochs of earlier runs, and an authority shutdown among them has no canonical owner. The receipt's
            // newestCursor is this window's own started event, so a stop diagnosis (letter_pause) reads from just
            // before it, never across that history.
            watermark = newest - 1;
        }

        status["newestCursor"] = watermark;

        // freshEpoch marks a window whose pre-start status reported neverStarted (durableEvents=false is a legitimate,
        // native-acknowledged state in that case, mirroring the policy clock window's own admission check).
        // AdvanceGame must not poll clock_read_events for such a window: with no prior owned epoch, cursor 0 can still
        // land on retained events from unrelated activity (e.g. authority acquire/revoke churn) that the native clock
        // reader refuses as lacking canonical ownership, rather than an empty page.
        status["freshEpoch"] = freshEpoch;
        return status;
    }

    /// <summary>Issues a read-only clock op ("status" or "events") or an owned "pause", mirroring TypedScenarioClock.call().</summary>
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
                        ["afterCursor"] = after.ToString(CultureInfo.InvariantCulture),
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

            case "pause":
            {
                var owner = arguments?["owner"].AsString() ?? "";
                if (owner != Owner)
                {
                    throw new AcceptanceException($"pause owner mismatch: {arguments?["owner"].ToJson()}");
                }

                var reply = await Wire(
                    "clock-pause",
                    "clock_pause",
                    new JsonObject
                    {
                        ["identity"] = Identity.DeepClone(),
                        ["owner"] = new JsonObject
                        {
                            ["controllerSessionId"] = owner,
                            ["epoch"] = (arguments?["epoch"].AsUInt64() ?? 0).ToString(CultureInfo.InvariantCulture),
                        },
                    },
                    cancellationToken).ConfigureAwait(false);
                var (_, status) = Harness.Outcome(reply, "status");
                return FinishStatus(status);
            }

            default:
                throw new AcceptanceException($"unsupported scenario clock op \"{op}\"");
        }
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
    /// Moves the Poll cursor forward to Change()'s newestCursor (the exclusive pre-dispatch watermark) so a case that
    /// polls the events of a window it started itself reads from that window, never from the retained authority churn
    /// before it (see AdvanceGame). Never regresses.
    /// </summary>
    public void SeekEvents(JsonObject started)
    {
        ArgumentNullException.ThrowIfNull(started);
        SeekEvents(started["newestCursor"].AsUInt64() ?? 0);
    }

    /// <summary>Moves the Poll cursor forward to <paramref name="cursor"/>; never regresses.</summary>
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
