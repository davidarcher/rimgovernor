using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// The durable-journal reads the quest and clearance cases make beyond the watch's (<see cref="IWatchStore"/>): Go read
/// them off <c>*store.Store</c> directly (<c>PlanHistoryWithMethods</c>, <c>LoadRounds</c>, <c>LoadStandard</c>,
/// <c>LoadMethods</c>, <c>LoadPlan</c>). The harness leaves the store's reader to the service/journal slice
/// (<see cref="ObservedSession.StoreOpener"/>, #3085, #3087); that reader implements this interface too, and a case
/// fails with a message naming it when the wired reader does not. Rows are JSON so the cases stay free of the store's
/// types.
/// </summary>
public interface ICaseJournal : IWatchStore, IDisposable
{
    /// <summary>
    /// The most recent <paramref name="limit"/> plans whose method id matches any of <paramref name="methodPatterns"/>
    /// (<c>prefix*</c> or exact), newest first (Go <c>PlanHistoryWithMethods</c>). Each row is
    /// <c>{plan, method, steps}</c>; a step is <c>{id, kind, stage, action}</c> with <c>id</c> the action id and <c>kind</c> the action's wire name
    /// (<c>Wire.Name(ActionKind)</c>), <c>stage</c> its current stage in the wire spelling (<c>completed</c>,
    /// <c>unsuccessful</c>, <c>cancelled</c>...), and <c>action</c> its payload in camelCase fields.
    /// </summary>
    Task<IReadOnlyList<JsonObject>> PlanHistoryAsync(int limit, IReadOnlyList<string> methodPatterns, CancellationToken cancellationToken);

    /// <summary>
    /// The recovery queue's row for <paramref name="id"/> in the latest persisted review (Go <c>LoadRounds</c> then
    /// <c>RecoveryQueue.Entries</c>): <c>{id, status, tier, short, reason}</c> with <c>status</c> and <c>tier</c> the
    /// lower-case enum names (<c>held</c>, <c>rest</c>...) and <c>short</c> an array. Null when the review has no queue or
    /// no such row.
    /// </summary>
    Task<JsonObject?> RecoveryEntryAsync(string id, CancellationToken cancellationToken);
}

/// <summary>Opens and reads the case journal.</summary>
internal static class CaseJournal
{
    /// <summary>Opens the run's journal reader as an <see cref="ICaseJournal"/>.</summary>
    internal static async Task<ICaseJournal> OpenAsync(Config config, CancellationToken cancellationToken)
    {
        var store = await ObservedSession.OpenStoreAsync(config, cancellationToken).ConfigureAwait(false);
        if (store is ICaseJournal journal)
        {
            return journal;
        }

        (store as IDisposable)?.Dispose();
        throw new AcceptanceException("the wired journal reader does not implement ICaseJournal (plan history and per-concern plans): the case cannot read the goal's durable state");
    }

    /// <summary>The actions of every completed step of <paramref name="plans"/> as <c>(kind, payload)</c>.</summary>
    internal static IEnumerable<(string Kind, JsonObject Action)> CompletedActions(IEnumerable<JsonObject> plans)
    {
        foreach (var plan in plans)
        {
            foreach (var step in plan["steps"].AsSlice())
            {
                var row = step.AsMap() ?? [];
                if (row["stage"].AsString() == "completed")
                {
                    yield return (row["kind"].AsString(), row["action"].AsMap() ?? []);
                }
            }
        }
    }

    /// <summary>The stage signature of <paramref name="plans"/>: <c>method:stage</c> per step (progress is any of them changing).</summary>
    internal static List<string> StageSignature(IEnumerable<JsonObject> plans)
    {
        var signature = new List<string>();
        foreach (var plan in plans)
        {
            foreach (var step in plan["steps"].AsSlice())
            {
                signature.Add(plan["method"].AsString() + ":" + step.AsMap()?["stage"].AsString());
            }
        }

        return signature;
    }
}
