namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>Go <c>ErrControl</c>: a planner read went stale or control is unavailable.</summary>
public sealed class ControlException : Exception
{
    public ControlException()
        : base("planner read stale or control unavailable") { }

    public ControlException(string detail)
        : base($"planner read stale or control unavailable: {detail}") { }

    public ControlException(string detail, Exception inner)
        : base($"planner read stale or control unavailable: {detail}", inner) { }
}

/// <summary>Go <c>errors.Join</c>: null when nothing failed, the error itself when one did, else an aggregate.</summary>
internal static class ErrorJoin
{
    public static Exception? Of(params Exception?[] errors)
    {
        var present = errors.Where(e => e is not null).Select(e => e!).ToArray();
        return present.Length switch
        {
            0 => null,
            1 => present[0],
            _ => new AggregateException(present),
        };
    }
}

/// <summary>
/// The separately held read client and trusted authority capability. Neither a model nor the read-only HTTP
/// service receives this owner (Go <c>NativeAuthority</c>).
/// </summary>
public interface INativeAuthority
{
    Task<PbAuthority.StatusReply> ReadAuthorityAsync(PbCommon.Identity identity, CancellationToken ct);

    Task<PbAuthority.ControlReply> SetModeAsync(PbAuthority.SetMode request, CancellationToken ct);

    Task<PbAuthority.ControlReply> RevokeAsync(PbAuthority.Revoke request, CancellationToken ct);
}

/// <summary>The controller session id the control namespaces its writes under (Go <c>SessionIdentity</c>).</summary>
public interface ISessionIdentity
{
    Task<ControllerSessionId> IdentityAsync(CancellationToken ct);
}

/// <summary>
/// Receives authority changes. It must synchronously invalidate queued and active writes and must not call back
/// into <see cref="Control"/>; <see cref="ControlConfig.StopWrites"/> subsequently joins those writers.
/// </summary>
public interface IAuthoritySink
{
    void UpdateAuthority(Authority authority);
}

public sealed record ControlConfig
{
    public required TimeSpan CallTimeout { get; init; }

    public required Func<CancellationToken, Task> StopWrites { get; init; }

    /// <summary>
    /// Joins invalidated commands and drains owned effects under the control gate. It must not call back into
    /// <see cref="Control"/> or acquire authority.
    /// </summary>
    public Func<CancellationToken, Task>? CleanupWrites { get; init; }

    /// <summary>
    /// <see cref="CleanupWrites"/> before an acquisition: it keeps the owned drafts a suspended plan still holds,
    /// since a resume in the same world continues that plan. Null falls back to <see cref="CleanupWrites"/>.
    /// </summary>
    public Func<CancellationToken, Task>? ResumeWrites { get; init; }

    /// <summary>
    /// Reads the actual native identity without entering the control gate. Without it, shutdown cannot retire a
    /// target by proving world replacement.
    /// </summary>
    public IWorldSource? Worlds { get; init; }
}

/// <summary>
/// Local-player authority over the native game. Auto means the bot holds authority outright; there is no
/// negotiated lease to renew or expire. A local-player interruption (detected natively) or an explicit call to
/// <see cref="ManualAsync"/> is what ends authority, and either bumps the generation the next write must match.
/// The profile lock (Go <c>ProfileOwner</c>) is dropped: the brain runs in the mod process, which is the only
/// owner of the profile.
/// </summary>
public sealed partial class Control : IDisposable
{
    private readonly object _mu = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ControlConfig _config;
    private readonly INativeAuthority _native;
    private readonly IAuthoritySink _sink;
    private readonly CancellationToken _lifetime;
    private readonly CancellationTokenRegistration _stopLifetime;
    private CancellationTokenSource _epochSource;
    private CancellationToken _epoch;
    private GenerationSnapshot _snapshot;
    private bool _haveTarget;
    private bool _observationKnown;
    // Granted: _snapshot.Native is a grant this process accepted and has not since observed revoked. A Disable
    // keeps it (the grant still stands natively); an observed Inactive, a replaced target or a fresh acquisition
    // clears it.
    private bool _granted;
    private bool _active;
    private bool _closing;
    private bool _closed;

    internal ControllerSessionId Namespace { get; }

    internal ControlConfig Config => _config;

    internal INativeAuthority Native => _native;

