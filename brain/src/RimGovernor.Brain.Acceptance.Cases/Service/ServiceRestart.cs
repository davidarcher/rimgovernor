using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>service/restart</c> (Go <c>restart.go</c>): the kill-and-restart acceptance for SIMP06. A controller started with
/// resume runs the bot for the observed world with no HTTP write at all, is killed mid-play, and a second controller on the
/// same state resumes autonomous play for the same world again without a launcher step, continuing the rounds past the
/// review the killed one left. Nothing here backs up or restores the state: the plan is re-derived from observation.
/// </summary>
internal static class ServiceRestart
{
    private static readonly TimeSpan FirstRounds = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan AttachWindow = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan SecondRounds = TimeSpan.FromSeconds(120);

    internal static AcceptanceCase Case() => new()
    {
        Name = "service/restart",
        Scope = "Kill-and-restart: serve --resume runs the bot for the observed world with no HTTP write, is killed, and a restarted controller on the "
            + "same state resumes autonomous play for the same world and advances the rounds, again with no player step.",
        Start = new LabStart(),

        // "work" is the lightest family that still produces a Rounds pass with a bound goal; the point is autonomy, not
        // any particular planner.
        Serve = new ServeSpec { Families = ["work"], Resume = true, Prefix = "restart" },
        Budget = TimeSpan.FromMinutes(10),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    // The reviewed world and its root plan, as the service reports them (Go store.Rounds.Snapshot).
    private sealed record Reviewed(string Colony, string Load, double Map, string Plan, double Native);

    private static async Task<Reviewed> ReadReviewedAsync(ServiceProcess service, CancellationToken cancellationToken)
    {
        var state = (await service.ApiAsync("GET", "/api/state", null, "", cancellationToken).ConfigureAwait(false)).Body;
        var identity = state.Member("identity").AsMap();
        var generation = state.Member("generation").AsMap();
        return new Reviewed(
            identity.Member("colonyId").AsString(),
            identity.Member("loadToken").AsString(),
            identity.Member("mapId").AsNumber(),
            generation.Member("plan").AsString(),
            generation.Member("native").AsNumber());
    }

    private static ulong ReviewTick(JsonObject review) => review["lastReviewTick"].AsUInt64() ?? 0;

    // The diagnostics WaitRoundsAsync gathered; a wait that fails throws before returning them, and the service's own
    // record of the exchanges (output/service/http-NNNN.json) stands in for them.
    private static JsonArray Diagnostics(IReadOnlyList<JsonObject> rows) => new([.. rows.Select(row => (JsonNode?)row.DeepClone())]);

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var report = s.Report;
        var identity = s.Identity;
        var service = await s.ServeAsync(s.Spec, cancellationToken).ConfigureAwait(false);
        var firstPid = service.Pid;

        // A pending letter would hold the clock; the keep-alive acknowledges them so the resumed controller plays.
        service.KeepAuthority(cancellationToken);
        JsonObject first;
        Reviewed firstWorld;
        try
        {
            IReadOnlyList<JsonObject> diagnostics;
            try
            {
                (first, diagnostics) = await service.WaitRoundsAsync(FirstRounds, cancellationToken).ConfigureAwait(false);
            }
            catch (AcceptanceException e)
            {
                throw new AcceptanceException($"first controller never played autonomously: {e.Message}", e);
            }

            report["first_diagnostics"] = Diagnostics(diagnostics);
            report["first_review"] = first.DeepClone();
            firstWorld = await ReadReviewedAsync(service, cancellationToken).ConfigureAwait(false);
            var reviewed = new JsonObject { ["colonyId"] = firstWorld.Colony, ["loadToken"] = firstWorld.Load, ["mapId"] = firstWorld.Map };
            if (first["reviewsEnabled"].AsBool() != true || !ServiceProcess.MatchesIdentity(reviewed, identity))
            {
                throw new AcceptanceException($"first review is not an enabled review of the observed world: {first.ToJson()}");
            }
        }
        catch
        {
            _ = await service.StopAsync().ConfigureAwait(false);
            throw;
        }

        // Kill mid-play. The game keeps running and frees the service's slot shortly after, so the restart may need a few
        // attempts to attach.
        _ = await service.StopAsync().ConfigureAwait(false);
        report["killed_pid"] = firstPid;
        var deadline = DateTimeOffset.UtcNow + AttachWindow;
        ServiceProcess restarted;
        while (true)
        {
            try
            {
                restarted = await service.RestartAsync(cancellationToken).ConfigureAwait(false);
                break;
            }
            catch (AcceptanceException e)
            {
                if (DateTimeOffset.UtcNow > deadline)
                {
                    throw new AcceptanceException($"restarted controller never attached: {e.Message}", e);
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
        }

        try
        {
            // The in-mod brain lives as long as the game does, so the restarted handle shares the killed one's process:
            // the Go check that the restart produced a new process cannot hold and the pid is recorded instead.
            report["restarted_pid"] = restarted.Pid;
            restarted.KeepAuthority(cancellationToken);
            deadline = DateTimeOffset.UtcNow + SecondRounds;
            var second = first;
            while (ReviewTick(second) <= ReviewTick(first) || second["reviewsEnabled"].AsBool() != true)
            {
                if (DateTimeOffset.UtcNow > deadline)
                {
                    throw new AcceptanceException($"restarted controller reached automate but the review did not advance past tick {ReviewTick(first)}: {second.ToJson()}");
                }

                try
                {
                    var remaining = deadline - DateTimeOffset.UtcNow;
                    IReadOnlyList<JsonObject> diagnostics;
                    (second, diagnostics) = await restarted.WaitRoundsAsync(remaining > TimeSpan.Zero ? remaining : TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                    report["second_diagnostics"] = Diagnostics(diagnostics);
                }
                catch (AcceptanceException e)
                {
                    throw new AcceptanceException($"restarted controller never resumed autonomous play: {e.Message}", e);
                }

                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }

            report["second_review"] = second.DeepClone();
            var secondWorld = await ReadReviewedAsync(restarted, cancellationToken).ConfigureAwait(false);

            // The world and root plan must not change across the restart.
            if (secondWorld.Colony != firstWorld.Colony || secondWorld.Load != firstWorld.Load || secondWorld.Map != firstWorld.Map || secondWorld.Plan != firstWorld.Plan)
            {
                throw new AcceptanceException($"restart changed the reviewed world: {firstWorld} -> {secondWorld}");
            }

            // Go also required a fresh native generation (the stale Auto revoked, a new grant issued). The brain is not
            // restarted here, so the generation can only stay or advance.
            if (secondWorld.Native < firstWorld.Native)
            {
                throw new AcceptanceException($"restart lost authority at an earlier native generation: {firstWorld} -> {secondWorld}");
            }

            report["review_revisions"] = new JsonObject
            {
                ["before_kill"] = ReviewTick(first),
                ["after_restart"] = ReviewTick(second),
            };
            report["native_generations"] = new JsonObject
            {
                ["before_kill"] = firstWorld.Native,
                ["after_restart"] = secondWorld.Native,
            };
        }
        finally
        {
            _ = await restarted.StopAsync().ConfigureAwait(false);
        }
    }
}
