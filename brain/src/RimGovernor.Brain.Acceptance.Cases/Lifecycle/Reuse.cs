using System.Globalization;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>lifecycle/reuse</c> (Go <c>reuse.go</c>, the former reuseaccept): the native acceptance for the reusable-game
/// lifecycle (<see cref="GameReuse"/>). One RimWorld process is launched; the lab save is then loaded several times
/// into it, each load a separate case that takes authority, drafts a colonist, undrafts, revokes authority and takes
/// and stops a per-case service handle, asserting the stopped handle is stopped. Between cases the lifecycle's own reset
/// contract must hold. A final negative case deliberately leaves a colonist drafted and asserts EndCase refuses to hand
/// the game on: it retires the process instead, so a dirty case can never leak into the next one. It says nothing about
/// mod static state, which a reload does not reset (see contracts/native-static-state.md).
/// <para>
/// Adaptation (#3090): the Go case launched a <c>rimgovernor serve</c> child per reload from a prebuilt binary and
/// asserted its SQLite state database and that the killed process no longer answered. The brain now lives inside the
/// game, so the per-reload service is a handle on the in-mod player API (<see cref="ServiceProcess.LaunchServiceAsync"/>):
/// there is no <c>-rimgovernor</c> binary to require, no per-case state database to find, and no session token (the
/// token check becomes the handle's health and session check). The stopped-handle assertion is
/// <see cref="ServiceProcess.AssertStoppedAsync"/>.
/// </para>
/// </summary>
internal static class LifecycleReuse
{
    /// <summary>How many clean baseline reloads run before the negative case; at least two so a second load token is compared against the first.</summary>
    private const int Reloads = 3;

    internal static AcceptanceCase Case() => new()
    {
        Name = "lifecycle/reuse",
        Scope = $"Reusable game (issue #22): one launch, {Reloads} lab reloads each taking authority, "
            + "drafting and releasing a colonist, revoking authority and running a per-case controller; "
            + "every reload verified against the reset contract; a final unclean case must retire the game. "
            + "No mod static-state reset is claimed.",
        Start = new OwnedStart(),
        Reason = "the assertion is the reusable-game lifecycle itself: the case launches the process, reloads the lab into it and must see it retired (games_stop) after an unclean case",

        // The per-reload controller is hosted by the case itself over its own game.
        Service = true,
        NoKeep = true,
        Budget = TimeSpan.FromMinutes(15),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    /// <summary>
    /// Drives the reusable-game lifecycle itself (an Owned start): the runner prepared the profile; the case launches the
    /// process, loads the lab into it per case and retires it at the end.
    /// </summary>
    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var cfg = s.Config;
        var report = s.Report;
        report["package_files"] = SessionHost.Default.PackageFiles(cfg);
        var reuse = await GameReuse.OpenReusableGameAsync(cfg, cancellationToken).ConfigureAwait(false);
        try
        {
            var h = await reuse.SessionAsync(cancellationToken).ConfigureAwait(false);
            var names = await h.DiscoveryAsync(cancellationToken).ConfigureAwait(false) ?? [];
            report["discovery"] = NewColonyKit.Strings(names);

            // The lab is the reload target: load it once so its cached save exists and is fresh.
            var lab = await LabStarts.StartLabAsync(cfg, SessionHost.Default, h, cancellationToken).ConfigureAwait(false);
            var labSave = lab["save"].AsString();
            foreach (var tool in new[]
            {
                "rimgovernor/lifecycle_read_identity",
                "rimgovernor/authority_control",
                "rimgovernor/observations_list_pawns",
                "rimgovernor/observations_read_colony_facts",
                "rimgovernor/operations_apply",
            })
            {
                if (!Dyn.Contains(names, tool))
                {
                    throw new AcceptanceException($"missing {tool} in discovery");
                }
            }

            var tokens = new List<string>();
            for (var i = 1; i <= Reloads; i++)
            {
                var name = string.Create(CultureInfo.InvariantCulture, $"case-{i}");
                ReuseCase c;
                try
                {
                    c = await reuse.BeginCaseAsync(name, labSave, Path.Combine(cfg.Output, name), cancellationToken).ConfigureAwait(false);
                }
                catch (AcceptanceException e)
                {
                    throw new AcceptanceException($"{name}: begin: {e.Message}", e);
                }

                tokens.Add(c.Reset.LoadToken);
                try
                {
                    await CleanCaseAsync(cfg, reuse, c, report, cancellationToken).ConfigureAwait(false);
                }
                catch (AcceptanceException e)
                {
                    await EndQuietlyAsync(reuse, c, cancellationToken).ConfigureAwait(false);
                    throw new AcceptanceException($"{name}: {e.Message}", e);
                }

                try
                {
                    await reuse.EndCaseAsync(c, false, cancellationToken).ConfigureAwait(false);
                }
                catch (AcceptanceException e)
                {
                    throw new AcceptanceException($"{name}: end: {e.Message}", e);
                }
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var token in tokens.Where(token => !seen.Add(token)))
            {
                throw new AcceptanceException($"load token \"{token}\" was issued twice");
            }

            report["load_tokens"] = NewColonyKit.Strings(tokens);

            // Negative case: leave a draft behind. EndCase must retire.
            ReuseCase unclean;
            try
            {
                unclean = await reuse.BeginCaseAsync("case-unclean", labSave, Path.Combine(cfg.Output, "case-unclean"), cancellationToken).ConfigureAwait(false);
            }
            catch (AcceptanceException e)
            {
                throw new AcceptanceException($"case-unclean: begin: {e.Message}", e);
            }

            try
            {
                _ = await DraftColonistAsync(unclean.Harness, unclean.Identity, "unclean", cancellationToken).ConfigureAwait(false);
            }
            catch (AcceptanceException e)
            {
                await EndQuietlyAsync(reuse, unclean, cancellationToken).ConfigureAwait(false);
                throw new AcceptanceException($"case-unclean: {e.Message}", e);
            }

            Exception? endError = null;
            try
            {
                await reuse.EndCaseAsync(unclean, false, cancellationToken).ConfigureAwait(false);
            }
            catch (ReuseRetiredException e)
            {
                endError = e;
            }

            if (endError is null)
            {
                throw new AcceptanceException("case-unclean: EndCase should have retired the game, got no error");
            }

            if (!reuse.IsRetired)
            {
                throw new AcceptanceException("case-unclean: lifecycle does not report retirement");
            }

            report["unclean_case_retired"] = reuse.RetiredReason;
            var refused = false;
            try
            {
                _ = await reuse.BeginCaseAsync("after-retire", labSave, Path.Combine(cfg.Output, "after-retire"), cancellationToken).ConfigureAwait(false);
            }
            catch (ReuseRetiredException)
            {
                refused = true;
            }

            if (!refused)
            {
                throw new AcceptanceException("BeginCase after retirement should refuse with ReuseRetiredException, got no error");
            }
        }
        finally
        {
            using var retire = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            try
            {
                await reuse.RetireAsync("run complete", retire.Token).ConfigureAwait(false);
            }
            catch (AcceptanceException)
            {
                // Best effort; the case's own result stands.
            }
            catch (OperationCanceledException)
            {
                // Same.
            }

            reuse.Record(report);
        }
    }

