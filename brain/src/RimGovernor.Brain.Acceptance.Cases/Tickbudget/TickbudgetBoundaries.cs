using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>tickbudget/boundaries</c> (Go <c>tickbudget/tickbudget.go</c>): exact native execution tick boundaries and
/// external clock ownership in a private game. Every speed runs exactly its tick budget and pauses verified, an
/// external pause or speed change stops the supervised clock and holds it until the player resumes, a lease expires
/// before its tick limit, and loading a save retires the old watcher without claiming the new session's clock.
/// </summary>
internal static class TickbudgetBoundaries
{
    private static readonly string[] Speeds = ["Normal", "Fast", "Superfast"];
    private static readonly ulong[] Budgets = [1, 37, 600];

    internal static AcceptanceCase Case() => new()
    {
        Name = "tickbudget/boundaries",
        Scope = "Verify exact native execution boundaries and external clock ownership in a private game.",
        Start = new LabStart(),
        Budget = TimeSpan.FromMinutes(5),
        Crew = new Crew { Size = 3 },
        Matrix = true,
        Run = RunAsync,
    };

    private static string RandomOwner() => "rimgovernor-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var report = s.Report;
        var h = s.Harness;

        // load reloads the lab and returns a typed clock, holding Auto, on the fresh load's identity: a load changes
        // the identity and the authority generation, so neither carries across.
        var labSave = report["start"].AsMap()?["save"].AsString() ?? "";
        if (labSave.Length == 0)
        {
            throw new AcceptanceException($"lab start recorded no cached save to reload: {report["start"].ToJson()}");
        }

        async Task<ScenarioClock> Load(string label)
        {
            _ = await h.CallAsync(
                label,
                "rimgovernor/load_game_ready",
                new JsonObject { ["saveName"] = labSave, ["readiness"] = "visual", ["timeoutMs"] = 90000, ["ignoreModCompatibility"] = false },
                cancellationToken).ConfigureAwait(false);
            var identity = await Session.ReadIdentityAsync(h, label + "-identity", cancellationToken).ConfigureAwait(false);
            var loaded = new ScenarioClock(h.WireFunc(), identity, RandomOwner(), report);
            _ = await loaded.AcquireAsync(label + "-acquire", cancellationToken).ConfigureAwait(false);
            return loaded;
        }

