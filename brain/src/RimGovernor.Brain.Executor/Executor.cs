using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;

namespace RimGovernor.Brain.Executor;

// Port of internal/executor/executor.go. Owns one guarded building attempt at a time. The runtime must hold the
// colony's process/store writer lease before enabling this local executor.
//
// Go's (Result, error) pairs stay pairs where the result carries partial progress next to the failure
// (RunBatchAsync reports both per item); everything else throws. Cancellation of the caller's token and
// authority cancellation both surface as OperationCanceledException or AuthorityException on the item. An
// uncertain native write is recorded as Receipt.Unknown and never retried here.

/// <summary>The guarded building executor.</summary>
public sealed partial class Executor : IDisposable
{
    private readonly IJournal _journal;
    private readonly IBoundary _boundary;
    private readonly IClock _clock;
    private readonly Limits _limits;
    private readonly IRoundsScope? _roundsScope;
    private readonly SemaphoreSlim _writer = new(1, 1);
    private readonly Lock _gate = new();

    private ICommsTradeBoundary? _commsTrade;
    private IHaulBoundary? _haul;
    private ITradeBoundary? _trade;
    private IMovementBoundary? _movement;
    private Authority _authority = new();
    private CancellationTokenSource _generation = new();
    private IReadOnlyList<ActionId> _activeActions = [];
    private CancellationTokenSource? _activeRun;
    private bool _stopped;

    /// <summary>Throws <see cref="DomainException"/> for a missing dependency or an invalid limit.</summary>
    public Executor(IJournal journal, IBoundary boundary, IClock clock, Limits limits, IRoundsScope? roundsScope = null)
    {
        DomainException.Require(
            journal is not null && boundary is not null && clock is not null && limits is not null
                && limits.MaxAge >= TimeSpan.Zero && limits.RunTimeout > TimeSpan.Zero && limits.JournalTimeout > TimeSpan.Zero,
            "invalid executor dependencies or limits"
        );
        _journal = journal;
        _boundary = boundary;
        _clock = clock;
        _limits = limits;
        _roundsScope = roundsScope;
    }

