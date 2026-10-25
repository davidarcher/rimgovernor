namespace RimGovernor.Brain.Runtime;

public sealed class SessionConfig
{
    public bool ReviewMethods { get; set; }
    public required ControlConfig Control { get; set; }
    public Executor.Limits Executor { get; set; } = new();
    public ClockCapabilities? Clock { get; set; }
    public HaulCapabilities? Haul { get; set; }
    public MovementCapabilities? Movement { get; set; }
    public TradeCapabilities? Trade { get; set; }

    /// <summary>
    /// The emitter the planners' static helpers write through. The host installs
    /// the one over its OTLP writer; left null, rows keep going to whatever was installed before (the null sink).
    /// </summary>
    public TelemetryEmitter? Telemetry { get; set; }
}

/// <summary>
/// Binds the single profile owner to one journal and executor. Its caller owns bridge/database handles and may
/// close them only after Close succeeds. Only an explicit trusted player path may call Acquire or create
/// submitted plans.
/// </summary>
public sealed partial class Session : IPlayerSession, IWorkerSession
{
    private readonly bool _reviewMethods;
    private readonly Control _control;
    private readonly Executor.Executor _executor;
    private readonly StoreJournal _journal;
    private readonly ClockHolder? _clock;
    private readonly PlannerWorkerSlot _clockWorkers;

    private Session(bool reviewMethods, Control control, Executor.Executor executor, StoreJournal journal, ClockHolder? clock, PlannerWorkerSlot clockWorkers)
    {
        _reviewMethods = reviewMethods;
        _control = control;
        _executor = executor;
        _journal = journal;
        _clock = clock;
        _clockWorkers = clockWorkers;
    }

