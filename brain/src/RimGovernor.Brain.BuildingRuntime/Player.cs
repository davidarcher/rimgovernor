using System.Runtime.ExceptionServices;

namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>Reads the actual native identity of the loaded world (Go <c>WorldSource</c>).</summary>
public interface IWorldSource
{
    Task<World> ReadWorldAsync(CancellationToken ct);
}

public sealed record PlayerConfig(TimeSpan CallTimeout, TimeSpan JournalTimeout);

/// <summary>What the player coordinator needs of the session that owns the control (Go <c>playerSession</c>).</summary>
public interface IPlayerSession
{
    /// <summary>
    /// Acquires Auto authority. <paramref name="handoff"/> is the native generation a map-focus handoff already
    /// produced (Go carried it in the context): the status read must show exactly that generation.
    /// </summary>
    Task<GenerationSnapshot> AcquireAsync(GenerationSnapshot requested, CancellationToken ct, NativeGeneration? handoff = null);

    Task ManualAsync(CancellationToken ct);

    Task ManualForResumeAsync(CancellationToken ct);

    bool HoldsGrant(GenerationSnapshot scope);

    bool TargetsWorld(World world);

    void Disable();

    ControlState State();

    Task CloseAsync(CancellationToken ct);
}

/// <summary>How long one caller waited for the player gate and who held it when the wait began (Go <c>gateWait</c>).</summary>
internal readonly record struct GateWait(TimeSpan Wait, string Holder, TimeSpan HolderHeld);

/// <summary>
/// Coordinates explicit trusted player requests. HTTP authentication and admission belong to its caller. It
/// never runs plans, renews leases or resumes time.
/// </summary>
public sealed partial class Player
{
    private readonly object _mu = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly PlayerConfig _config;
    private readonly JournalStore _journal;
    private readonly IPlayerSession _session;
    private readonly IWorldSource _worlds;
    private readonly CancellationToken _lifetime;
    private readonly CancellationTokenRegistration _stopLifetime;
    private CancellationTokenSource _epochSource;
    private CancellationToken _epoch;
    private bool _closing;
    private bool _closed;
    // Holder names the caller holding the gate and when it took it, so a slow wait can name what it queued behind.
    private string _holder = "";
    private DateTimeOffset _holderSince;

    internal JournalStore Journal => _journal;

    private Player(PlayerConfig config, JournalStore journal, IPlayerSession session, IWorldSource worlds, CancellationToken lifetime)
    {
        _config = config;
        _journal = journal;
        _session = session;
        _worlds = worlds;
        _lifetime = lifetime;
        _epochSource = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        _epoch = _epochSource.Token;
        _stopLifetime = lifetime.Register(() =>
        {
            lock (_mu)
            {
                if (!_closed)
                {
                    _epochSource.Cancel();
                    try
                    {
                        _session.Disable();
                    }
                    catch (Exception)
                    {
                        // the epoch is already cancelled; nothing left to report to
                    }
                }
            }
        });
    }

    public static Player Create(PlayerConfig config, JournalStore journal, Session session, IWorldSource worlds, CancellationToken lifetime)
    {
        if (!ReferenceEquals(session.Journal, journal))
        {
            throw new ControlException("NewPlayer: session == nil || session.journal != journal");
        }
        return Create(config, journal, (IPlayerSession)session, worlds, lifetime);
    }

    internal static Player Create(PlayerConfig config, JournalStore journal, IPlayerSession session, IWorldSource worlds, CancellationToken lifetime)
    {
        if (config.CallTimeout <= TimeSpan.Zero || config.CallTimeout > TimeSpan.FromMinutes(1) || config.JournalTimeout <= TimeSpan.Zero || config.JournalTimeout > TimeSpan.FromMinutes(1))
        {
            throw new ControlException("newPlayer: config.CallTimeout or config.JournalTimeout out of range");
        }
        lifetime.ThrowIfCancellationRequested();
        session.Disable();
        return new Player(config, journal, session, worlds, lifetime);
    }