    /// <summary>Releases the writer slot and the generation token. Call <see cref="StopAsync"/> first: this does not join an active dispatch.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _generation.Dispose();
        }
        _writer.Dispose();
    }

    /// <summary>The executor with explicit walk-to-cell orders for drafted pawns (Go <c>NewWithMovement</c>).</summary>
    public static Executor NewWithMovement(IJournal journal, IBoundary building, IMovementBoundary move, IClock clock, Limits limits, IRoundsScope? roundsScope = null)
    {
        DomainException.Require(move is not null, "movement boundary required");
        return new Executor(journal, building, clock, limits, roundsScope) { _movement = move };
    }

    /// <summary>Activates the haul capability, wired explicitly rather than inferred from a composed boundary.</summary>
    public void EnableHaul(IHaulBoundary haul)
    {
        DomainException.Require(haul is not null, "haul boundary required");
        _haul = haul;
    }

    /// <summary>Activates the trade capability; capabilities are wired this way instead of inferred from a composed boundary.</summary>
    public void EnableTrade(ITradeBoundary trade)
    {
        DomainException.Require(trade is not null, "trade boundary required");
        _trade = trade;
    }

    public void EnableCommsTradeRequests(ICommsTradeBoundary boundary)
    {
        _commsTrade = boundary ?? throw new EvidenceException();
    }

    /// <summary>
    /// Invalidates queued and active work. Disabled authority permits later observation and reconciliation, but
    /// never dispatch. An unloaded world may use a zero snapshot while disabled; it cannot establish observation
    /// attribution.
    /// </summary>
    public void UpdateAuthority(Authority authority)
    {
        ArgumentNullException.ThrowIfNull(authority);
        if (authority.Enabled)
        {
            authority.Snapshot.Validate();
        }
        lock (_gate)
        {
            if (_stopped)
            {
                if (authority.Enabled)
                {
                    throw new StoppedException();
                }
                return;
            }
            if (authority == _authority)
            {
                return;
            }
            _generation.Cancel();
            _generation.Dispose();
            _generation = new CancellationTokenSource();
            _authority = authority;
        }
    }

    private Authority Current()
    {
        lock (_gate)
        {
            return _authority;
        }
    }

    /// <summary>
    /// Permanently rejects new work and joins the current dispatch, including its bounded receipt journal write.
    /// A timeout (the token) means the owner must retain its process lock and retry before closing the journal
    /// or transport.
    /// </summary>
    public async Task StopAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            _stopped = true;
            _authority = _authority with { Enabled = false };
            _generation.Cancel();
            _activeRun?.Cancel();
        }
        await _writer.WaitAsync(ct).ConfigureAwait(false);
        _writer.Release();
    }

    private async Task GuardAsync(GenerationSnapshot expected, CancellationToken generation, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (generation.IsCancellationRequested)
        {
            throw new AuthorityException();
        }
        var current = Current();
        if (!current.Enabled)
        {
            throw new AuthorityException();
        }
        if (!current.Snapshot.Matches(expected))
        {
            if (_roundsScope is null)
            {
                throw new AuthorityException();
            }
            try
            {
                await _roundsScope.AuthorizeRoundsPlanAsync(current.Snapshot, expected, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                throw new AuthorityException(e);
            }
            if (generation.IsCancellationRequested || Current() != current)
            {
                throw new AuthorityException();
            }
        }
    }

    /// <summary>Cancels an action, first cancelling its in-flight dispatch if it is the active one.</summary>
    public Task<Progress> CancelAsync(PlanId plan, ActionId action, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_activeRun is not null && _activeActions.Contains(action))
            {
                _activeRun.Cancel();
            }
        }
        return _journal.CancelAsync(plan, action, ct);
    }

    /// <summary>
    /// Takes the writer slot for the actions, reloads the plan once and runs <paramref name="body"/> under the
    /// run timeout, cancelled when authority changes.
    /// </summary>
    private async Task WithPlanAsync(PlanId plan, IReadOnlyList<ActionId> actions, Func<PlanState, Authority, CancellationToken, CancellationToken, Task> body, CancellationToken ct)
    {
        using var run = CancellationTokenSource.CreateLinkedTokenSource(ct);
        run.CancelAfter(_limits.RunTimeout);
        CancellationToken generation;
        Authority authority;
        lock (_gate)
        {
            if (_stopped)
            {
                throw new StoppedException();
            }
            generation = _generation.Token;
            authority = _authority;
        }
        using var stop = generation.Register(static state => ((CancellationTokenSource)state!).Cancel(), run);
        await _writer.WaitAsync(run.Token).ConfigureAwait(false);
        try
        {
            run.Token.ThrowIfCancellationRequested();
            lock (_gate)
            {
                _activeActions = actions;
                _activeRun = run;
            }
            try
            {
                if (generation.IsCancellationRequested)
                {
                    throw new AuthorityException();
                }
                var state = await _journal.LoadPlanAsync(plan, run.Token).ConfigureAwait(false);
                await body(state, authority, generation, run.Token).ConfigureAwait(false);
            }
            finally
            {
                lock (_gate)
                {
                    _activeActions = [];
                    _activeRun = null;
                }
            }
        }
        finally
        {
            _writer.Release();
        }
    }

    // The spec action and progress of actionId in state; null for one the plan does not carry.
    private static (DomainAction? Action, Progress? Progress) Find(PlanState state, ActionId actionId)
    {
        DomainAction? action = null;
        Progress? progress = null;
        foreach (var candidate in state.Spec.Actions)
        {
            if (candidate.Id == actionId)
            {
                action = candidate;
                break;
            }
        }
        foreach (var candidate in state.Progress)
        {
            if (candidate.View.Action == actionId)
            {
                progress = candidate;
                break;
            }
        }
        return (action, progress);
    }

    private async Task<(ExecutorResult Result, Exception? Error)> RunLoadedAsync(PlanState state, ActionId actionId, Authority authority, CancellationToken generation, CancellationToken ct)
    {
        var (action, progress) = Find(state, actionId);
        if (action is null || progress is null)
        {
            return (new ExecutorResult(), new DomainException("missing or unsupported action"));
        }
        if (action.Kind == ActionKind.CommsTradeRequest && _commsTrade is not null)
        {
            return await RunCommsTradeRequestAsync(action, progress, authority, generation, ct).ConfigureAwait(false);
        }
        if (action.Kind.IntentMode() && progress.View.Unresolved)
        {
            return await SettleIntentAsync(progress).ConfigureAwait(false);
        }
        if (action.Kind == ActionKind.Movement && _movement is not null)
        {
            return await RunMovementAsync(action, progress, authority, generation, ct).ConfigureAwait(false);
        }
        if (action.Kind == ActionKind.Haul && _haul is not null)
        {
            return await RunHaulAsync(action, progress, authority, generation, ct).ConfigureAwait(false);
        }
        if (action.Kind == ActionKind.Trade && _trade is not null)
        {
            return await RunTradeAsync(action, progress, authority, generation, ct).ConfigureAwait(false);
        }
        return (new ExecutorResult(), new DomainException("missing or unsupported action"));
    }

    // A failed native write: a failure the transport proves happened before the call was issued is a no-effect
    // Unsent receipt; anything else is uncertain and must reconcile.
    private static Domain.Receipt ReceiptAfterCallError(Exception error) =>
        error is WriteUnsentException ? Domain.Receipt.Unsent : Domain.Receipt.Unknown;

    private bool Fresh(DateTimeOffset start, DateTimeOffset end)
    {
        var now = _clock.Now();
        return start != default && end != default && end >= start && now >= end && now >= start && now - start <= _limits.MaxAge;
    }

    private Task<(ExecutorResult Result, Exception? Error)> RecordAsync(ExecutorResult result, PlanId plan, Placement placement, Domain.Receipt kind, Exception? cause) =>
        RecordZoneAsync(result, plan, placement, kind, "", cause);

    // Records the receipt. Caller and authority cancellation must not erase the attempt: only the bounded local
    // journal write uses a fresh token, while native calls never outlive their authority.
    private async Task<(ExecutorResult Result, Exception? Error)> RecordZoneAsync(ExecutorResult result, PlanId plan, Placement placement, Domain.Receipt kind, string zone, Exception? cause)
    {
        using var bound = new CancellationTokenSource(_limits.JournalTimeout);
        Exception? error = null;
        try
        {
            var progress = zone.Length != 0 && kind == Domain.Receipt.Accepted
                ? await _journal.RecordZoneReceiptAsync(plan, placement.Action.Id, placement.Attempt, zone, bound.Token).ConfigureAwait(false)
                : await _journal.RecordReceiptAsync(plan, placement.Action.Id, placement.Attempt, kind, bound.Token).ConfigureAwait(false);
            result = result with { Progress = progress };
        }
        catch (Exception e)
        {
            error = e;
        }
        return (result, Errors.Join(cause, error));
    }

    // Closes an intent-mode attempt dispatched without a recorded receipt: nothing is observed, the outcome is
    // recorded unknown and the idempotent intent is dispatched again.
    private Task<(ExecutorResult Result, Exception? Error)> SettleIntentAsync(Progress progress)
    {
        var v = progress.View;
        return RecordAsync(new ExecutorResult { Progress = progress }, v.Plan, new Placement(progress.Action, v.Attempt, v.Snapshot, v.Tick), Domain.Receipt.Unknown, null);
    }

    // The error a cancelled token stands for (Go ctx.Err()), or null.
    private static OperationCanceledException? CancelledBy(CancellationToken ct) =>
        ct.IsCancellationRequested ? new OperationCanceledException(ct) : null;
}

internal static class Errors
{
    /// <summary>The one error, none, or an aggregate of every present error (Go <c>errors.Join</c>).</summary>
    internal static Exception? Join(params Exception?[] errors)
    {
        var present = new List<Exception>(errors.Length);
        foreach (var e in errors)
        {
            if (e is not null)
            {
                present.Add(e);
            }
        }
        return present.Count switch
        {
            0 => null,
            1 => present[0],
            _ => new AggregateException(present),
        };
    }
}
