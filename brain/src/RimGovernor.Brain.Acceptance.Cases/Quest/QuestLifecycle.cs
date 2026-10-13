using System.Text.Json.Nodes;
using static RimGovernor.Brain.Acceptance.Cases.CaseJson;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>What the lifecycle quest cases (decree, expedition, hospitality, joiner, first-title) share (Go <c>lifecycle.go</c>).</summary>
internal static class QuestLifecycle
{
    /// <summary>The quest's row in the native world progression census.</summary>
    internal static async Task<JsonObject> QuestAsync(Harness h, JsonObject identity, string id, CancellationToken cancellationToken)
    {
        var observed = await TradeKit.ObserveAsync(
            h,
            "quest-lifecycle-census",
            "observations_read_world_progression",
            identity,
            new JsonObject { ["includeStorage"] = false },
            cancellationToken).ConfigureAwait(false);
        foreach (var raw in observed["quests"].AsSlice())
        {
            var row = raw.AsMap() ?? [];
            if (row["id"].AsString() == id)
            {
                return row;
            }
        }

        throw new AcceptanceException($"quest {id} absent from native world progression");
    }

    /// <summary>
    /// Waits until <paramref name="ready"/> accepts the journal's quest and decree plan history, ending early with the
    /// service's exit; progress is any plan step changing stage or the game tick moving.
    /// </summary>
    internal static async Task WaitOrdersAsync(ServiceProcess service, ICaseJournal journal, Func<IReadOnlyList<JsonObject>, bool> ready, CancellationToken cancellationToken)
    {
        async Task<ulong> Tick(CancellationToken ct)
        {
            var row = await service.GetAsync("GET", "/api/player/colony", ct).ConfigureAwait(false);
            return (ulong)Num(row["tick"]);
        }

        var wait = new Wait
        {
            Ceiling = TimeSpan.FromMinutes(8),
            Stall = Wait.StallBudget(),
            Terminal = service.Exited,
            Ticks = 30000,
            Tick = Tick,
        };
        await wait.WaitProgressAsync(
            async ct =>
            {
                var plans = await journal.PlanHistoryAsync(256, ["quest-*", "decree-*"], ct).ConfigureAwait(false);
                var observedTick = await Tick(ct).ConfigureAwait(false);
                return (Wait.Signature(string.Join(',', CaseJournal.StageSignature(plans)), observedTick), ready(plans));
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs the reattached game until every quest in <paramref name="ids"/> ended in success natively.</summary>
    internal static async Task FinishAsync(ISession s, CancellationToken cancellationToken, params string[] ids)
    {
        var h = s.Reattach();
        var wait = new Wait { Stall = Wait.StallBudget() };
        var elapsed = await RunAt.RunUntilAsync(
            h,
            "quest-native-completion",
            30000,
            wait,
            async ct =>
            {
                var states = new List<string>(ids.Length);
                var done = true;
                foreach (var id in ids)
                {
                    var row = await QuestAsync(h, s.Identity, id, ct).ConfigureAwait(false);
                    var state = row["state"].AsString();
                    states.Add(state);
                    if (state is "QUEST_STATUS_ENDED_FAILED" or "QUEST_STATUS_ENDED_INVALID" or "QUEST_STATUS_ENDED_OFFER_EXPIRED")
                    {
                        throw new AcceptanceException($"quest {id} ended {state}");
                    }

                    done = done && state == "QUEST_STATUS_ENDED_SUCCESS";
                    s.Report["quest_" + id] = row.DeepClone();
                }

                return (Wait.Signature(string.Join(',', states)), done);
            },
            quiet: true,
            report: s.Report,
            cancellationToken).ConfigureAwait(false);
        s.Report["completion_ticks"] = elapsed;
    }

    /// <summary>Starts the case's declared serve spec, takes authority and keeps it granted.</summary>
    internal static async Task<ServiceProcess> StartServiceAsync(ISession s, CancellationToken cancellationToken)
    {
        var service = await s.ServeAsync(s.Spec, cancellationToken).ConfigureAwait(false);
        _ = await service.AcquireAsync(cancellationToken).ConfigureAwait(false);
        service.KeepAuthority(cancellationToken);
        return service;
    }
}