    public ControlState State() => _session.State();

    /// <summary>One caller's passage through the player gate: the call token, the epoch it ran under and the release.</summary>
    internal sealed class Entry(Player player, CancellationTokenSource call, CancellationToken epoch, GateWait waited) : IDisposable
    {
        public CancellationToken Call => call.Token;

        public CancellationToken Epoch => epoch;

        public GateWait Waited => waited;

        public void Dispose()
        {
            lock (player._mu)
            {
                player._holder = "";
                player._holderSince = default;
            }
            player._gate.Release();
            call.Dispose();
        }
    }

    /// <summary>Takes the player gate for a caller named by <paramref name="label"/> (clock_step, rounds_review, submit, manual, ...).</summary>
    internal async Task<Entry> EnterAsync(CancellationToken ct, string label, bool manual)
    {
        CancellationToken epoch;
        string holder;
        DateTimeOffset holderSince;
        lock (_mu)
        {
            if (_closing || _closed || _lifetime.IsCancellationRequested)
            {
                throw new ControlException("enter: p.closing || p.closed || p.lifetime.Err() != nil");
            }
            if (manual)
            {
                _epochSource.Cancel();
                _epochSource = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
                _epoch = _epochSource.Token;
                // Local safety comes before a potentially stale request, queue or database.
                _session.Disable();
            }
            epoch = _epoch;
            holder = _holder;
            holderSince = _holderSince;
        }
        var began = DateTimeOffset.UtcNow;
        // Queueing and the native call each receive CallTimeout, so waiting cannot consume the call's budget.
        // Both end with the epoch.
        using (var wait = CancellationTokenSource.CreateLinkedTokenSource(ct, epoch))
        {
            wait.CancelAfter(_config.CallTimeout);
            await _gate.WaitAsync(wait.Token).ConfigureAwait(false);
        }
        var acquired = DateTimeOffset.UtcNow;
        var waited = new GateWait(acquired - began, holder, holder.Length > 0 ? acquired - holderSince : TimeSpan.Zero);
        lock (_mu)
        {
            _holder = label;
            _holderSince = acquired;
        }
        var call = CancellationTokenSource.CreateLinkedTokenSource(ct, epoch);
        call.CancelAfter(_config.CallTimeout);
        var entry = new Entry(this, call, epoch, waited);
        try
        {
            Current(call.Token, epoch);
        }
        catch
        {
            entry.Dispose();
            throw;
        }
        return entry;
    }

    /// <summary>
    /// Opens a bounded context for a read-only caller that claims no gate: a census issues only native reads,
    /// which need no ordering against the planner steps and writes that hold the gate (a step holds it up to
    /// CallTimeout), so queuing one behind them only makes it miss its deadline.
    /// </summary>
    internal CancellationTokenSource Observe(CancellationToken ct)
    {
        bool closed;
        lock (_mu)
        {
            closed = _closing || _closed || _lifetime.IsCancellationRequested;
        }
        if (closed)
        {
            throw new ControlException("observe: p.closing || p.closed || p.lifetime.Err() != nil");
        }
        var call = CancellationTokenSource.CreateLinkedTokenSource(ct);
        call.CancelAfter(_config.CallTimeout);
        return call;
    }

    internal void Current(CancellationToken ct, CancellationToken epoch)
    {
        ct.ThrowIfCancellationRequested();
        lock (_mu)
        {
            if (_closing || _closed || _epoch != epoch || epoch.IsCancellationRequested)
            {
                throw new ControlException("current: p.closing || p.closed || p.epoch != epoch || epoch.Err() != nil");
            }
        }
    }

