using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using static RimGovernor.Brain.Acceptance.Cases.CaseJson;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// The <c>clearance/recovery-*</c> cases (Go <c>recovery_remote.go</c>): full-map recovery acceptance. They extend the
/// clearance/salvage-remote fixtures (test/loot_remote_drop, test/salvage_remote); each proves one decision of the one
/// recovery queue.
/// </summary>
internal static class ClearanceRecoveryRemote
{
    /// <summary>The per-thing safety holds a hostile beside a ruin may produce: the census-wide threat and the route verdict past it.</summary>
    private static readonly string[] HoldWords = [PolicyFunctions.RemoteHoldThreat, PolicyFunctions.RemoteHoldRouteUnsafe];

    internal static IEnumerable<AcceptanceCase> Cases()
    {
        yield return ClearanceSalvageRemote.RecoveryCase(
            "clearance/recovery-covered",
            "A far-edge ruin whose Steel is not short (stock above the floor) is still recovered; the queue ranks it in the last tier.",
            (s, ct) => ClearanceSalvageRemote.RunSalvageAsync(s, new ClearanceSalvageRemote.SalvageRun("covered", Covered: true), ct));
        yield return ClearanceSalvageRemote.RecoveryCase(
            "clearance/recovery-cluster",
            "A cluster of roof-holding ruins under a thin roof is recovered roof-first in one batch: roofs come off, every ruin goes, no roof collapses.",
            (s, ct) => ClearanceSalvageRemote.RunSalvageAsync(s, new ClearanceSalvageRemote.SalvageRun("cluster", Cluster: true), ct));
        yield return ClearanceSalvageRemote.RecoveryCase(
            "clearance/recovery-loot-covered",
            "A forbidden far-edge Steel stack is released and delivered although Steel stock covers demand.",
            RunLootCoveredAsync);
        yield return ClearanceSalvageRemote.RecoveryCase(
            "clearance/recovery-hostile",
            "A ruin with a hostile beside it stays held in the queue; nothing is designated and nothing is delivered.",
            RunHostileAsync);
        yield return ClearanceSalvageRemote.RecoveryCase(
            "clearance/recovery-foreign-ids",
            "A foreign building claim and a plant cut apply by the Go-built Thing_<Def><n> id, which resolves through native RefIndex.Thing (#2293).",
            RunForeignIdsAsync);
    }

    private static async Task RunLootCoveredAsync(ISession s, CancellationToken cancellationToken)
    {
        var drop = await s.Harness.CallAsync("remote-cell", "test/loot_remote_drop", new JsonObject { ["covered"] = true }, cancellationToken).ConfigureAwait(false);
        s.Report["drop"] = drop.DeepClone();
        var stack = drop["id"].AsString();
        if (!Flag(drop["success"]) || stack.Length == 0 || Num(drop["stock"]) <= 200)
        {
            throw new AcceptanceException($"covered loot fixture: {drop.ToJson()}");
        }

        var service = await ClearanceKit.StartAsync(s, cancellationToken).ConfigureAwait(false);
        using (var journal = await CaseJournal.OpenAsync(s.Config, cancellationToken).ConfigureAwait(false))
        {
            await ClearanceKit.ServiceWait(service).WaitProgressAsync(
                async ct =>
                {
                    var released = false;
                    foreach (var (kind, action) in await ClearanceSalvageRemote.CompletedActionsAsync(journal, ct).ConfigureAwait(false))
                    {
                        released = released || (kind == Wire.Name(ActionKind.SupplyAllow) && !Flag(action["forbidden"]) && action["thing"].AsString() == stack);
                    }

                    var entry = await journal.RecoveryEntryAsync(stack, ct).ConfigureAwait(false);
                    if (entry is not null && entry["status"].AsString() == "held")
                    {
                        throw new AcceptanceException($"covered loot held: {entry["reason"].AsString()}");
                    }

                    return ("loot release", released);
                },
                cancellationToken).ConfigureAwait(false);
        }

        _ = await service.StopAsync().ConfigureAwait(false);
        await ClearanceKit.ReattachAsync(s, cancellationToken).ConfigureAwait(false);
        for (var i = 0; i < 8; i++)
        {
            _ = await s.AdvanceAsync(1000, null, cancellationToken).ConfigureAwait(false);
            var audit = await s.Harness.CallAsync(string.Create(CultureInfo.InvariantCulture, $"loot-audit-{i}"), "test/loot_remote_drop", new JsonObject { ["audit"] = true }, cancellationToken).ConfigureAwait(false);
            s.Report["audit"] = audit.DeepClone();
            if (Flag(audit["moved"]) && Num(audit["delivered"]) > 0)
            {
                return;
            }
        }

        throw new AcceptanceException("covered loot not delivered within 8000 ticks");
    }

