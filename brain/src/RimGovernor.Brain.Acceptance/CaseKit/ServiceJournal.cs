using System.Globalization;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Store;
using Journal = RimGovernor.Brain.Store.Store;
using ReviewRecord = RimGovernor.Brain.Store.ReviewState;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// The durable journal a serve-driven case reads beside the player API: the case opens <c>&lt;output&gt;/service.sqlite</c>
/// read through the store and polls its ReviewState record and plan history. The journal path follows the convention the
/// checkpoint ring already uses.
/// </summary>
public static class ServiceJournal
{
    /// <summary>The journal file of a run (<c>&lt;output&gt;/service.sqlite</c>).</summary>
    public static string PathOf(Config config)
    {
        ArgumentNullException.ThrowIfNull(config);

        // A kept game keeps the state path it was launched with, which an earlier run's output may name.
        if (config.Configuration.Length > 0 && HeadlessProfile.LaunchedEnvValue(config.Configuration, HeadlessProfile.StateVariable) is { Length: > 0 } launched)
        {
            return launched;
        }

        return System.IO.Path.Combine(config.Output, Checkpoints.CheckpointStoreFile);
    }

    /// <summary>Opens the run's durable journal, retrying while the service is still creating it.</summary>
    public static Task<Journal> OpenWithRetryAsync(Config config, CancellationToken cancellationToken) =>
        OpenWithRetryAsync(PathOf(config), cancellationToken);

