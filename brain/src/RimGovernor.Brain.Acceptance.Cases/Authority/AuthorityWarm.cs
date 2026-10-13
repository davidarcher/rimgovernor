using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>authority/warm</c> (Go <c>warm.go</c>) verifies authority and clock-journal continuity when a controller dies with
/// Auto granted and a typed epoch running. It releases the first session, unloads its game, prepares the profile again and
/// loads another save into the same process. The next controller must read the first epoch's journal from cursor zero and
/// retain Auto throughout the hold window. This catches profile preparation deleting a kept process's journal and native
/// epoch state retaining a disposed game.
/// </summary>
internal static class AuthorityWarm
{
    private const string WarmController = "authority-warm";

    /// <summary>How long phase 2's grant must stay active, in game time: an in-game hour the game actually runs.</summary>
    private const int WarmHoldTicks = Wait.TicksPerHour;

    private static readonly TimeSpan CloseBudget = TimeSpan.FromSeconds(90);

    internal static AcceptanceCase Case() => new()
    {
        Name = "authority/warm",
        Scope = "A process that hosted a controller killed mid-epoch (authority Auto, typed clock epoch running, session "
            + "dropped) keeps the next controller's Auto grant after unload and reload of another save into the same process (#119).",
        Start = new OwnedStart(),
        NoKeep = true,
        Budget = TimeSpan.FromMinutes(5),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    // The case owns the process: phase 1 keeps it whatever the environment says, phase 2 must attach to it, and the case
    // retires it at the end.
    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var cfg = s.Config;
        var report = s.Report;
        var killed = await Game.OpenGameAsync(cfg, cancellationToken).ConfigureAwait(false);
        killed.Keep = true;
        var phase1 = new JsonObject { ["reused"] = killed.Reused };
        report["phase1_killed_controller"] = phase1;
        var h = killed.Harness;
        string previousLoadToken;
        var failed = true;
        try
        {
            var identity = await WarmStageAsync(cfg, h, "p1", cancellationToken).ConfigureAwait(false);
            var granted = await Authority.GrantAutoAsync(h.WireFunc(), "p1-grant", identity, cancellationToken).ConfigureAwait(false);
            var generation = Authority.GrantGeneration(granted);
            phase1["identity"] = identity.DeepClone();
            phase1["generation"] = generation;
            var startReply = await h.WireAsync(
                "p1-clock-start",
                "clock_start",
                new JsonObject
                {
                    ["authority"] = new JsonObject
                    {
                        ["identity"] = identity.DeepClone(),
                        ["expectedGeneration"] = generation.ToString(CultureInfo.InvariantCulture),
                        ["attempt"] = new JsonObject { ["controllerSessionId"] = WarmController, ["actionId"] = "clock-start", ["attemptId"] = "1" },
                    },
                    ["speed"] = "SPEED_SUPERFAST",
                    ["leaseMs"] = 30000,
                    ["maxTicks"] = 600000,
                    ["policy"] = new JsonObject
                    {
                        ["mode"] = "WATCH_MODE_COLONY",
                        ["healthDropFraction"] = 0.5,
                        ["minHealthFraction"] = 0.2,
                        ["hostileWithin"] = 40,
                        ["injuryStopCooldownMs"] = 0,
                    },
                },
                cancellationToken).ConfigureAwait(false);
            JsonObject receipt;
            try
            {
                receipt = Harness.Outcome(startReply, "receipt").Value;
            }
            catch (AcceptanceException e)
            {
                throw new AcceptanceException($"p1-clock-start: expected a receipt: {e.Message}", e);
            }

            if (receipt["applied"].AsMap() is { } applied && applied["status"].AsMap()?.ContainsKey("running") != true)
            {
                throw new AcceptanceException($"p1-clock-start: expected a running epoch, got {receipt.ToJson()}");
            }

            previousLoadToken = identity["loadToken"].AsString();
            failed = false;
        }
        finally
        {
            if (failed)
            {
                await CloseAsync(killed, report).ConfigureAwait(false);
            }
        }

        // The controller vanishes: the session drops with the epoch running and the grant active. Close returns the game to
        // the main menu and keeps the process.
        killed.Keep = true;
        await CloseAsync(killed, report).ConfigureAwait(false);
        if (report["stop"].AsString().StartsWith("kept running", StringComparison.Ordinal) != true)
        {
            throw new AcceptanceException($"phase 1 did not keep the process: {report["game_reuse"].ToJson()}");
        }

        phase1["stop"] = report["stop"]?.DeepClone();
        await WarmPhase2Async(cfg, previousLoadToken, report, cancellationToken).ConfigureAwait(false);
    }

    // The next controller attaching to the kept process. It is a separate harness in practice, so it prepares the
    // profile again.
    private static async Task WarmPhase2Async(Config cfg, string previousLoadToken, Report report, CancellationToken cancellationToken)
    {
        try
        {
            cfg.PrepareConfig();
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"phase 2 prepare profile: {e.Message}", e);
        }

        var next = await Game.OpenGameAsync(cfg, cancellationToken).ConfigureAwait(false);

