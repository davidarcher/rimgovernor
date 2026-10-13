using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.Executor;

// Port of internal/executor haul.go, movement.go, trade.go and comms_trade_request.go: the kinds with their own
// flow. Go's (Result, error) pair is kept: a step that fails returns the progress reached so far with the error.

public sealed partial class Executor
{
    // The progress reached so far by one flow, so a failure still reports it.
    private sealed class Flow(Progress start)
    {
        public ExecutorResult Result { get; set; } = new() { Progress = start };
    }

    private static async Task<(ExecutorResult Result, Exception? Error)> RunFlowAsync(Flow flow, Func<Task<(ExecutorResult Result, Exception? Error)>> body)
    {
        try
        {
            return await body().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            return (flow.Result, e);
        }
    }

    // Whether the action is already settled (nothing to do), the snapshot an unsettled one dispatches under, or a
    // throw for evidence that cannot be dispatched.
    private bool TryEnter(Progress progress, Authority authority, out GenerationSnapshot expected)
    {
        var v = progress.View;
        expected = authority.Snapshot;
        switch (v.Stage)
        {
            case Stage.Completed or Stage.Cancelled or Stage.Unsuccessful:
                return false;
            case Stage.Pending or Stage.Prepared:
                break;
            default:
                throw new EvidenceException();
        }
        if (expected.Plan != v.Plan || expected.Revision != v.Revision)
        {
            // A routine method plan runs under the root authority; guard re-authorizes it before every native call.
            if (_roundsScope is null)
            {
                throw new AuthorityException();
            }
            expected = expected with { Plan = v.Plan, Revision = v.Revision };
        }
        return true;
    }

    // Checks the receipt native answered against the placement, then that the journal would accept it.
    private static (Domain.Receipt Kind, Exception? Error) CheckReceipt(Receipt receipt, Progress next, Placement attempt, GenerationSnapshot expected, ActionId action, Exception? callError, bool classify)
    {
        var kind = receipt.Kind;
        var error = callError;
        if (error is not null)
        {
            kind = classify ? ReceiptAfterCallError(error) : Domain.Receipt.Unknown;
        }
        else if (receipt.Action != action || receipt.Attempt != attempt.Attempt || receipt.Snapshot != expected)
        {
            (kind, error) = (Domain.Receipt.Unknown, new EvidenceException());
        }
        try
        {
            _ = next.RecordReceipt(attempt.Attempt, kind);
        }
        catch (DomainException e)
        {
            (kind, error) = (Domain.Receipt.Unknown, Errors.Join(error, new EvidenceException(e)));
        }
        return (kind, error);
    }

    // Sends one haul intent. The only read is the tick the dispatch is journaled at; native checks the pawn and
    // the item when it applies, and the receipt settles the attempt.
    private Task<(ExecutorResult Result, Exception? Error)> RunHaulAsync(DomainAction action, Progress p, Authority authority, CancellationToken generation, CancellationToken ct)
    {
        var flow = new Flow(p);
        return RunFlowAsync(flow, async () =>
        {
            var haul = _haul!;
            if (action.Haul is null || p.Action != action)
            {
                return (flow.Result, new EvidenceException());
            }
            if (!TryEnter(p, authority, out var expected))
            {
                return (flow.Result, null);
            }
            var v = p.View;
            await GuardAsync(expected, generation, ct).ConfigureAwait(false);
            var inspection = await haul.InspectHaulAsync(new Target(action, expected), ct).ConfigureAwait(false);
            if (inspection.Snapshot != expected)
            {
                throw new HeldException();
            }
            var tick = v.Tick > inspection.Tick ? v.Tick : inspection.Tick;
            var next = await _journal.PrepareAsync(v.Plan, v.Action, expected, tick, ct).ConfigureAwait(false);
            flow.Result = flow.Result with { Progress = next };
            next = await _journal.DispatchAsync(v.Plan, v.Action, expected, tick, ct).ConfigureAwait(false);
            flow.Result = flow.Result with { Progress = next };
            var attempt = new Placement(action, next.View.Attempt, expected, tick);
            try
            {
                await GuardAsync(expected, generation, ct).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                return await RecordAsync(flow.Result, v.Plan, attempt, Domain.Receipt.Unknown, e).ConfigureAwait(false);
            }
            flow.Result = flow.Result with { NativeCalled = true };
            var (receipt, callError) = await WriteAsync(() => haul.WriteHaulAsync(attempt, ct)).ConfigureAwait(false);
            // A failed haul write is uncertain whatever its cause: it is never classified as unsent.
            var (kind, error) = CheckReceipt(receipt, next, attempt, expected, v.Action, callError, classify: false);
            return await RecordAsync(flow.Result, v.Plan, attempt, kind, Errors.Join(error, CancelledBy(ct))).ConfigureAwait(false);
        });
    }