    /// <summary>The most recently committed plans admitted under a method matching the GLOB pattern.</summary>
    public static Task<IReadOnlyList<PlanState>> PlanHistoryAsync(Journal journal, string pattern, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(journal);
        return Task.Run(() => journal.PlanHistoryWithMethods(256, [pattern], cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Every MethodId <paramref name="need"/> bound in the persisted review's world, retired plans included (the
    /// latest attempt of each); none while no review was persisted yet.
    /// </summary>
    public static async Task<IReadOnlyList<string>> NeedMethodsAsync(Journal journal, Policy.NeedId need, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(journal);
        if (await TryReviewAsync(journal, cancellationToken).ConfigureAwait(false) is not { } review)
        {
            return [];
        }
        var history = await Task.Run(() => journal.OwnerMethodHistory(journal.LoadNeed(need, review.Snapshot, cancellationToken), cancellationToken), cancellationToken).ConfigureAwait(false);
        return [.. history.Select(m => m.Method.Value)];
    }

    /// <summary>The persisted review, or null while none was persisted yet.</summary>
    public static async Task<ReviewRecord?> TryReviewAsync(Journal journal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(journal);
        try
        {
            return await Task.Run(() => journal.LoadReview(cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>Runs <paramref name="body"/> under a time limit; running out of time is an <see cref="AcceptanceException"/>, the caller's own cancellation stays a cancellation.</summary>
    public static async Task<T> BoundedAsync<T>(TimeSpan limit, Func<CancellationToken, Task<T>> body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(limit);
        try
        {
            return await body(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"timed out after {limit}"));
        }
    }

    /// <summary>Opens the journal at <paramref name="path"/>, retrying for 60 seconds while the service is still creating it.</summary>
    public static async Task<Journal> OpenWithRetryAsync(string path, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        using var pace = Wait.Pace(TimeSpan.FromMilliseconds(500));
        while (true)
        {
            try
            {
                return await Journal.OpenAsync(path, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException && DateTime.UtcNow <= deadline)
            {
                _ = e;
            }

            await pace.NextAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Polls until the service reaches automate mode and a ReviewState pass has been persisted (a revision above zero),
    /// returning the persisted review. Besides <paramref name="timeout"/> the wait stalls (<see cref="Wait.StallBudget"/>)
    /// when neither the service mode nor the review revision changes.
    /// </summary>
    public static async Task<ReviewRecord> WaitReviewAsync(
        ServiceProcess service,
        Journal journal,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(journal);
        string? unreadable = null;
        string? stateError = null;
        var sawAutomate = false;
        var review = new ReviewRecord();
        var wait = new Wait { Ceiling = timeout, Stall = Wait.StallBudget(), Terminal = service.Exited };
        try
        {
            await wait.WaitProgressAsync(async ct =>
            {
                ServiceApiReply? state = null;
                try
                {
                    state = await service.ApiAsync("GET", "/api/state", null, "", ct).ConfigureAwait(false);
                }
                catch (NativeTransportException e)
                {
                    // Polled again next round; the last failure names an unreachable service.
                    stateError = e.Message;
                }

                var mode = ServiceJson.State(state?.Body)?.Mode ?? "";

                if (mode == "automate")
                {
                    sawAutomate = true;
                }

                try
                {
                    review = await journal.LoadReviewAsync(ct).ConfigureAwait(false);
                    if (review.Revision > 0 && sawAutomate)
                    {
                        return ("", true);
                    }
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    unreadable = e.Message;
                }

                return (Wait.Signature(mode, review.Revision), false);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (WaitError e) when (!sawAutomate)
        {
            throw new AcceptanceException($"service never reached automate mode after acquire: {e.Message}"
                + (stateError is null ? "" : $"; the state read failed: {stateError}"), e);
        }
        catch (WaitError e)
        {
            throw new AcceptanceException($"service reached automate mode but the ReviewState pass was never persisted (revision 0): {e.Message}"
                + (unreadable is null ? "" : $"; the journal read failed: {unreadable}"), e);
        }

        return review;
    }

    /// <summary>
    /// Polls the durable ReviewState record under <paramref name="w"/> until <paramref name="ready"/> accepts it. The
    /// progress signature is the review's latches and progress rows, so a review that keeps revising without moving
    /// either stalls.
    /// </summary>
    public static Task<ReviewRecord> WaitReviewAsync(Journal journal, Wait w, Func<ReviewRecord, bool> ready, CancellationToken cancellationToken) =>
        WaitReviewAsync(
            journal,
            w,
            ready,
            review => Wait.Signature(review.Latches.ToString(), string.Join(';', review.Progress.Select(p => p.Concern.Value))),
            cancellationToken);

    /// <summary>
    /// <see cref="WaitReviewAsync(Journal, Wait, Func{ReviewRecord, bool}, CancellationToken)"/> with the caller's own
    /// progress signature (the wait stalls when it stops changing).
    /// </summary>
    public static async Task<ReviewRecord> WaitReviewAsync(
        Journal journal,
        Wait w,
        Func<ReviewRecord, bool> ready,
        Func<ReviewRecord, string> signature,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(ready);
        ArgumentNullException.ThrowIfNull(signature);
        var review = new ReviewRecord();
        var wait = w.Interval <= TimeSpan.Zero ? w with { Interval = TimeSpan.FromSeconds(1) } : w;
        try
        {
            await wait.WaitProgressAsync(async ct =>
            {
                review = await journal.LoadReviewAsync(ct).ConfigureAwait(false);
                if (ready(review))
                {
                    return ("", true);
                }

                return (signature(review), false);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (WaitError e)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"{e.Message} (review revision {review.Revision})"), e);
        }

        return review;
    }

    /// <summary>
    /// Polls plan <paramref name="planId"/> in the journal under <paramref name="w"/> until <paramref name="check"/>
    /// accepts its state; the check's signature is whatever of the plan must move.
    /// </summary>
    public static async Task<PlanState> WaitPlanAsync(
        Journal journal,
        Wait w,
        PlanId planId,
        Func<PlanState, (string Signature, bool Done)> check,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(check);
        PlanState? state = null;
        var wait = w.Interval <= TimeSpan.Zero ? w with { Interval = TimeSpan.FromSeconds(1) } : w;
        await wait.WaitProgressAsync(async ct =>
        {
            state = await journal.LoadPlanAsync(planId, ct).ConfigureAwait(false);
            return check(state);
        }, cancellationToken).ConfigureAwait(false);
        return state!;
    }
}