        // An Owned case leaves the root's process stopped.
        next.Keep = false;
        try
        {
            var phase2 = new JsonObject { ["reused"] = next.Reused };
            report["phase2_next_controller"] = phase2;
            if (!next.Reused)
            {
                throw new AcceptanceException("phase 2 launched a fresh process instead of attaching to the kept one");
            }

            var h = next.Harness;
            var identity = await WarmStageAsync(cfg, h, "p2", cancellationToken).ConfigureAwait(false);
            if (identity["loadToken"].AsString() == previousLoadToken)
            {
                throw new AcceptanceException($"phase 2 reloaded under the same load token {identity.ToJson()}");
            }

            var clockBefore = await h.WireAsync("p2-clock-before", "clock_read_status", new JsonObject { ["identity"] = identity.DeepClone() }, cancellationToken).ConfigureAwait(false);
            phase2["clock_before_grant"] = clockBefore.DeepClone();

            // The journal phase 1 wrote to belongs to the kept process: a page from cursor 0 must succeed with its rows
            // intact.
            var eventsReply = await h.WireAsync(
                "p2-clock-events",
                "clock_read_events",
                new JsonObject { ["identity"] = identity.DeepClone(), ["afterCursor"] = "0", ["limit"] = 50 },
                cancellationToken).ConfigureAwait(false);
            JsonObject page;
            try
            {
                page = Harness.Outcome(eventsReply, "page").Value;
            }
            catch (AcceptanceException e)
            {
                throw new AcceptanceException($"phase 2 could not page the kept process's clock journal from cursor 0: {e.Message}", e);
            }

            var newest = (long)page["newestCursor"].AsNumber();
            var lost = (long)page["lostCount"].AsNumber();
            phase2["clock_journal"] = new JsonObject
            {
                ["newestCursor"] = newest,
                ["lostCount"] = lost,
                ["gap"] = page["gap"]?.DeepClone(),
                ["events"] = page["events"].AsSlice().Count,
            };
            var gap = page["gap"].AsBool() == true;
            if (gap || lost != 0)
            {
                throw new AcceptanceException($"phase 2 found the kept process's clock journal broken (gap {(gap ? "true" : "false")}, lost {lost}): {page.ToJson()}");
            }

            if (newest < 1)
            {
                throw new AcceptanceException($"phase 2 found no journal rows from phase 1's epoch: {page.ToJson()}");
            }

            var (statusBefore, before) = await Authority.AuthorityStatusAsync(h.WireFunc(), "p2-status", identity, cancellationToken).ConfigureAwait(false);
            phase2["authority_before_grant"] = statusBefore.DeepClone();
            var granted = await Authority.GrantAutoAtAsync(h.WireFunc(), "p2-grant", identity, before, cancellationToken).ConfigureAwait(false);
            var generation = Authority.GrantGeneration(granted);
            phase2["identity"] = identity.DeepClone();
            phase2["generation"] = generation;

            // The grant must hold for the whole window, with the game running: every read Active(Auto) at the granted
            // generation. The first loss is the finding.
            var reads = 0;
            var held = Stopwatch.StartNew();
            var startTick = await h.TickAsync(cancellationToken).ConfigureAwait(false);

            // The probe ends the run once the hold window has passed; RunUntil's own budget (twice the window) is the
            // failure bound, not the hold.
            var elapsed = await RunAt.RunUntilAsync(
                h,
                "p2-hold",
                2UL * WarmHoldTicks,
                new Wait { Stall = Wait.StallBudget() },
                async ct =>
                {
                    var tick = await h.TickAsync(ct).ConfigureAwait(false);
                    var (status, observed) = await Authority.AuthorityStatusAsync(
                        h.WireFunc(),
                        string.Create(CultureInfo.InvariantCulture, $"p2-hold-{reads:D3}"),
                        identity,
                        ct).ConfigureAwait(false);
                    reads++;
                    var active = status["active"].AsMap();
                    if (active is null || active["mode"].AsString() != "MODE_AUTO" || observed != generation)
                    {
                        var clock = await h.WireAsync("p2-clock-at-loss", "clock_read_status", new JsonObject { ["identity"] = identity.DeepClone() }, ct).ConfigureAwait(false);
                        phase2["loss"] = new JsonObject
                        {
                            ["after_reads"] = reads,
                            ["elapsed_ms"] = held.ElapsedMilliseconds,
                            ["authority"] = status.DeepClone(),
                            ["generation"] = observed,
                            ["clock"] = clock.DeepClone(),
                        };
                        throw new AcceptanceException(string.Create(
                            CultureInfo.InvariantCulture,
                            $"phase 2 lost the Auto grant at generation {generation} after {reads} reads: {status.ToJson()}"));
                    }

                    return ("", tick - startTick >= WarmHoldTicks);
                },
                cancellationToken).ConfigureAwait(false);
            phase2["held_reads"] = reads;
            phase2["held_ticks"] = elapsed;
            _ = await Authority.RevokeManualAsync(h.WireFunc(), "p2-release", identity, granted, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await CloseAsync(next, report).ConfigureAwait(false);
        }
    }

    // Loads the lab, pauses it and returns its identity.
    private static async Task<JsonObject> WarmStageAsync(Config cfg, Harness h, string prefix, CancellationToken cancellationToken)
    {
        _ = await LabStarts.StartLabAsync(cfg, SessionHost.Default, h, cancellationToken).ConfigureAwait(false);
        _ = await h.CallAsync(prefix + "-pause", "rimgovernor/set_time_speed", new JsonObject { ["speed"] = "Paused", ["ultraSpeedBoost"] = false }, cancellationToken).ConfigureAwait(false);
        var identityReply = await h.WireAsync(prefix + "-identity", "lifecycle_read_identity", new JsonObject(), cancellationToken).ConfigureAwait(false);
        var loaded = Harness.Outcome(identityReply, "loaded").Value;
        return loaded["context"].AsMap()?["identity"].AsMap()
            ?? throw new AcceptanceException($"{prefix}: no identity in {identityReply.ToJson()}");
    }

    // Close runs under its own bound: the case's token may already be cancelled when the hold is retired.
    private static async Task CloseAsync(Game game, Report report)
    {
        using var bound = new CancellationTokenSource(CloseBudget);
        await game.CloseAsync(report, bound.Token).ConfigureAwait(false);
    }
}
