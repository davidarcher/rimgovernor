using RimGovernor.Contracts.Wire.Lifecycle;
namespace RimGovernor.Brain.BuildingRuntime;

// Port of internal/buildingruntime/clock_cleanup.go.
public sealed partial class ClockCoordinator
{
    /// <summary>
    /// Joins commands and their journal writes, including after <see cref="Stop"/> (Go <c>Cleanup</c>). It never
    /// acquires authority or a lease; only the immutable original epoch can be paused.
    /// </summary>
    public Task Cleanup(CancellationToken ct) => CleanupObserved(null, ct);

    /// <summary>
    /// <see cref="Cleanup"/> reusing a validated status for a matching epoch identity, avoiding redundant native
    /// reads. A null status fetches identity and status (Go <c>CleanupObserved</c>).
    /// </summary>
    public async Task CleanupObserved(PbClock.Status? observed, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();
            var attempts = await _journal.LoadClockAttemptsAsync(4096, ct).ConfigureAwait(false);
            var failures = new List<Exception>();
            foreach (var attempt in attempts)
            {
                if (attempt.SupersededAt is not null || attempt.Intent.Command.Start is null || attempt.Phase is not (ClockPhase.Dispatched or ClockPhase.Uncertain))
                {
                    continue;
                }
                if (ct.IsCancellationRequested)
                {
                    failures.Add(new OperationCanceledException(ct));
                    throw Joined(failures);
                }
                using var call = CancellationTokenSource.CreateLinkedTokenSource(ct);
                call.CancelAfter(_config.CallTimeout);
                try
                {
                    await CleanupStartAsync(attempt, call.Token).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    failures.Add(e);
                }
            }
            IReadOnlyList<ClockEpochObligation> epochs;
            try
            {
                epochs = await _journal.LoadClockEpochsAsync(4096, ct).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                failures.Add(e);
                throw Joined(failures);
            }
            foreach (var epoch in epochs)
            {
                if (Terminal(epoch.Stage))
                {
                    continue;
                }
                if (ct.IsCancellationRequested)
                {
                    failures.Add(new OperationCanceledException(ct));
                    throw Joined(failures);
                }
                using var call = CancellationTokenSource.CreateLinkedTokenSource(ct);
                call.CancelAfter(_config.CallTimeout);
                try
                {
                    await CleanupEpochAsync(epoch, observed, call.Token).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    failures.Add(e);
                }
            }
            if (failures.Count != 0)
            {
                throw Joined(failures);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static Exception Joined(List<Exception> failures) => ErrorJoin.Of([.. failures])!;

    private async Task<PbCommon.ObservationContext> CleanupIdentityAsync(CancellationToken ctx)
    {
        var reply = await _native.IdentityAsync(ctx).ConfigureAwait(false);
        ctx.ThrowIfCancellationRequested();
        if (reply.Outcome is not IdentityReplyOutcome.LoadedCase { Value: var loaded })
        {
            throw new HeldException();
        }
        var current = loaded.Context;
        BridgeApi.ValidateContext(current);
        return current ?? throw new InvalidOperationException("observation context required");
    }

    private async Task CleanupStartAsync(ClockAttempt v, CancellationToken ctx)
    {
        var current = await CleanupIdentityAsync(ctx).ConfigureAwait(false);
        if (!Equals(current.Identity, Boundary.Identity(v.Intent.Snapshot)))
        {
            await _journal.MarkClockScopeSupersededAsync(v.Intent.RequestId, current, ctx).ConfigureAwait(false);
            return;
        }
        var (result, error) = await ReconcileLockedAsync(v.Intent.RequestId, ctx).ConfigureAwait(false);
        if (error is not null)
        {
            throw error;
        }
        if (result!.SupersededAt is null && result.Phase is ClockPhase.Dispatched or ClockPhase.Uncertain)
        {
            throw new HeldException();
        }
    }

    private async Task CleanupEpochAsync(ClockEpochObligation v, PbClock.Status? observed, CancellationToken ctx)
    {
        PbCommon.ObservationContext current;
        PbClock.Status? status = null;
        if (observed is not null && Equals(observed.Context?.Identity, v.Epoch!.Origin?.Identity))
        {
            current = observed.Context!;
            status = observed;
        }
        else
        {
            current = await CleanupIdentityAsync(ctx).ConfigureAwait(false);
        }
        if (status is null && Equals(current.Identity, v.Epoch!.Origin?.Identity))
        {
            var reply = await _native.ReadClockStatusAsync(current.Identity!, ctx).ConfigureAwait(false);
            var read = ValidatedStatus(reply.Outcome is PbClock.StatusReplyOutcome.StatusCase { Value: var s } ? s : null, current.Identity);
            var readContext = read.Context ?? throw new InvalidOperationException("clock status context required");
            if (readContext.Tick.GetValueOrDefault() < current.Tick.GetValueOrDefault()
                || (current.NativeGeneration is not null && (readContext.NativeGeneration is null || readContext.NativeGeneration.GetValueOrDefault() < current.NativeGeneration.GetValueOrDefault())))
            {
                throw new BridgeContractException("clock status older than the observed identity");
            }
            status = read;
            current = readContext;
        }
        ctx.ThrowIfCancellationRequested();
        v = await _journal.ObserveClockEpochAsync(v.StartRequestId, v.Sequence, current, status, ctx).ConfigureAwait(false);
        if (Terminal(v.Stage))
        {
            return;
        }
        if (v.Stage != ClockEpochStage.Required)
        {
            throw new HeldException();
        }
        v = await _journal.BeginClockPauseAsync(v.StartRequestId, v.Sequence, ctx).ConfigureAwait(false);

        // Once dispatch is durable, cancellation cannot discard pause uncertainty.
        if (ctx.IsCancellationRequested)
        {
            throw await PauseUncertainAsync(v, new OperationCanceledException(ctx)).ConfigureAwait(false);
        }
        PbClock.Status paused;
        try
        {
            var reply2 = await _writer.OwnedPauseAsync(
                new PbClock.OwnedRequest { Identity = v.Epoch!.Origin?.Identity, Owner = v.Epoch.Owner },
                ctx).ConfigureAwait(false);
            if (reply2.Outcome is not PbClock.StatusReplyOutcome.StatusCase { Value: var pausedStatus })
            {
                throw new HeldException();
            }
            paused = pausedStatus;
        }
        catch (Exception e)
        {
            throw await PauseUncertainAsync(v, ErrorJoin.Of(e, CancelledBy(ctx), new HeldException())!).ConfigureAwait(false);
        }
        ClockEpochObligation settled;
        try
        {
            using var persist = new CancellationTokenSource(_config.JournalTimeout);
            settled = await _journal.ObserveClockEpochAsync(v.StartRequestId, v.Sequence, paused.Context ?? throw new EvidenceException(), paused, persist.Token).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            throw await PauseUncertainAsync(v, ErrorJoin.Of(e, CancelledBy(ctx))!).ConfigureAwait(false);
        }
        if (!Terminal(settled.Stage))
        {
            throw ErrorJoin.Of(CancelledBy(ctx), new HeldException())!;
        }
        ctx.ThrowIfCancellationRequested();
    }

    private async Task<Exception> PauseUncertainAsync(ClockEpochObligation v, Exception cause)
    {
        Exception? err = null;
        try
        {
            using var persist = new CancellationTokenSource(_config.JournalTimeout);
            await _journal.MarkClockPauseUncertainAsync(v.StartRequestId, v.Sequence, persist.Token).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            err = e;
        }
        return ErrorJoin.Of(cause, err)!;
    }

    /// <summary>
    /// Pauses a game the player runs by hand under a stopped epoch this process owned (Go <c>RepauseObserved</c>).
    /// The status is the stopped one the caller just read and validated (a scheduler step's bundle); the owned pause
    /// names its epoch, so a replacement owner's clock is never touched. The epoch is already settled, so nothing is
    /// journaled: the returned status is what the admission that follows reads its pause facts from.
    /// </summary>
    public async Task<PbClock.Status> RepauseObserved(PbClock.Status observed, CancellationToken ct)
    {
        var epoch = observed.State is PbClock.StatusState.StoppedCase { Value: var stopped } ? stopped.Epoch : null;
        if (epoch is null || epoch.Owner is null || epoch.Origin is null || observed.Context is not { Identity: { } identity, Tick: var observedTick })
        {
            throw new EvidenceException();
        }
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var call = CancellationTokenSource.CreateLinkedTokenSource(ct);
            call.CancelAfter(_config.CallTimeout);
            var reply = await _writer.OwnedPauseAsync(new PbClock.OwnedRequest { Identity = identity, Owner = epoch.Owner }, call.Token).ConfigureAwait(false);
            var status = ValidatedStatus(reply.Outcome is PbClock.StatusReplyOutcome.StatusCase { Value: var s } ? s : null, identity);
            if (status.State is not PbClock.StatusState.StoppedCase || status.Context?.Tick < observedTick.GetValueOrDefault())
            {
                throw new EvidenceException();
            }
            return status;
        }
        finally
        {
            _gate.Release();
        }
    }
}
