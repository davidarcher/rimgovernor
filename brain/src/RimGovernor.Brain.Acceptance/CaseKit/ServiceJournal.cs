using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Store;
using Journal = RimGovernor.Brain.Store.Store;
using ReviewRecord = RimGovernor.Brain.Store.Rounds;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// The durable journal a serve-driven case reads beside the player API (Go <c>na.OpenStoreWithRetry</c>,
/// <c>ServiceProcess.WaitRounds</c> and <c>na.WaitReview</c>): the case opens <c>&lt;output&gt;/service.sqlite</c>
/// read through the store and polls its Rounds record and plan history. The journal path follows the convention the
/// checkpoint ring already uses.
/// </summary>
public static class ServiceJournal
{
    /// <summary>The journal file of a run (<c>&lt;output&gt;/service.sqlite</c>).</summary>
    public static string PathOf(Config config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return System.IO.Path.Combine(config.Output, Checkpoints.CheckpointStoreFile);
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

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Polls until the service reaches automate mode and a Rounds pass has been persisted (a revision above zero),
    /// returning the persisted review and the diagnostics it gathered. Besides <paramref name="timeout"/> the wait
    /// stalls (<see cref="Wait.StallBudget"/>) when neither the service mode nor the review revision changes.
    /// </summary>
    public static async Task<(ReviewRecord Review, IReadOnlyList<JsonObject> Diagnostics)> WaitRoundsAsync(
        ServiceProcess service,
        Journal journal,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(journal);
        var diagnostics = new List<JsonObject>();
        var sawAutomate = false;
        var review = new ReviewRecord();
        var wait = new Wait { Ceiling = timeout, Stall = Wait.StallBudget(), Terminal = service.Exited };
        try
        {
            await wait.WaitProgressAsync(async ct =>
            {
                var entry = new JsonObject();
                JsonObject? state = null;
                try
                {
                    state = (await service.ApiAsync("GET", "/api/state", null, "", ct).ConfigureAwait(false)).Body;
                    entry["state"] = state?.DeepClone();
                }
                catch (NativeTransportException e)
                {
                    entry["state_error"] = e.Message;
                }

                try
                {
                    entry["clock"] = (await service.ApiAsync("GET", "/api/player/clock", null, "", ct).ConfigureAwait(false)).Body?.DeepClone();
                }
                catch (NativeTransportException e)
                {
                    entry["clock_error"] = e.Message;
                }

                diagnostics.Add(entry);
                var mode = state.Member("mode").AsString();
                if (mode == "automate")
                {
                    sawAutomate = true;
                }

                try
                {
                    review = await journal.LoadRoundsAsync(ct).ConfigureAwait(false);
                    if (review.Revision > 0 && sawAutomate)
                    {
                        return ("", true);
                    }
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    _ = e;
                }

                return (Wait.Signature(mode, review.Revision), false);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (WaitError e) when (!sawAutomate)
        {
            throw new AcceptanceException($"service never reached automate mode after acquire: {e.Message}", e);
        }
        catch (WaitError e)
        {
            throw new AcceptanceException($"service reached automate mode but the Rounds pass was never persisted (revision 0): {e.Message}", e);
        }

        return (review, diagnostics);
    }

    /// <summary>
    /// Polls the durable Rounds record under <paramref name="w"/> until <paramref name="ready"/> accepts it. The
    /// progress signature is the review's latches and goal bindings, so a review that keeps revising without moving
    /// either stalls.
    /// </summary>
    public static async Task<ReviewRecord> WaitReviewAsync(Journal journal, Wait w, Func<ReviewRecord, bool> ready, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(ready);
        var review = new ReviewRecord();
        var wait = w.Interval <= TimeSpan.Zero ? w with { Interval = TimeSpan.FromSeconds(1) } : w;
        try
        {
            await wait.WaitProgressAsync(async ct =>
            {
                review = await journal.LoadRoundsAsync(ct).ConfigureAwait(false);
                if (ready(review))
                {
                    return ("", true);
                }

                var needs = review.Standards.Select(b => b.Concern + "=" + b.Standard).ToArray();
                return (Wait.Signature(review.Latches.ToString(), string.Join(';', needs)), false);
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