    // Sends one move intent once its draft prerequisite has applied: guard, prepare, dispatch, write. Native
    // validates the move against live state when it applies; the receipt settles it.
    private Task<(ExecutorResult Result, Exception? Error)> RunMovementAsync(DomainAction action, Progress progress, Authority authority, CancellationToken generation, CancellationToken ct)
    {
        var flow = new Flow(progress);
        return RunFlowAsync(flow, async () =>
        {
            var movement = _movement!;
            if (action.Movement is not { } m || progress.Action != action)
            {
                return (flow.Result, new EvidenceException());
            }
            if (!TryEnter(progress, authority, out var expected))
            {
                return (flow.Result, null);
            }
            var v = progress.View;
            await GuardAsync(expected, generation, ct).ConfigureAwait(false);
            var state = await _journal.LoadPlanAsync(v.Plan, ct).ConfigureAwait(false);
            var latest = state.Progress.FirstOrDefault(p => p.View.Action == v.Action);
            if (latest is null || latest.Action != action)
            {
                return (flow.Result, new EvidenceException());
            }
            flow.Result = flow.Result with { Progress = latest };
            var prerequisite = state.Progress.FirstOrDefault(p => p.View.Action == m.DraftAction);
            if (prerequisite is null)
            {
                return (flow.Result, new EvidenceException());
            }
            var d = prerequisite.View;
            if (d.Stage != Stage.Completed || d.Unresolved || !d.Snapshot.SameWorld(expected))
            {
                return (flow.Result, new HeldException());
            }
            var tick = latest.View.Tick > d.Tick ? latest.View.Tick : d.Tick;
            var next = await _journal.PrepareAsync(v.Plan, v.Action, expected, tick, ct).ConfigureAwait(false);
            flow.Result = flow.Result with { Progress = next };
            await GuardAsync(expected, generation, ct).ConfigureAwait(false);
            next = await _journal.DispatchAsync(v.Plan, v.Action, expected, tick, ct).ConfigureAwait(false);
            flow.Result = flow.Result with { Progress = next };
            var attempt = new Placement(action, next.View.Attempt, expected, tick);
            try
            {
                await GuardAsync(expected, generation, ct).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                return await RecordAsync(flow.Result, v.Plan, attempt, Domain.Receipt.Unknown, e).ConfigureAwait(false);
            }
            flow.Result = flow.Result with { NativeCalled = true };
            var (receipt, callError) = await WriteAsync(() => movement.WriteMovementAsync(attempt, ct)).ConfigureAwait(false);
            var (kind, error) = CheckReceipt(receipt, next, attempt, expected, v.Action, callError, classify: true);
            return await RecordAsync(flow.Result, v.Plan, attempt, kind, Errors.Join(error, CancelledBy(ct))).ConfigureAwait(false);
        });
    }

