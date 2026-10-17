using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using RimGovernor.Brain.Store.Codec;
using Journal = RimGovernor.Brain.Store.Store;
using ReviewRecord = RimGovernor.Brain.Store.Rounds;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// The case-side reader of the in-mod brain's state database (the file the harness points the game at with
/// <c>RIMGOVERNOR_STATE</c>, <see cref="ServiceJournal.PathOf"/>): one store handle serving the watch's projections
/// (<see cref="IWatchStore"/>). Every read is a snapshot of committed rows; nothing here writes.
/// </summary>
internal sealed class CaseStore : IWatchStore, IDisposable
{
    private readonly Journal _store;

    private CaseStore(Journal store) => _store = store;

    /// <summary>Opens the run's journal, retrying while the game is still creating it.</summary>
    internal static async Task<CaseStore> OpenAsync(Config config, CancellationToken cancellationToken) =>
        new(await ServiceJournal.OpenWithRetryAsync(config, cancellationToken).ConfigureAwait(false));

    public void Dispose() => _store.Dispose();

    public async Task<ulong> RoundsRevisionAsync(CancellationToken cancellationToken) =>
        (await ReadAsync("read rounds", ct => _store.LoadRounds(ct), cancellationToken).ConfigureAwait(false)).Revision;

    public Task<NeedSample> SampleNeedAsync(string concern, CancellationToken cancellationToken) =>
        ReadAsync("sample " + concern, ct => SampleNeed(new ConcernId(concern), ct), cancellationToken);

    private static async Task<T> ReadAsync<T>(string what, Func<CancellationToken, T> read, CancellationToken cancellationToken)
    {
        try
        {
            return await Task.Run(() => read(cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException and not AcceptanceException)
        {
            throw new AcceptanceException($"journal {what}: {e.Message}", e);
        }
    }

    private NeedSample SampleNeed(ConcernId need, CancellationToken ct)
    {
        var at = WatchSample.Stamp(DateTimeOffset.UtcNow);
        var review = _store.LoadRounds(ct);
        BlockedReason? blocked = null;
        foreach (var progress in review.Progress)
        {
            if (progress.Concern == need)
            {
                blocked = progress.Blocked;
            }
        }

        var sample = new NeedSample { At = at, ReviewRevision = (long)review.Revision, ReviewTick = review.Tick.Value, Blocked = blocked };
        return new NeedId(need.Value) is { IsValid: true } needId ? SampleNeed(review, needId, sample, ct) : sample;
    }

    /// <summary>
    /// A family Need's sample (#3253): a Need is derived, never stored, so the sample holds its owner's unretired
    /// Methods in the review's world; it is bound once the review exists.
    /// </summary>
    private NeedSample SampleNeed(ReviewRecord review, NeedId need, NeedSample sample, CancellationToken ct)
    {
        if (review.Revision == 0)
        {
            return sample;
        }

        var owner = _store.LoadNeed(need, review.Snapshot, ct);
        return sample with
        {
            ConcernBound = true,
            MethodCount = owner.Methods.Count,
            Plans = [.. owner.Methods.Select(method => DescribeMethod(method.Method, method.Plan, ct))],
        };
    }

    private MethodSample DescribeMethod(MethodId method, PlanId plan, CancellationToken ct)
    {
        PlanState state;
        try
        {
            state = _store.LoadPlan(plan, ct);
        }
        catch (Exception e) when (e is DomainException or NotFoundException)
        {
            return new MethodSample { Plan = plan.Value, Error = e.Message };
        }

        var stages = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var kinds = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var unsuccessful = new List<UnsuccessfulAction>();
        foreach (var progress in state.Progress)
        {
            var stage = Wire.Name(progress.View.Stage);
            stages[stage] = stages.GetValueOrDefault(stage) + 1;
            var kind = Wire.Name(progress.Action.Kind);
            kinds[kind] = kinds.GetValueOrDefault(kind) + 1;
            if (progress.View.Stage == Stage.Unsuccessful)
            {
                unsuccessful.Add(new UnsuccessfulAction(progress.View.Action.Value, kind, Wire.Name(progress.View.UnsuccessfulCause())));
            }
        }

        return new MethodSample { Plan = plan.Value, Method = method.Value, Actions = state.Spec.Actions.Count, Stages = stages, Kinds = kinds, Unsuccessful = unsuccessful };
    }
}
