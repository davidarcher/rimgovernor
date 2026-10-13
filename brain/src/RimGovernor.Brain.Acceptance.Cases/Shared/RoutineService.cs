using System.Globalization;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// What the service-driven routine verticals (floor, light, power, refrigeration) share: the launch-and-acquire sequence,
/// the bounded waits over the player API and the independent native read after the service lets go of the game (Go: the
/// repeated preamble of each case's <c>run</c>).
/// </summary>
/// <remarks>
/// Go read the service's durable journal (<c>store.Store</c>: the Rounds record, plans, goals). The brain now runs in the
/// mod and the player API is the only window onto it, so a latch is observed as the concern's row in
/// <c>/api/routines</c> and a plan as <c>/api/plan</c>. The API has no latch sets, no standard bindings and no goal
/// method history; the cases state where they fall back to the independent native census instead.
/// </remarks>
internal sealed class RoutineService
{
    private Func<Task<JsonObject>>? _stopKeepAlive;

    private RoutineService(ServiceProcess service, string token)
    {
        Service = service;
        Token = token;
    }

    /// <summary>The running service handle.</summary>
    internal ServiceProcess Service { get; }

    /// <summary>The explicit-player session token (empty on the C# backend).</summary>
    internal string Token { get; }

    /// <summary>What the authority keep-alive counted, once <see cref="StopKeepAliveAsync"/> ran.</summary>
    internal JsonObject? Reacquisitions { get; private set; }

    /// <summary>
    /// Launches the service composed with <paramref name="families"/>, waits for it to attach to the fixture's identity,
    /// resumes without an anchor plan (a player construction project would commit the colony's builder), starts the
    /// authority keep-alive and waits for the first persisted rounds pass.
    /// </summary>
    internal static async Task<RoutineService> StartAsync(ISession s, string prefix, IReadOnlyList<string> families, CancellationToken cancellationToken)
    {
        var report = s.Report;
        var service = await s.LaunchAsync(new ServiceLaunch { Families = families }, cancellationToken).ConfigureAwait(false);
        var token = await service.SessionTokenAsync(cancellationToken).ConfigureAwait(false);
        var attached = await service.WaitAttachedAsync(s.Identity, TimeSpan.FromSeconds(90), cancellationToken).ConfigureAwait(false);
        report["service_state_attached"] = attached.DeepClone();
        var rootPlanId = await service.ResumeAsync(prefix, s.Identity, token, report, cancellationToken).ConfigureAwait(false);
        report["root_plan"] = rootPlanId;
        var run = new RoutineService(service, token);
        run._stopKeepAlive = new AuthorityKeepAlive(service, prefix, s.Identity, token).Start(cancellationToken);
        try
        {
            var (review, diagnostics) = await service.WaitRoundsAsync(TimeSpan.FromSeconds(90), cancellationToken).ConfigureAwait(false);
            report["diagnostic_post_acquire"] = new JsonArray([.. diagnostics.Select(d => (JsonNode?)d.DeepClone())]);
            report["rounds_review_first"] = review.DeepClone();
        }
        catch (Exception)
        {
            _ = await run.StopKeepAliveAsync().ConfigureAwait(false);
            throw;
        }

        return run;
    }

    /// <summary>Stops the keep-alive (idempotent) and returns what it counted.</summary>
    internal async Task<JsonObject> StopKeepAliveAsync()
    {
        if (_stopKeepAlive is { } stop)
        {
            _stopKeepAlive = null;
            Reacquisitions = await stop().ConfigureAwait(false);
        }

        return Reacquisitions ?? [];
    }

    /// <summary>
    /// Runs one wait under a wall-clock limit (Go: <c>context.WithTimeout</c> around the wait). A limit that fired surfaces as
    /// a failure naming <paramref name="what"/>; the caller's own cancellation stays a cancellation.
    /// </summary>
    internal static async Task<T> BoundedAsync<T>(Func<CancellationToken, Task<T>> wait, TimeSpan limit, string what, CancellationToken caller)
    {
        ArgumentNullException.ThrowIfNull(wait);
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(caller);
        bound.CancelAfter(limit);
        try
        {
            return await wait(bound.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException e) when (bound.IsCancellationRequested && !caller.IsCancellationRequested)
        {
            throw new AcceptanceException($"{what}: timed out after {limit}", e);
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"{what}: {e.Message}", e);
        }
    }