        async Task<JsonObject> Stopped(ScenarioClock c, string label)
        {
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (true)
            {
                JsonObject result;
                try
                {
                    result = await c.CallAsync("status", null, cancellationToken).ConfigureAwait(false);
                }
                catch (AcceptanceException e)
                {
                    throw new AcceptanceException($"{label}: {e.Message}", e);
                }

                if (result["active"].AsBool() != true)
                {
                    return result;
                }

                if (DateTime.UtcNow > deadline)
                {
                    throw new AcceptanceException($"{label}: timed out waiting for the native clock to stop");
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
            }
        }

        ScenarioClock? clock = null;
        var results = new JsonArray();
        foreach (var speed in Speeds)
        {
            foreach (var budget in Budgets)
            {
                var label = string.Create(CultureInfo.InvariantCulture, $"{speed}-{budget}");
                clock = await Load("load-" + label).ConfigureAwait(false);
                JsonObject start;
                try
                {
                    start = await clock.ChangeAsync(speed, budget, cancellationToken).ConfigureAwait(false);
                }
                catch (AcceptanceException e)
                {
                    throw new AcceptanceException($"start-{label}: {e.Message}", e);
                }

                clock.SeekEvents(start);
                var end = await Stopped(clock, "stopped-" + label).ConfigureAwait(false);
                var pauseVerified = end["pauseVerified"].AsBool() == true;
                if (end["stopReason"].AsString() != "tick_budget" || !pauseVerified)
                {
                    throw new AcceptanceException($"{label}: unexpected stop result {end.ToJson()}");
                }

                if (end["tickDeadline"].AsNumber() != start["startTick"].AsNumber() + budget)
                {
                    throw new AcceptanceException($"{label}: tick deadline did not match start tick plus budget: start={start.ToJson()} end={end.ToJson()}");
                }

                var observed = await RunAt.ReadPauseStateAsync(h.WireFunc(), "readback-" + label, cancellationToken).ConfigureAwait(false);
                if (observed["tick"].AsNumber() != end["tickDeadline"].AsNumber())
                {
                    throw new AcceptanceException($"{label}: readback tick did not match tickDeadline: {observed.ToJson()}");
                }

                if (observed["paused"].AsBool() != true)
                {
                    throw new AcceptanceException($"{label}: readback was not paused: {observed.ToJson()}");
                }

                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
                var stable = await RunAt.ReadPauseStateAsync(h.WireFunc(), "stable-" + label, cancellationToken).ConfigureAwait(false);
                if (stable["tick"].AsNumber() != end["tickDeadline"].AsNumber())
                {
                    throw new AcceptanceException($"{label}: game ticks moved after the verified pause: {stable.ToJson()}");
                }

                IReadOnlyList<JsonNode?> events;
                try
                {
                    events = await clock.PollAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (AcceptanceException e)
                {
                    throw new AcceptanceException($"drain-{label}: {e.Message}", e);
                }

                var tickBudgetEvents = events.Count(raw => raw.ToJson().Contains("STOP_REASON_TICK_BUDGET", StringComparison.Ordinal));
                if (tickBudgetEvents > 1)
                {
                    throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"{label}: expected at most one tick_budget event, found {tickBudgetEvents}"));
                }

                if (clock.Hold.Length != 0)
                {
                    throw new AcceptanceException($"{label}: unexpected clock hold \"{clock.Hold}\"");
                }

                results.Add(new JsonObject
                {
                    ["speed"] = speed,
                    ["budget"] = budget,
                    ["start"] = start.DeepClone(),
                    ["stop"] = end.DeepClone(),
                    ["readback"] = observed.DeepClone(),
                    ["stable"] = stable.DeepClone(),
                });
                await Console.Out.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"PASS {speed}: exactly {budget} ticks")).ConfigureAwait(false);
            }
        }

        report["results"] = results.DeepClone();
        clock ??= await Load("load-initial").ConfigureAwait(false);

        foreach (var (externalSpeed, reason) in new[] { ("Paused", "external_pause"), ("Fast", "external_speed_changed") })
        {
            try
            {
                _ = await clock.ChangeAsync("Normal", 3000, cancellationToken).ConfigureAwait(false);
            }
            catch (AcceptanceException e)
            {
                throw new AcceptanceException($"external-resume-{reason}: {e.Message}", e);
            }

            _ = await h.CallAsync(
                "external-override-" + reason,
                "rimgovernor/set_time_speed",
                new JsonObject { ["speed"] = externalSpeed, ["ultraSpeedBoost"] = false },
                cancellationToken).ConfigureAwait(false);
            var end = await Stopped(clock, "external-stopped-" + reason).ConfigureAwait(false);
            if (end["stopReason"].AsString() != reason)
            {
                throw new AcceptanceException($"external-{reason}: unexpected stop result {end.ToJson()}");
            }

            try
            {
                _ = await clock.ChangeAsync("Normal", 100, cancellationToken).ConfigureAwait(false);
                throw new AcceptanceException($"external hold \"{reason}\" was overridden");
            }
            catch (AcceptanceException e) when (e.Message.Contains("external clock hold", StringComparison.Ordinal))
            {
                // The documented refusal: an external hold stays until the player resumes.
                Tolerated.Note("external hold refusal", e);
            }

            clock.Hold = "";
            results.Add(new JsonObject { ["external_speed"] = externalSpeed, ["stop"] = end.DeepClone() });
            await Console.Out.WriteLineAsync($"PASS {reason}: explicit resume required").ConfigureAwait(false);
        }

        report["results"] = results.DeepClone();

        // A lease that is never renewed stops the clock before its tick limit.
        clock.LeaseMs = 1000;
        try
        {
            _ = await clock.ChangeAsync("Normal", 3000, cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"lease-start: {e.Message}", e);
        }

        clock.LeaseMs = 0;
        var expired = await Stopped(clock, "lease-expired").ConfigureAwait(false);
        var expiredVerified = expired["pauseVerified"].AsBool() == true;
        if (expired["stopReason"].AsString() != "lease_expired" || !expiredVerified)
        {
            throw new AcceptanceException($"lease-expired: unexpected stop result {expired.ToJson()}");
        }

        if (expired["lastTick"].AsNumber() >= expired["tickDeadline"].AsNumber())
        {
            throw new AcceptanceException($"lease-expired: expected lastTick before tickDeadline: {expired.ToJson()}");
        }

        results.Add(new JsonObject { ["lease_expiry"] = expired.DeepClone() });
        report["results"] = results.DeepClone();
        await Console.Out.WriteLineAsync("PASS lease expiry before tick limit").ConfigureAwait(false);

        // Loading a native save retires the old watcher without claiming the new session's clock or carrying its old
        // tick deadline over.
        clock = await Load("prelude-load").ConfigureAwait(false);
        try
        {
            _ = await clock.ChangeAsync("Normal", 3000, cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"prelude-start: {e.Message}", e);
        }

        clock = await Load("reload-baseline").ConfigureAwait(false);
        var changed = await Stopped(clock, "session-changed").ConfigureAwait(false);
        if (changed["stopReason"].AsString() != "session_changed")
        {
            throw new AcceptanceException($"session-changed: unexpected stop result {changed.ToJson()}");
        }

        results.Add(new JsonObject { ["load_change"] = changed.DeepClone() });
        report["results"] = results.DeepClone();
    }
}