    private Control(ControlConfig config, ControllerSessionId ns, INativeAuthority native, IAuthoritySink sink, CancellationToken lifetime)
    {
        _config = config;
        Namespace = ns;
        _native = native;
        _sink = sink;
        _lifetime = lifetime;
        _epochSource = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        _epoch = _epochSource.Token;
        _stopLifetime = lifetime.Register(() =>
        {
            lock (_mu)
            {
                if (!_closed)
                {
                    _ = InvalidateLocked();
                }
            }
        });
    }

    public static async Task<Control> CreateAsync(ControlConfig config, ISessionIdentity identity, INativeAuthority native, IAuthoritySink sink, CancellationToken lifetime)
    {
        if (config.CallTimeout <= TimeSpan.Zero || config.CallTimeout > TimeSpan.FromMinutes(1))
        {
            throw new ControlException("NewControl: config.CallTimeout out of range");
        }
        var ns = await identity.IdentityAsync(lifetime).ConfigureAwait(false);
        if (!ControlId(ns.Value))
        {
            throw new ControlException("NewControl: invalid controller session id");
        }
        var control = new Control(config, ns, native, sink, lifetime);
        try
        {
            sink.UpdateAuthority(new Authority());
        }
        catch
        {
            control._epochSource.Cancel();
            control._stopLifetime.Dispose();
            control._gate.Dispose();
            throw;
        }
        return control;
    }

    /// <summary>
    /// Must be called only by an authenticated explicit-player entrypoint. The wire contract carries identity,
    /// generation and the requested mode; the requested snapshot's plan and revision are scoped intent, not
    /// authentication. Fresh native CAS replaces the requested native generation.
    /// </summary>
    public async Task<GenerationSnapshot> AcquireAsync(GenerationSnapshot requested, CancellationToken ct, NativeGeneration? handoff = null)
    {
        try
        {
            return await AcquireOnceAsync(requested, handoff, ct).ConfigureAwait(false);
        }
        catch (EpochReplacedException) when (!ct.IsCancellationRequested)
        {
            return await AcquireOnceAsync(requested, handoff, ct).ConfigureAwait(false);
        }
    }

    /// <summary>An Acquire whose SetMode succeeded after a concurrent Disable replaced the epoch: the stale grant was revoked and one fresh Acquire is allowed.</summary>
    private sealed class EpochReplacedException()
        : Exception("planner read stale or control unavailable: Acquire: epoch replaced during SetMode");