    private Task<(ExecutorResult Result, Exception? Error)> RunTradeAsync(DomainAction action, Progress p, Authority authority, CancellationToken generation, CancellationToken ct)
    {
        var flow = new Flow(p);
        return RunFlowAsync(flow, async () =>
        {
            var trade = _trade!;
            if (action.Trade is null || p.Action != action)
            {
                return (flow.Result, new EvidenceException());
            }
            if (!TryEnter(p, authority, out var expected))
            {
                return (flow.Result, null);
            }
            var v = p.View;
            var minimum = v.Tick;
            var inspection = new TradeInspection();
            for (var round = 0; round < 2; round++)
            {
                await GuardAsync(expected, generation, ct).ConfigureAwait(false);
                inspection = await trade.InspectTradeAsync(new Target(action, expected), ct).ConfigureAwait(false);
                await GuardAsync(expected, generation, ct).ConfigureAwait(false);
                if (inspection.Facts.Snapshot != expected || !Fresh(inspection.StartedAt, inspection.ObservedAt))
                {
                    throw new HeldException();
                }
                if (inspection.Facts.PreviewTick > minimum)
                {
                    minimum = inspection.Facts.PreviewTick;
                }
                var decision = PolicyFunctions.EvaluateTrade(new TradeRequest { Action = action, Progress = flow.Result.Progress!, Current = expected, MinimumTick = minimum, Facts = inspection.Facts });
                flow.Result = flow.Result with { Refused = [.. decision.Refused] };
                if (!decision.Admitted)
                {
                    var held = await HoldRefusalAsync(v.Plan, v.Action, decision.Refused, minimum, flow.Result.Progress!, ct).ConfigureAwait(false);
                    flow.Result = flow.Result with { Progress = held };
                    throw new HeldException();
                }
                var prepared = await _journal.PrepareAsync(v.Plan, v.Action, expected, inspection.Facts.PreviewTick, ct).ConfigureAwait(false);
                flow.Result = flow.Result with { Progress = prepared };
            }
            await GuardAsync(expected, generation, ct).ConfigureAwait(false);
            if (!Fresh(inspection.StartedAt, inspection.ObservedAt))
            {
                throw new HeldException();
            }
            var next = await _journal.DispatchAsync(v.Plan, v.Action, expected, inspection.Facts.PreviewTick, ct).ConfigureAwait(false);
            flow.Result = flow.Result with { Progress = next };
            var attempt = new Placement(action, next.View.Attempt, expected, inspection.Facts.PreviewTick);
            try
            {
                await GuardAsync(expected, generation, ct).ConfigureAwait(false);
                if (!Fresh(inspection.StartedAt, inspection.ObservedAt))
                {
                    throw new HeldException();
                }
            }
            catch (Exception e)
            {
                return await RecordAsync(flow.Result, v.Plan, attempt, Domain.Receipt.Unknown, e).ConfigureAwait(false);
            }
            flow.Result = flow.Result with { NativeCalled = true };
            var (receipt, callError) = await WriteAsync(() => trade.WriteTradeAsync(attempt, ct)).ConfigureAwait(false);
            var (kind, error) = CheckReceipt(receipt, next, attempt, expected, v.Action, callError, classify: false);
            return await RecordAsync(flow.Result, v.Plan, attempt, kind, Errors.Join(error, CancelledBy(ct))).ConfigureAwait(false);
        });
    }

    // The native write: its receipt, or the error it failed with (an empty receipt then).
    private static async Task<(Receipt Receipt, Exception? Error)> WriteAsync(Func<Task<Receipt>> write)
    {
        try
        {
            return (await write().ConfigureAwait(false), null);
        }
        catch (Exception e)
        {
            return (new Receipt(), e);
        }
    }

