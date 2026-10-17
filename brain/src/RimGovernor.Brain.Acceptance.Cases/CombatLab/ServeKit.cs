using System.Globalization;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// What the served combat lab cases share (Go <c>metrics_case.go</c> and <c>hunt.go</c>): the planner families a fight
/// is served with, the pause mode that keeps a letter from pausing the served game, and a window of served game ticks.
/// </summary>
internal static class CombatServe
{
    /// <summary>Sets Prefs.AutomaticPauseMode (scripts/fixtures/LetterFixture.cs).</summary>
    internal const string PauseModeTool = "test/letter_pause_mode";

    /// <summary>Labels the response evidence of a metrics run (the served clock runs Ultrafast uncapped like every acceptance case).</summary>
    internal const string MetricsSpeed = "Ultrafast";

    /// <summary>Today's autopilot answer to a fight: the routine defense planner (hold-the-line / squad defense), with the rescue and tend families it hands downed colonists to.</summary>
    internal static readonly IReadOnlyList<string> MetricsFamilies = ["defense", "tend", "rescue"];

    /// <summary>
    /// Sets the pause mode to Never: a MajorThreat letter mid-fight (a berserk colonist) would pause the game as the
    /// player would, and the authority flip then drops the fight plan.
    /// </summary>
    internal static async Task NeverPauseAsync(Harness h, CancellationToken cancellationToken)
    {
        var reply = await h.CallAsync("pause-mode-never", PauseModeTool, new JsonObject { ["mode"] = "Never" }, cancellationToken).ConfigureAwait(false);
        if (reply["mode"].AsString() != "Never")
        {
            throw new AcceptanceException($"{PauseModeTool} did not take: {reply.ToJson()}");
        }
    }

    /// <summary>Pauses the game after a served window.</summary>
    internal static async Task PauseAfterAsync(Harness h, CancellationToken cancellationToken) =>
        _ = await h.CallAsync("pause-after", "rimgovernor/set_time_speed", new JsonObject { ["speed"] = "Paused", ["ultraSpeedBoost"] = false }, cancellationToken).ConfigureAwait(false);

    /// <summary>A lab_stage read or other action that must succeed (Go <c>rawRead</c>).</summary>
    internal static async Task<JsonObject> RawReadAsync(Harness h, JsonObject args, CancellationToken cancellationToken)
    {
        var reply = await h.CallAsync("combatlab-metrics-" + args["action"].AsString(), CombatFixtures.LabTool, args, cancellationToken).ConfigureAwait(false);
        return reply["success"].AsBool() == true
            ? reply
            : throw new AcceptanceException($"{CombatFixtures.LabTool} {args["action"].ToJson()} refused: {reply.ToJson()}");
    }

    /// <summary>The served game's tick from the player API, -1 until it reports one.</summary>
    internal static async Task<int> ServedTickAsync(ServiceProcess service, int last, CancellationToken cancellationToken)
    {
        try
        {
            var reply = await service.ApiAsync("GET", "/api/state", null, "", cancellationToken).ConfigureAwait(false);
            if (reply.Status == 200 && reply.Body?["game"].AsMap() is { } game && game["tick"] is { } tick)
            {
                return (int)tick.AsNumber();
            }
        }
        catch (AcceptanceException tolerated)
        {
            // The poll is a probe: an answer that did not arrive is retried on the next round.
            Tolerated.Note("served tick poll", tolerated);
        }

        return last;
    }

    /// <summary>Lets the served game run until its tick reaches <paramref name="until"/>, failing when the service exits or the clock sits still for the stall budget.</summary>
    internal static async Task<int> ServeTicksAsync(ServiceProcess service, int until, CancellationToken cancellationToken)
    {
        var tick = -1;
        var wait = new Wait { Stall = Wait.StallBudget(), Interval = TimeSpan.FromSeconds(1), Terminal = service.Exited };
        await wait.WaitProgressAsync(
            async ct =>
            {
                tick = await ServedTickAsync(service, tick, ct).ConfigureAwait(false);
                return (tick.ToString(CultureInfo.InvariantCulture), tick >= until);
            },
            cancellationToken).ConfigureAwait(false);
        return tick;
    }

    /// <summary>
    /// Serves the planners for <paramref name="ticks"/> game ticks past the current tick, then stops the service and
    /// reattaches the harness. <paramref name="resume"/> starts the service on an already running fight.
    /// </summary>
    internal static async Task<Harness> ServedWindowAsync(ISession s, int ticks, bool resume, CancellationToken cancellationToken)
    {
        var (_, start) = await CombatStage.ReadAsync(s.Harness, cancellationToken).ConfigureAwait(false);
        var service = await s.ServeAsync(s.Spec with { Resume = resume }, cancellationToken).ConfigureAwait(false);
        try
        {
            if (!resume)
            {
                _ = await service.AcquireAsync(cancellationToken).ConfigureAwait(false);
            }

            service.KeepAuthority(cancellationToken);
            var tick = await ServeTicksAsync(service, start + ticks, cancellationToken).ConfigureAwait(false);
            s.Report[string.Create(CultureInfo.InvariantCulture, $"servedTick+{ticks}")] = tick;
        }
        finally
        {
            _ = await service.StopAsync().ConfigureAwait(false);
        }

        var h = s.Reattach();
        await PauseAfterAsync(h, cancellationToken).ConfigureAwait(false);
        return h;
    }
}
