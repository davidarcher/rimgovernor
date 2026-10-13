using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;
using static RimGovernor.Brain.Acceptance.Cases.CaseJson;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>clearance/salvage-remote</c> (Go <c>salvage_remote.go</c>), and the case factory and fixture helpers the
/// <c>clearance/recovery-*</c> cases share: a useful far-edge ruin is deconstructed and its steel delivered without
/// adding Home.
/// </summary>
internal static class ClearanceSalvageRemote
{
    /// <summary>
    /// One case over the remote-recovery fixtures (test/loot_remote_drop, test/salvage_remote). The cases share one start
    /// and one serve spec; they differ in the fixture scenario and in what they assert.
    /// </summary>
    internal static AcceptanceCase RecoveryCase(string name, string scope, Func<ISession, CancellationToken, Task> run) => new()
    {
        Name = name,
        Scope = scope,
        Start = new FixtureStart { Op = "test/storage_haul_prepare", Args = new JsonObject { ["itemCount"] = 1 }, On = new LabStart() },
        RequiredOps = ["test/loot_remote_drop", "test/salvage_remote"],
        Quiet = QuietMode.Required,
        QuietWorld = true,
        Budget = TimeSpan.FromMinutes(4),
        Crew = new Crew { Size = 3 },
        Stall = TimeSpan.FromSeconds(60),
        Serve = new ServeSpec { Families = ["clearance", "supply", "resource"] },
        Run = (s, ct) => run(s, ct),
    };

    internal static AcceptanceCase Case() => RecoveryCase(
        "clearance/salvage-remote",
        "A useful far-edge ruin is deconstructed and its steel delivered without adding Home.",
        (s, ct) => RunSalvageAsync(s, new SalvageRun(), ct));

    /// <summary>
    /// Varies the salvage flow: the fixture scenario (<c>covered</c> stocks Steel above its floor, <c>cluster</c> stands
    /// three roof-holding walls under a thin roof) and what the run must observe besides the delivery.
    /// </summary>
    /// <param name="Scenario">The fixture scenario name.</param>
    /// <param name="Covered">The ruin's Steel is not short, so the queue must rank it in the last tier and still recover it (demand never holds a thing).</param>
    /// <param name="Cluster">Every wall goes, roof first, and no roof collapses.</param>
    internal sealed record SalvageRun(string Scenario = "", bool Covered = false, bool Cluster = false);

    /// <summary>Drops the remote loot and stages the salvage fixture for a scenario.</summary>
    internal static async Task<JsonObject> PrepareRemoteAsync(ISession s, string scenario, CancellationToken cancellationToken)
    {
        var drop = await s.Harness.CallAsync("remote-cell", "test/loot_remote_drop", [], cancellationToken).ConfigureAwait(false);
        s.Report["drop"] = drop.DeepClone();
        if (!Flag(drop["success"]))
        {
            throw new AcceptanceException($"remote fixture: {drop.ToJson()}");
        }

        var staged = await s.Harness.CallAsync("salvage-ready", "test/salvage_remote", new JsonObject { ["prepare"] = true, ["scenario"] = scenario }, cancellationToken).ConfigureAwait(false);
        s.Report["prepared"] = staged.DeepClone();
        return !Flag(staged["success"]) || !Flag(staged["homeUnchanged"])
            ? throw new AcceptanceException($"salvage fixture: {staged.ToJson()}")
            : staged;
    }

    /// <summary>Walks every plan in the journal's window and returns each completed action as <c>(kind, payload)</c>.</summary>
    internal static async Task<List<(string Kind, JsonObject Action)>> CompletedActionsAsync(ICaseJournal journal, CancellationToken cancellationToken)
    {
        var plans = await journal.PlanHistoryAsync(256, ["*"], cancellationToken).ConfigureAwait(false);
        return [.. CaseJournal.CompletedActions(plans)];
    }

    internal static async Task RunSalvageAsync(ISession s, SalvageRun run, CancellationToken cancellationToken)
    {
        var staged = await PrepareRemoteAsync(s, run.Scenario, cancellationToken).ConfigureAwait(false);
        var target = staged["target"].AsString();
        if (run.Covered && Num(staged["stock"]) <= 200)
        {
            throw new AcceptanceException($"covered fixture must stock Steel above its 200 floor: {staged.ToJson()}");
        }

        if (run.Cluster && staged["cluster"].AsSlice().Count < 3)
        {
            throw new AcceptanceException($"cluster fixture must stand at least three ruins: {staged.ToJson()}");
        }

        var service = await ClearanceKit.StartAsync(s, cancellationToken).ConfigureAwait(false);
        bool rankedRest = false, roofOff = false;
        var journal = await CaseJournal.OpenAsync(s.Config, cancellationToken).ConfigureAwait(false);
        try
        {
            await ClearanceKit.ServiceWait(service).WaitProgressAsync(
                async ct =>
                {
                    var entry = await journal.RecoveryEntryAsync(target, ct).ConfigureAwait(false);
                    var status = entry?["status"].AsString() ?? "";
                    if (entry is not null && entry["tier"].AsString() == "rest" && entry["short"].AsSlice().Count == 0 && status != "held")
                    {
                        rankedRest = true;
                    }

                    var done = false;
                    foreach (var (kind, action) in await CompletedActionsAsync(journal, ct).ConfigureAwait(false))
                    {
                        done = done || (kind == Wire.Name(ActionKind.Deconstruction) && action["target"].AsString() == target);
                        roofOff = roofOff || kind == Wire.Name(ActionKind.RemoveRoof);
                    }

                    if (run.Covered && !rankedRest)
                    {
                        return ("waiting for a covered ranking", false);
                    }

                    return done ? ("salvage completed", true) : (Wait.Signature(entry is not null, status, roofOff), false);
                },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            journal.Dispose();
        }

        _ = await service.StopAsync().ConfigureAwait(false);
        await ClearanceKit.ReattachAsync(s, cancellationToken).ConfigureAwait(false);
        s.Report["ranked_covered"] = rankedRest;
        s.Report["roof_removed_first"] = roofOff;
        for (var i = 0; i < 8; i++)
        {
            _ = await s.AdvanceAsync(1000, null, cancellationToken).ConfigureAwait(false);
            var audit = await s.Harness.CallAsync(string.Create(CultureInfo.InvariantCulture, $"salvage-audit-{i}"), "test/salvage_remote", [], cancellationToken).ConfigureAwait(false);
            s.Report["audit"] = audit.DeepClone();
            if (!Flag(audit["homeUnchanged"]))
            {
                throw new AcceptanceException($"home changed: {audit.ToJson()}");
            }

            if (run.Cluster)
            {
                if (ClusterDone(audit))
                {
                    if (!roofOff)
                    {
                        throw new AcceptanceException($"cluster removed without a roof-off action: {audit.ToJson()}");
                    }

                    if (Num(audit["rubble"]) != 0)
                    {
                        throw new AcceptanceException($"a roof collapsed over the cluster: {audit.ToJson()}");
                    }

                    return;
                }

                continue;
            }

            if (!Flag(audit["present"]) && Num(audit["delivered"]) > 0)
            {
                return;
            }
        }

        throw new AcceptanceException("salvage yield not delivered within 8000 ticks");
    }

    /// <summary>Every cluster ruin is gone and its steel arrived.</summary>
    private static bool ClusterDone(JsonObject audit)
    {
        var cluster = audit["cluster"].AsSlice();
        return cluster.All(raw => !Flag(raw.AsMap()?["present"])) && cluster.Count > 0 && Num(audit["delivered"]) > 0;
    }
}
