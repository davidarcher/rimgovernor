namespace RimGovernor.Brain.BuildingRuntime;

// Port of internal/buildingruntime/clock_coordinator.go (with clock_window.go and clock_cleanup.go in the sibling
// partials). Go's (value, error) returns that carry both stay a tuple here (the attempt a failed command last
// reached and why it failed); everything else throws. The lease source is dropped with the lease: the in-process
// host has no epoch lease to renew (#3074), so the dispatch no longer asks for one before a write.

/// <summary>
/// Serializes the owned clock commands behind one gate, journals each before and after its native call, and
/// reconciles an uncertain one against the native attempt ledger (Go <c>ClockCoordinator</c>). It never replays a
/// command and never turns a read refusal into no-effect proof.
/// </summary>
public sealed partial class ClockCoordinator : IDisposable
{
    private readonly Lock _mu = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly StoreJournal _journal;
    private readonly IClockNative _native;
    private readonly IClockWriter _writer;
    private readonly IClock _clock;
    private readonly ClockCoordinatorConfig _config;
    private Authority _authority = new();
    private CancellationTokenSource _generation = new();
    private bool _stopped;

    private ClockCoordinator(StoreJournal journal, IClockNative native, IClockWriter writer, IClock clock, ClockCoordinatorConfig config)
    {
        _journal = journal;
        _native = native;
        _writer = writer;
        _clock = clock;
        _config = config;
    }

    /// <summary>Releases the gate and the generation token. Call <see cref="Stop"/> first: this does not join a command in flight.</summary>
    public void Dispose()
    {
        lock (_mu)
        {
            _generation.Dispose();
        }
        _gate.Dispose();
    }

    /// <summary>Go <c>NewClockCoordinator</c>; throws for a missing dependency or a bound outside (0, 1 minute].</summary>
    public static ClockCoordinator Create(StoreJournal journal, IClockNative native, IClockWriter writer, IClock clock, ClockCoordinatorConfig config)
    {
        if (journal is null || native is null || writer is null || clock is null || config is null
            || config.CallTimeout <= TimeSpan.Zero || config.CallTimeout > TimeSpan.FromMinutes(1)
            || config.JournalTimeout <= TimeSpan.Zero || config.JournalTimeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentException("invalid clock coordinator dependencies");
        }
        return new ClockCoordinator(journal, native, writer, clock, config);
    }