    private async Task<GenerationSnapshot> AcquireOnceAsync(GenerationSnapshot requested, NativeGeneration? handoff, CancellationToken ct)
    {
        requested.Validate();
        if (requested.Revision.Value == 0)
        {
            throw new ControlException("Acquire: requested.Revision == 0");
        }
        var observed = await ObserveForAcquireAsync(requested, handoff, ct).ConfigureAwait(false);
        using var entry = observed.Entry;
        var epoch = observed.Epoch;
        var status = observed.Status;
        var generation = status.Outcome?.Status?.Context?.NativeGeneration.GetValueOrDefault() ?? 0UL;
        if (generation == ulong.MaxValue)
        {
            throw new ControlException("Acquire: generation == ^uint64(0)");
        }
        if (status.Outcome?.Status?.State?.Inactive is null)
        {
            // Auto authority observed before this process ever targeted the world is what a killed controller
            // leaves behind. This process is the only author, so it reclaims by revoking at the observed
            // generation and acquiring fresh, rather than staying unresumable until a local player interrupts
            // the game. Its own grant, disabled locally on a clock hold and still Auto at the generation it
            // accepted, is re-acquired in place: one SetMode, one generation, no Manual round trip. Once this
            // process has targeted the world, any other Active is its own uncertain grant, which only Manual
            // reconciles.
            var active = status.Outcome?.Status?.State?.Active;
            if (active is null || active.Mode != PbAuthority.Mode.Auto)
            {
                throw new ControlException("Acquire: active == nil || active.GetMode() != a.Mode_MODE_AUTO");
            }
            if (observed.EverTargeted)
            {
                if (!HoldsGrant(requested, generation))
                {
                    throw new ControlException("Acquire: !control.holdsGrant(requested, generation)");
                }
            }
            else
            {
                PbAuthority.ControlReply result;
                try
                {
                    result = await _native.RevokeAsync(new PbAuthority.Revoke { Identity = ControlIdentity(requested), ExpectedGeneration = generation, Reason = PbAuthority.RevocationReason.Manual }, entry.Token).ConfigureAwait(false);
                }
                catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    throw FailedObservation(e, epoch);
                }
                var revoked = result.Outcome?.Revoked;
                if (revoked is null || !ContextValid(revoked.Context) || !Equals(revoked.Context.Identity, ControlIdentity(requested)) || revoked.Context.NativeGeneration is null || revoked.Context.NativeGeneration.GetValueOrDefault() != generation + 1 || generation + 1 == ulong.MaxValue)
                {
                    throw new ControlException("Acquire: revoked == nil || bridge.ValidateContext(revoked.Context) != nil || !proto.Equal(revoked.Context.Identity, ...");
                }
                generation = revoked.Context.NativeGeneration.GetValueOrDefault();
            }
        }
        requested = requested with { Native = new NativeGeneration(generation) };
        lock (_mu)
        {
            if (epoch.IsCancellationRequested || _closing)
            {
                throw new ControlException("Acquire: epoch.Err() != nil || control.closing");
            }
            _snapshot = requested;
            _haveTarget = true;
            _granted = false;
        }
        // The write runs under the caller's context and the call timeout, not the epoch: the SetMode itself is
        // the AuthorityChanged the clock poll ingested, and the poll's Disable on it cancelled the in-flight
        // call and left the resume uncertain. The gate stays held.
        using var writeSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
        writeSource.CancelAfter(_config.CallTimeout);
        var write = writeSource.Token;
        var reply = await _native.SetModeAsync(new PbAuthority.SetMode { Identity = ControlIdentity(requested), ExpectedGeneration = generation, Mode = PbAuthority.Mode.Auto }, write).ConfigureAwait(false);
        requested = requested with { Native = new NativeGeneration(requested.Native.Value + 1) };
        if (epoch.IsCancellationRequested && !IsClosing() && reply.Outcome?.Granted?.Context?.NativeGeneration == requested.Native.Value)
        {
            // The epoch was replaced mid-write: this grant is stale evidence. Revoke it at the known generation,
            // then the caller acquires again under the new episode.
            _ = await _native.RevokeAsync(new PbAuthority.Revoke { Identity = ControlIdentity(requested), ExpectedGeneration = requested.Native.Value, Reason = PbAuthority.RevocationReason.Manual }, write).ConfigureAwait(false);
            throw new EpochReplacedException();
        }
        AcceptGrant(reply, requested, write, epoch);
        return requested;
    }

    private bool IsClosing()
    {
        lock (_mu)
        {
            return _closing;
        }
    }

    /// <summary>
    /// Whether an Active observed at <paramref name="generation"/> in the requested world is the grant this
    /// process accepted and merely disabled locally. The grant is world-scoped: the plan it is re-acquired
    /// under may differ from the one it was granted under.
    /// </summary>
    private bool HoldsGrant(GenerationSnapshot requested, ulong generation)
    {
        lock (_mu)
        {
            return _granted && _haveTarget && _snapshot.SameWorld(requested) && _snapshot.Native.Value == generation;
        }
    }

    /// <summary>
    /// Whether the control's last accepted grant is in <paramref name="scope"/>'s world and still stands as far
    /// as it has observed: authority may be disabled locally (a clock hold) but no read or revoke has shown the
    /// native side off since. A player resume in that state re-acquires directly instead of revoking first.
    /// </summary>
    public bool HoldsGrant(GenerationSnapshot scope)
    {
        lock (_mu)
        {
            return !_closing && !_closed && _granted && _haveTarget && _snapshot.SameWorld(scope);
        }
    }

    /// <summary>Bounds how often ObserveForAcquire starts over after its epoch was invalidated underneath the status read.</summary>
    private const int AcquireObservationRetries = 3;

    private sealed record AcquireObservation(Entry Entry, bool EverTargeted, PbAuthority.StatusReply Status, CancellationToken Epoch);

    /// <summary>
    /// Acquire's read phase: a fresh epoch, the gate, owned cleanup and the authority status read, before any
    /// native write. Disable is called outside the gate (the clock poll on a gap, an interrupting event or a
    /// pending hold) and cancels the epoch the read runs under, which surfaced as a cancelled authority status
    /// read and an uncertain resume. Nothing has been written at that point, so an epoch cancelled while the
    /// caller's context is still live is not an uncertain outcome: start over under the next epoch, a bounded
    /// number of times, and only report the failure once it persists.
    /// </summary>
    private async Task<AcquireObservation> ObserveForAcquireAsync(GenerationSnapshot requested, NativeGeneration? handoff, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            CancellationToken epoch;
            bool everTargeted;
            Exception? invalidated;
            lock (_mu)
            {
                if (_closing || _lifetime.IsCancellationRequested)
                {
                    throw new ControlException("observeForAcquire: control.closing || control.lifetime.Err() != nil");
                }
                invalidated = InvalidateLocked();
                epoch = _epoch;
                everTargeted = _haveTarget;
            }
            if (invalidated is not null)
            {
                throw invalidated;
            }
            Entry? entry = null;
            Exception? failure = null;
            try
            {
                entry = await EnterAsync(ct, epoch).ConfigureAwait(false);
                var call = entry.Token;
                if (_config.ResumeWrites is { } drain)
                {
                    await drain(call).ConfigureAwait(false);
                }
                else if (_config.CleanupWrites is { } cleanup)
                {
                    await cleanup(call).ConfigureAwait(false);
                }
                var status = await _native.ReadAuthorityAsync(ControlIdentity(requested), call).ConfigureAwait(false);
                try
                {
                    ControlStatus(status, requested);
                    if (handoff is { } required && (required.Value == 0 || (status.Outcome?.Status?.Context?.NativeGeneration.GetValueOrDefault() ?? 0UL) != required.Value))
                    {
                        throw new ControlException();
                    }
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    throw FailedObservation(e, epoch);
                }
                call.ThrowIfCancellationRequested();
                return new AcquireObservation(entry, everTargeted, status, epoch);
            }
            catch (Exception e)
            {
                failure = e;
            }
            entry?.Dispose();
            if (attempt >= AcquireObservationRetries || ct.IsCancellationRequested || !epoch.IsCancellationRequested)
            {
                throw failure;
            }
        }
    }

    /// <summary>
    /// Checks that the bot currently holds Auto-mode authority for the given snapshot. It is checked again
    /// immediately before native dispatch.
    /// </summary>
    public bool Lease(GenerationSnapshot snapshot)
    {
        lock (_mu)
        {
            if (!LiveLocked() || !_snapshot.Matches(snapshot))
            {
                throw new ControlException("Lease: !control.liveLocked() || !control.snapshot.Matches(snapshot)");
            }
            return true;
        }
    }

    /// <summary>
    /// Disables local authority before waiting for any native call. Inspection reconciles an uncertain SetMode
    /// call, but never adopts a grant from it. Its native revoke and owned cleanup run under the gate but not
    /// under the control epoch: the revoke itself is the AuthorityChanged event the clock poll ingests, and the
    /// poll's Disable on that fresh evidence replaces the epoch.
    /// </summary>
    public Task ManualAsync(CancellationToken ct) => ManualCoreAsync(false, ct);

    /// <summary>
    /// The Manual a player resume runs after an observed revocation, before it re-acquires: it drains like
    /// Acquire (ResumeWrites), so the drafts a plan or an open fight still holds stay owned.
    /// </summary>
    public Task ManualForResumeAsync(CancellationToken ct) => ManualCoreAsync(true, ct);

    private async Task ManualCoreAsync(bool resume, CancellationToken ct)
    {
        Exception? err;
        lock (_mu)
        {
            if (_closing)
            {
                throw new ControlException("manual: control.closing");
            }
            err = InvalidateLocked();
        }
        Entry entry;
        try
        {
            entry = await EnterGateAsync(ct).ConfigureAwait(false);
        }
        catch (Exception enterErr)
        {
            throw ErrorJoin.Of(err, enterErr)!;
        }
        using (entry)
        {
            err = ErrorJoin.Of(err, await Capture(async () => await RevokeAsync(PbAuthority.RevocationReason.Manual, entry.Token).ConfigureAwait(false)).ConfigureAwait(false));
            if (resume && _config.ResumeWrites is { } drain)
            {
                err = ErrorJoin.Of(err, await Capture(() => drain(entry.Token)).ConfigureAwait(false));
            }
            else if (_config.CleanupWrites is { } cleanup)
            {
                err = ErrorJoin.Of(err, await Capture(() => cleanup(entry.Token)).ConfigureAwait(false));
            }
        }
        if (err is not null)
        {
            throw err;
        }
    }

    private static async Task<Exception?> Capture(Func<Task> work)
    {
        try
        {
            await work().ConfigureAwait(false);
            return null;
        }
        catch (Exception e)
        {
            return e;
        }
    }

    /// <summary>Releases the control gate. Call it after <see cref="CloseAsync"/> succeeded (or never started), when no caller can still wait on the gate.</summary>
    public void Dispose()
    {
        _stopLifetime.Dispose();
        _gate.Dispose();
    }

    /// <summary>
    /// Retryable when the writer drain or native revoke fails. Authority stays disabled. The caller retains
    /// bridge and store ownership until Close succeeds. No uncertain native response is translated into a retry
    /// grant.
    /// </summary>
    public async Task CloseAsync(CancellationToken ct)
    {
        Exception? err;
        lock (_mu)
        {
            if (_closed)
            {
                return;
            }
            _closing = true;
            err = InvalidateLocked();
        }
        using var callSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
        callSource.CancelAfter(_config.CallTimeout);
        var call = callSource.Token;
        try
        {
            await _gate.WaitAsync(call).ConfigureAwait(false);
        }
        catch (OperationCanceledException cancelled)
        {
            throw ErrorJoin.Of(err, cancelled)!;
        }
        try
        {
            if (call.IsCancellationRequested)
            {
                throw ErrorJoin.Of(err, new OperationCanceledException(call))!;
            }
            lock (_mu)
            {
                if (_closed)
                {
                    return;
                }
            }
            var drainErr = await Capture(() => _config.StopWrites(call)).ConfigureAwait(false);
            if (drainErr is not null)
            {
                throw ErrorJoin.Of(err, drainErr)!;
            }
            var revokeErr = await Capture(async () => await ShutdownTargetAsync(call).ConfigureAwait(false)).ConfigureAwait(false);
            lock (_mu)
            {
                if (err is not null || revokeErr is not null)
                {
                    throw ErrorJoin.Of(err, revokeErr)!;
                }
                _closed = true;
                _stopLifetime.Dispose();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Runs under the control gate after writers have drained. A positively observed replacement retires only
    /// the old revoke target; it never adopts or writes authority in the replacement world.
    /// </summary>
    private async Task ShutdownTargetAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        GenerationSnapshot snapshot;
        bool known;
        lock (_mu)
        {
            snapshot = _snapshot;
            known = _haveTarget;
        }
        if (known && _config.Worlds is { } worlds)
        {
            var actual = await worlds.ReadWorldAsync(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            actual.Validate();
            if (actual != Player.PlayerWorld(snapshot))
            {
                return;
            }
        }
        await RevokeAsync(PbAuthority.RevocationReason.Shutdown, ct).ConfigureAwait(false);
    }

    private Exception? InvalidateLocked()
    {
        _epochSource.Cancel();
        _epochSource.Dispose();
        _epochSource = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
        _epoch = _epochSource.Token;
        _active = false;
        var value = new Authority();
        if (_observationKnown)
        {
            value = value with { Snapshot = _snapshot };
        }
        return Capture(() => _sink.UpdateAuthority(value));
    }

    private static Exception? Capture(Action work)
    {
        try
        {
            work();
            return null;
        }
        catch (Exception e)
        {
            return e;
        }
    }

    /// <summary>
    /// A cleanup identity is not current observation authority. Keep it for revoke and refresh retries while
    /// cancelling reconciliation until a fresh read succeeds. Returns the error to throw.
    /// </summary>
    private Exception FailedObservation(Exception cause, CancellationToken epoch)
    {
        lock (_mu)
        {
            if (_epoch != epoch || _closing)
            {
                return cause;
            }
            return FailedObservationLocked(cause);
        }
    }

    private Exception FailedObservationLocked(Exception cause)
    {
        _observationKnown = false;
        return ErrorJoin.Of(cause, InvalidateLocked())!;
    }

    /// <summary>
    /// Whether authority is currently held. There is no lease deadline; live means the last observation or grant
    /// said Auto and nothing has invalidated it since.
    /// </summary>
    private bool LiveLocked() => !_closing && !_lifetime.IsCancellationRequested && _active;

    /// <summary>One entry through the gate: the call token (timeout and epoch) and the release.</summary>
    private sealed class Entry(SemaphoreSlim gate, CancellationTokenSource source) : IDisposable
    {
        public CancellationToken Token => source.Token;

        public void Dispose()
        {
            gate.Release();
            source.Dispose();
        }
    }

    private async Task<Entry> EnterAsync(CancellationToken ct, CancellationToken epoch)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(ct, epoch);
        source.CancelAfter(_config.CallTimeout);
        try
        {
            await _gate.WaitAsync(source.Token).ConfigureAwait(false);
        }
        catch
        {
            source.Dispose();
            throw;
        }
        if (source.IsCancellationRequested)
        {
            _gate.Release();
            source.Dispose();
            throw new OperationCanceledException(source.Token);
        }
        if (epoch.IsCancellationRequested)
        {
            _gate.Release();
            source.Dispose();
            throw new ControlException("enter: epoch.Err() != nil");
        }
        return new Entry(_gate, source);
    }

    /// <summary>
    /// Takes the gate under the caller's context and the call timeout alone, for work that turns authority off
    /// (Manual) and must outlive a concurrent Disable's epoch replacement.
    /// </summary>
    private async Task<Entry> EnterGateAsync(CancellationToken ct)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(ct);
        source.CancelAfter(_config.CallTimeout);
        try
        {
            await _gate.WaitAsync(source.Token).ConfigureAwait(false);
        }
        catch
        {
            source.Dispose();
            throw;
        }
        if (source.IsCancellationRequested)
        {
            _gate.Release();
            source.Dispose();
            throw new OperationCanceledException(source.Token);
        }
        return new Entry(_gate, source);
    }

    private void AcceptGrant(PbAuthority.ControlReply reply, GenerationSnapshot snapshot, CancellationToken call, CancellationToken epoch)
    {
        var grant = reply.Outcome?.Granted;
        if (grant is null || !ContextValid(grant.Context) || !Equals(grant.Context.Identity, ControlIdentity(snapshot)) || grant.Context.NativeGeneration is null || grant.Context.NativeGeneration.GetValueOrDefault() != snapshot.Native.Value || grant.Authority is null || grant.Authority.Mode.GetValueOrDefault() != PbAuthority.Mode.Auto)
        {
            throw new ControlException("acceptGrant: grant == nil || bridge.ValidateContext(grant.Context) != nil || !proto.Equal(grant.Context.Identity, ...");
        }
        lock (_mu)
        {
            if (call.IsCancellationRequested || epoch.IsCancellationRequested || _closing)
            {
                throw new ControlException("acceptGrant: call.Err() != nil || epoch.Err() != nil || control.closing");
            }
            _snapshot = snapshot;
            _active = true;
            _granted = true;
            _observationKnown = true;
            try
            {
                _sink.UpdateAuthority(new Authority { Snapshot = snapshot, Enabled = true });
            }
            catch
            {
                _ = InvalidateLocked();
                throw;
            }
        }
    }

    private async Task RevokeAsync(PbAuthority.RevocationReason reason, CancellationToken ct)
    {
        GenerationSnapshot snapshot;
        bool known;
        CancellationToken epoch;
        lock (_mu)
        {
            snapshot = _snapshot;
            known = _haveTarget;
            epoch = _epoch;
        }
        if (!known)
        {
            return;
        }
        PbAuthority.StatusReply reply;
        try
        {
            reply = await _native.ReadAuthorityAsync(ControlIdentity(snapshot), ct).ConfigureAwait(false);
            ControlStatus(reply, snapshot);
        }
        catch (Exception e)
        {
            throw FailedObservation(e, epoch);
        }
        ct.ThrowIfCancellationRequested();
        var status = reply.Outcome?.Status ?? throw new ControlException("revoke: status == nil");
        var statusGeneration = status.Context?.NativeGeneration.GetValueOrDefault() ?? 0UL;
        if (status.State?.Inactive is not null)
        {
            PublishDisabled(snapshot, statusGeneration, ct);
            return;
        }
        var active = status.State?.Active;
        if (active is null || active.Mode.GetValueOrDefault() != PbAuthority.Mode.Auto)
        {
            throw new ControlException("revoke: active == nil || active.GetMode() != a.Mode_MODE_AUTO");
        }
        var generation = statusGeneration;
        if (generation == ulong.MaxValue)
        {
            throw new ControlException("revoke: generation == ^uint64(0)");
        }
        var result = await _native.RevokeAsync(new PbAuthority.Revoke { Identity = ControlIdentity(snapshot), ExpectedGeneration = generation, Reason = reason }, ct).ConfigureAwait(false);
        var revoked = result.Outcome?.Revoked;
        if (revoked is null || !ContextValid(revoked.Context) || !Equals(revoked.Context.Identity, ControlIdentity(snapshot)) || revoked.Context.NativeGeneration is null || revoked.Context.NativeGeneration.GetValueOrDefault() != generation + 1 || revoked.Authority is null || revoked.Authority.Reason is null || revoked.Authority.Reason.GetValueOrDefault() != reason)
        {
            throw new ControlException("revoke: revoked == nil || bridge.ValidateContext(revoked.Context) != nil || !proto.Equal(revoked.Context.Identity, ...");
        }
        PublishDisabled(snapshot, revoked.Context.NativeGeneration.GetValueOrDefault(), ct);
    }

    private void PublishDisabled(GenerationSnapshot expected, ulong generation, CancellationToken ct)
    {
        lock (_mu)
        {
            if (ct.IsCancellationRequested || !_snapshot.Matches(expected))
            {
                throw new ControlException("publishDisabled: ctx.Err() != nil || !control.snapshot.Matches(expected)");
            }
            _snapshot = _snapshot with { Native = new NativeGeneration(generation) };
            _active = false;
            _granted = false;
            _observationKnown = true;
            _sink.UpdateAuthority(new Authority { Snapshot = _snapshot });
        }
    }

    /// <summary>
    /// Seeds a read-only restart target after the caller validates the durable plan and revision. It cannot
    /// replace live authority or adopt a grant.
    /// </summary>
    public async Task ObserveTargetAsync(GenerationSnapshot requested, CancellationToken ct)
    {
        requested.Validate();
        if (requested.Revision.Value == 0)
        {
            throw new ControlException("ObserveTarget: requested.Revision == 0");
        }
        CancellationToken epoch;
        lock (_mu)
        {
            if (_closing || LiveLocked())
            {
                throw new ControlException("ObserveTarget: control.closing || control.liveLocked()");
            }
            epoch = _epoch;
        }
        using var entry = await EnterAsync(ct, epoch).ConfigureAwait(false);
        var call = entry.Token;
        lock (_mu)
        {
            if (_closing || epoch.IsCancellationRequested || _epoch != epoch || LiveLocked())
            {
                throw new ControlException("ObserveTarget: control.closing || epoch.Err() != nil || control.epoch != epoch || control.liveLocked()");
            }
        }
        PbAuthority.StatusReply? reply = null;
        Exception? err = null;
        try
        {
            reply = await _native.ReadAuthorityAsync(ControlIdentity(requested), call).ConfigureAwait(false);
            ControlStatus(reply, requested);
        }
        catch (Exception e)
        {
            err = e;
        }
        lock (_mu)
        {
            if (call.IsCancellationRequested || epoch.IsCancellationRequested || _epoch != epoch || _closing)
            {
                var cause = ErrorJoin.Of(new ControlException(), call.IsCancellationRequested ? new OperationCanceledException(call) : null)!;
                if (_epoch == epoch && !_closing)
                {
                    throw FailedObservationLocked(cause);
                }
                throw cause;
            }
            if (err is not null)
            {
                throw FailedObservationLocked(err);
            }
            requested = requested with { Native = new NativeGeneration(reply!.Outcome?.Status?.Context?.NativeGeneration.GetValueOrDefault() ?? 0UL) };
            // The target plan is only a read scope; the grant this process holds is world-scoped and still
            // stands while native reports it Auto at the generation it accepted (the worker retargets reads
            // under a hold).
            var still = _granted && _haveTarget && _snapshot.SameWorld(requested) && _snapshot.Native == requested.Native && reply.Outcome?.Status?.State?.Active is not null;
            _snapshot = requested;
            _haveTarget = true;
            _granted = still;
            _observationKnown = true;
            _sink.UpdateAuthority(new Authority { Snapshot = _snapshot });
        }
    }

    /// <summary>
    /// Observes the stored target without acquiring authority. Known revocation or generation changes remain
    /// available for read reconciliation.
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct)
    {
        CancellationToken epoch;
        GenerationSnapshot snapshot;
        lock (_mu)
        {
            if (_closing || !_haveTarget)
            {
                throw new ControlException("Refresh: control.closing || !control.haveTarget");
            }
            epoch = _epoch;
            snapshot = _snapshot;
        }
        using var entry = await EnterAsync(ct, epoch).ConfigureAwait(false);
        var call = entry.Token;
        PbAuthority.StatusReply? reply = null;
        Exception? err = null;
        try
        {
            reply = await _native.ReadAuthorityAsync(ControlIdentity(snapshot), call).ConfigureAwait(false);
            ControlStatus(reply, snapshot);
        }
        catch (Exception e)
        {
            err = e;
        }
        lock (_mu)
        {
            if (call.IsCancellationRequested || epoch.IsCancellationRequested || _epoch != epoch || _closing)
            {
                var cause = ErrorJoin.Of(new ControlException(), call.IsCancellationRequested ? new OperationCanceledException(call) : null)!;
                if (_epoch == epoch && !_closing)
                {
                    throw FailedObservationLocked(cause);
                }
                throw cause;
            }
            if (err is not null)
            {
                throw FailedObservationLocked(err);
            }
            var status = reply!.Outcome?.Status ?? throw new ControlException("Refresh: status == nil");
            var active = status.State?.Active;
            var statusGeneration = status.Context?.NativeGeneration.GetValueOrDefault() ?? 0UL;
            if (statusGeneration != snapshot.Native.Value || active is null || active.Mode.GetValueOrDefault() != PbAuthority.Mode.Auto)
            {
                var invalidated = InvalidateLocked();
                _snapshot = _snapshot with { Native = new NativeGeneration(statusGeneration) };
                _granted = false;
                _observationKnown = true;
                throw ErrorJoin.Of(invalidated, Capture(() => _sink.UpdateAuthority(new Authority { Snapshot = _snapshot }))) ?? new ControlException();
            }
            _active = true;
        }
    }

    internal static void ControlStatus(PbAuthority.StatusReply reply, GenerationSnapshot snapshot)
    {
        var status = reply.Outcome?.Status;
        if (status is null || !ContextValid(status.Context) || !Equals(status.Context.Identity, ControlIdentity(snapshot)) || status.Context.NativeGeneration is null)
        {
            throw new ControlException("controlStatus: status == nil || bridge.ValidateContext(status.Context) != nil || !proto.Equal(status.Context.Identity, ...");
        }
        if (status.State?.Inactive is { } inactive)
        {
            if (inactive.Reason is null || inactive.Reason.GetValueOrDefault() <= 0 || inactive.Reason.GetValueOrDefault() > PbAuthority.RevocationReason.Unavailable)
            {
                throw new ControlException("controlStatus: inactive.Reason == nil || *inactive.Reason <= 0 || *inactive.Reason > a.RevocationReason_REVOCATION_REASON_UNAVAILABLE");
            }
            return;
        }
        if (status.State?.Active is { } active && active.Mode.GetValueOrDefault() == PbAuthority.Mode.Auto)
        {
            return;
        }
        throw new ControlException("controlStatus: check failed");
    }

    private static bool ContextValid([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] PbCommon.ObservationContext? context)
    {
        try
        {
            BridgeApi.ValidateContext(context);
            return true;
        }
        catch (DomainException)
        {
            return false;
        }
    }

    internal static PbCommon.Identity ControlIdentity(GenerationSnapshot s) =>
        new() { ColonyId = s.Colony.Value, LoadToken = s.Load.Value, MapId = s.Map.Value };

    internal static bool ControlId(string value) =>
        !string.IsNullOrWhiteSpace(value) && System.Text.Encoding.UTF8.GetByteCount(value) <= 256 && !value.Contains('\0', StringComparison.Ordinal);
}