    /// <summary>The bounds of every journal poll: the shared stall budget, and the service exiting on its own ends a wait at once.</summary>
    internal Wait StoreWait() => new() { Stall = Wait.StallBudget(), Terminal = Service.Exited };

    /// <summary>
    /// Waits until the routines read binds <paramref name="concern"/> (a <c>progress</c> row names it): the review has
    /// latched the measured deficiency and committed the standing goal. Go also required the latch set to hold the
    /// subject; the API does not expose it, so the case's native census read before launch carries that fact.
    /// </summary>
    internal async Task<JsonObject> WaitBoundAsync(string concern, TimeSpan ceiling, CancellationToken cancellationToken)
    {
        var wait = StoreWait() with { Ceiling = ceiling };
        try
        {
            return await Service.WaitReviewAsync(wait, routines => ConcernRow(routines, concern) is not null, cancellationToken).ConfigureAwait(false);
        }
        catch (WaitError e)
        {
            throw new AcceptanceException($"review never bound {concern}: {e.Message}", e);
        }
    }

    /// <summary>The <c>/api/routines</c> progress row of <paramref name="concern"/>, or null when it is not bound.</summary>
    internal static JsonObject? ConcernRow(JsonObject routines, string concern) =>
        routines["progress"].AsSlice().Select(row => row.AsMap()).FirstOrDefault(row => row?["concern"].AsString() == concern);

    /// <summary>
    /// Reads the typed upkeep section <paramref name="field"/> of the colony facts the way the Go projection does: the
    /// upkeep section's observed arm, its issues (a census that could not be taken fails the read) and the field's own
    /// observed arm, returned.
    /// </summary>
    internal static async Task<JsonObject> ReadUpkeepAsync(Harness h, JsonObject identity, string label, string field, CancellationToken cancellationToken)
    {
        var reply = await h.WireAsync(
            label,
            "observations_read_colony_facts",
            new JsonObject { ["scope"] = new JsonObject { ["expectedIdentity"] = identity.DeepClone() }, ["planning"] = false },
            cancellationToken).ConfigureAwait(false);
        var (_, observed) = Harness.Outcome(reply, "observed");
        var upkeep = SectionArm(observed["upkeep"], label, "upkeep unavailable");
        foreach (var raw in upkeep["issues"].AsSlice())
        {
            var issue = raw.AsMap() ?? [];
            if (issue["field"].AsString() == field)
            {
                throw new AcceptanceException($"{label}: {field} census unavailable: {issue.ToJson()}");
            }
        }

        return SectionArm(upkeep[field], label, field + " section unavailable");
    }