    /// <summary>Invalidates queued and active commands when the shared authority changes (Go <c>UpdateAuthority</c>).</summary>
    public void UpdateAuthority(Authority value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Enabled && (!value.Snapshot.IsValid || value.Snapshot.Native.Value == 0 || value.Snapshot.Revision.Value == 0))
        {
            throw new AuthorityException();
        }
        lock (_mu)
        {
            if (_stopped)
            {
                if (value.Enabled)
                {
                    throw new StoppedException();
                }
                return;
            }
            if (value == _authority)
            {
                return;
            }
            _generation.Cancel();
            _generation = new CancellationTokenSource();
            _authority = value;
        }
    }

    /// <summary>Permanently rejects new commands and joins the one in flight (Go <c>Stop</c>); a cancelled token means retry.</summary>
    public async Task Stop(CancellationToken ct)
    {
        lock (_mu)
        {
            _stopped = true;
            _authority = _authority with { Enabled = false };
            _generation.Cancel();
        }
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        _gate.Release();
    }

    /// <summary>One entered gate: the bounded call token, the generation it was admitted under and the release.</summary>
    private sealed class Entry(ClockCoordinator owner, CancellationTokenSource call, CancellationToken generation) : IDisposable
    {
        private int _released;

        public CancellationToken Call => call.Token;

        public CancellationToken Generation => generation;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                owner._gate.Release();
                call.Dispose();
            }
        }
    }

    private async Task<Entry> EnterAsync(CancellationToken ct)
    {
        CancellationToken generation;
        lock (_mu)
        {
            if (_stopped)
            {
                throw new StoppedException();
            }
            generation = _generation.Token;
        }
        var call = CancellationTokenSource.CreateLinkedTokenSource(ct, generation);
        call.CancelAfter(_config.CallTimeout);
        try
        {
            await _gate.WaitAsync(call.Token).ConfigureAwait(false);
        }
        catch
        {
            call.Dispose();
            throw;
        }
        if (call.IsCancellationRequested)
        {
            _gate.Release();
            call.Dispose();
            throw generation.IsCancellationRequested ? new AuthorityException() : new OperationCanceledException(call.Token);
        }
        return new Entry(this, call, generation);
    }

    private void Guard(GenerationSnapshot snapshot, CancellationToken call, CancellationToken generation)
    {
        if (generation.IsCancellationRequested)
        {
            throw new AuthorityException();
        }
        call.ThrowIfCancellationRequested();
        Authority current;
        bool stopped;
        lock (_mu)
        {
            current = _authority;
            stopped = _stopped;
        }
        if (stopped)
        {
            throw new StoppedException();
        }
        if (!current.Enabled || current.Snapshot != snapshot)
        {
            throw new AuthorityException();
        }
    }

    private static ClockExpectation Expectation(ClockAttempt v)
    {
        var s = v.Intent.Snapshot;
        return new ClockExpectation(Boundary.Identity(s), v.NativeAttempt, s.Native.Value, v.Intent.Command);
    }

    internal static bool Terminal(ClockEpochStage stage) =>
        stage is ClockEpochStage.Paused or ClockEpochStage.Retired or ClockEpochStage.Superseded;

    /// <summary>The epoch a running, stopping or stopped status carries, null otherwise (Go <c>clockCoordinatorEpoch</c>).</summary>
    internal static PbClock.Epoch? EpochOf(PbClock.Status? s) =>
        s?.StateCase switch
        {
            PbClock.Status.StateOneofCase.Running => s.Running.Epoch,
            PbClock.Status.StateOneofCase.Stopping => s.Stopping.Epoch,
            PbClock.Status.StateOneofCase.Stopped => s.Stopped.Epoch,
            _ => null,
        };

    internal static bool SameEpoch(PbClock.Epoch? a, PbClock.Epoch? b) =>
        a is not null && b is not null && Equals(a.Owner, b.Owner) && Equals(a.Origin, b.Origin) && Equals(a.Policy, b.Policy)
            && a.StartTick == b.StartTick && a.TickDeadline == b.TickDeadline;

    private static OperationCanceledException? CancelledBy(CancellationToken ct) => ct.IsCancellationRequested ? new OperationCanceledException(ct) : null;

    /// <summary>
    /// Checks the journal and the live clock status before a dispatch. <paramref name="observed"/>, when non-null, is a
    /// validated status the caller read under the same serialized scope moments ago
    /// (<see cref="ClockWindowRequest.Status"/>); it stands in for the native clock status read.
    /// </summary>
    private async Task InspectAsync(ClockAttempt v, PbClock.Status? observed, CancellationToken ctx)
    {
        var epochs = await _journal.LoadClockEpochsAsync(4096, ctx).ConfigureAwait(false);
        var command = v.Intent.Command;
        PbClock.Epoch? original = null;
        if (command.Start is not null)
        {
            var attempts = await _journal.LoadClockAttemptsAsync(4096, ctx).ConfigureAwait(false);
            foreach (var other in attempts)
            {
                if (other.SupersededAt is null && other.Intent.RequestId != v.Intent.RequestId && other.Intent.Command.Start is not null
                    && other.Phase is ClockPhase.Dispatched or ClockPhase.Uncertain)
                {
                    throw new HeldException();
                }
            }
            foreach (var epoch in epochs)
            {
                if (!Terminal(epoch.Stage))
                {
                    throw new HeldException();
                }
            }
        }
        else
        {
            original = command.Renew is not null ? command.Renew.Original : command.Speed!.Original;
            var found = false;
            foreach (var epoch in epochs)
            {
                if (SameEpoch(epoch.Epoch, original) && epoch.Stage == ClockEpochStage.Required)
                {
                    var admitted = await _journal.LookupClockAttemptAsync(epoch.StartRequestId, ctx).ConfigureAwait(false);
                    if (admitted.Intent.Snapshot != v.Intent.Snapshot)
                    {
                        throw new HeldException();
                    }
                    found = true;
                }
            }
            if (!found)
            {
                throw new HeldException();
            }
        }
        var identity = Boundary.Identity(v.Intent.Snapshot);
        var status = observed;
        if (status is null)
        {
            var reply = await _native.ReadClockStatusAsync(identity, ctx).ConfigureAwait(false);
            status = reply.Status;
        }
        BridgeApi.ValidateClockStatus(status, identity);
        ctx.ThrowIfCancellationRequested();
        if (status.StateCase == PbClock.Status.StateOneofCase.Unavailable)
        {
            throw new HeldException();
        }
        if (!status.Context.HasNativeGeneration || status.Context.NativeGeneration != v.Intent.Snapshot.Native.Value)
        {
            throw new HeldException();
        }
        if (command.Start is not null)
        {
            if (status.StateCase is PbClock.Status.StateOneofCase.Running or PbClock.Status.StateOneofCase.Stopping)
            {
                throw new HeldException();
            }
            if (v.Intent.Window is { } window
                && (!status.HasActualPaused || !status.ActualPaused
                    || !status.HasNativeTickBoundary || !status.NativeTickBoundary
                    || !status.HasDurableEvents
                    || (!status.DurableEvents && status.StateCase != PbClock.Status.StateOneofCase.NeverStarted)
                    || status.Context.Tick != window.Tick.Value
                    || !status.HasNewestCursor || status.NewestCursor != window.CapturedCursor))
            {
                throw new HeldException();
            }
            return;
        }
        var actual = EpochOf(status);
        if (status.StateCase != PbClock.Status.StateOneofCase.Running || !SameEpoch(actual, original)
            || actual!.RequestedSpeed != original!.RequestedSpeed || actual.LastTick < original.LastTick)
        {
            throw new HeldException();
        }
    }

    private async Task<(ClockAttempt Attempt, Exception? Error)> UncertainAsync(ClockAttempt v, Exception? cause)
    {
        using var bound = new CancellationTokenSource(_config.JournalTimeout);
        try
        {
            var saved = await _journal.MarkClockUncertainAsync(v.Intent.RequestId, bound.Token).ConfigureAwait(false);
            return (saved, cause);
        }
        catch (Exception e)
        {
            return (v, ErrorJoin.Of(cause, e));
        }
    }

    private async Task<(ClockAttempt Attempt, Exception? Error)> RecordAsync(ClockAttempt v, PbClock.ControlReply? reply, Exception? cause)
    {
        try
        {
            BridgeApi.ValidateClockControlReply(reply, Expectation(v));
        }
        catch (Exception e)
        {
            return await UncertainAsync(v, ErrorJoin.Of(cause, e)).ConfigureAwait(false);
        }
        using var bound = new CancellationTokenSource(_config.JournalTimeout);
        try
        {
            var saved = await _journal.RecordClockReplyAsync(v.Intent.RequestId, reply!, bound.Token).ConfigureAwait(false);
            return (saved, cause);
        }
        catch (Exception e)
        {
            return (v, ErrorJoin.Of(cause, e));
        }
    }

    /// <summary>Runs one trusted clock command (Go <c>Command</c>); a finite window start goes through <see cref="CommandWindow"/>.</summary>
    public Task<(ClockAttempt? Attempt, Exception? Error)> Command(ClockIntent intent, CancellationToken ct)
    {
        if (intent.Window is not null)
        {
            return Task.FromResult<(ClockAttempt?, Exception?)>((null, new HeldException()));
        }
        return CommandAsync(intent, null, null, ct);
    }

    /// <summary>
    /// Runs one serialized clock command. <paramref name="check"/>, when set, re-evaluates the caller's admission at
    /// each gate; <paramref name="observed"/>, when set, is the status the pre-dispatch inspection checks instead of
    /// reading it natively (see <see cref="ClockWindowRequest.Status"/>).
    /// </summary>
    private async Task<(ClockAttempt? Attempt, Exception? Error)> CommandAsync(ClockIntent intent, Action<ClockIntent>? check, Func<PbClock.Status?>? observed, CancellationToken ct)
    {
        Entry entry;
        try
        {
            entry = await EnterAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            return (null, e);
        }
        using (entry)
        {
            var call = entry.Call;
            var generation = entry.Generation;
            ClockAttempt? last = null;
            try
            {
                Guard(intent.Snapshot, call, generation);
                check?.Invoke(intent);
                var (prepared, _) = await _journal.PrepareClockAsync(intent, call).ConfigureAwait(false);
                var v = prepared;
                last = v;
                if (v.Phase != ClockPhase.Prepared)
                {
                    return (v, null);
                }
                await InspectAsync(v, observed?.Invoke(), call).ConfigureAwait(false);
                Guard(v.Intent.Snapshot, call, generation);
                check?.Invoke(v.Intent);
                v = await _journal.DispatchClockAsync(v.Intent.RequestId, call).ConfigureAwait(false);
                last = v;
                try
                {
                    Guard(v.Intent.Snapshot, call, generation);
                    check?.Invoke(v.Intent);
                    Guard(v.Intent.Snapshot, call, generation);
                }
                catch (Exception e)
                {
                    return await UncertainAsync(v, e).ConfigureAwait(false);
                }
                var e0 = Expectation(v);
                var pre = new PbAuthority.WritePrecondition { Identity = e0.Identity, Attempt = e0.Attempt, ExpectedGeneration = e0.NativeGeneration };
                PbClock.ControlReply? reply = null;
                Exception? callError = null;
                try
                {
                    var command = v.Intent.Command;
                    if (command.Start is { } s)
                    {
                        var request = new PbClock.StartRequest
                        {
                            Authority = pre,
                            Speed = s.Speed,
                            Policy = s.Policy,
                            LeaseMs = s.LeaseMs,
                            MaxTicks = s.MaxTicks,
                            TestAcceleration = s.TestAcceleration,
                        };
                        if (s.BlindTickBudget != 0)
                        {
                            request.BlindTickBudget = s.BlindTickBudget;
                        }
                        if (s.MaxTicksPerSecond != 0)
                        {
                            request.MaxTicksPerSecond = s.MaxTicksPerSecond;
                        }
                        if (s.WirePacing is { } pacing)
                        {
                            request.Pacing = pacing;
                        }
                        reply = await _writer.StartAsync(request, call).ConfigureAwait(false);
                    }
                    else if (command.Renew is { } r)
                    {
                        var request = new PbClock.RenewRequest
                        {
                            Epoch = new PbClock.OwnedRequest { Identity = e0.Identity, Owner = r.Original!.Owner },
                            Authority = pre,
                            LeaseMs = r.LeaseMs,
                        };
                        reply = await _writer.RenewAsync(request, r.Original, call).ConfigureAwait(false);
                    }
                    else if (command.Speed is { } sp)
                    {
                        var request = new PbClock.SpeedRequest
                        {
                            Epoch = new PbClock.OwnedRequest { Identity = e0.Identity, Owner = sp.Original!.Owner },
                            Authority = pre,
                            Speed = sp.Speed,
                        };
                        if (sp.MaxTicksPerSecond is { } ceiling)
                        {
                            request.MaxTicksPerSecond = ceiling;
                        }
                        reply = await _writer.ChangeSpeedAsync(request, sp.Original, call).ConfigureAwait(false);
                    }
                }
                catch (Exception e)
                {
                    callError = e;
                }
                return await RecordAsync(v, reply, ErrorJoin.Of(callError, CancelledBy(call))).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                return (last, e);
            }
        }
    }

    /// <summary>Never replays a command or turns a read refusal into no-effect proof (Go <c>Reconcile</c>).</summary>
    public async Task<(ClockAttempt? Attempt, Exception? Error)> Reconcile(string id, CancellationToken ct)
    {
        Entry entry;
        try
        {
            entry = await EnterAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            return (null, e);
        }
        using (entry)
        {
            return await ReconcileLockedAsync(id, entry.Call).ConfigureAwait(false);
        }
    }

    private async Task<(ClockAttempt? Attempt, Exception? Error)> ReconcileLockedAsync(string id, CancellationToken call)
    {
        ClockAttempt v;
        try
        {
            v = await _journal.LookupClockAttemptAsync(id, call).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            return (null, e);
        }
        if (v.SupersededAt is not null || v.Phase is not (ClockPhase.Dispatched or ClockPhase.Uncertain))
        {
            return (v, null);
        }
        var identity = Boundary.Identity(v.Intent.Snapshot);
        PbClock.AttemptReply reply;
        try
        {
            reply = await _native.ReadClockAttemptAsync(new PbClock.AttemptRequest { Identity = identity, Attempt = v.NativeAttempt }, call).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            return await UncertainAsync(v, ErrorJoin.Of(e, CancelledBy(call), new HeldException())).ConfigureAwait(false);
        }
        if (reply.OutcomeCase != PbClock.AttemptReply.OutcomeOneofCase.Receipt)
        {
            if (reply.OutcomeCase == PbClock.AttemptReply.OutcomeOneofCase.Unknown && v.Intent.Command.Start is not null)
            {
                bool absent;
                try
                {
                    absent = await StartAbsentAsync(v, identity, call).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    return await UncertainAsync(v, ErrorJoin.Of(e, CancelledBy(call), new HeldException())).ConfigureAwait(false);
                }
                if (absent)
                {
                    var refusal = new PbClock.ControlReply
                    {
                        Failure = new PbCommon.Failure
                        {
                            Code = PbCommon.FailureCode.NotFound,
                            Detail = "start never admitted: native ledger unknown and no newer clock epoch",
                        },
                    };
                    return await RecordAsync(v, refusal, CancelledBy(call)).ConfigureAwait(false);
                }
            }
            return await UncertainAsync(v, ErrorJoin.Of(CancelledBy(call), new HeldException())).ConfigureAwait(false);
        }
        return await RecordAsync(v, new PbClock.ControlReply { Receipt = reply.Receipt }, CancelledBy(call)).ConfigureAwait(false);
    }

    /// <summary>
    /// Turns a native-unknown start into positive no-effect evidence. The native ledger is unsaved per load, so under
    /// the same load token an unknown key was never admitted; a start that had taken effect would have produced a
    /// newer epoch owned by this session, and the clock status always carries the latest epoch. Both must agree
    /// before the journal records the refusal.
    /// </summary>
    private async Task<bool> StartAbsentAsync(ClockAttempt v, PbCommon.Identity identity, CancellationToken ctx)
    {
        var reply = await _native.ReadClockStatusAsync(identity, ctx).ConfigureAwait(false);
        var status = reply.Status;
        BridgeApi.ValidateClockStatus(status, identity);
        if (status.StateCase == PbClock.Status.StateOneofCase.Unavailable || !Equals(status.Context.Identity, identity))
        {
            return false;
        }
        var actual = EpochOf(status);
        if (actual is null)
        {
            return status.StateCase == PbClock.Status.StateOneofCase.NeverStarted;
        }
        if (actual.Owner.ControllerSessionId != (v.NativeAttempt?.ControllerSessionId ?? string.Empty))
        {
            return true;
        }
        var epochs = await _journal.LoadClockEpochsAsync(4096, ctx).ConfigureAwait(false);
        foreach (var known in epochs)
        {
            if (known.StartRequestId != v.Intent.RequestId && known.Epoch is not null && known.Epoch.Owner.Epoch >= actual.Owner.Epoch)
            {
                return true;
            }
        }
        return false;
    }
}