    private static async Task RunHostileAsync(ISession s, CancellationToken cancellationToken)
    {
        var staged = await ClearanceSalvageRemote.PrepareRemoteAsync(s, "", cancellationToken).ConfigureAwait(false);
        var target = staged["target"].AsString();
        var threat = await s.Harness.CallAsync("threat-spawn", "test/salvage_remote", new JsonObject { ["threat"] = "spawn" }, cancellationToken).ConfigureAwait(false);
        s.Report["threat"] = threat.DeepClone();
        if (!Flag(threat["success"]) || !Flag(threat["threatPresent"]))
        {
            throw new AcceptanceException($"hostile fixture: {threat.ToJson()}");
        }

        var service = await ClearanceKit.StartAsync(s, cancellationToken).ConfigureAwait(false);
        using (var journal = await CaseJournal.OpenAsync(s.Config, cancellationToken).ConfigureAwait(false))
        {
            await ClearanceKit.ServiceWait(service).WaitProgressAsync(
                async ct =>
                {
                    var entry = await journal.RecoveryEntryAsync(target, ct).ConfigureAwait(false);
                    if (entry is null)
                    {
                        return ("waiting for the ruin's queue row", false);
                    }

                    var reason = entry["reason"].AsString();
                    if (entry["status"].AsString() != "held")
                    {
                        throw new AcceptanceException($"hostile-adjacent ruin {entry["status"].AsString()}: {reason}");
                    }

                    if (Array.IndexOf(HoldWords, reason) >= 0)
                    {
                        s.Report["hold"] = reason;
                        return ("held", true);
                    }

                    throw new AcceptanceException($"hostile-adjacent ruin held for \"{reason}\", want one of [{string.Join(' ', HoldWords)}]");
                },
                cancellationToken).ConfigureAwait(false);
        }

        _ = await service.StopAsync().ConfigureAwait(false);
        await ClearanceKit.ReattachAsync(s, cancellationToken).ConfigureAwait(false);
        _ = await s.AdvanceAsync(1000, null, cancellationToken).ConfigureAwait(false);
        var audit = await s.Harness.CallAsync("hostile-audit", "test/salvage_remote", [], cancellationToken).ConfigureAwait(false);
        s.Report["audit"] = audit.DeepClone();
        if (!Flag(audit["present"]) || Num(audit["designations"]) != 0 || Num(audit["delivered"]) != 0)
        {
            throw new AcceptanceException($"held ruin was worked: {audit.ToJson()}");
        }
    }

    /// <summary>
    /// Rebuilds a native thing id the way the planners do and requires it to equal the id native reported: the form
    /// RefIndex.Thing resolves.
    /// </summary>
    private static string GoBuiltId(string native, string def)
    {
        var prefix = "Thing_" + def;
        var tail = native.StartsWith(prefix, StringComparison.Ordinal) ? native[prefix.Length..] : native;
        if (!ulong.TryParse(tail, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
        {
            throw new AcceptanceException($"thing id \"{native}\" is not Thing_{def}<n>");
        }

        var built = new Thing { Id = n, Def = def }.LoadId();
        return built != native ? throw new AcceptanceException($"go-built id \"{built}\" differs from native \"{native}\"") : built;
    }

    private static async Task RunForeignIdsAsync(ISession s, CancellationToken cancellationToken)
    {
        var staged = await ClearanceSalvageRemote.PrepareRemoteAsync(s, "probe", cancellationToken).ConfigureAwait(false);
        var ruin = GoBuiltId(staged["target"].AsString(), "Battery");
        var plant = GoBuiltId(staged["plant"].AsString(), "Plant_Bush");
        var cell = staged["plantCell"].AsMap() ?? throw new AcceptanceException($"probe fixture reported no plant cell: {staged.ToJson()}");
        var h = s.Harness;
        _ = await h.CallAsync("initial-pause", "rimgovernor/set_time_speed", new JsonObject { ["speed"] = "Paused", ["ultraSpeedBoost"] = false }, cancellationToken).ConfigureAwait(false);
        var clock = new ScenarioClock(h.WireFunc(), s.Identity, "recovery-foreign-ids-acceptance", s.Report);
        _ = await clock.AcquireAsync("acquire", cancellationToken).ConfigureAwait(false);

        async Task Apply(string key, JsonObject action)
        {
            action["key"] = key;
            var reply = await h.WireAsync(key, "operations_apply", new JsonObject { ["identity"] = s.Identity.DeepClone(), ["actions"] = new JsonArray(action) }, cancellationToken).ConfigureAwait(false);
            var results = reply["results"].AsSlice();
            if (results.Count != 1)
            {
                throw new AcceptanceException($"{key}: expected one result: {reply.ToJson()}");
            }

            var result = results[0].AsMap() ?? [];
            if (result["applied"].AsMap() is null)
            {
                throw new AcceptanceException($"{key}: {ruin} / {plant} did not resolve through native: {result.ToJson()}");
            }
        }

        await Apply("foreign-claim", new JsonObject { ["buildingPatch"] = new JsonObject { ["thingId"] = ruin, ["claim"] = new JsonObject() } }).ConfigureAwait(false);
        await Apply(
            "foreign-cut",
            new JsonObject
            {
                ["designate"] = new JsonObject
                {
                    ["designation"] = "THING_DESIGNATION_CUT_PLANT",
                    ["target"] = new JsonObject { ["id"] = plant },
                    ["cell"] = new JsonObject { ["x"] = (int)Num(cell["x"]), ["z"] = (int)Num(cell["z"]) },
                },
            }).ConfigureAwait(false);
        var audit = await h.CallAsync("foreign-audit", "test/salvage_remote", [], cancellationToken).ConfigureAwait(false);
        s.Report["audit"] = audit.DeepClone();
        if (!Flag(audit["claimed"]) || !Flag(audit["plantCut"]))
        {
            throw new AcceptanceException($"claim or cut did not take effect: {audit.ToJson()}");
        }
    }
}