    // The "observed" arm of a typed section outcome, or a failure naming the section.
    private static JsonObject SectionArm(JsonNode? section, string label, string what)
    {
        try
        {
            return Harness.Outcome(section.AsMap() ?? [], "observed").Value;
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"{label}: {what}: {e.Message}", e);
        }
    }

    /// <summary>The game tick the brain's snapshot reports (<c>/api/state</c> <c>game.tick</c>).</summary>
    internal async Task<ulong> GameTickAsync(CancellationToken cancellationToken)
    {
        var state = await Service.GetAsync("GET", "/api/state", cancellationToken).ConfigureAwait(false);
        return state["game"].AsMap()?["tick"].AsUInt64() ?? 0;
    }

    /// <summary>GET /api/plan?id=...; the body of the plan.</summary>
    internal async Task<JsonObject> LoadPlanAsync(string planId, CancellationToken cancellationToken)
    {
        var reply = await Service.ApiAsync("GET", "/api/plan?id=" + Uri.EscapeDataString(planId), null, "", cancellationToken).ConfigureAwait(false);
        if (reply.Status != 200)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"load plan {planId}: status {reply.Status} body={reply.Body.ToJson()}"));
        }

        return reply.Body ?? [];
    }

    /// <summary>The building a plan action places (<c>defName</c>, <c>x</c>, <c>z</c>), or null for a non-building action.</summary>
    internal static JsonObject? Building(JsonNode? action) => action.AsMap()?["building"].AsMap();

    /// <summary>The game tick at which a finished plan's first action completed.</summary>
    internal static long CompletedTick(JsonObject state) =>
        (long)(state["actions"].AsSlice() is { Count: > 0 } actions ? actions[0].AsMap()?["progress"].AsMap()?["tick"].AsNumber() ?? 0 : 0);

    /// <summary>
    /// Waits for a native census to satisfy <paramref name="done"/> while the service keeps the game running, bounded by
    /// <paramref name="ceiling"/> and the shared stall budget over <paramref name="signature"/>. This is the case's
    /// replacement for the journal's released latch: the measured census, not the receipt, is what the latch follows.
    /// </summary>
    internal async Task WaitCensusAsync(string what, TimeSpan ceiling, Func<CancellationToken, Task<(string Signature, bool Done)>> census, CancellationToken cancellationToken, ulong ticks = 0)
    {
        var wait = StoreWait() with { Ceiling = ceiling };
        if (ticks > 0)
        {
            // The census moves only as the game runs, so the game-time budget bounds the cooling itself and the signature
            // is whatever the census reports.
            wait = wait with { Interval = TimeSpan.FromSeconds(1), Ticks = ticks, Tick = GameTickAsync };
        }

        try
        {
            await wait.WaitProgressAsync(new Probe(census), cancellationToken).ConfigureAwait(false);
        }
        catch (WaitError e)
        {
            throw new AcceptanceException($"{what}: {e.Message}", e);
        }
    }

    /// <summary>
    /// Waits until the supervised clock is back in automate mode: a momentary Manual state while the keep-alive renews
    /// authority is not a terminal failure, persistent loss fails within the shared stall budget.
    /// </summary>
    internal async Task WaitRunningAsync(string what, CancellationToken cancellationToken)
    {
        Exception? last = null;
        try
        {
            await StoreWait().WaitProgressAsync(
                async ct =>
                {
                    try
                    {
                        await Rounds.AssertRoundsRunningAsync(Service.GetAsync, ct).ConfigureAwait(false);
                        last = null;
                    }
                    catch (AcceptanceException e)
                    {
                        last = e;
                    }

                    return ("authority", last is null);
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (WaitError e)
        {
            throw new AcceptanceException($"{what} authority recovery: {e.Message} (last state: {last?.Message})", e);
        }
    }

    /// <summary>
    /// Lets the harness hold the game again: stops the keep-alive and the service, reattaches and pauses the clock, the
    /// independent native read after the service releases the game slot.
    /// </summary>
    internal async Task<Harness> ReleaseAsync(ISession s, CancellationToken cancellationToken)
    {
        s.Report["authority_reacquisitions"] = (await StopKeepAliveAsync().ConfigureAwait(false)).DeepClone();
        _ = await Service.StopAsync().ConfigureAwait(false);
        var h = s.Reattach();
        _ = await h.CallAsync("pause-after", "rimgovernor/set_time_speed", new JsonObject { ["speed"] = "Paused", ["ultraSpeedBoost"] = false }, cancellationToken).ConfigureAwait(false);
        return h;
    }

    /// <summary>
    /// The failure evidence of a case that did not reach its own final read: stops the service and runs
    /// <paramref name="read"/> over the reattached harness, recording a failure of either under <c>postmortem_error</c>.
    /// </summary>
    internal static async Task PostmortemAsync(ISession s, RoutineService? run, Func<Harness, CancellationToken, Task> read)
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try
        {
            if (run is not null)
            {
                s.Report["authority_reacquisitions"] ??= (await run.StopKeepAliveAsync().ConfigureAwait(false)).DeepClone();
                _ = await run.Service.StopAsync().ConfigureAwait(false);
            }

            await read(s.Reattach(), bound.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException || bound.IsCancellationRequested)
        {
            s.Report["postmortem_error"] = "reopen session for the postmortem: " + e.Message;
        }
    }
}
