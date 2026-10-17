using System.Text.Json;
using System.Text.Json.Nodes;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// What the combat sweep's served fights share: the planner families a fight is served with, the pause mode that keeps
/// a letter from pausing the served game, and the served game's tick.
/// </summary>
public static class CombatServe
{
    /// <summary>Sets Prefs.AutomaticPauseMode (scripts/fixtures/LetterFixture.cs).</summary>
    public const string PauseModeTool = "test/letter_pause_mode";

    /// <summary>Labels the response evidence of a metrics run (the served clock runs Ultrafast uncapped like every acceptance case).</summary>
    public const string MetricsSpeed = "Ultrafast";

    /// <summary>Today's autopilot answer to a fight: the routine defense planner (hold-the-line / squad defense), with the rescue and tend families it hands downed colonists to.</summary>
    public static readonly IReadOnlyList<string> MetricsFamilies = ["defense", "tend", "rescue"];

    /// <summary>
    /// Sets the pause mode to Never: a MajorThreat letter mid-fight (a berserk colonist) would pause the game as the
    /// player would, and the authority flip then drops the fight plan.
    /// </summary>
    public static async Task NeverPauseAsync(Harness h, CancellationToken cancellationToken)
    {
        var reply = await h.CallAsync("pause-mode-never", PauseModeTool, new JsonObject { { "mode", "Never" } }, cancellationToken).ConfigureAwait(false);
        if (Typed<Fx.LetterPauseMode>(PauseModeTool, reply).Mode != "Never")
        {
            throw new AcceptanceException($"{PauseModeTool} did not take: {reply.ToJson()}");
        }
    }

    /// <summary>Pauses the game after a served window.</summary>
    public static async Task PauseAfterAsync(Harness h, CancellationToken cancellationToken) =>
        _ = await h.PauseClockAsync("pause-after", cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// A lab_stage action that must succeed (Go <c>rawRead</c>): the raw reply, which the bundle keeps as the native
    /// wrote it.
    /// </summary>
    public static async Task<JsonObject> RawReadAsync(Harness h, string action, CancellationToken cancellationToken)
    {
        var reply = await h.CallAsync("combatlab-metrics-" + action, CombatFixtures.LabTool, new JsonObject { { "action", action } }, cancellationToken).ConfigureAwait(false);
        return Typed<Fx.FixtureDone>(CombatFixtures.LabTool, reply).Success
            ? reply
            : throw new AcceptanceException($"{CombatFixtures.LabTool} \"{action}\" refused: {reply.ToJson()}");
    }

    /// <summary>The typed record of a fixture reply; a reply that does not decode is an <see cref="AcceptanceException"/>.</summary>
    internal static T Typed<T>(string tool, JsonObject? reply)
    {
        try
        {
            return Fx.FixtureJson.Deserialize<T>(reply?.ToJsonString() ?? "null");
        }
        catch (JsonException e)
        {
            throw new AcceptanceException($"{tool}: the reply does not decode as {typeof(T).Name}: {e.Message}: {reply.ToJson()}", e);
        }
    }

    /// <summary>The served game's tick from the player API, -1 until it reports one.</summary>
    public static async Task<int> ServedTickAsync(ServiceProcess service, int last, CancellationToken cancellationToken)
    {
        try
        {
            var reply = await service.ApiAsync("GET", "/api/state", null, "", cancellationToken).ConfigureAwait(false);
            if (reply.Status == 200 && ServiceJson.State(reply.Body)?.Game?.Tick is { } tick)
            {
                return (int)tick;
            }
        }
        catch (AcceptanceException tolerated)
        {
            // The poll is a probe: an answer that did not arrive is retried on the next round.
            Console.Error.WriteLine($"tolerated: served tick poll: {tolerated.Message}");
        }

        return last;
    }
}