    public static async Task<Session> CreateAsync(
        SessionConfig config,
        StoreJournal journal,
        IBoundaryNative native,
        INativeAuthority authority,
        IActionsWriter writer,
        IClock clock,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(native);
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(clock);
        if (config.Movement is { Writer: null })
        {
            throw new ArgumentException("complete movement capabilities required");
        }
        if (config.Clock is { } cc && (cc.Native is null || cc.Writer is null))
        {
            throw new ArgumentException("complete clock capabilities required");
        }
        if (config.Telemetry is { } telemetry)
        {
            Tel.Emitter = telemetry;
        }
        var sink = new SessionSink { ClockWorkers = new PlannerWorkerSlot() };
        var ns = await journal.IdentityAsync(ct).ConfigureAwait(false);
        var controlConfig = config.Control with
        {
            StopWrites = sink.Stop,
            CleanupWrites = sink.Cleanup,
            ResumeWrites = SessionSink.Resume,
            // The world source is the host's (a facade ReadTick), never derived from the clock capability: the clock
            // native client reads a clock status, not the loaded world's identity.
            Worlds = config.Control.Worlds,
        };
        var control = await Control.CreateAsync(controlConfig, new StoreSessionIdentity(journal), authority, sink, ct).ConfigureAwait(false);

        async Task Cleanup()
        {
            using var shutdown = new CancellationTokenSource(config.Control.CallTimeout);
            await control.CloseAsync(shutdown.Token).ConfigureAwait(false);
        }

        try
        {
            lock (sink.Mu)
            {
                sink.Control = control;
            }
            var place = new BuildingBoundary(native, writer, clock, ns.Value);
            MovementBoundary? moves = null;
            if (config.Movement is not null)
            {
                moves = new MovementBoundary(config.Movement.Writer);
            }
            if (config.Haul is { } haulCaps && (haulCaps.Native is null || haulCaps.Writer is null))
            {
                throw new ControlException("NewSession: config.Haul != nil && (config.Haul.Native == nil || config.Haul.Writer == nil)");
            }
            if (config.Trade is { } tradeCaps && (tradeCaps.Native is null || tradeCaps.Writer is null))
            {
                throw new ControlException("NewSession: config.Trade != nil && (config.Trade.Native == nil || config.Trade.Writer == nil)");
            }
            var routine = new PlanAuthorizer(journal, config.ReviewMethods);
            var executorJournal = new ExecutorJournal(journal);
            var worker = moves is not null
                ? Executor.Executor.NewWithMovement(executorJournal, place, moves, clock, config.Executor, routine)
                : new Executor.Executor(executorJournal, place, clock, config.Executor, routine);
            // Each optional capability is wired directly onto worker with its own typed boundary value, never
            // discovered by a type test on a composed boundary.
            if (config.Trade is { } trade)
            {
                var tradeBoundary = new TradeBoundary(clock, trade);
                if (trade.Requests is not null)
                {
                    worker.EnableCommsTradeRequests(tradeBoundary);
                }
                worker.EnableTrade(tradeBoundary);
            }
            if (config.Haul is { } haul)
            {
                worker.EnableHaul(new HaulBoundary(haul.Native, haul.Writer, clock));
            }
            ClockHolder? holder = null;
            if (config.Clock is { } clockCaps)
            {
                holder = ClockHolder.Create(clockCaps.Writer, config.Control.CallTimeout);
            }
            try
            {
                sink.Attach(worker, holder, ct);
            }
            catch
            {
                if (holder is not null)
                {
                    await holder.Stop(CancellationToken.None).ConfigureAwait(false);
                }
                throw;
            }
            return new Session(config.ReviewMethods, control, worker, journal, holder, sink.ClockWorkers);
        }
        catch
        {
            await Cleanup().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Binds explicit intent to the exact durable plan revision. A proposal without a stored plan cannot obtain a write lease.</summary>
    public async Task<GenerationSnapshot> AcquireAsync(GenerationSnapshot requested, CancellationToken ct, NativeGeneration? handoff = null)
    {
        var state = await _journal.LoadPlanAsync(requested.Plan, ct).ConfigureAwait(false);
        if (state.Spec.Revision != requested.Revision)
        {
            throw new Executor.AuthorityException();
        }
        return await _control.AcquireAsync(requested, ct, handoff).ConfigureAwait(false);
    }

    internal StoreJournal Journal => _journal;

    public ControlState State() => _control.State();

    public bool HoldsGrant(GenerationSnapshot scope) => _control.HoldsGrant(scope);

    public void Disable() => _control.Disable();

    /// <summary>Disables authority only if the state still is the one the caller observed; a <see cref="ConflictException"/> otherwise.</summary>
    internal void DisableObserved(ControlState observed) => _control.DisableObserved(observed);

    /// <summary>Attaches only read reconciliation to a durable plan. It cannot obtain a lease, even if native status reports an active owner for this namespace.</summary>
    public async Task ObserveTargetAsync(GenerationSnapshot requested, CancellationToken ct)
    {
        var state = await _journal.LoadPlanAsync(requested.Plan, ct).ConfigureAwait(false);
        if (state.Spec.Revision != requested.Revision)
        {
            throw new Executor.AuthorityException();
        }
        await _control.ObserveTargetAsync(requested, ct).ConfigureAwait(false);
    }

    public Task RefreshAsync(CancellationToken ct) => _control.RefreshAsync(ct);

    public bool TargetsWorld(World world) => _control.TargetsWorld(world);

    public Task ManualAsync(CancellationToken ct) => _control.ManualAsync(ct);

    public Task ManualForResumeAsync(CancellationToken ct) => _control.ManualForResumeAsync(ct);

    /// <summary>Dispatches a plan's actions in one native Apply.</summary>
    public async Task<IReadOnlyList<Executor.BatchItem>> RunBatchAsync(PlanId plan, IReadOnlyList<ActionId> actions, CancellationToken ct)
    {
        // The plan's method intent rides with the writes the dispatch issues.
        using var intent = OperationIntentScope.Begin(await Session.PlanIntentAsync(_journal, plan, ct).ConfigureAwait(false));
        return await _executor.RunBatchAsync(plan, actions, ct).ConfigureAwait(false);
    }

    /// <summary>Reissues a lost movement order as a new journaled attempt under current authority.</summary>
    public async Task<Executor.BatchItem> ReassertMovementAsync(PlanId plan, ActionId action, Tick observed, CancellationToken ct)
    {
        using var intent = OperationIntentScope.Begin(await PlanIntentAsync(_journal, plan, ct).ConfigureAwait(false));
        return await _executor.ReassertMovementAsync(plan, action, observed, ct).ConfigureAwait(false);
    }

    public Task CloseAsync(CancellationToken ct) => _control.CloseAsync(ct);

    /// <summary>Whether the session carries the owned clock boundary (a <see cref="ClockCapabilities"/> was configured).</summary>
    public bool HasClock => _clock is not null;

    /// <summary>
    /// Registers what drives the session's clock windows, to be stopped before the clock coordinator on a stop. Throws when the session has no clock, is closing, or already has one.
    /// </summary>
    public void AttachClockWorker(PlannerRunDriver worker)
    {
        if (_clock is null)
        {
            throw new ControlException("attachClockWorker: s.clock == nil");
        }
        _clockWorkers.Attach(worker);
    }

    /// <summary>
    /// Holds the native clock for <paramref name="reason"/> (the bot stops acting: Manual, a save, shutdown); a
    /// session without the clock capability holds nothing.
    /// </summary>
    public Task HoldClockAsync(RimGovernor.Brain.GameFacts.Clock.HoldReason reason, string detail, CancellationToken ct) =>
        _clock is null ? Task.CompletedTask : _clock.HoldAsync(reason, detail, ct);

    public bool ReviewMethodsEnabled() => _reviewMethods;
}

/// <summary>Relays authority changes between the control and the executor/clock it is wired to.</summary>
internal sealed partial class SessionSink
{
    internal readonly object Mu = new();
    internal Executor.Executor? Executor;
    internal Control? Control;
    internal ClockHolder? Clock;
    internal PlannerWorkerSlot ClockWorkers = null!;

    public void UpdateAuthority(Executor.Authority value)
    {
        lock (Mu)
        {
            if (Executor is null)
            {
                if (value.Enabled)
                {
                    throw new ControlException("UpdateAuthority: value.Enabled");
                }
                return;
            }
            var errors = new List<Exception>();
            try
            {
                Executor.UpdateAuthority(value);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                errors.Add(e);
            }
            if (Clock is not null)
            {
                try
                {
                    Clock.UpdateAuthority(value);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    errors.Add(e);
                }
            }
            if (errors.Count == 1)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(errors[0]).Throw();
            }
            if (errors.Count > 1)
            {
                throw new AggregateException(errors);
            }
        }
    }

    internal async Task Stop(CancellationToken ct)
    {
        var workerStopped = await ClockWorkers.Stop(ct).ConfigureAwait(false);
        Executor.Executor? e;
        ClockHolder? clock;
        lock (Mu)
        {
            e = Executor;
            clock = Clock;
        }
        if (e is null)
        {
            return;
        }
        var errors = new List<Exception>();
        try
        {
            await e.StopAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            errors.Add(ex);
        }
        if (clock is not null)
        {
            try
            {
                await clock.Stop(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors.Add(ex);
            }
        }
        if (errors.Count == 1)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(errors[0]).Throw();
        }
        if (errors.Count > 1)
        {
            throw new AggregateException(errors);
        }
        if (clock is not null && !workerStopped)
        {
            await clock.HoldAsync(RimGovernor.Brain.GameFacts.Clock.HoldReason.Review, "stop", ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Holds the clock for an explicit Manual, a checkpoint save and shutdown. Drafted pawns stay drafted; with
    /// authority inactive the game's own auto-undraft applies again.
    /// </summary>
    internal async Task Cleanup(CancellationToken ct)
    {
        ClockHolder? clock;
        lock (Mu)
        {
            clock = Clock;
        }
        if (clock is not null)
        {
            await clock.HoldAsync(RimGovernor.Brain.GameFacts.Clock.HoldReason.Review, "bot_off", ct).ConfigureAwait(false);
        }
    }

    /// <summary>Before a resume in the same world: nothing is owed, the driver's Run resumes time once authority is back.</summary>
    internal static Task Resume(CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// Publish only after the final fallible construction check. Until publication, failure cleanup releases the
    /// owner without draining unrelated durable work.
    /// </summary>
    internal void Attach(Executor.Executor worker, ClockHolder? clock, CancellationToken ct)
    {
        lock (Mu)
        {
            ct.ThrowIfCancellationRequested();
            Executor = worker;
            Clock = clock;
        }
    }
}

/// <summary>Adapts the journal's controller session id read to the control's identity source.</summary>
internal sealed class StoreSessionIdentity(StoreJournal store)
{
    public async Task<ControllerSessionId> IdentityAsync(CancellationToken ct) => new((await store.IdentityAsync(ct).ConfigureAwait(false)).Value);
}

/// <summary>
/// The intent text the writes of one dispatch carry: it is stamped as the
/// purpose of every action that has none. The scope follows the async flow.
/// </summary>
internal static class OperationIntentScope
{
    private static readonly AsyncLocal<string?> s_current = new();

    /// <summary>The intent of the dispatch in flight, empty when none was set.</summary>
    internal static string Current => s_current.Value ?? "";

    /// <summary>Names why the writes under the scope are issued; an empty intent leaves the scope as it was.</summary>
    internal static IDisposable Begin(string intent)
    {
        var previous = s_current.Value;
        if (intent.Length != 0)
        {
            s_current.Value = intent;
        }
        return new Restore(previous);
    }

    private sealed class Restore(string? previous) : IDisposable
    {
        public void Dispose() => s_current.Value = previous;
    }
}

partial class Session
{
    /// <summary>
    /// The method intent of a plan's writes; a plan no goal admitted, or a failed lookup, dispatches without one.
    /// </summary>
    internal static async Task<string> PlanIntentAsync(StoreJournal journal, PlanId plan, CancellationToken ct)
    {
        try
        {
            var method = await journal.PlanMethodAsync(plan, ct).ConfigureAwait(false);
            return method is null ? "" : OperationIntent.Of(method);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return "";
        }
    }
}
