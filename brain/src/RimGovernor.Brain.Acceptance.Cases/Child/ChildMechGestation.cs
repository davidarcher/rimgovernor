using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Store;
using Journal = RimGovernor.Brain.Store.Store;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>child/mech-gestation</c> (Go <c>mech_gestation.go</c>) proves a mechanitor gestates a mech inside its bandwidth and
/// the controller gives it a work order: the MaintainMechs goal queues a gestation bill on the gestator, the game forms
/// the mech, and the routine's mech control sets the mech's control group to the mode its kind calls for.
/// </summary>
internal static class ChildMechGestation
{
    private const string PrepareTool = "test/mech_gestation_prepare";
    private const string AdvanceTool = "test/mech_gestation_advance";
    private const string InspectTool = "test/mech_gestation_inspect";

    // The two journal waits (the bill, then the mech's work mode); the stall budget ends either earlier when nothing moves.
    private static readonly TimeSpan BillCeiling = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan BirthCeiling = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Bounds the native run that hauls the ingredients and starts the forming bill (under a game day); the fixture then
    /// completes its gestation cycles, so the days-long gestation never runs.
    /// </summary>
    private const ulong FormTicks = 30000;

    internal static AcceptanceCase Case() => new()
    {
        Name = "child/mech-gestation",
        Scope = "Issue #1692: a mechanitor with a powered gestator is owed a mech by MaintainMechs, which bills the gestator "
            + "inside the mechanitor's free bandwidth; the game hauls the ingredients and forms the mech, and the routine's mech "
            + "control gives its control group the work mode its kind calls for; the fixture completes the forming bill's gestation cycles so the game days of gestation are not run. A Go snapshot over recorded facts cannot cover "
            + "it: the chain is vanilla's own (the Bill_Mech bandwidth rule, hauling, gestation cycles under power, the mech's "
            + "birth into an overseer's control group), asserted on the live mechanitor and mech.",
        Start = new FixtureStart { Op = PrepareTool, On = new LabStart() },
        Expansions = ["ludeon.rimworld.biotech"],
        NoKeep = true,
        Quiet = QuietMode.Required,
        RequiredOps = [PrepareTool, AdvanceTool, InspectTool],

        // mechs bills the gestator and, with work, runs the mech control; the shelter family keeps supervised windows
        // running and dialog answers the letters (the birth raises one).
        Serve = new ServeSpec
        {
            Families = ["mechs", "work", "shelter", "dialog"],
            NativeTimeout = TimeSpan.FromSeconds(15),
            Prefix = "child-mech-gestation",
        },
        Budget = TimeSpan.FromMinutes(12),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    /// <summary>Whether the journal holds a completed bill on the gestator.</summary>
    private static async Task<bool> MechBillPlacedAsync(Journal journal, string gestator, CancellationToken cancellationToken)
    {
        foreach (var plan in await ChildCases.PlanHistoryAsync(journal, "bill-*", cancellationToken).ConfigureAwait(false))
        {
            foreach (var progress in plan.Progress)
            {
                if (progress.Action.ProductionBill is { } bill && bill.Bench == gestator && progress.View.Stage == Stage.Completed)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Whether the journal holds a completed mech work mode setting.</summary>
    private static async Task<bool> MechModeWrittenAsync(Journal journal, CancellationToken cancellationToken)
    {
        foreach (var plan in await ChildCases.PlanHistoryAsync(journal, "work-*", cancellationToken).ConfigureAwait(false))
        {
            foreach (var progress in plan.Progress)
            {
                if (progress.Action.PawnSettings is { MechWorkMode: not null } && progress.View.Stage == Stage.Completed)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>The work and escort modes the game names by role: the Biotech catalog's MechWorkModeRoles rows.</summary>
    private readonly record struct MechModes(string Work, string Escort);

    /// <summary>Reads the definition catalog and returns the mode each role flag marks; exactly one row must carry each.</summary>
    private static async Task<MechModes> RoleModesAsync(Harness h, JsonObject identity, CancellationToken cancellationToken)
    {
        var reply = await h.WireAsync(
            "definition-catalog",
            "observations_read_definition_catalog",
            new JsonObject { ["scope"] = new JsonObject { ["expectedIdentity"] = identity.DeepClone() } },
            cancellationToken).ConfigureAwait(false);
        var observed = Harness.Outcome(reply, "observed").Value;
        var biotech = observed["biotech"].AsMap();
        string work = "", escort = "";
        int works = 0, escorts = 0;
        foreach (var raw in biotech?["mechWorkModes"].AsSlice() ?? [])
        {
            var row = raw.AsMap();
            if (row?["work"].AsBool() == true)
            {
                work = row["defName"].AsString();
                works++;
            }

            if (row?["escort"].AsBool() == true)
            {
                escort = row["defName"].AsString();
                escorts++;
            }
        }

        return works != 1 || escorts != 1
            ? throw new AcceptanceException($"catalog marks {works} work and {escorts} escort modes, want one each: {biotech?["mechWorkModes"].ToJson() ?? "null"}")
            : new MechModes(work, escort);
    }

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var report = s.Report;
        var prepared = s.Prepared ?? [];
        var mechanitor = prepared["mechanitorId"].AsString();
        var gestator = prepared["gestatorId"].AsString();
        var free = prepared["freeBandwidth"].AsNumber();
        if (mechanitor.Length == 0 || gestator.Length == 0 || free <= 0 || prepared["recipes"].AsSlice().Count == 0)
        {
            throw new AcceptanceException($"fixture: unexpected mech gestation staging: {prepared.ToJson()}");
        }

        report["mechanitor"] = mechanitor;
        report["gestator"] = gestator;
        report["fixture"] = prepared.DeepClone();

        // Serve runs the controller until the journal shows done, then releases the game slot. The signature carries the
        // game day so a slow phase does not read as a stall and a broken one does.
        async Task ServeAsync(string what, TimeSpan ceiling, Func<Journal, CancellationToken, Task<bool>> done)
        {
            var service = await ObservedSession.Of(s).ServeAsync(s.Spec, cancellationToken).ConfigureAwait(false);
            try
            {
                _ = await service.AcquireAsync(cancellationToken).ConfigureAwait(false);
                service.KeepAuthority(cancellationToken);
                var store = await ChildCases.OpenJournalAsync(s, cancellationToken).ConfigureAwait(false);
                try
                {
                    try
                    {
                        await new Wait { Ceiling = ceiling, Stall = Wait.StallBudget(), Terminal = service.Exited }.WaitProgressAsync(
                            async ct =>
                            {
                                var review = await ChildCases.TryRoundsAsync(store, ct).ConfigureAwait(false);
                                if (review is null)
                                {
                                    return (Wait.Signature("no-review"), false);
                                }

                                var ok = await done(store, ct).ConfigureAwait(false);
                                return (Wait.Signature(ok, review.Tick.Value / Wait.TicksPerDay), ok);
                            },
                            cancellationToken).ConfigureAwait(false);
                    }
                    catch (AcceptanceException e)
                    {
                        throw new AcceptanceException($"{what}: {e.Message}", e);
                    }
                }
                finally
                {
                    store.Dispose();
                }

                report["keepalive_" + what] = (await service.StopAsync().ConfigureAwait(false))?.DeepClone();
            }
            finally
            {
                _ = await service.StopAsync().ConfigureAwait(false);
            }
        }

        // The colony's answer to a mechanitor with bandwidth to spare: a gestation bill on its gestator.
        await ServeAsync(
            "gestation bill",
            BillCeiling,
            (journal, ct) => MechBillPlacedAsync(journal, gestator, ct)).ConfigureAwait(false);

        // The game hauls the ingredients and starts the bill; the fixture then completes its gestation cycles (a game-days
        // wait that is vanilla's, not the controller's).
        var h = s.Reattach();
        JsonObject? advanced = null;
        ulong elapsed = 0;
        try
        {
            elapsed = await RunAt.RunUntilAsync(
                h,
                "mech-forming",
                FormTicks,
                new Wait { Stall = Wait.StallBudget() },
                async ct =>
                {
                    var reply = await h.CallAsync("mech-advance", AdvanceTool, new JsonObject { ["gestatorId"] = gestator }, ct).ConfigureAwait(false);
                    if (reply["success"].AsBool() != true)
                    {
                        throw new AcceptanceException($"mech advance refused: {reply.ToJson()}");
                    }

                    advanced = reply;
                    var done = reply["advanced"].AsBool() == true;
                    return (Wait.Signature(done), done);
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException e)
        {
            report["form_ticks"] = elapsed;
            report["advanced"] = advanced?.DeepClone();
            throw new AcceptanceException($"gestation never started forming: {e.Message}", e);
        }

        report["form_ticks"] = elapsed;
        report["advanced"] = advanced?.DeepClone();
        _ = await h.CallAsync("pause-forming", "rimgovernor/set_time_speed", new JsonObject { ["speed"] = "Paused", ["ultraSpeedBoost"] = false }, cancellationToken).ConfigureAwait(false);

        // The birth, then the controller's work order for the mech: a completed work plan that sets pawn settings (the
        // mech control group's mode).
        await ServeAsync(
            "mech work mode",
            BirthCeiling,
            MechModeWrittenAsync).ConfigureAwait(false);

        // Native postconditions after the service releases the game slot.
        h = s.Reattach();
        var modes = await RoleModesAsync(h, s.Identity, cancellationToken).ConfigureAwait(false);
        report["modes"] = new JsonObject { ["work"] = modes.Work, ["escort"] = modes.Escort };
        var reply = await h.CallAsync("mech-inspect", InspectTool, new JsonObject { ["mechanitorId"] = mechanitor, ["gestatorId"] = gestator }, cancellationToken).ConfigureAwait(false);
        report["inspect"] = reply.DeepClone();
        if (reply["success"].AsBool() != true)
        {
            throw new AcceptanceException($"mech inspect refused: {reply.ToJson()}");
        }

        if (reply["mechanitorDead"].AsBool() == true)
        {
            throw new AcceptanceException($"the mechanitor died: {reply.ToJson()}");
        }

        var mechs = reply["mechs"].AsSlice();
        if (mechs.Count == 0)
        {
            throw new AcceptanceException($"the mechanitor controls no mech: {reply.ToJson()}");
        }

        double cost = 0;
        foreach (var raw in mechs)
        {
            var mech = raw.AsMap() ?? [];
            if (mech["dead"].AsBool() == true)
            {
                throw new AcceptanceException($"mech {mech["id"].ToJson()} is dead");
            }

            if (mech["spawned"].AsBool() != true)
            {
                throw new AcceptanceException($"mech {mech["id"].ToJson()} is not on the map");
            }

            if (mech["overseerId"].AsString() != mechanitor)
            {
                throw new AcceptanceException($"mech {mech["id"].ToJson()} is overseen by {mech["overseerId"].ToJson()}, not the mechanitor {mechanitor}");
            }

            // A work mech's group works, any other kind's escorts (policy.PlanMechControl); the modes are the catalog's
            // role rows, never names.
            var want = mech["workMech"].AsBool() == true ? modes.Work : modes.Escort;
            var got = mech["mode"].AsString();
            if (got != want)
            {
                throw new AcceptanceException($"mech {mech["id"].ToJson()} ({mech["kind"].ToJson()}) runs mode \"{got}\" in group {mech["controlGroup"].ToJson()}, want \"{want}\"");
            }

            cost += mech["bandwidthCost"].AsNumber();
        }

        var total = reply["totalBandwidth"].AsNumber();
        var used = reply["usedBandwidth"].AsNumber();
        var gestating = reply["usedBandwidthFromGestation"].AsNumber();
        report["bandwidth"] = new JsonObject { ["total"] = total, ["used"] = used, ["gestation"] = gestating, ["mechCost"] = cost };
        if (used + gestating > total)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"used bandwidth {used} plus gestation {gestating} exceeds the total {total}"));
        }

        if (used < cost)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"used bandwidth {used} is below the controlled mechs' cost {cost}"));
        }
    }
}