    /// <summary>EndCase(failed) for an already failed case; the retirement it causes is expected and the case's own error stands.</summary>
    private static async Task EndQuietlyAsync(GameReuse reuse, ReuseCase c, CancellationToken cancellationToken)
    {
        try
        {
            await reuse.EndCaseAsync(c, true, cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException)
        {
            // Ignored: the failure being reported is the case's.
        }
    }

    /// <summary>One well-behaved case: authority on, draft, release, authority off, then a service handle taken and stopped against this very load.</summary>
    private static async Task CleanCaseAsync(Config cfg, GameReuse reuse, ReuseCase c, Report report, CancellationToken cancellationToken)
    {
        var h = c.Harness;
        var identity = c.Identity;
        var row = new JsonObject { ["loadToken"] = c.Reset.LoadToken };
        var row0 = await DraftColonistAsync(h, identity, c.Name, cancellationToken).ConfigureAwait(false);
        var pawn0 = row0["pawn"].AsMap();
        JsonObject job;
        try
        {
            job = await ApplyIntents.ApplyDraftAsync(h, "undraft", identity, c.Name + "-undraft", pawn0.Member("id").AsString(), false, cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"undraft: {e.Message}", e);
        }

        if (job["issued"].AsBool() != true)
        {
            throw new AcceptanceException($"undraft did not clear drafted: {job.ToJson()}");
        }

        // Every admitted write advances the native generation, so revoke against the status read after the release, not
        // the grant's generation.
        var statusReply = await h.WireAsync("revoke-status", "authority_read_status", new JsonObject { ["identity"] = identity.DeepClone() }, cancellationToken).ConfigureAwait(false);
        var (_, status) = Harness.Outcome(statusReply, "status");
        var revokeReply = await h.WireAsync(
            "revoke",
            "authority_control",
            new JsonObject
            {
                ["revoke"] = new JsonObject
                {
                    ["identity"] = identity.DeepClone(),
                    ["expectedGeneration"] = status["context"].Member("nativeGeneration")?.DeepClone(),
                    ["reason"] = "REVOCATION_REASON_MANUAL",
                },
            },
            cancellationToken).ConfigureAwait(false);
        _ = LifecycleCases.Outcome("revoke", revokeReply, "revoked");
        row["drafted_and_released"] = Draft.Target(row0)["entityId"]?.DeepClone();

        await reuse.ReleaseSessionAsync().ConfigureAwait(false);
        var serviceReport = new Report();
        ServiceProcess service;
        try
        {
            service = await ServiceProcess.LaunchServiceAsync(
                new Config
                {
                    Root = cfg.Root,
                    Output = c.Output,
                    Headless = cfg.Headless,
                    Graphics = cfg.Graphics,
                    GameId = cfg.GameId,
                    Timeout = cfg.Timeout,
                    Configuration = cfg.Configuration,
                    Expansions = cfg.Expansions,
                    FixtureOps = cfg.FixtureOps,
                    QuietWorld = cfg.QuietWorld,
                    KeepLoaded = cfg.KeepLoaded,
                    Resumed = cfg.Resumed,
                    ServiceProfile = cfg.ServiceProfile,
                },
                new ServiceLaunch(),
                serviceReport,
                cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"launch controller: {e.Message}", e);
        }

        try
        {
            _ = await service.SessionTokenAsync(cancellationToken).ConfigureAwait(false);
            _ = await service.WaitAttachedAsync(identity, TimeSpan.FromSeconds(90), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ = await service.StopAsync().ConfigureAwait(false);
        }

        await service.AssertStoppedAsync(cancellationToken).ConfigureAwait(false);
        serviceReport["session_checked"] = true;
        serviceReport["stopped"] = true;
        row["controller"] = serviceReport.Data.DeepClone();
        report[c.Name] = row;
    }

    /// <summary>
    /// Sets authority to Auto and drafts the first standing colonist through the draft intent, returning the pawn's
    /// post-draft row.
    /// </summary>
    private static async Task<JsonObject> DraftColonistAsync(Harness h, JsonObject identity, string label, CancellationToken cancellationToken)
    {
        var statusReply = await h.WireAsync(label + "-status", "authority_read_status", new JsonObject { ["identity"] = identity.DeepClone() }, cancellationToken).ConfigureAwait(false);
        var (_, status) = Harness.Outcome(statusReply, "status");
        var grantReply = await h.WireAsync(
            label + "-auto",
            "authority_control",
            new JsonObject
            {
                ["setMode"] = new JsonObject
                {
                    ["identity"] = identity.DeepClone(),
                    ["expectedGeneration"] = status["context"].Member("nativeGeneration")?.DeepClone(),
                    ["mode"] = "MODE_AUTO",
                },
            },
            cancellationToken).ConfigureAwait(false);
        _ = LifecycleCases.Outcome("set Auto", grantReply, "granted");

        async Task<JsonObject> ListPawnsAsync(string step, JsonNode? ids)
        {
            var filter = new JsonObject { ["colonist"] = true, ["downed"] = false };
            if (ids is not null)
            {
                filter["ids"] = ids;
            }

            var reply = await h.WireAsync(
                $"{label}-{step}",
                "observations_list_pawns",
                new JsonObject { ["scope"] = new JsonObject { ["expectedIdentity"] = identity.DeepClone() }, ["filter"] = filter },
                cancellationToken).ConfigureAwait(false);
            var (_, observed) = LifecycleCases.Outcome(step, reply, "observed");
            var rows = observed["pawns"].AsSlice();
            return rows.Count == 0 ? throw new AcceptanceException($"{step}: no standing colonist") : rows[0].AsMap() ?? [];
        }

        var before = await ListPawnsAsync("pawns", null).ConfigureAwait(false);
        if (before["drafted"].AsBool() == true)
        {
            throw new AcceptanceException("baseline colonist is already drafted");
        }

        var pawnId = before["pawn"].Member("id").AsString();
        try
        {
            _ = await ApplyIntents.ApplyDraftAsync(h, label + "-draft", identity, label + "-draft", pawnId, true, cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"draft: {e.Message}", e);
        }

        var after = await ListPawnsAsync("drafted", new JsonArray(pawnId)).ConfigureAwait(false);
        return after["drafted"].AsBool() == true ? after : throw new AcceptanceException($"colonist {pawnId} is not drafted after the receipt");
    }
}
