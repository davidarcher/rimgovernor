using System.Globalization;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>lifecycle/shutdown</c> (Go <c>lifecycle.go</c> runShutdown, the former shutdownaccept): an orderly end of the game
/// revokes native authority as REVOCATION_REASON_SHUTDOWN, which a controller can tell apart from the lease lapse a
/// vanished bot leaves behind. The case grants Auto, ends the game the way the player's quit-to-menu does (the
/// <c>test/shutdown_unload</c> fixture op), and reads authority for the ended game's identity once no game is loaded.
/// </summary>
internal static class LifecycleShutdown
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "lifecycle/shutdown",
        Scope = "Unloading the game with Auto granted revokes native authority as REVOCATION_REASON_SHUTDOWN at generation+1; authority_read_status for the ended game's identity reports that retained state with its final tick once no game is loaded; a fresh game afterwards reports the old identity stale and its own authority fresh. Process exit (Root.Shutdown) is hooked the same way but is not wire-observable, so it is not exercised.",
        Start = new LabStart(),
        Reason = "the case unloads the runner's game and starts a second one; the process ends on a game the next case did not open",
        NoKeep = true,
        Budget = TimeSpan.FromMinutes(8),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var h = s.Harness;
        var report = s.Report;
        LifecycleCases.RequireTools(s, "rimgovernor/authority_read_status", "rimgovernor/authority_control", "rimgovernor/lifecycle_read_identity");
        if (!Dyn.Contains(s.Names, Game.UnloadTool))
        {
            throw new AcceptanceException($"missing {Game.UnloadTool} in discovery; rebuild the native mod with -Fixture ShutdownFixture");
        }

        // The runner opened the game paused; this is the identity the unload ends.
        var identity = s.Identity;

        // Setup: grant Auto at the fresh game's generation. Authority is initialized by the game's own update polling; a
        // read that lands before that reports it unavailable, and the first generation is always 1.
        var (generation, state, _) = await LifecycleCases.ReadAuthorityAsync(h, "status-fresh", identity, cancellationToken).ConfigureAwait(false);
        if (state == "unavailable")
        {
            generation = 1;
        }

        var granted = await LifecycleCases.GrantAsync(h, "grant", identity, generation, cancellationToken).ConfigureAwait(false);
        if (granted != generation + 1)
        {
            throw new AcceptanceException($"grant: expected generation {generation + 1}, got {granted}");
        }

        report["setup"] = new JsonObject { ["generationBefore"] = generation, ["generationGranted"] = granted };

        // Case 1: end the game with Auto still granted. The unload is queued as a long event, so the read is polled until
        // no game is loaded and the retained state answers for the ended identity.
        var unloaded = await h.CallAsync("unload", Game.UnloadTool, new JsonObject(), cancellationToken).ConfigureAwait(false);
        if (unloaded["success"].AsBool() != true)
        {
            throw new AcceptanceException($"unload refused: {unloaded.ToJson()}");
        }

        var unloadTick = (long)unloaded["tick"].AsNumber();
        var (afterGeneration, inactive, tick) = await WaitShutdownAsync(h, identity, "status-after-unload", cancellationToken).ConfigureAwait(false);
        if (inactive["reason"].AsString() != "REVOCATION_REASON_SHUTDOWN")
        {
            throw new AcceptanceException($"status-after-unload: expected REVOCATION_REASON_SHUTDOWN, got {inactive.ToJson()}");
        }

        if (afterGeneration != granted + 1)
        {
            throw new AcceptanceException($"status-after-unload: expected generation {granted + 1}, got {afterGeneration}");
        }

        if (tick < unloadTick)
        {
            throw new AcceptanceException($"status-after-unload: retained tick {tick} precedes the unload tick {unloadTick}");
        }

        report["case_unload_shutdown"] = new JsonObject { ["inactive"] = inactive.DeepClone(), ["generation"] = afterGeneration, ["tick"] = tick, ["unloadTick"] = unloadTick };

        // Case 2: the process survived the unload and a fresh game does not inherit the retained state: the old identity
        // is stale against the new game, and the new game's own authority starts unowned.
        var next = await LifecycleCases.NewGameAsync(s, "new-game-after", cancellationToken).ConfigureAwait(false);
        if (Dyn.DeepEqual(next, identity))
        {
            throw new AcceptanceException($"new-game-after: expected a fresh identity, got the ended game's {next.ToJson()}");
        }

        var staleReply = await h.WireAsync("status-old-identity", "authority_read_status", new JsonObject { ["identity"] = identity.DeepClone() }, cancellationToken).ConfigureAwait(false);
        if (LifecycleCases.FailureCode(staleReply) != "FAILURE_CODE_STALE_IDENTITY")
        {
            throw new AcceptanceException($"status-old-identity: expected FAILURE_CODE_STALE_IDENTITY, got {staleReply.ToJson()}");
        }

        var (nextGeneration, nextState, nextValue) = await LifecycleCases.ReadAuthorityAsync(h, "status-next", next, cancellationToken).ConfigureAwait(false);
        if (nextState == "active")
        {
            throw new AcceptanceException($"status-next: fresh game inherited active authority: {nextValue.ToJson()}");
        }

        if (nextState == "inactive" && nextGeneration != 1)
        {
            throw new AcceptanceException($"status-next: fresh game did not start at generation 1: {nextValue.ToJson()} at {nextGeneration}");
        }

        report["case_fresh_game"] = new JsonObject { ["state"] = nextState, ["generation"] = nextGeneration };
    }

    /// <summary>
    /// Polls authority_read_status for the ended game's identity until it reports an inactive state, returning that
    /// state's generation, inactive message and context tick. The unload runs as a queued long event across a scene
    /// change, so until it completes a read may still see the live game as active or fail unavailable while the maps are
    /// being torn down; the last reply is reported if the deadline passes.
    /// </summary>
    private static async Task<(ulong Generation, JsonObject Inactive, long Tick)> WaitShutdownAsync(Harness h, JsonObject identity, string label, CancellationToken cancellationToken)
    {
        ulong generation = 0;
        long tick = 0;
        JsonObject? inactive = null;
        JsonObject? last = null;
        var attempt = 0;
        try
        {
            await new Wait { Ceiling = TimeSpan.FromSeconds(30), Interval = TimeSpan.FromMilliseconds(250) }.WaitProgressAsync(
                async token =>
                {
                    var reply = await h.WireAsync(string.Create(CultureInfo.InvariantCulture, $"{label}-{attempt}"), "authority_read_status", new JsonObject { ["identity"] = identity.DeepClone() }, token).ConfigureAwait(false);
                    attempt++;
                    last = reply;
                    if (reply["status"].AsMap() is { } status && reply.Count == 1)
                    {
                        if (status["inactive"].AsMap() is { } found)
                        {
                            var context = status["context"].AsMap();
                            if (!Dyn.DeepEqual(context?["identity"], identity))
                            {
                                throw new AcceptanceException($"{label}: retained context names {context?["identity"].ToJson()}, not the ended game's {identity.ToJson()}");
                            }

                            inactive = found;
                            generation = (ulong)context.Member("nativeGeneration").AsNumber();
                            tick = (long)context.Member("tick").AsNumber();
                            return ("", true);
                        }

                        return status.ContainsKey("active")
                            ? (Wait.Signature("active"), false)
                            : throw new AcceptanceException($"{label}: expected active-then-inactive, got {status.ToJson()}");
                    }

                    if (LifecycleCases.FailureCode(reply) != "FAILURE_CODE_UNAVAILABLE")
                    {
                        throw new AcceptanceException($"{label}: expected a status outcome or a transient unavailable failure, got {reply.ToJson()}");
                    }

                    return (Wait.Signature("unavailable"), false);
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (WaitError e)
        {
            throw new AcceptanceException($"{label}: the ended game never reported inactive authority; last reply {last?.ToJson()}: {e.Message}", e);
        }

        return (generation, inactive!, tick);
    }
}
