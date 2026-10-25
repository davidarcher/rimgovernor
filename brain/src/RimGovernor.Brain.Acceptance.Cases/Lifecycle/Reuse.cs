using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Bridge;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>lifecycle/reuse</c>: the reusable-game lifecycle (<see cref="GameReuse"/>). One RimWorld process is launched; the
/// lab save is then loaded several times into it, each load a separate case that takes authority, drafts a colonist,
/// undrafts, revokes authority and takes and stops a per-case service handle on the in-mod player API, asserting the
/// stopped handle is stopped. Between cases the lifecycle's own reset contract must hold. A final negative case leaves a
/// colonist drafted and asserts EndCase refuses to hand the game on: it retires the process instead, so a dirty case can
/// never leak into the next one. It says nothing about mod static state, which a reload does not reset (see
/// contracts/native-static-state.md).
/// </summary>
internal static class LifecycleReuse
{
    /// <summary>How many clean baseline reloads run before the negative case; at least two so a second load token is compared against the first.</summary>
    private const int Reloads = 3;

    internal static TypedCase Case() => new()
    {
        Name = "lifecycle/reuse",
        Scope = $"Reusable game (issue #22): one launch, {Reloads} lab reloads each taking authority, drafting and releasing a colonist, revoking authority and "
            + "running a per-case service handle that stops; every reload verified against the reset contract with a fresh load token; a final unclean case "
            + "must retire the game and a later BeginCase is refused. No mod static-state reset is claimed. Spends no ticks.",
        Ticks = 0,
        Tier = CaseTier.OnDemand,
        Start = new OwnedStart(),
        ClaimsStart = new StartClaim { Launches = 1 },
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var reuse = await s.LaunchAsync("launch", cancellationToken).ConfigureAwait(false);
        var cfg = reuse.Config;
        try
        {
            // The lab is the reload target: load it once so its cached save exists and is fresh.
            var h = await reuse.SessionAsync(cancellationToken).ConfigureAwait(false);
            var labSave = await LabStarts.StartLabAsync(cfg, SessionHost.Default, h, cancellationToken).ConfigureAwait(false);

            var tokens = new List<string>();
            for (var i = 1; i <= Reloads; i++)
            {
                var name = string.Create(CultureInfo.InvariantCulture, $"case-{i}");
                var c = await BeginAsync(reuse, name, labSave, cancellationToken).ConfigureAwait(false);
                tokens.Add(c.Reset.LoadToken);
                try
                {
                    await CleanCaseAsync(s, reuse, c, cancellationToken).ConfigureAwait(false);
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

            if (tokens.Distinct(StringComparer.Ordinal).Count() != tokens.Count)
            {
                throw new AcceptanceException($"a load token was issued twice: [{string.Join(' ', tokens)}]");
            }

            s.Record("load_tokens", NewColonyStart.Strings(tokens));

            // Negative case: leave a draft behind. EndCase must retire.
            var unclean = await BeginAsync(reuse, "case-unclean", labSave, cancellationToken).ConfigureAwait(false);
            try
            {
                _ = await DraftColonistAsync(s, Harness.IdentityOf(unclean.Identity), "unclean", cancellationToken).ConfigureAwait(false);
            }
            catch (AcceptanceException e)
            {
                await EndQuietlyAsync(reuse, unclean, cancellationToken).ConfigureAwait(false);
                throw new AcceptanceException($"case-unclean: {e.Message}", e);
            }

            var retired = false;
            try
            {
                await reuse.EndCaseAsync(unclean, false, cancellationToken).ConfigureAwait(false);
            }
            catch (ReuseRetiredException)
            {
                retired = true;
            }

            if (!retired || !reuse.IsRetired)
            {
                throw new AcceptanceException($"case-unclean: EndCase should have retired the game (threw {retired}, retired {reuse.IsRetired})");
            }

            s.Record("unclean_case_retired", reuse.RetiredReason);
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
            catch (AcceptanceException tolerated)
            {
                Tolerated.Note("retire", tolerated);
            }
            catch (OperationCanceledException tolerated)
            {
                Tolerated.Note("retire", tolerated);
            }

            s.Record("reuse", new JsonArray([.. reuse.Rows().Select(row => (JsonNode?)row.DeepClone())]));
        }
    }

    private static async Task<ReuseCase> BeginAsync(GameReuse reuse, string name, string labSave, CancellationToken cancellationToken)
    {
        try
        {
            return await reuse.BeginCaseAsync(name, labSave, Path.Combine(reuse.Config.Output, name), cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"{name}: begin: {e.Message}", e);
        }
    }

    /// <summary>EndCase(failed) for an already failed case; the retirement it causes is expected and the case's own error stands.</summary>
    private static async Task EndQuietlyAsync(GameReuse reuse, ReuseCase c, CancellationToken cancellationToken)
    {
        try
        {
            await reuse.EndCaseAsync(c, true, cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException tolerated)
        {
            Tolerated.Note("end case", tolerated);
        }
    }

    /// <summary>One well-behaved case: authority on, draft, release, authority off, then a service handle taken and stopped against this very load.</summary>
    private static async Task CleanCaseAsync(TypedSession s, GameReuse reuse, ReuseCase c, CancellationToken cancellationToken)
    {
        var identity = Harness.IdentityOf(c.Identity);
        var pawn = await DraftColonistAsync(s, identity, c.Name, cancellationToken).ConfigureAwait(false);
        var undraft = await ApplyDraftAsync(s, c.Name + "-undraft", identity, pawn, drafted: false, cancellationToken).ConfigureAwait(false);
        if (undraft.Job?.Issued != true)
        {
            throw new AcceptanceException($"undraft did not clear drafted: {undraft}");
        }

        // Every admitted write advances the native generation, so revoke against the status read after the release.
        var status = await TypedAuthority.StatusAsync(s, c.Name + "-revoke-status", identity, cancellationToken).ConfigureAwait(false);
        var revoke = await s.CallAsync(
            c.Name + "-revoke",
            NativeOps.ControlAuthority,
            new RimGovernor.Brain.GameOrders.Authority.ControlRequest
            {
                Operation = new RimGovernor.Brain.GameOrders.Authority.Revoke { Identity = identity, ExpectedGeneration = (status.Context ?? throw new AcceptanceException("revoke: the status carried no context")).NativeGeneration.Claimed("revoke: the status named no generation"), Reason = RimGovernor.Brain.GameFacts.Authority.RevocationReason.Manual },
            },
            cancellationToken).ConfigureAwait(false);
        if (revoke.Outcome?.Revoked is null)
        {
            throw new AcceptanceException($"revoke: want revoked, got {revoke.Outcome}");
        }

        await reuse.ReleaseSessionAsync().ConfigureAwait(false);
        var cfg = reuse.Config;
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
                },
                new ServiceLaunch(),
                serviceReport,
                cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"launch service: {e.Message}", e);
        }

        try
        {
            _ = await service.SessionTokenAsync(cancellationToken).ConfigureAwait(false);
            _ = await service.WaitAttachedAsync(c.Identity, TimeSpan.FromSeconds(90), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ = await service.StopAsync().ConfigureAwait(false);
        }

        await service.AssertStoppedAsync(cancellationToken).ConfigureAwait(false);
        s.Record(c.Name, NewColonyStart.Strings([c.Reset.LoadToken, pawn, "service stopped"]));
    }

    /// <summary>Sets authority to Auto and drafts the first standing colonist through the draft intent; returns the drafted pawn's id.</summary>
    private static async Task<string> DraftColonistAsync(TypedSession s, RimGovernor.Brain.GameFacts.Common.Identity identity, string label, CancellationToken cancellationToken)
    {
        var status = await TypedAuthority.StatusAsync(s, label + "-status", identity, cancellationToken).ConfigureAwait(false);
        _ = await TypedAuthority.GrantAsync(s, label + "-auto", identity, (status.Context ?? throw new AcceptanceException($"{label}-status: no generation: {status}")).NativeGeneration.Claimed($"{label}-status: no generation: {status}"), cancellationToken).ConfigureAwait(false);
        var before = await StandingAsync(s, label + "-pawns", identity, [], cancellationToken).ConfigureAwait(false);
        if (before.Drafted)
        {
            throw new AcceptanceException($"{label}: the baseline colonist {before.Pawn.Id} is already drafted");
        }

        var pawn = before.Pawn.Id;
        _ = await ApplyDraftAsync(s, label + "-draft", identity, pawn, drafted: true, cancellationToken).ConfigureAwait(false);
        var after = await StandingAsync(s, label + "-drafted", identity, [pawn], cancellationToken).ConfigureAwait(false);
        return after.Drafted ? pawn : throw new AcceptanceException($"{label}: colonist {pawn} is not drafted after the receipt");
    }

    // The first standing colonist, or the one ids names.
    private static async Task<RimGovernor.Brain.GameFacts.Observations.PawnState> StandingAsync(TypedSession s, string label, RimGovernor.Brain.GameFacts.Common.Identity identity, string[] ids, CancellationToken cancellationToken)
    {
        var reply = await s.CallAsync(
            label,
            NativeOps.ListPawns,
            new RimGovernor.Brain.GameOrders.Observations.ListPawnsRequest { Scope = TypedReads.Scope(identity), Filter = new RimGovernor.Brain.GameFacts.Observations.PawnFilter { Colonist = RimGovernor.Brain.GameFacts.Observations.PawnMembership.Only, Downed = RimGovernor.Brain.GameFacts.Observations.PawnMembership.Excluded, Ids = [.. ids] } },
            cancellationToken).ConfigureAwait(false);
        var rows = reply.Pawns;
        return rows.Count > 0 ? rows[0] : throw new AcceptanceException($"{label}: no standing colonist");
    }

    // One draft intent through operations_apply; returns the applied receipt's effect.
    private static async Task<RimGovernor.Brain.GameFacts.Receipts.EffectEvidenceEffect> ApplyDraftAsync(TypedSession s, string label, RimGovernor.Brain.GameFacts.Common.Identity identity, string pawn, bool drafted, CancellationToken cancellationToken)
    {
        var reply = await s.CallAsync(
            label,
            NativeOps.Apply,
            new RimGovernor.Brain.GameOrders.Operations.ApplyRequest
            {
                Identity = identity,
                Actions = [new RimGovernor.Brain.GameOrders.Operations.Action { Key = TypedLifecycle.Stamp(label), Intent = new RimGovernor.Brain.GameOrders.Operations.DraftIntent { PawnId = pawn, Drafted = drafted } }], DeferSnapshot = false },
            cancellationToken).ConfigureAwait(false);
        var receipt = reply.Results.ToList() is [{ Outcome.Applied: { } applied }] ? applied : throw new AcceptanceException($"{label}: want one applied result, got {reply.BatchFailure} {string.Join("; ", reply.Results)}");
        return receipt.Outcome?.Applied?.Observed?.Effect ?? throw new AcceptanceException($"{label}: the receipt names no effect: {receipt}");
    }
}