    internal async Task WorldAsync(World expected, CancellationToken ct)
    {
        expected.Validate();
        var actual = await _worlds.ReadWorldAsync(ct).ConfigureAwait(false);
        actual.Validate();
        if (actual != expected)
        {
            throw new ConflictException();
        }
        ct.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// Historical replay is returned before any native effect; even Granted never restores a live lease. Actual
    /// permission is reported separately by <see cref="State"/>. Returns null when the request was never begun.
    /// </summary>
    private async Task<ControlRecord?> LookupAsync(ControlRequest request, CancellationToken ct)
    {
        try
        {
            var old = await _journal.LookupControlAsync(request.RequestId, ct).ConfigureAwait(false);
            if (old.Request != request)
            {
                throw new ConflictException();
            }
            return old;
        }
        catch (NotFoundException)
        {
            return null;
        }
    }

    private async Task<ControlRecord> FinishAsync(ControlRecord record, ControlPhase phase, NativeGeneration generation, Exception? cause)
    {
        ControlRecord result;
        try
        {
            using var journal = new CancellationTokenSource(_config.JournalTimeout);
            result = await _journal.CompleteControlAsync(record.Request.RequestId, phase, generation, journal.Token).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            throw ErrorJoin.Of(cause, e, DisableCaptured())!;
        }
        if (cause is not null)
        {
            ExceptionDispatchInfo.Capture(cause).Throw();
        }
        return result;
    }

    private Exception? DisableCaptured()
    {
        try
        {
            _session.Disable();
            return null;
        }
        catch (Exception e)
        {
            return e;
        }
    }

    /// <summary>Records the request uncertain and throws; always throws, as the Go helper always returned an error.</summary>
    private Task<ControlRecord> UncertainAsync(ControlRecord record, Exception cause) =>
        FinishAsync(record, ControlPhase.Uncertain, new NativeGeneration(0), ErrorJoin.Of(cause, DisableCaptured()));

    /// <summary>Enables autonomous play for the requested world under its root plan.</summary>
    public async Task<ControlRecord> ResumeAsync(ControlRequest request, CancellationToken ct)
    {
        using var entry = await EnterAsync(ct, "resume", false).ConfigureAwait(false);
        return await ResumeHeldAsync(entry.Call, entry.Epoch, request).ConfigureAwait(false);
    }

    private async Task<ControlRecord> ResumeHeldAsync(CancellationToken call, CancellationToken epoch, ControlRequest request)
    {
        if (request.Kind != ControlKind.Resume)
        {
            throw new ConflictException();
        }
        if (await LookupAsync(request, call).ConfigureAwait(false) is { } old)
        {
            return old;
        }
        _session.Disable();
        _ = await StopRoundsAsync(call).ConfigureAwait(false);
        await WorldAsync(request.World, call).ConfigureAwait(false);
        Current(call, epoch);
        var (record, created) = await _journal.BeginControlAsync(request, call).ConfigureAwait(false);
        if (!created)
        {
            return record;
        }
        return await CompleteResumeHeldAsync(call, epoch, record).ConfigureAwait(false);
    }

    private async Task<ControlRecord> CompleteResumeHeldAsync(CancellationToken call, CancellationToken epoch, ControlRecord record, NativeGeneration? handoff = null)
    {
        var request = record.Request;
        GenerationSnapshot snapshot;
        try
        {
            Current(call, epoch);
            var root = await _journal.EnsureRootPlanAsync(request.World, call).ConfigureAwait(false);
            snapshot = new GenerationSnapshot(request.World.Colony, request.World.Map, request.World.Load, root.Spec.Id, root.Spec.Revision, new NativeGeneration(0));
        }
        catch (Exception e)
        {
            return await UncertainAsync(record, e).ConfigureAwait(false);
        }
        GenerationSnapshot granted;
        try
        {
            // A grant this process still holds for that scope as far as it has observed (disabled locally on a
            // clock hold, or live) is re-acquired in place by Acquire: one native generation per resume, not the
            // Manual->Auto pair that rebinds every prepared action. Manual first stays for a genuine switch: an
            // observed revocation or an uncertain grant. Manual also revokes a grant left standing by a failed
            // status read (observation unknown); the held-grant check answers that case by re-acquiring it in
            // place instead. That Manual keeps the drafts a plan or an open fight still holds, as the Acquire
            // drain does: a player pause and resume mid-raid must not undraft the defenders.
            if (_session.TargetsWorld(request.World) && !_session.HoldsGrant(snapshot))
            {
                await _session.ManualForResumeAsync(call).ConfigureAwait(false);
            }
            // Recheck actual world after cleanup, before attempting new authority.
            await WorldAsync(request.World, call).ConfigureAwait(false);
            Current(call, epoch);
            granted = await _session.AcquireAsync(snapshot, call, handoff).ConfigureAwait(false);
            Current(call, epoch);
            var expected = snapshot with { Native = granted.Native };
            var actual = _session.State();
            if (granted.Native.Value == 0 || granted != expected || !actual.Enabled || !actual.ObservationKnown || actual.Snapshot != granted)
            {
                throw new ControlException("Resume: granted.Native == 0 || granted != expected || !actual.Enabled || !actual.ObservationKnown || actual.Snapshot != granted");
            }
        }
        catch (Exception e)
        {
            return await UncertainAsync(record, e).ConfigureAwait(false);
        }
        return await FinishAsync(record, ControlPhase.Running, granted.Native, null).ConfigureAwait(false);
    }

    internal static World PlayerWorld(GenerationSnapshot snapshot) => new(snapshot.Colony, snapshot.Load, snapshot.Map);

    /// <summary>
    /// Always stops local writes first, including on historical replay or a stale browser world. Only fresh
    /// matching scope permits native cleanup.
    /// </summary>
    public async Task<ControlRecord> PauseAsync(ControlRequest request, CancellationToken ct)
    {
        using var entry = await EnterAsync(ct, "manual", true).ConfigureAwait(false);
        var call = entry.Call;
        var epoch = entry.Epoch;
        _ = await StopRoundsAsync(call).ConfigureAwait(false);
        if (request.Kind != ControlKind.Pause)
        {
            throw new ConflictException();
        }
        if (await LookupAsync(request, call).ConfigureAwait(false) is { } old)
        {
            return old;
        }
        var (record, created) = await _journal.BeginControlAsync(request, call).ConfigureAwait(false);
        if (!created)
        {
            return record;
        }
        try
        {
            await WorldAsync(request.World, call).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            return await FinishAsync(record, ControlPhase.Refused, new NativeGeneration(0), e).ConfigureAwait(false);
        }
        try
        {
            Current(call, epoch);
            // Manual revokes this process's own grant for the world when its observation is known, and also when
            // a failed status read left the observation unknown while the grant still stands natively: Acquire
            // refuses an Active it once targeted, so without the revoke every later request would end uncertain.
            if (_session.TargetsWorld(request.World))
            {
                await _session.ManualAsync(call).ConfigureAwait(false);
            }
            Current(call, epoch);
        }
        catch (Exception e)
        {
            return await UncertainAsync(record, e).ConfigureAwait(false);
        }
        return await FinishAsync(record, ControlPhase.Paused, new NativeGeneration(0), null).ConfigureAwait(false);
    }

    /// <summary>
    /// Retains the session and caller-owned handles on a failed drain. It may be retried, but no new player
    /// request is accepted once closing begins.
    /// </summary>
    public async Task CloseAsync(CancellationToken ct)
    {
        Exception? disableErr;
        lock (_mu)
        {
            if (_closed)
            {
                return;
            }
            _closing = true;
            _epochSource.Cancel();
            disableErr = DisableCaptured();
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
            throw ErrorJoin.Of(disableErr, cancelled)!;
        }
        try
        {
            lock (_mu)
            {
                if (_closed)
                {
                    return;
                }
            }
            try
            {
                await _session.CloseAsync(call).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                throw ErrorJoin.Of(disableErr, e)!;
            }
            lock (_mu)
            {
                _closed = true;
                _stopLifetime.Dispose();
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