    /// <summary>
    /// What a comms trade request's inspection proves about an attempt. It requires payment evidence, never a
    /// queued receipt: a lost response lacking sufficient correlation remains unknown, and an accepted queued job
    /// that vanished without moving its CAS tick was interrupted.
    /// </summary>
    public static (Effect Effect, UnsuccessfulReason Reason) CommsTradeRequestEffect(CommsTradeRequest request, ProgressView view, CommsTradeInspection read)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(read);
        if (read.Tick < view.Tick)
        {
            return (Effect.Unknown, UnsuccessfulReason.None);
        }
        if (!read.RequestKnown)
        {
            return (Effect.Unsuccessful, UnsuccessfulReason.TargetDead);
        }
        if (read.LastRequestTick > request.ExpectedLastRequestTick && read.LastRequestTick >= view.Tick.Value && (read.MatchingArrival || read.MatchingSeller))
        {
            return (Effect.Completed, UnsuccessfulReason.None);
        }
        // Native's queue owns the arrival and retry deadline. These bounds only retire a lost attempt after the
        // complete queue and seller reads are empty; they never create a second cooldown fact or authorize resend.
        long expiry = request.Kind == TradeRequestKind.Orbital ? 5001 : 360000;
        if (read.LastRequestTick > request.ExpectedLastRequestTick && !read.MatchingArrival && !read.MatchingSeller && read.Tick.Value > read.LastRequestTick + expiry)
        {
            return (Effect.Unsuccessful, UnsuccessfulReason.NativeExpired);
        }
        if (read.LastRequestTick == request.ExpectedLastRequestTick)
        {
            if (read.MatchingWork)
            {
                return (Effect.Pending, UnsuccessfulReason.None);
            }
            if (view.Receipt.TryGetValue(out var receipt) && receipt == Domain.Receipt.Accepted)
            {
                return (Effect.Unsuccessful, UnsuccessfulReason.NativeInterrupted);
            }
        }
        return (Effect.Unknown, UnsuccessfulReason.None);
    }

    private Task<(ExecutorResult Result, Exception? Error)> RunCommsTradeRequestAsync(DomainAction action, Progress progress, Authority authority, CancellationToken generation, CancellationToken ct)
    {
        var flow = new Flow(progress);
        return RunFlowAsync(flow, async () =>
        {
            var comms = _commsTrade!;
            if (action.CommsTradeRequest is not { } request || progress.Action != action)
            {
                return (flow.Result, new EvidenceException());
            }
            var view = progress.View;
            if (view.Stage is Stage.Completed or Stage.Unsuccessful || (view.Stage == Stage.Cancelled && !view.Unresolved))
            {
                return (flow.Result, null);
            }
            var snapshot = authority.Snapshot;
            if (snapshot.Plan != view.Plan || snapshot.Revision != view.Revision)
            {
                if (_roundsScope is null)
                {
                    throw new AuthorityException();
                }
                snapshot = snapshot with { Plan = view.Plan, Revision = view.Revision };
            }
            await GuardAsync(snapshot, generation, ct).ConfigureAwait(false);
            var read = await comms.InspectCommsTradeRequestAsync(new Target(action, snapshot), ct).ConfigureAwait(false);
            await GuardAsync(snapshot, generation, ct).ConfigureAwait(false);
            if (read.Snapshot != snapshot || read.Tick < view.Tick || !Fresh(read.StartedAt, read.ObservedAt))
            {
                throw new HeldException();
            }
            if (view.Unresolved)
            {
                var (effect, reason) = CommsTradeRequestEffect(request, view, read);
                // Unknown facts do not move the last verified pre-payment tick: later queue and arrival
                // evidence must still correlate to this attempt.
                if (effect == Effect.Unknown)
                {
                    return (flow.Result, null);
                }
                var observed = await _journal.ObserveAsync(
                    view.Plan,
                    new Observation(view.Action, view.Attempt, snapshot, read.Tick, effect, ObservationCausality.AfterDispatch, reason),
                    snapshot,
                    ct
                ).ConfigureAwait(false);
                return (flow.Result with { Progress = observed }, null);
            }
            if (!read.Eligible || read.LastRequestTick != request.ExpectedLastRequestTick)
            {
                var cancelled = await _journal.CancelAsync(view.Plan, view.Action, ct).ConfigureAwait(false);
                return (flow.Result with { Progress = cancelled }, null);
            }
            var next = await _journal.PrepareAsync(view.Plan, view.Action, snapshot, read.Tick, ct).ConfigureAwait(false);
            flow.Result = flow.Result with { Progress = next };
            await GuardAsync(snapshot, generation, ct).ConfigureAwait(false);
            if (!Fresh(read.StartedAt, read.ObservedAt))
            {
                throw new HeldException();
            }
            next = await _journal.DispatchAsync(view.Plan, view.Action, snapshot, read.Tick, ct).ConfigureAwait(false);
            flow.Result = flow.Result with { Progress = next };
            var placement = new Placement(action, next.View.Attempt, snapshot, read.Tick);
            try
            {
                await GuardAsync(snapshot, generation, ct).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                return await RecordAsync(flow.Result, view.Plan, placement, Domain.Receipt.Unknown, e).ConfigureAwait(false);
            }
            flow.Result = flow.Result with { NativeCalled = true };
            var (receipt, callError) = await WriteAsync(() => comms.WriteCommsTradeRequestAsync(placement, ct)).ConfigureAwait(false);
            var kind = receipt.Kind;
            var error = callError;
            if (error is not null)
            {
                kind = ReceiptAfterCallError(error);
            }
            else if (receipt.Action != view.Action || receipt.Attempt != placement.Attempt || receipt.Snapshot != snapshot)
            {
                (kind, error) = (Domain.Receipt.Unknown, new EvidenceException());
            }
            return await RecordAsync(flow.Result, view.Plan, placement, kind, Errors.Join(error, CancelledBy(ct))).ConfigureAwait(false);
        });
    }
}
