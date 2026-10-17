using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>service/restart</c> (Go <c>restart.go</c>): the kill-and-restart acceptance for SIMP06. A controller started with
/// resume runs the bot for the observed world with no HTTP write at all, is stopped mid-play, and a second controller on
/// the same state resumes autonomous play for the same world again without a launcher step, continuing the rounds past
/// the review the stopped one left. Nothing here backs up or restores the state: the plan is re-derived from observation.
/// </summary>
internal static class ServiceRestart
{
    // Each controller's game-tick budget for persisting an enabled review: several review rounds.
    private const int RoundsTicks = 10000;

    internal static TypedCase Case() => new()
    {
        Name = "service/restart",
        Scope = "Kill-and-restart: serve --resume runs the bot for the observed world with no HTTP write and persists an enabled review; it is stopped, "
            + "and a restarted controller on the same state resumes autonomous play for the same world and root plan and advances the review past "
            + "the stopped one's, again with no player step. A unit test cannot prove the in-mod brain re-attaching to a live game.",
        Ticks = 2 * RoundsTicks,
        Tier = CaseTier.OnDemand,

        // "work" is the lightest family that still produces a Rounds pass with a bound goal; the point is autonomy, not
        // any particular planner.
        Serve = new ServeSpec { Families = ["work"], Resume = true, Prefix = "restart" },
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var world = new GenerationSnapshot(new ColonyId(s.Identity.ColonyId), new MapId(s.Identity.MapId), new LoadId(s.Identity.LoadToken), default, default, default);

        // The first controller: an enabled review of the observed world.
        Store.Rounds? first = null;
        _ = await s.ServeUntilAsync(
            "first",
            RoundsTicks,
            async ct =>
            {
                first = await s.ReviewAsync(ct).ConfigureAwait(false);
                return (Signature(first), first is { Enabled: true, Revision: > 0 } && first.Snapshot.SameWorld(world));
            },
            cancellationToken).ConfigureAwait(false);
        var before = first ?? throw new AcceptanceException("first controller persisted no review");
        s.Record("first_review", Evidence(before));

        // The restart: a fresh controller on the same journal must review again, later, for the same world and plan.
        Store.Rounds? second = null;
        _ = await s.ServeUntilAsync(
            "restarted",
            RoundsTicks,
            async ct =>
            {
                second = await s.ReviewAsync(ct).ConfigureAwait(false);
                return (Signature(second), second is { Enabled: true } && second.Tick > before.Tick);
            },
            cancellationToken).ConfigureAwait(false);
        var after = second ?? throw new AcceptanceException("restarted controller persisted no review");
        s.Record("second_review", Evidence(after));

        // The world and root plan must not change across the restart.
        if (!after.Snapshot.SameWorld(before.Snapshot) || after.Snapshot.Plan != before.Snapshot.Plan)
        {
            throw new AcceptanceException($"restart changed the reviewed world: {before.Snapshot} -> {after.Snapshot}");
        }

        // The in-mod brain outlives the controller, so the native generation can only stay or advance.
        if (after.Snapshot.Native.Value < before.Snapshot.Native.Value)
        {
            throw new AcceptanceException($"restart lost authority at an earlier native generation: {before.Snapshot} -> {after.Snapshot}");
        }
    }

    private static string Signature(Store.Rounds? review) =>
        review is null ? "none" : string.Create(CultureInfo.InvariantCulture, $"{review.Revision}/{review.Tick.Value}/{review.Enabled}");

    private static JsonObject Evidence(Store.Rounds review) => new(
    [
        new KeyValuePair<string, JsonNode?>("revision", review.Revision),
        new KeyValuePair<string, JsonNode?>("tick", review.Tick.Value),
        new KeyValuePair<string, JsonNode?>("enabled", review.Enabled),
        new KeyValuePair<string, JsonNode?>("plan", review.Snapshot.Plan.Value),
        new KeyValuePair<string, JsonNode?>("native", review.Snapshot.Native.Value),
    ]);
}
